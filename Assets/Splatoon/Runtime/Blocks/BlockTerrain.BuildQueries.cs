using System;
using System.Collections.Generic;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        const int MaxCachedBuildDisks = 64, MaxCachedBuildExtent = 64, MaxCachedStaticBuildCells = 32768;
        readonly Dictionary<(int extent, float radiusSquared), OccupiedBuildDisk> _buildDisks = new();
        readonly Queue<(int extent, float radiusSquared)> _buildDiskOrder = new();
        readonly Dictionary<Vector3Int, StaticBuildOverlap> _staticBuildOverlaps = new();

        // Call on map initialization, round clear and disposal. Player and protection
        // checks remain live, and generic MeshCollider geometry is never cached.
        void ResetBuildQueriesCache()
        {
            _buildDisks.Clear(); _buildDiskOrder.Clear(); _staticBuildOverlaps.Clear(); _stageBuildWorld = null;
        }

        IEnumerable<Vector3Int> OrderedBuildCandidates(Command command)
        {
            var n = Axis(command.Normal);
            var first = BlockWorld.CellAt(command.Point + (Vector3)n * (BlockWorld.CellSize * .51f));
            var u = n.y != 0 ? Vector3Int.right : Vector3Int.up;
            var v = Vector3Int.RoundToInt(Vector3.Cross(n, u));
            int worldSpan = Mathf.CeilToInt(Mathf.Max(Mathf.Max(command.BuildDimensions.x, command.BuildDimensions.y),
                BlockWorld.Top - command.BuildBottom) / BlockWorld.CellSize);
            int extent = Mathf.CeilToInt(Mathf.Min(command.Radius / BlockWorld.CellSize, worldSpan));
            int endLayer = SurfaceOnly ? 1 : Math.Min(worldSpan, Math.Max(1, command.Count)), startLayer = 0;
            // A layer perpendicular to n cannot contain an in-bounds cell if its
            // n coordinate is outside the world. Do not shorten any valid layer.
            while (startLayer < endLayer && !BuildAxisInBounds(first + n * startLayer, n, command.BuildDimensions, command.BuildBottom)) startLayer++;
            while (endLayer > startLayer && !BuildAxisInBounds(first + n * (endLayer - 1), n, command.BuildDimensions, command.BuildBottom)) endLayer--;
            if (startLayer == endLayer) yield break;
            float radiusSquared = command.Radius * command.Radius;
            var disk = extent <= MaxCachedBuildExtent ? BuildDisk(extent, radiusSquared) : null;
            ulong proofGeneration = 0; int proofLayer = -1, proofsThisMoveNext = 0;
            for (int layer = startLayer; layer < endLayer; layer++)
            {
                var origin = first + n * layer;
                if (disk != null)
                {
                    int index = 0, stripIndex = 0;
                    while (index < disk.Offsets.Length)
                    {
                        if (SkipProtectedHorizontalLayer(first, n, v, extent, layer, endLayer,
                            ref proofGeneration, ref proofLayer, ref proofsThisMoveNext, out bool tail))
                        { if (tail) yield break; break; }
                        if (SkipOccupiedBuildPrefix(disk, origin, u, v, ref index, ref stripIndex,
                            ref proofsThisMoveNext, out bool firstFree))
                        {
                            if (index == disk.Offsets.Length) break;
                            if (!firstFree) continue;
                        }
                        if (SkipProtectedVerticalBuildPrefix(disk, command, origin, n, u, v,
                            ref index, ref stripIndex, ref proofsThisMoveNext))
                        {
                            if (index == disk.Offsets.Length) break;
                            continue;
                        }
                        var offset = disk.Offsets[index++];
                        yield return origin + u * offset.x + v * offset.y;
                        proofsThisMoveNext = 0;
                    }
                }
                else
                {
                    // Very large valid radii keep the streaming path, without a
                    // potentially enormous cached array or a reduced footprint.
                    foreach (var offset in EnumerateBuildDisk(extent, radiusSquared))
                    {
                        if (SkipProtectedHorizontalLayer(first, n, v, extent, layer, endLayer,
                            ref proofGeneration, ref proofLayer, ref proofsThisMoveNext, out bool tail))
                        { if (tail) yield break; break; }
                        yield return origin + u * offset.x + v * offset.y;
                        proofsThisMoveNext = 0;
                    }
                }
            }
        }

        // Each descriptor is one original ring edge pair, in the original
        // interleaved -/+ side order. It never crosses a float-membership gap.
        readonly struct OccupiedBuildStrip
        {
            public readonly int Start, End, FirstK, Ring;
            public readonly bool AlongU;
            public OccupiedBuildStrip(int start, int end, int firstK, int ring, bool alongU)
            { Start = start; End = end; FirstK = firstK; Ring = ring; AlongU = alongU; }
        }
        sealed class OccupiedBuildDisk
        {
            public readonly Vector2Int[] Offsets;
            public readonly OccupiedBuildStrip[] Strips;
            public OccupiedBuildDisk(Vector2Int[] offsets)
            {
                Offsets = offsets;
                var strips = new List<OccupiedBuildStrip>();
                for (int index = 1; index < offsets.Length;)
                {
                    var a = offsets[index]; var b = offsets[index + 1];
                    bool alongU = a.x == b.x;
                    int ring = alongU ? -a.y : -a.x;
                    int firstK = alongU ? a.x : a.y;
                    int start = index; index += 2;
                    while (index + 1 < offsets.Length)
                    {
                        int k = firstK + (index - start) / 2;
                        var expectedA = alongU ? new Vector2Int(k, -ring) : new Vector2Int(-ring, k);
                        var expectedB = alongU ? new Vector2Int(k, ring) : new Vector2Int(ring, k);
                        if (offsets[index] != expectedA || offsets[index + 1] != expectedB) break;
                        index += 2;
                    }
                    strips.Add(new OccupiedBuildStrip(start, index, firstK, ring, alongU));
                }
                Strips = strips.ToArray();
            }
        }

        // Only the synchronous StageCommands wrapper installs this world. Do
        // not capture a published world, chunk or row across an iterator yield.
        BlockWorld _stageBuildWorld;

        bool SkipOccupiedBuildPrefix(OccupiedBuildDisk disk, Vector3Int origin, Vector3Int u, Vector3Int v,
            ref int index, ref int stripIndex, ref int proofsThisMoveNext, out bool firstFree)
        {
            firstFree = false;
            if (_stageBuildWorld == null || index == 0 || proofsThisMoveNext >= 2) return false;
            if (stripIndex < disk.Strips.Length && index >= disk.Strips[stripIndex].End) stripIndex++;
            if (stripIndex >= disk.Strips.Length) return false;
            var strip = disk.Strips[stripIndex];
            int relative = index - strip.Start, side = relative & 1;
            int k = strip.FirstK + relative / 2;
            var varying = strip.AlongU ? u : v; var fixedAxis = strip.AlongU ? v : u;
            int axis = varying.x != 0 ? 0 : varying.y != 0 ? 1 : 2;
            int direction = varying[axis];
            var at = origin + varying * k;
            var lane0 = at - fixedAxis * strip.Ring; var lane1 = at + fixedAxis * strip.Ring;
            int local = lane0[axis] & (BlockWorld.ChunkSize - 1);
            int pairs = Math.Min((strip.End - index + side) / 2,
                direction > 0 ? BlockWorld.ChunkSize - local : local + 1);
            proofsThisMoveNext++; BuildOccupiedMaskQueries++;
            // At most two local rows of at most 16 bits; a partially consumed
            // pair excludes its already yielded -side. Missing chunks are empty.
            int first0 = FirstFreeBuildOrdinal(_stageBuildWorld, lane0, axis, direction, local, pairs, side, 0, side);
            int first1 = FirstFreeBuildOrdinal(_stageBuildWorld, lane1, axis, direction, local, pairs, 0, 1, side);
            int length = pairs * 2 - side, free = Math.Min(first0, first1);
            int skipped = Math.Min(length, free);
            if (skipped == 0) return false;
            index += skipped; BuildOccupiedCandidatesSkipped += skipped;
            firstFree = free < length;
            return true;
        }
        // Review candidate only: count separately from horizontal and occupied proofs.
        public long BuildVerticalProtectedPrefixProofs { get; private set; }
        public long BuildVerticalProtectedCandidatesSkipped { get; private set; }

        bool SkipProtectedVerticalBuildPrefix(OccupiedBuildDisk disk, Command command, Vector3Int origin, Vector3Int n,
            Vector3Int u, Vector3Int v, ref int index, ref int stripIndex, ref int proofsThisMoveNext)
        {
            var checkpoint = _stageCommandCheckpoint;
            if (!_stageConstructionZonesActive || _stageConstructionZoneCount == 0 || n.y == 0 ||
                checkpoint == null || !ReferenceEquals(checkpoint.Command, command) ||
                index == 0 || proofsThisMoveNext >= 2) return false;
            if (stripIndex < disk.Strips.Length && index >= disk.Strips[stripIndex].End) stripIndex++;
            if (stripIndex >= disk.Strips.Length) return false;
            var strip = disk.Strips[stripIndex];
            int relative = index - strip.Start, side = relative & 1;
            int k = strip.FirstK + relative / 2;
            var varying = strip.AlongU ? u : v; var fixedAxis = strip.AlongU ? v : u;
            int axis = varying.x != 0 ? 0 : 2, direction = varying[axis];
            long firstX = (long)origin.x + (long)varying.x * k;
            long firstZ = (long)origin.z + (long)varying.z * k;
            int local = (int)((axis == 0 ? firstX : firstZ) & (BlockWorld.ChunkSize - 1));
            int pairs = Math.Min((strip.End - index + side) / 2,
                direction > 0 ? BlockWorld.ChunkSize - local : local + 1);
            if (pairs <= 0) return false;
            long lastK = (long)k + pairs - 1;
            long lastX = (long)origin.x + (long)varying.x * lastK;
            long lastZ = (long)origin.z + (long)varying.z * lastK;
            long xSpread = Math.Abs((long)fixedAxis.x * strip.Ring);
            long zSpread = Math.Abs((long)fixedAxis.z * strip.Ring);
            long lowX = Math.Min(firstX, lastX) - xSpread, highX = Math.Max(firstX, lastX) + xSpread;
            long lowZ = Math.Min(firstZ, lastZ) - zSpread, highZ = Math.Max(firstZ, lastZ) + zSpread;
            if (lowX < int.MinValue || highX > int.MaxValue || lowZ < int.MinValue || highZ > int.MaxValue) return false;
            var low = BlockWorld.Center(new Vector3Int((int)lowX, 0, (int)lowZ));
            var high = BlockWorld.Center(new Vector3Int((int)highX, 0, (int)highZ));
            float middle = (World.Bottom + BlockWorld.Top) * .5f;
            var half = new Vector3(BlockWorld.CellSize * .5f, (BlockWorld.Top - World.Bottom) * .5f, BlockWorld.CellSize * .5f);
            var a = new Vector3(low.x, middle, low.z); var b = new Vector3(low.x, middle, high.z);
            var c = new Vector3(high.x, middle, low.z); var d = new Vector3(high.x, middle, high.z);
            // Share the existing two-proof MoveNext bound. No positive result survives
            // a yield: the exact stage geometry is read again for the next prefix.
            proofsThisMoveNext++; BuildVerticalProtectedPrefixProofs++;
            for (int i = 0; i < _stageConstructionZoneCount; i++)
            {
                var zone = _stageConstructionZones[i];
                // One identical live OBB must cover every corner of this conservative
                // rectangle. Different zones at different corners cannot prove its hole.
                if (!AabbIntersectsBox(a, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) ||
                    !AabbIntersectsBox(b, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) ||
                    !AabbIntersectsBox(c, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) ||
                    !AabbIntersectsBox(d, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z)) continue;
                int skipped = pairs * 2 - side;
                // Retain this rejected original prefix before advancing. A failed
                // transaction can replay it against changed live protection later.
                checkpoint.ProtectedBuildSpans.AddLast(new ProtectedBuildReplaySpan(checkpoint.Visited.Count,
                    origin, varying, fixedAxis, k, strip.Ring, side, pairs));
                index += skipped; BuildVerticalProtectedCandidatesSkipped += skipped; return true;
            }
            return false;
        }

        static int FirstFreeBuildOrdinal(BlockWorld world, Vector3Int lane, int axis, int direction,
            int local, int pairs, int firstStep, int side, int consumedSide)
        {
            int count = pairs - firstStep;
            if (count == 0) return int.MaxValue;
            int low = direction > 0 ? local + firstStep : local - pairs + 1;
            uint mask = ((1u << count) - 1u) << low;
            uint free = ~(uint)world.OccupiedRow(lane, axis) & mask;
            if (free == 0) return int.MaxValue;
            int bit = direction > 0 ? LowestBuildBit(free) : HighestBuildBit(free);
            return (bit - local) * direction * 2 + side - consumedSide;
        }
        // Fixed four-step searches, not per-bit or per-cell proof loops.
        static int LowestBuildBit(uint bits)
        {
            int bit = 0;
            if ((bits & 255u) == 0) { bits >>= 8; bit += 8; }
            if ((bits & 15u) == 0) { bits >>= 4; bit += 4; }
            if ((bits & 3u) == 0) { bits >>= 2; bit += 2; }
            if ((bits & 1u) == 0) bit++;
            return bit;
        }
        static int HighestBuildBit(uint bits)
        {
            int bit = 0;
            if ((bits & 65280u) != 0) { bits >>= 8; bit += 8; }
            if ((bits & 240u) != 0) { bits >>= 4; bit += 4; }
            if ((bits & 12u) != 0) { bits >>= 2; bit += 2; }
            if ((bits & 2u) != 0) bit++;
            return bit;
        }

        bool SkipProtectedHorizontalLayer(Vector3Int first, Vector3Int n, Vector3Int v, int extent, int layer, int endLayer,
            ref ulong proofGeneration, ref int proofLayer, ref int proofsThisMoveNext, out bool tail)
        {
            tail = false;
            if (!_stageConstructionZonesActive || n.y != 0 || _stageConstructionZoneCount == 0 ||
                proofGeneration == _stageConstructionZoneGeneration && proofLayer == layer) return false;
            // A long sequence of separately protected layers must not hide unbounded
            // proof work inside one MoveNext. Fall back to one ordinary candidate.
            if (proofsThisMoveNext == 2) return false;
            proofsThisMoveNext++;
            // A negative proof is reusable only in this synchronous stage and layer.
            // A positive proof is consumed by this MoveNext, never suspended across stages.
            proofGeneration = _stageConstructionZoneGeneration; proofLayer = layer;
            BuildHorizontalProofAttempts++;
            if (ProtectedHorizontalRectangle(first, n, v, extent, layer, endLayer))
            { tail = true; BuildHorizontalProtectedTails++; return true; }
            if (ProtectedHorizontalRectangle(first, n, v, extent, layer, layer + 1))
            { BuildHorizontalProtectedLayers++; return true; }
            return false;
        }
        bool ProtectedHorizontalRectangle(Vector3Int first, Vector3Int n, Vector3Int v, int extent, int startLayer, int endLayer)
        {
            if (!_stageConstructionZonesActive || n.y != 0 || extent < 0 || startLayer < 0 || endLayer <= startLayer) return false;
            long firstX = (long)first.x + (long)n.x * startLayer, lastX = (long)first.x + (long)n.x * (endLayer - 1);
            long firstZ = (long)first.z + (long)n.z * startLayer, lastZ = (long)first.z + (long)n.z * (endLayer - 1);
            long xSpread = Math.Abs((long)v.x * extent), zSpread = Math.Abs((long)v.z * extent);
            long lowX = Math.Min(firstX, lastX) - xSpread, highX = Math.Max(firstX, lastX) + xSpread;
            long lowZ = Math.Min(firstZ, lastZ) - zSpread, highZ = Math.Max(firstZ, lastZ) + zSpread;
            if (lowX < int.MinValue || highX > int.MaxValue || lowZ < int.MinValue || highZ > int.MaxValue) return false;
            var low = BlockWorld.Center(new Vector3Int((int)lowX, 0, (int)lowZ));
            var high = BlockWorld.Center(new Vector3Int((int)highX, 0, (int)highZ));
            float middle = (World.Bottom + BlockWorld.Top) * .5f;
            var half = new Vector3(BlockWorld.CellSize * .5f, (BlockWorld.Top - World.Bottom) * .5f, BlockWorld.CellSize * .5f);
            var a = new Vector3(low.x, middle, low.z); var b = new Vector3(low.x, middle, high.z);
            var c = new Vector3(high.x, middle, low.z); var d = new Vector3(high.x, middle, high.z);
            for (int i = 0; i < _stageConstructionZoneCount; i++)
            {
                var zone = _stageConstructionZones[i];
                // All four corners must intersect one identical live OBB. For fixed
                // column half extents, its intersecting-centre set is convex.
                if (AabbIntersectsBox(a, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) &&
                    AabbIntersectsBox(b, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) &&
                    AabbIntersectsBox(c, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z) &&
                    AabbIntersectsBox(d, half, zone.Center, zone.Extent, zone.X, zone.Y, zone.Z)) return true;
            }
            return false;
        }

        static bool BuildAxisInBounds(Vector3Int cell, Vector3Int n, Vector2 dimensions, float bottom)
        {
            var point = BlockWorld.Center(cell);
            return n.x != 0 ? point.x >= -dimensions.x * .5f && point.x < dimensions.x * .5f
                : n.y != 0 ? point.y >= bottom && point.y < BlockWorld.Top
                : point.z >= -dimensions.y * .5f && point.z < dimensions.y * .5f;
        }

        OccupiedBuildDisk BuildDisk(int extent, float radiusSquared)
        {
            // Keep the exact float input used by the membership expression. An
            // integer radius threshold can alias adjacent float boundary cases.
            var key = (extent, radiusSquared);
            if (_buildDisks.TryGetValue(key, out var disk)) return disk;
            var offsets = new List<Vector2Int>();
            foreach (var offset in EnumerateBuildDisk(extent, radiusSquared)) offsets.Add(offset);
            disk = new OccupiedBuildDisk(offsets.ToArray());
            if (_buildDisks.Count >= MaxCachedBuildDisks) _buildDisks.Remove(_buildDiskOrder.Dequeue());
            _buildDisks.Add(key, disk); _buildDiskOrder.Enqueue(key);
            return disk;
        }

        static IEnumerable<Vector2Int> EnumerateBuildDisk(int extent, float radiusSquared)
        {
            yield return Vector2Int.zero;
            for (int ring = 1; ring <= extent; ring++)
            {
                for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    if ((x * x + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radiusSquared + .00001f)
                        yield return new Vector2Int(x, ring * side);
                for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    if ((y * y + ring * ring) * BlockWorld.CellSize * BlockWorld.CellSize <= radiusSquared + .00001f)
                        yield return new Vector2Int(ring * side, y);
            }
        }

        bool FullyProtectedVerticalBuild(Command command)
        {
            var n = Axis(command.Normal);
            if (n.y == 0 || _constructionZones.Length == 0) return false;
            // The untrimmed square is a conservative superset of the exact disk.
            // Huge radii or lattice integer overflow keep the ordinary path.
            float cellRadius = command.Radius / BlockWorld.CellSize;
            if (!(cellRadius >= 0) || cellRadius >= int.MaxValue) return false;
            int extent = Mathf.CeilToInt(cellRadius);
            var first = BlockWorld.CellAt(command.Point + (Vector3)n * (BlockWorld.CellSize * .51f));
            if (first.x < int.MinValue + extent || first.x > int.MaxValue - extent ||
                first.z < int.MinValue + extent || first.z > int.MaxValue - extent) return false;
            var low = BlockWorld.Center(first - new Vector3Int(extent, 0, extent));
            var high = BlockWorld.Center(first + new Vector3Int(extent, 0, extent));
            float middle = (World.Bottom + BlockWorld.Top) * .5f;
            var half = new Vector3(BlockWorld.CellSize * .5f, (BlockWorld.Top - World.Bottom) * .5f, BlockWorld.CellSize * .5f);
            var a = new Vector3(low.x, middle, low.z); var b = new Vector3(low.x, middle, high.z);
            var c = new Vector3(high.x, middle, low.z); var d = new Vector3(high.x, middle, high.z);
            foreach (var zone in _constructionZones)
            {
                if (zone == null || !zone.isActiveAndEnabled) continue;
                var volume = zone.Volume;
                if (volume == null || !volume.enabled || !volume.isTrigger) continue;
                Vector3 scale = volume.transform.lossyScale;
                Vector3 size = Vector3.Scale(volume.size * .5f, Abs(scale));
                if (size.x <= 0 || size.y <= 0 || size.z <= 0) continue;
                var center = volume.transform.TransformPoint(volume.center); var rotation = volume.transform.rotation;
                // For this same live OBB and fixed column half-extents, intersecting
                // centres form a convex set. Four corners prove the whole square.
                if (AabbIntersectsBox(a, half, center, size, rotation) && AabbIntersectsBox(b, half, center, size, rotation) &&
                    AabbIntersectsBox(c, half, center, size, rotation) && AabbIntersectsBox(d, half, center, size, rotation)) return true;
            }
            return false;
        }

        bool BuildMapOverlapCached(Vector3Int cell)
        {
            if (ExcludedConstruction(cell)) return true;
            int mask = SurfaceMapMask;
            if (_staticBuildOverlaps.TryGetValue(cell, out var cached))
            {
                if (_staticColliders.Contains(cached.Collider) && cached.Matches(mask)) { BuildStaticCacheHits++; return true; }
                _staticBuildOverlaps.Remove(cell);
            }
            int count = Physics.OverlapBoxNonAlloc(BlockWorld.Center(cell), Vector3.one * (BlockWorld.CellSize * .45f),
                _overlaps, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
            // Preserve the existing overflow behavior without caching a result
            // that could depend on unrelated, moving colliders in this buffer.
            if (count == _overlaps.Length) { BuildOverlapBufferFull++; return true; }
            for (int i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (!_staticColliders.Contains(collider)) continue;
                BuildStaticOverlaps++;
                if (StaticBuildOverlap.TryCapture(collider, mask, out var overlap))
                {
                    if (_staticBuildOverlaps.Count >= MaxCachedStaticBuildCells) _staticBuildOverlaps.Clear();
                    _staticBuildOverlaps[cell] = overlap;
                }
                return true;
            }
            return false;
        }

        readonly struct StaticBuildOverlap
        {
            public readonly Collider Collider;
            readonly Matrix4x4 _transform;
            readonly Vector3 _center, _size;
            readonly float _radius, _height;
            readonly int _mask, _layer, _direction;
            readonly byte _kind;

            StaticBuildOverlap(Collider collider, int mask, byte kind, Vector3 center, Vector3 size,
                float radius = 0, float height = 0, int direction = 0)
            {
                Collider = collider; _mask = mask; _layer = collider.gameObject.layer; _kind = kind;
                _transform = collider.transform.localToWorldMatrix; _center = center; _size = size;
                _radius = radius; _height = height; _direction = direction;
            }
            public static bool TryCapture(Collider collider, int mask, out StaticBuildOverlap state)
            {
                state = default;
                if (collider == null || collider.attachedRigidbody != null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                    (mask & (1 << collider.gameObject.layer)) == 0) return false;
                if (collider is BoxCollider box) state = new StaticBuildOverlap(box, mask, 1, box.center, box.size);
                else if (collider is SphereCollider sphere) state = new StaticBuildOverlap(sphere, mask, 2, sphere.center, default, sphere.radius);
                else if (collider is CapsuleCollider capsule)
                    state = new StaticBuildOverlap(capsule, mask, 3, capsule.center, default, capsule.radius, capsule.height, capsule.direction);
                else return false;
                return true;
            }
            public bool Matches(int mask)
            {
                var collider = Collider;
                if (collider == null || collider.attachedRigidbody != null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                    _mask != mask || _layer != collider.gameObject.layer || (mask & (1 << _layer)) == 0 ||
                    !_transform.Equals(collider.transform.localToWorldMatrix)) return false;
                return _kind == 1 && collider is BoxCollider box && _center.Equals(box.center) && _size.Equals(box.size)
                    || _kind == 2 && collider is SphereCollider sphere && _center.Equals(sphere.center) && _radius == sphere.radius
                    || _kind == 3 && collider is CapsuleCollider capsule && _center.Equals(capsule.center) && _radius == capsule.radius
                        && _height == capsule.height && _direction == capsule.direction;
            }
        }
    }
}
