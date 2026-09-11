using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Painting;
namespace Splatoon.Prototype
{
    public sealed class PrototypeArena : MonoBehaviour
    {
        public static PrototypeArena Current { get; private set; }
        [Tooltip("两队各两个出生点，橙队在前、蓝队在后")] public Transform[] SpawnPoints;
        public int LayoutVersion = 3;
        public Vector2 Dimensions = new(32, 64);
        public float OwnershipCellSize = .125f;
        public string BakedTopology;
        public readonly SortedDictionary<int, PaintSurface> Surfaces = new();
        public double OrangeArea => Surfaces.Values.Sum(s => s.Ownership?.OrangeArea ?? 0);
        public double BlueArea => Surfaces.Values.Sum(s => s.Ownership?.BlueArea ?? 0);
        public double TotalArea => Surfaces.Values.Sum(s => s.Ownership?.TotalArea ?? 0);
        public int CellCount => Surfaces.Values.Sum(s => s.Ownership?.Cells.Length ?? 0);
        public static readonly Color Orange = new(1f, .30f, .055f);
        public static readonly Color Blue = new(.08f, .42f, 1f);
        public static Color TeamColor(byte team) => team == 1 ? Orange : Blue;
        private void Awake()
        {
            Current = this;
            if (LubanConfigService.Current.IsReady) InitializeRuntime();
        }
        public void InitializeRuntime()
        {
            var config = GameplayConfig.Arena;
            if (LayoutVersion != config.LayoutVersion || Dimensions != new Vector2(config.Width, config.Length) || OwnershipCellSize != config.CellSize)
                throw new InvalidOperationException("场景布局与配置不一致");
            RegisterSurfaces();
            if (SpawnPoints == null || SpawnPoints.Length != 4 || SpawnPoints.Any(p => p == null)) throw new InvalidOperationException("场景需要四个出生点");
            if (string.IsNullOrEmpty(BakedTopology) || BakedTopology != ComputeTopology()) throw new InvalidOperationException("地图已修改，请先执行：喷墨对战/地图/校验并烘焙当前地图");
            foreach (var surface in Surfaces.Values) surface.InitializeOwnership(config.CellSize);
            if (TotalArea <= 0) throw new InvalidOperationException("地图缺少可计分区域");
        }
        public void RegisterSurfaces()
        {
            Surfaces.Clear();
            foreach (var surface in GetComponentsInChildren<PaintSurface>(true))
                if (surface.SurfaceId <= 0 || !Surfaces.TryAdd(surface.SurfaceId, surface)) throw new InvalidOperationException("表面 ID 重复或无效");
        }
        public string ComputeTopology()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(LayoutVersion); writer.Write(Dimensions.x); writer.Write(Dimensions.y); writer.Write(OwnershipCellSize);
            foreach (var s in GetComponentsInChildren<PaintSurface>(true).OrderBy(s => s.SurfaceId))
            {
                writer.Write(s.SurfaceId); writer.Write(s.Scores); writer.Write(s.Resolution);
                writer.Write(s.WalkableSize.x); writer.Write(s.WalkableSize.y);
                for (int i = 0; i < 16; i++) writer.Write(s.transform.localToWorldMatrix[i]);
                var mesh = s.GetComponent<MeshFilter>().sharedMesh;
                writer.Write(mesh.vertexCount);
                foreach (var v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                writer.Write(mesh.triangles.Length); foreach (int i in mesh.triangles) writer.Write(i);
                foreach (var uv in mesh.uv2) { writer.Write(uv.x); writer.Write(uv.y); }
                writer.Write(s.BlockedCells.Length); foreach (int i in s.BlockedCells) writer.Write(i);
            }
            foreach (var p in SpawnPoints) { writer.Write(p.position.x); writer.Write(p.position.y); writer.Write(p.position.z); }
            // Colliders without paint (rails, boundary posts) also change walkability and must invalidate the bake.
            foreach (var c in GetComponentsInChildren<Collider>(true).OrderBy(c => HierarchyPath(c.transform), StringComparer.Ordinal))
            {
                writer.Write(HierarchyPath(c.transform)); writer.Write(c.enabled); writer.Write(c.isTrigger); writer.Write(c.gameObject.activeSelf);
                for (int i = 0; i < 16; i++) writer.Write(c.transform.localToWorldMatrix[i]);
                if (c is BoxCollider box)
                { writer.Write(box.center.x); writer.Write(box.center.y); writer.Write(box.center.z); writer.Write(box.size.x); writer.Write(box.size.y); writer.Write(box.size.z); }
                else if (c is MeshCollider meshCollider)
                { writer.Write(meshCollider.convex); foreach (var v in meshCollider.sharedMesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); } }
            }
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
        private string HierarchyPath(Transform value) => value == transform ? "" : HierarchyPath(value.parent) + "/" + value.name;
        public static Vector3 Spawn(byte team, int slot)
        {
            if (Current == null || team < 1 || team > 2 || slot < 0 || slot > 1) throw new InvalidOperationException("出生点或队伍无效");
            return Current.SpawnPoints[(team - 1) * 2 + slot].position;
        }
        public byte FloorOwner(Vector3 feet)
        {
            if (!Physics.Raycast(feet + Vector3.up * .2f, Vector3.down, out var hit, .55f, ~(1 << 8), QueryTriggerInteraction.Ignore)) return 255;
            var surface = hit.collider.GetComponent<PaintSurface>();
            return surface?.Ownership == null ? (byte)255 : surface.Ownership.At(surface.transform.InverseTransformPoint(hit.point));
        }
        public void Apply(PaintStamp stamp, bool updateOwnership)
        {
            if (!Surfaces.TryGetValue(stamp.SurfaceId, out var surface)) throw new InvalidOperationException("未知涂色表面：" + stamp.SurfaceId);
            ApplyToSurface(surface, stamp, updateOwnership);
            // A floor split into rendering tiles is still one continuous paintable plane.
            // Only coplanar neighbours participate, so a bridge never paints the ground below it.
            if (!surface.Scores) return;
            foreach (var neighbour in Surfaces.Values)
            {
                if (neighbour == surface || !neighbour.Scores || Vector3.Dot(neighbour.transform.up, surface.transform.up) < .9999f) continue;
                var local = neighbour.transform.InverseTransformPoint(stamp.Position);
                if (Mathf.Abs(local.y) > .005f || Mathf.Abs(local.x) > neighbour.WalkableSize.x / 2 + stamp.Radius || Mathf.Abs(local.z) > neighbour.WalkableSize.y / 2 + stamp.Radius) continue;
                ApplyToSurface(neighbour, stamp, updateOwnership);
            }
        }
        private static void ApplyToSurface(PaintSurface surface, PaintStamp stamp, bool updateOwnership)
        {
            surface.Apply(stamp);
            if (updateOwnership && surface.Ownership != null && Vector3.Dot(surface.transform.up, stamp.Normal) >= .5f)
                surface.Ownership.Paint(surface.transform.InverseTransformPoint(stamp.Position), InkBrush.OwnershipRadius(stamp.Radius, stamp.Hardness, stamp.Strength, GameplayConfig.Global.PaintThreshold), stamp.Team);
        }
        public Dictionary<int, byte[]> CaptureOwnership() => Surfaces.Values.Where(s => s.Ownership != null).ToDictionary(s => s.SurfaceId, s => (byte[])s.Ownership.Cells.Clone());
        public void RestoreOwnership(IReadOnlyDictionary<int, byte[]> grids)
        {
            var walkable = Surfaces.Values.Where(s => s.Ownership != null).ToArray();
            if (grids.Count != walkable.Length) throw new InvalidOperationException("归属表面数量不一致");
            foreach (var s in walkable) { if (!grids.TryGetValue(s.SurfaceId, out var data)) throw new InvalidOperationException("缺少归属表面"); s.Ownership.ValidateSnapshot(data); }
            foreach (var s in walkable) s.Ownership.Restore(grids[s.SurfaceId]);
        }
        public uint OwnershipHash()
        {
            uint hash = 2166136261;
            foreach (var s in Surfaces.Values) if (s.Ownership != null)
            { hash = unchecked((hash ^ (uint)s.SurfaceId) * 16777619); foreach (byte b in s.Ownership.Cells) hash = unchecked((hash ^ b) * 16777619); }
            return hash;
        }
        public void ClearPaint() { foreach (var surface in Surfaces.Values) surface.Clear(); }
        private void OnDestroy() { if (Current == this) Current = null; }
    }
}
