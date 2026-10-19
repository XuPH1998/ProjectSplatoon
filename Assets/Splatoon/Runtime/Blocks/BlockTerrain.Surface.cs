using System;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        const float BlockRadiusPadding = BlockWorld.CellSize * .5f + .001f;
        const int SurfaceMapMask = ~((1 << BlockDynamicWorld.Layer) | (1 << TerrainLayer) | (1 << 8) | (1 << 10) | (1 << SubWeaponTarget.Layer));
        public bool SurfaceOnly { get; private set; }
        public BlockDynamicWorld Dynamic { get; private set; }
        public bool IsAuthoredMapCollider(Collider collider) => collider != null && _staticColliders.Contains(collider);

        /// <summary>Production mode keeps flat map ownership separate from free bodies and legacy solid voxels.</summary>
        public void EnableSurfaceMode()
        {
            if (SurfaceOnly) return;
            if (!Initialized) throw new InvalidOperationException("Initialize block terrain before enabling surface mode.");
            SurfaceOnly = true;
            InitializeSurfacePaints();
            _gravityColumns.Clear(); _gravityCursor.Clear();
            ResetGravityCohorts(); ResetBlockSpread();
            BlockDynamicWorld.ConfigureCollisionLayers();
            Dynamic = GetComponent<BlockDynamicWorld>();
            if (Dynamic == null) Dynamic = gameObject.AddComponent<BlockDynamicWorld>();
            Dynamic.Initialize(this);
            Dynamic.SetAuthority(_authority && _accepting);
        }

        bool SurfaceBuildAllowed(Vector3Int cell, Vector3 normal)
        {
            if (ExcludedConstruction(cell) || !TryOriginalMapSurface(cell, normal, out var support)) return false;
            int count = Physics.OverlapBoxNonAlloc(BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .5f - .0001f),
                _overlaps, Quaternion.identity, SurfaceMapMask, QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) { BuildOverlapBufferFull++; return false; }
            bool supportMayOverlap = SupportingGeometryIsBehindFace(cell, support);
            for (int i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (!_staticColliders.Contains(collider)) continue;
                // A lattice that does not divide an authored plane can put its
                // first cube partly into that supporting wall/floor. Exempt only
                // that exact collider after proving all its geometry is behind
                // the support plane and this cube's centre is in front of it.
                if (collider == support.collider && supportMayOverlap) continue;
                BuildStaticOverlaps++; return false;
            }
            return !SurfaceBuildOverlapsDynamic(cell);
        }

        static bool SupportingGeometryIsBehindFace(Vector3Int cell, RaycastHit support)
        {
            Vector3 n = support.normal.normalized;
            const float epsilon = .0001f;
            var bounds = support.collider.bounds;
            float furthest = Vector3.Dot(bounds.center - support.point, n) + Vector3.Dot(bounds.extents, Abs(n));
            return furthest <= epsilon && Vector3.Dot(BlockWorld.Center(cell) - support.point, n) >= -epsilon;
        }

        bool TryOriginalMapSurface(Vector3Int cell, Vector3 normal, out RaycastHit surface)
        {
            surface = default;
            if (normal.sqrMagnitude < .000001f) return false;
            normal.Normalize();
            var axis = Axis(normal);
            int coordinate = axis.x != 0 ? 0 : axis.y != 0 ? 1 : 2;
            // Start outside the cube: a valid first layer can already overlap
            // its original support when the map plane is off the lattice.
            int count = Physics.BoxCastNonAlloc(BlockWorld.Center(cell) + normal * BlockWorld.CellSize,
                Vector3.one * (BlockWorld.CellSize * .45f),
                -normal, _rayHits, Quaternion.identity, BlockWorld.CellSize * 2,
                SurfaceMapMask, QueryTriggerInteraction.Ignore);
            if (count == _rayHits.Length) return false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = _rayHits[i];
                if (hit.distance >= nearest) continue;
                surface = hit; nearest = hit.distance;
            }
            if (surface.collider == null || surface.distance <= 0 || !_staticColliders.Contains(surface.collider) ||
                surface.collider.GetComponentInParent<PaintSurface>() == null || Vector3.Dot(surface.normal, normal) < .5f) return false;
            // The nearest real map face must produce this same first quantized
            // layer. Never search through an obstruction for a support behind it.
            var first = BlockWorld.CellAt(surface.point + (Vector3)axis * (BlockWorld.CellSize * .51f));
            return first[coordinate] == cell[coordinate];
        }

        bool SurfaceBuildOverlapsDynamic(Vector3Int cell)
        {
            int count = Physics.OverlapBoxNonAlloc(BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .499f),
                _overlaps, Quaternion.identity, 1 << BlockDynamicWorld.Layer, QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) return true;
            for (int i = 0; i < count; i++)
                if (_overlaps[i].attachedRigidbody != null && !_staticColliders.Contains(_overlaps[i])) return true;
            return false;
        }

        /// <summary>Map-space ownership for movement; the motor still follows the original map face.</summary>
        public bool TrySurfaceContactFromMap(RaycastHit hit, out BlockContact contact)
        {
            contact = default;
            if (SurfacePaints != null) return TrySurfacePaintContact(hit.collider, hit.point, hit.normal, out contact);
            if (!SurfaceOnly || !Initialized || hit.collider == null || !_staticColliders.Contains(hit.collider) ||
                hit.collider.GetComponentInParent<PaintSurface>() == null || hit.normal.sqrMagnitude < .000001f) return false;
            var axis = Axis(hit.normal);
            var first = BlockWorld.CellAt(hit.point + (Vector3)axis * (BlockWorld.CellSize * .51f));
            // Small differences at a snapped face may select an adjacent row. Only a
            // coating that still projects onto this exact map collider is eligible.
            for (int index = 0; index < 3; index++)
            {
                var cell = first + axis * (index == 0 ? 0 : index == 1 ? -1 : 1);
                if (!World.TryGet(cell, out var value) || !value.SurfaceAttached ||
                    !TryOriginalMapSurface(cell, hit.normal, out var support) || support.collider != hit.collider) continue;
                var chunk = BlockWorld.ChunkAt(cell);
                contact = new BlockContact { Owner = value.Owner, Point = hit.point, Normal = hit.normal,
                    OrientationNormal = hit.normal, Kind = BlockContactKind.SurfaceAttached, Stable = true, Climbable = Mathf.Abs(hit.normal.y) < .7f,
                    Cell = cell, Chunk = chunk, Revision = World.ChunkRevision(chunk) };
                return true;
            }
            return false;
        }

        /// <summary>Admission uses the entire new cube and the current published collision scene.</summary>
        public bool IsDynamicSpawnAllowed(Vector3 center)
            => IsDynamicSpawnAllowed(center, false);

        // TrySpawn supplies a validated unit quaternion. Production Build keeps
        // using the identity overload below without rotated geometry work.
        internal bool IsDynamicSpawnAllowed(Vector3 center, Quaternion rotation)
        {
            if (rotation.x == 0 && rotation.y == 0 && rotation.z == 0) return IsDynamicSpawnAllowed(center, false);
            if (!Initialized || !_accepting || !SurfaceOnly || !Finite(center)) return false;
            float half = BlockWorld.CellSize * .5f;
            var extent = (Abs(rotation * Vector3.right) + Abs(rotation * Vector3.up) + Abs(rotation * Vector3.forward)) * half;
            if (Mathf.Abs(center.x) + extent.x > World.Dimensions.x * .5f ||
                Mathf.Abs(center.z) + extent.z > World.Dimensions.y * .5f ||
                center.y - extent.y < World.Bottom || center.y + extent.y > BlockWorld.Top) return false;
            var low = BlockWorld.CellAt(center - extent + Vector3.one * .000001f);
            var high = BlockWorld.CellAt(center + extent - Vector3.one * .000001f);
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if ((World.TryGet(cell, out _) || _pending != null && _pending.Next.TryGet(cell, out _)) &&
                    CellIntersectsBox(BlockWorld.Center(cell), half, center, Vector3.one * (half - .00001f), rotation)) return false;
            }
            // Use the whole rotated footprint for height-independent protected
            // columns. Its AABB conservatively excludes corners near a zone.
            var columnCenter = new Vector3(center.x, (World.Bottom + BlockWorld.Top) * .5f, center.z);
            var columnHalf = new Vector3(extent.x, (BlockWorld.Top - World.Bottom) * .5f, extent.z);
            foreach (var zone in _constructionZones)
            {
                if (zone == null || !zone.isActiveAndEnabled) continue;
                var volume = zone.Volume;
                if (volume == null || !volume.enabled || !volume.isTrigger) continue;
                var zoneExtent = Vector3.Scale(volume.size * .5f, Abs(volume.transform.lossyScale));
                if (zoneExtent.x <= 0 || zoneExtent.y <= 0 || zoneExtent.z <= 0) continue;
                if (AabbIntersectsBox(columnCenter, columnHalf, volume.transform.TransformPoint(volume.center), zoneExtent, volume.transform.rotation)) return false;
            }
            // Latest snapshots can lead the live hit proxies. The complete cube
            // AABB is a conservative admission guard for capsules and paper.
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (player == null || !player.IsSpawned || !player.Snapshot.Value.IsAlive) continue;
                var actor = new SafetyActor(player, player.Snapshot.Value);
                var state = actor.State; var shape = actor.Shape;
                float height = state.Swimming || state.CompactBody ? shape.CompactHeight : shape.Height;
                float bottom = state.Position.y + shape.Radius;
                float top = state.Position.y + Mathf.Max(shape.Radius, height - shape.Radius);
                float dx = Mathf.Max(0, Mathf.Abs(center.x - state.Position.x) - extent.x);
                float dz = Mathf.Max(0, Mathf.Abs(center.z - state.Position.z) - extent.z);
                float dy = Mathf.Max(0, Mathf.Max(bottom - center.y - extent.y, center.y - extent.y - top));
                if (dx * dx + dy * dy + dz * dz <= shape.Radius * shape.Radius + .0000001f) return false;
                if (!state.ShowsSwimBody) continue;
                if (actor.PaperRects == null)
                {
                    if (AabbIntersectsBox(center, extent, state.PaperCenter,
                        new Vector3(actor.PaperSize.x * .5f, actor.PaperSize.y * .5f, actor.PaperThickness * .5f), state.PaperRotation)) return false;
                }
                else foreach (var rect in actor.PaperRects)
                    if (AabbIntersectsBox(center, extent, state.PaperCenter + state.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                        new Vector3(rect.width * .5f, rect.height * .5f, actor.PaperThickness * .5f), state.PaperRotation)) return false;
            }
            return Physics.OverlapBoxNonAlloc(center, Vector3.one * (half - .0001f), _overlaps,
                rotation, Physics.AllLayers, QueryTriggerInteraction.Ignore) == 0;
        }

        // Build only sets constructionChecked after proving this exact x/z column
        // against the live zones in the same synchronous call. Occupancy stays live.
        internal bool IsDynamicSpawnAllowed(Vector3 center, bool constructionChecked)
        {
            if (!Initialized || !_accepting || !SurfaceOnly || !Finite(center)) return false;
            float half = BlockWorld.CellSize * .5f;
            if (Mathf.Abs(center.x) + half > World.Dimensions.x * .5f ||
                Mathf.Abs(center.z) + half > World.Dimensions.y * .5f ||
                center.y - half < World.Bottom || center.y + half > BlockWorld.Top) return false;
            var low = BlockWorld.CellAt(center - Vector3.one * (half - .000001f));
            var high = BlockWorld.CellAt(center + Vector3.one * (half - .000001f));
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (World.TryGet(cell, out _) || _pending != null && _pending.Next.TryGet(cell, out _)) return false;
            }
            if (!constructionChecked && IsDynamicColumnProtected(center)) return false;
            // Check the latest snapshots as well as live colliders: the shot may be
            // resolved before the current tick publishes a player's hit proxies.
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (player == null || !player.IsSpawned || !player.Snapshot.Value.IsAlive) continue;
                var actor = new SafetyActor(player, player.Snapshot.Value);
                var state = actor.State; var shape = actor.Shape;
                float height = state.Swimming || state.CompactBody ? shape.CompactHeight : shape.Height;
                if (CellIntersectsCapsule(center, half, state.Position, height, shape.Radius)) return false;
                if (!state.ShowsSwimBody) continue;
                if (actor.PaperRects == null)
                {
                    if (CellIntersectsBox(center, half, state.PaperCenter,
                        new Vector3(actor.PaperSize.x * .5f, actor.PaperSize.y * .5f, actor.PaperThickness * .5f), state.PaperRotation)) return false;
                }
                else foreach (var rect in actor.PaperRects)
                    if (CellIntersectsBox(center, half, state.PaperCenter + state.PaperRotation * new Vector3(rect.center.x, rect.center.y, 0),
                        new Vector3(rect.width * .5f, rect.height * .5f, actor.PaperThickness * .5f), state.PaperRotation)) return false;
            }
            int overlaps = Physics.OverlapBoxNonAlloc(center, Vector3.one * (half - .0001f), _overlaps,
                Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            return overlaps == 0;
        }

        // Protection is deliberately independent of height, exactly as admission.
        internal bool IsDynamicColumnProtected(Vector3 center)
        {
            float half = BlockWorld.CellSize * .5f;
            var columnCenter = new Vector3(center.x, (World.Bottom + BlockWorld.Top) * .5f, center.z);
            var columnHalf = new Vector3(half, (BlockWorld.Top - World.Bottom) * .5f, half);
            foreach (var zone in _constructionZones)
            {
                if (zone == null || !zone.isActiveAndEnabled) continue;
                var volume = zone.Volume;
                if (volume == null || !volume.enabled || !volume.isTrigger) continue;
                var extent = Vector3.Scale(volume.size * .5f, Abs(volume.transform.lossyScale));
                if (extent.x <= 0 || extent.y <= 0 || extent.z <= 0) continue;
                if (AabbIntersectsBox(columnCenter, columnHalf, volume.transform.TransformPoint(volume.center), extent, volume.transform.rotation)) return true;
            }
            return false;
        }
    }
}
