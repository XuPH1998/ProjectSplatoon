using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;
namespace Splatoon.Prototype
{
    public sealed class PrototypeArena : MonoBehaviour
    {
        public static PrototypeArena Current { get; private set; }
        [Tooltip("两队各四个出生点，粉队在前、蓝队在后")] public Transform[] SpawnPoints;
        public int LayoutVersion = 4;
        public Vector2 Dimensions = new(32, 64);
        public float OwnershipCellSize = .125f;
        public string BakedTopology;
        public readonly SortedDictionary<int, PaintSurface> Surfaces = new();
        PaintSurface[] _paintSurfaces = Array.Empty<PaintSurface>();
        readonly GroundPaintSeams _groundSeams = new();
        readonly HashSet<PaintSurface> _paintedSurfaces = new();
        readonly List<(PaintSurface surface, PaintStamp stamp)> _seamQueue = new();
        HeroChangeZone[] _heroChangeZones;
        public void RegisterHeroChangeZones() => _heroChangeZones = GetComponentsInChildren<HeroChangeZone>(true);
        public bool IsInHeroChangeZone(byte team, Vector3 position)
        {
            if (_heroChangeZones == null) RegisterHeroChangeZones();
            foreach (var zone in _heroChangeZones)
                if (zone != null && zone.Contains(team, position)) return true;
            return false;
        }
        public double PinkArea => Surfaces.Values.Sum(s => s.Ownership?.PinkArea ?? 0);
        public double BlueArea => Surfaces.Values.Sum(s => s.Ownership?.BlueArea ?? 0);
        public double TotalArea => Surfaces.Values.Sum(s => s.Ownership?.TotalArea ?? 0);
        public int CellCount => Surfaces.Values.Sum(s => s.Ownership?.Cells.Length ?? 0);
        public static readonly Color Pink = new(.9433962f, .27945885f, .47586557f);
        public static readonly Color Blue = new(125f / 255, 227f / 255, 232f / 255);
        public static Color TeamColor(byte team) => team == 1 ? Pink : Blue;
        private void Awake()
        {
            Current = this;
            if (LubanConfigService.Current.IsReady) InitializeRuntime();
        }
        public void InitializeRuntime()
        {
            var config = GameplayConfig.Map;
            if (OwnershipCellSize != config.CellSize)
                throw new InvalidOperationException("场景归属网格与配置不一致");
            RegisterSurfaces();
            RegisterHeroChangeZones();
            if (SpawnPoints == null || SpawnPoints.Length != 2 * TeamSelectionRules.Capacity || SpawnPoints.Any(p => p == null)) throw new InvalidOperationException("场景需要八个出生点，每队四个");
            if (string.IsNullOrEmpty(BakedTopology) || BakedTopology != ComputeTopology()) throw new InvalidOperationException("地图已修改，请先执行：喷墨对战/地图/校验并烘焙当前地图");
            foreach (var surface in Surfaces.Values) { InkShapeAtlas.Configure(surface.ShapeAtlas); surface.InitializeOwnership(config.CellSize); }
            if (TotalArea <= 0) throw new InvalidOperationException("地图缺少可计分区域");
        }
        public void RegisterSurfaces()
        {
            Surfaces.Clear();
            foreach (var surface in GetComponentsInChildren<PaintSurface>(true))
                if (surface.SurfaceId <= 0 || !Surfaces.TryAdd(surface.SurfaceId, surface)) throw new InvalidOperationException("表面 ID 重复或无效");
            _paintSurfaces = Surfaces.Values.ToArray();
            _groundSeams.Build(_paintSurfaces);
        }
        public string ComputeTopology()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(LayoutVersion); writer.Write(Dimensions.x); writer.Write(Dimensions.y); writer.Write(OwnershipCellSize);
            foreach (var s in GetComponentsInChildren<PaintSurface>(true).OrderBy(s => s.SurfaceId))
            {
                writer.Write(s.SurfaceId); writer.Write(s.Scores); writer.Write(s.Resolution); writer.Write(s.Height);
                writer.Write(s.WalkableSize.x); writer.Write(s.WalkableSize.y);
                for (int i = 0; i < 16; i++) writer.Write(s.transform.localToWorldMatrix[i]);
                var mesh = s.GetComponent<MeshFilter>().sharedMesh;
                writer.Write(mesh.vertexCount);
                foreach (var v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                writer.Write(mesh.triangles.Length); foreach (int i in mesh.triangles) writer.Write(i);
                foreach (var uv in mesh.uv2) { writer.Write(uv.x); writer.Write(uv.y); }
                writer.Write(s.BlockedCells.Length); foreach (int i in s.BlockedCells) writer.Write(i);
                writer.Write(s.WallRegions.Length);
                foreach (var r in s.WallRegions)
                {
                    writer.Write(r.Id); writer.Write(r.Climbable);
                    writer.Write(r.Origin.x); writer.Write(r.Origin.y); writer.Write(r.Origin.z);
                    writer.Write(r.Rotation.x); writer.Write(r.Rotation.y); writer.Write(r.Rotation.z); writer.Write(r.Rotation.w);
                    writer.Write(r.Size.x); writer.Write(r.Size.y);
                    writer.Write(r.BlockedCells.Length); foreach (int cell in r.BlockedCells) writer.Write(cell);
                }
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
            foreach (var zone in GetComponentsInChildren<HeroChangeZone>(true).OrderBy(z => HierarchyPath(z.transform), StringComparer.Ordinal))
            {
                writer.Write(HierarchyPath(zone.transform)); writer.Write(zone.Team);
                writer.Write(zone.enabled); writer.Write(zone.gameObject.activeInHierarchy);
            }
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
        private string HierarchyPath(Transform value) => value == transform ? "" : HierarchyPath(value.parent) + "/" + value.name;
        public static Vector3 Spawn(byte team, int slot)
        {
            if (Current == null || team < 1 || team > 2 || slot < 0 || slot >= TeamSelectionRules.Capacity) throw new InvalidOperationException("出生点或队伍无效");
            return Current.SpawnPoints[(team - 1) * TeamSelectionRules.Capacity + slot].position;
        }
        public byte FloorOwner(Vector3 feet)
        {
            if (!Physics.Raycast(feet + Vector3.up * .2f, Vector3.down, out var hit, .55f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return 255;
            var surface = hit.collider.GetComponent<PaintSurface>();
            return surface != null && surface.QueryRegion(hit.point, hit.normal, out var contact) ? contact.Owner : (byte)255;
        }
        public static bool TryGetGround(Vector3 feet, out byte owner, float slopeLimit = 45)
        {
            owner = 255;
            if (!Physics.Raycast(feet + Vector3.up * .08f, Vector3.down, out var hit, .16f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) || hit.normal.y < Mathf.Cos(slopeLimit * Mathf.Deg2Rad)) return false;
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            owner = surface != null && surface.QueryRegion(hit.point, hit.normal, out var contact) ? contact.Owner : (byte)0;
            return true;
        }
        public double LastPaintGainedArea {get;private set;}
        public void Apply(PaintStamp stamp, bool updateOwnership)
        {
            LastPaintGainedArea=0;
            using var marker = FramePerformance.PaintCpu.Auto(); FramePerformance.PaintStamps++;
            if (!Surfaces.TryGetValue(stamp.SurfaceId, out var surface)) throw new InvalidOperationException("未知涂色表面：" + stamp.SurfaceId);
            _paintedSurfaces.Clear(); _seamQueue.Clear();
            ApplyPlane(surface, stamp, updateOwnership);
            for (int i = 0; i < _seamQueue.Count; i++)
            {
                var pending = _seamQueue[i]; var links = _groundSeams.From(pending.surface);
                if (links == null || Vector3.Dot(pending.surface.transform.up, pending.stamp.Normal) < .9999f) continue;
                foreach (var link in links)
                    if (!_paintedSurfaces.Contains(link.Target) && link.TryFold(pending.stamp, out var folded))
                        ApplyPlane(link.Target, folded, updateOwnership);
            }
        }
        void ApplyPlane(PaintSurface surface, PaintStamp stamp, bool updateOwnership)
        {
            var brush = new InkShapeAtlas.Brush(stamp);
            ApplyToSurface(surface, stamp, brush, updateOwnership);
            // A floor split into rendering tiles is still one continuous paintable plane.
            // Only coplanar neighbours participate, so a bridge never paints the ground below it.
            foreach (var neighbour in _paintSurfaces)
            {
                if (_paintedSurfaces.Contains(neighbour)) continue;
                foreach (var region in neighbour.CachedRegions)
                {
                    if (Vector3.Dot(region.Normal(neighbour), stamp.Normal) < .9999f) continue;
                    var inverse = region.Inverse(neighbour); var local = inverse.MultiplyPoint3x4(stamp.Position);
                    if (Mathf.Abs(local.y) > .005f) continue;
                    var extent = brush.LocalExtents(inverse);
                    if (Mathf.Abs(local.x) > region.Size.x / 2 + extent.x || Mathf.Abs(local.z) > region.Size.y / 2 + extent.y) continue;
                    ApplyToSurface(neighbour, stamp, brush, updateOwnership); break;
                }
            }
        }
        private void ApplyToSurface(PaintSurface surface, PaintStamp stamp, InkShapeAtlas.Brush brush, bool updateOwnership)
        {
            _paintedSurfaces.Add(surface);
            if (surface.Scores) _seamQueue.Add((surface, stamp));
            surface.Apply(stamp);
            if (updateOwnership)
            {
                var grid=surface.Ownership;double before=grid==null?0:stamp.Team==1?grid.PinkArea:grid.BlueArea;
                surface.ApplyRegions(stamp,brush);
                if(grid!=null){double after=stamp.Team==1?grid.PinkArea:grid.BlueArea;
                    float scale=Vector3.Cross(surface.transform.TransformVector(Vector3.right),surface.transform.TransformVector(Vector3.forward)).magnitude;
                    LastPaintGainedArea+=System.Math.Max(0,after-before)*scale;}
            }
        }
        public Dictionary<int, SurfaceOwnershipGrid> RegionGrids() => Surfaces.Values
            .SelectMany(s => s.GameplayRegions.Select(r => new { Key = r.Key(s), r.Grid })).ToDictionary(p => p.Key, p => p.Grid);
        public Dictionary<int, int> OwnershipSizes() => RegionGrids().ToDictionary(p => p.Key, p => p.Value.SnapshotBytes);
        public Dictionary<int, byte[]> CaptureOwnership() => RegionGrids().ToDictionary(p => p.Key, p => p.Value.Capture());
        public void RestoreOwnership(IReadOnlyDictionary<int, byte[]> grids)
        {
            var regions = RegionGrids();
            if (grids.Count != regions.Count) throw new InvalidOperationException("归属区域数量不一致");
            foreach (var pair in regions) { if (!grids.TryGetValue(pair.Key, out var data)) throw new InvalidOperationException("缺少归属区域"); pair.Value.ValidateSnapshot(data); }
            foreach (var pair in regions) pair.Value.Restore(grids[pair.Key]);
        }
        public uint OwnershipHash()
        {
            uint hash = 2166136261;
            foreach (var pair in RegionGrids().OrderBy(p => p.Key))
            { hash = unchecked((hash ^ (uint)pair.Key) * 16777619); foreach (byte b in pair.Value.Cells) hash = unchecked((hash ^ b) * 16777619); foreach (byte b in pair.Value.State) hash = unchecked((hash ^ b) * 16777619); }
            return hash;
        }
        public void ClearPaint() { foreach (var surface in Surfaces.Values) surface.Clear(); }
        private void OnDestroy() { if (Current == this) Current = null; }
    }
}
