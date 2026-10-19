using System;
using System.Collections.Generic;
using Splatoon.Painting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        BlockSurfaceWorld _legacySurfacePaints;
        public BlockSurfaceWorld SurfacePaints { get => FixedBlocks?.Surfaces ?? _legacySurfacePaints; private set => _legacySurfacePaints = value; }
        public event Action SurfacePaintChanged;
        Dictionary<Collider, int[]> _surfacePaintFaces;
        readonly Dictionary<(int face, int u, int v), Mesh> _surfacePaintMeshes = new();
        readonly HashSet<(int face, int u, int v)> _surfacePaintDirty = new();
        Material _surfacePaintMaterial;
        readonly List<BlockSurfaceKey> _surfacePaintChangedKeys = new();

        void InitializeSurfacePaints()
        {
            SurfacePaints = new BlockSurfaceWorld(Round);
            _surfacePaintFaces = BlockSurfaceGeometry.Capture(SurfacePaints, _staticColliders);
        }

        public bool TryGetSurfacePaint(Collider map, Vector3 point, Vector3 normal, out BlockSurfaceCell cell)
        {
            cell = default;
            if (SurfacePaints == null || map == null || !map.enabled || !map.gameObject.activeInHierarchy ||
                !Finite(point) || !Finite(normal) || normal.sqrMagnitude < .000001f ||
                !_surfacePaintFaces.TryGetValue(map, out var faces)) return false;
            normal.Normalize();
            bool found = false; float nearestPlane = float.PositiveInfinity;
            foreach (int id in faces)
            {
                if (SurfacePaints.TryGetFace(id, out var face) && Vector3.Dot(face.Normal, normal) > .95f &&
                    SurfacePaints.TryProject(id, point, out var key) && SurfacePaints.TryGet(key, out var candidate))
                {
                    // Projection tolerance admits nearby parallel charts. Resolve the actual
                    // closest plane before accepting ownership, independently of catalog order.
                    float distance = Mathf.Abs(Vector3.Dot(point - face.Origin, face.Normal));
                    if (found && (distance > nearestPlane || distance.Equals(nearestPlane) && id >= cell.Key.FaceId)) continue;
                    found = true; nearestPlane = distance; cell = candidate;
                }
            }
            return found;
        }

        public bool TryGetSurfacePaint(Vector3 point, Vector3 normal, out BlockSurfaceCell cell)
        {
            cell = default;
            if (SurfacePaints == null || !Finite(point) || !Finite(normal) || normal.sqrMagnitude < .000001f) return false;
            normal.Normalize();
            int count = Physics.RaycastNonAlloc(point + normal * .04f, -normal, _rayHits, .08f, SurfaceMapMask, QueryTriggerInteraction.Ignore);
            if (count == _rayHits.Length) return false;
            int nearest = -1; float distance = float.PositiveInfinity;
            for (int i = 0; i < count; i++) if (_rayHits[i].distance < distance)
            { nearest = i; distance = _rayHits[i].distance; }
            return nearest >= 0 && TryGetSurfacePaint(_rayHits[nearest].collider, _rayHits[nearest].point, normal, out cell);
        }

        public bool TrySurfacePaintContact(Collider map, Vector3 point, Vector3 normal, out BlockContact contact)
        {
            contact = default;
            if (!TryGetSurfacePaint(map, point, normal, out var paint) || paint.Owner == 0 ||
                !SurfacePaints.TryGetFace(paint.Key.FaceId, out var face)) return false;
            contact = new BlockContact { Kind = BlockContactKind.SurfacePaint, SurfaceKey = paint.Key, Owner = paint.Owner,
                Point = point, Normal = face.Normal, OrientationNormal = face.Normal, Stable = true,
                Climbable = Mathf.Abs(face.Normal.y) < .7f, Revision = paint.Revision };
            return true;
        }

        public Vector3 SurfacePaintCenter(BlockSurfaceKey key)
            => SurfacePaints != null && SurfacePaints.TryGetCenter(key, out var center) ? center : throw new ArgumentException("Unknown surface paint cell.");

        // Callers batch externally observable events until all representations have committed.
        public bool PaintSurfaceCell(BlockSurfaceKey key, byte owner)
        {
            if (FixedGridMode)
            {
                var tx = FixedBlocks.BeginMutation(); tx.Paint(new[] { key }, owner, 1);
                if (tx.TryCommit(out var result, out string error)) return result.Added > 0;
                RecordFixedFailure(error); return false;
            }
            if (!Initialized || !_accepting || SurfacePaints == null || !SurfacePaints.TryGet(key, out var old) || old.Owner == owner ||
                !SurfacePaints.TryGetCenter(key, out var center) || !SurfacePaintFitsWorld(key, center) || IsDynamicColumnProtected(center)) return false;
            if (!SurfacePaints.TrySet(key, owner, out _)) return false;
            DirtySurfacePaint(key); _surfacePaintChangedKeys.Add(key); return true;
        }

        bool SurfacePaintFitsWorld(BlockSurfaceKey key, Vector3 center)
        {
            if (!SurfacePaints.TryGetFace(key.FaceId, out var face)) return false;
            var extent = (Abs(face.U) + Abs(face.V)) * (BlockSurfaceWorld.CellSize * .5f);
            const float epsilon = .00001f;
            return Mathf.Abs(center.x) + extent.x <= World.Dimensions.x * .5f + epsilon &&
                Mathf.Abs(center.z) + extent.z <= World.Dimensions.y * .5f + epsilon &&
                center.y - extent.y >= World.Bottom - epsilon && center.y + extent.y <= BlockWorld.Top + epsilon;
        }

        public void NotifySurfacePaintChanged()
        {
            if (_surfacePaintChangedKeys.Count != 0)
            {
                Dynamic?.WakeSurfacePaintContacts(_surfacePaintChangedKeys);
                _surfacePaintChangedKeys.Clear();
            }
            SurfacePaintChanged?.Invoke();
        }

        public bool PrepareSurfacePaintImport(byte[] bytes, bool full, out BlockSurfaceWorld next)
        {
            next = null;
            if (SurfacePaints == null) return false;
            var candidate = SurfacePaints.Clone();
            if (!(full ? candidate.TryImportFull(bytes) : candidate.TryImportDelta(bytes))) return false;
            next = candidate; return true;
        }

        // Only prepared, topology-checked data is installed by the reliable lifecycle commit.
        public void InstallSurfacePaints(BlockSurfaceWorld next)
        {
            if (FixedGridMode) throw new InvalidOperationException("Fixed occupancy and surface paint must be installed by one paired commit.");
            if (next == null || SurfacePaints == null || next.Round != Round || next.TopologyHash != SurfacePaints.TopologyHash)
                throw new InvalidOperationException("Surface paint install does not match this map and round.");
            SurfacePaints = next; RefreshSurfacePaintRendering();
        }

        public BlockMutationResult PaintSurfaceDisk(Vector3 point, Vector3 normal, float radius, byte team, int count)
        {
            if (FixedGridMode) return BuildFixedImpact(point, normal, radius, team, count, true);
            var result = new BlockMutationResult();
            if (!_accepting || team is not (1 or 2) || count <= 0 || !ValidRadius(radius) ||
                !TryGetSurfacePaint(point, normal, out var origin)) return result;
            foreach (var key in SurfacePaints.EnumerateDisk(origin.Key.FaceId, origin.Key, radius))
            {
                if (result.Added >= count) break;
                if (!SurfacePaints.TryGet(key, out var previous) || previous.Owner == team || !PaintSurfaceCell(key, team)) continue;
                result.Added++;
                if (previous.Owner != 0) result.RemovedEnemy++;
            }
            result.Revision = SurfacePaints.Revision;
            if (result.Added > 0) NotifySurfacePaintChanged();
            return result;
        }

        /// <summary>All ordinary map impacts use the same ownership classification before spending their reservation.</summary>
        public int BuildOrPaintMapSurface(Vector3 point, Vector3 normal, float radius, byte team, int count, out bool freeBodies)
        {
            freeBodies = false;
            if (FixedGridMode) return BuildFixedImpact(point, normal, radius, team, count, true).Added;
            if (team is not (1 or 2) || count <= 0 || !_accepting || !ValidRadius(radius) ||
                !TryGetSurfacePaint(point, normal, out var surface)) return 0;
            freeBodies = surface.Owner == team;
            return freeBodies ? Dynamic.Build(point, normal, radius, team, count) : PaintSurfaceDisk(point, normal, radius, team, count).Added;
        }

        public BlockMutationResult DestroySurfacePaint(Vector3 point, float enemyRadius, float friendlyRadius,
            byte team, bool enemyOnly, int maxEnemy = int.MaxValue)
        {
            if (FixedGridMode) return DestroyFixedSurfacePaint(point, enemyRadius, friendlyRadius, team, enemyOnly, maxEnemy);
            var result = new BlockMutationResult();
            if (!_accepting || SurfacePaints == null || team is not (1 or 2) || !Finite(point) ||
                !ValidRadius(enemyRadius) || !ValidRadius(friendlyRadius)) return result;
            var selected = new List<(float distance, BlockSurfaceKey key, byte owner)>();
            foreach (var cell in SurfacePaints.EnumeratePainted())
            {
                bool friendly = cell.Owner == team;
                if (friendly && enemyOnly) continue;
                float radius = friendly ? friendlyRadius : enemyRadius;
                var center = SurfacePaintCenter(cell.Key);
                float squared = (center - point).sqrMagnitude;
                if (radius > 0 && squared <= (radius + BlockRadiusPadding) * (radius + BlockRadiusPadding) &&
                    SurfacePaintVisible(point, center, cell.Key)) selected.Add((squared, cell.Key, cell.Owner));
            }
            selected.Sort((a, b) => { int c = a.distance.CompareTo(b.distance); if (c != 0) return c;
                c = a.key.FaceId.CompareTo(b.key.FaceId); if (c != 0) return c;
                c = a.key.U.CompareTo(b.key.U); return c != 0 ? c : a.key.V.CompareTo(b.key.V); });
            foreach (var cell in selected)
            {
                if (cell.owner != team && result.RemovedEnemy >= maxEnemy) continue;
                // Destruction is not construction; protected zones never prevent removal.
                if (!SurfacePaints.TrySet(cell.key, 0, out _)) continue;
                DirtySurfacePaint(cell.key);
                _surfacePaintChangedKeys.Add(cell.key);
                if (cell.owner == team) result.RemovedFriendly++; else result.RemovedEnemy++;
            }
            result.Revision = SurfacePaints.Revision;
            if (result.Changed > 0) NotifySurfacePaintChanged();
            return result;
        }

        bool SurfacePaintVisible(Vector3 from, Vector3 to, BlockSurfaceKey key)
        {
            if (!SurfacePaints.TryGetFace(key.FaceId, out var face)) return false;
            // Use a point just outside the target plane. The opposite side of a thin
            // wall remains occluded by the real map; paint itself has no thickness.
            return FixedGridMode ? Visible(from, to + face.Normal * .001f) : Dynamic == null || Dynamic.Visible(from, to + face.Normal * .001f);
        }

        void DirtySurfacePaint(BlockSurfaceKey key)
            => _surfacePaintDirty.Add((key.FaceId, Mathf.FloorToInt(key.U / 16f), Mathf.FloorToInt(key.V / 16f)));

        public void RefreshSurfacePaintRendering()
        {
            foreach (var key in _surfacePaintMeshes.Keys) _surfacePaintDirty.Add(key);
            if (SurfacePaints != null) foreach (var cell in SurfacePaints.EnumeratePainted()) DirtySurfacePaint(cell.Key);
        }

        void ClearSurfacePaintRendering()
        {
            foreach (var mesh in _surfacePaintMeshes.Values) if (mesh != null) DisposeObject(mesh);
            _surfacePaintMeshes.Clear(); _surfacePaintDirty.Clear();
            _surfacePaintChangedKeys.Clear();
        }

        void DrawSurfacePaint()
        {
            if (SurfacePaints == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            if (_surfacePaintMaterial == null)
            {
                var shader = Resources.Load<Shader>("BlockSurfacePaint");
                if (shader == null) throw new InvalidOperationException("Missing BlockSurfacePaint shader.");
                _surfacePaintMaterial = new Material(shader) { name = "Block surface paint" };
            }
            foreach (var page in _surfacePaintDirty) RebuildSurfacePaintPage(page);
            _surfacePaintDirty.Clear();
            foreach (var mesh in _surfacePaintMeshes.Values)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, _surfacePaintMaterial, 0, null, 0, null, ShadowCastingMode.Off, true);
        }

        void RebuildSurfacePaintPage((int face, int u, int v) page)
        {
            if (!SurfacePaints.TryGetFace(page.face, out var face)) return;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>();
            var colors = new List<Color32>(); var uv = new List<Vector2>(); var indices = new List<int>();
            float size = BlockWorld.CellSize;
            for (int v = page.v * 16; v < (page.v + 1) * 16; v++) for (int u = page.u * 16; u < (page.u + 1) * 16; u++)
            {
                if (!SurfacePaints.TryGet(new BlockSurfaceKey(page.face, u, v), out var cell) || cell.Owner == 0) continue;
                var p = face.Origin + face.U * (u * size) + face.V * (v * size) + face.Normal * .0005f;
                int start = vertices.Count; Color32 color = cell.Owner == 1 ? Prototype.PrototypeArena.Pink : Prototype.PrototypeArena.Blue;
                vertices.Add(p); vertices.Add(p + face.U * size); vertices.Add(p + (face.U + face.V) * size); vertices.Add(p + face.V * size);
                for (int i = 0; i < 4; i++) { normals.Add(face.Normal); colors.Add(color); }
                uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.one); uv.Add(Vector2.up);
                indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
                indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
            }
            if (_surfacePaintMeshes.Remove(page, out var old) && old != null) DisposeObject(old);
            if (vertices.Count == 0) return;
            var mesh = new Mesh { name = $"Surface paint {page.face}/{page.u}/{page.v}" };
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetColors(colors); mesh.SetUVs(0, uv); mesh.SetTriangles(indices, 0); mesh.RecalculateBounds();
            _surfacePaintMeshes.Add(page, mesh);
        }

        static void DisposeObject(UnityEngine.Object value)
        { if (value == null) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
