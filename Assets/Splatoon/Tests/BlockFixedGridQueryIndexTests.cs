#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockFixedGridQueryIndexTests
    {
        sealed class Environment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard, IBlockFixedGridRecoveryRollback
        {
            internal bool ThrowRecovery;
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            {
                bool bridge = cell.y >= 6;
                support = new BlockFixedGridMapSupport(bridge ? 12 : 11, bridge ? 1.88f : .08f,
                    new BlockSurfaceKey(bridge ? 12 : 11, cell.x, cell.z));
                return true;
            }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embedded, bool relocating, out string reason)
            { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason) { reason = null; return true; }
            public void CommitPreparedRecovery() { if (ThrowRecovery) throw new InvalidOperationException("query rollback fixture"); }
            public void RollbackPreparedRecovery() { }
        }
        sealed class Fixture
        {
            internal readonly Environment Env = new();
            internal readonly BlockFixedGridWorld World;
            internal Fixture()
            {
                var surfaces = new BlockSurfaceWorld(7); var keys = new List<Vector2Int>();
                for (int z = -4; z <= 4; z++) for (int x = -4; x <= 4; x++) keys.Add(new Vector2Int(x, z));
                surfaces.RegisterFace(11, Vector3.up * .08f, Vector3.right, Vector3.forward, keys);
                surfaces.RegisterFace(12, Vector3.up * 1.88f, Vector3.right, Vector3.forward, keys);
                foreach (int face in new[] { 11, 12 }) foreach (var key in keys)
                    surfaces.TrySet(new BlockSurfaceKey(face, key.x, key.y), 1, out _);
                World = new BlockFixedGridWorld(new BlockWorld(new Vector2(16, 16), 7, -2), surfaces, Env);
            }
            internal BlockFixedGridState Add(Vector3Int candidate)
            {
                var before = World.NextId;
                Assert.That(World.Build(new[] { candidate }, 1, 1).Added, Is.EqualTo(1), World.LastFailure);
                Assert.That(World.TryGet(before, out var state), Is.True); return state;
            }
            internal void Populate()
            {
                var random = new System.Random(63018);
                for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++)
                {
                    int ground = random.Next(0, 5), bridge = random.Next(0, 5);
                    for (int y = 0; y < ground; y++) Add(new Vector3Int(x, 3, z));
                    for (int y = 0; y < bridge; y++) Add(new Vector3Int(x, 9, z));
                }
            }
        }

        [Test] public void SparseCapsuleSweepsPreserveLegacyDdaAndExhaustiveContactMembership()
        {
            var f = new Fixture(); f.Populate(); var random = new System.Random(631063);
            for (int sample = 0; sample < 1200; sample++)
            {
                Vector3 center = Next(random, new Vector3(-3, -.5f, -3), new Vector3(3, 4, 3));
                Vector3 direction = Next(random, -Vector3.one, Vector3.one).normalized;
                float radius = sample % 7 == 0 ? 0 : (float)random.NextDouble() * .45f;
                float segment = sample % 3 == 0 ? .65f : (float)random.NextDouble() * .5f;
                float distance = (float)random.NextDouble() * 9 + .001f;
                AssertCastMatchesLegacyAndEveryCube(f.World, center, segment, radius, direction, distance, "long " + sample);
            }
        }

        [Test] public void ShortCapsuleSweepsPreserveLegacyDdaAcrossGridAndPathThresholds()
        {
            var f = new Fixture(); f.Populate(); var random = new System.Random(65018);
            float[] radii = { 0, .01f, .1f, .14976911f, .2f, .279f, .3f, .349f, .35f, .6f };
            float[] segments = { 0, .001f, .5f, .65f };
            float[] distances = { 0, .0000001f, .001f, .03f, .05f, .1f, .15f, .29999998f, .3f, .30000004f };
            float[] offsets = { -.000008f, -.0000001f, 0, .0000001f, .000008f };
            for (int sample = 0; sample < 3000; sample++)
            {
                Vector3 center = Next(random, new Vector3(-1.6f, -.4f, -1.6f), new Vector3(1.6f, 3.8f, 1.6f));
                // Both signs of grid boundaries, radius multiples and one-ULP
                // changes around the .30m path threshold require real contacts.
                if (sample % 3 == 0) center.x = Mathf.Round(center.x / BlockWorld.CellSize) * BlockWorld.CellSize + offsets[sample / 3 % offsets.Length];
                if (sample % 5 == 0) center.z = Mathf.Round(center.z / BlockWorld.CellSize) * BlockWorld.CellSize + offsets[sample / 5 % offsets.Length];
                Vector3 direction = Next(random, -Vector3.one, Vector3.one);
                if (sample % 4 == 0) direction.y = 0;
                if (sample % 7 == 0) direction = Vector3.down;
                if (sample % 11 == 0) direction = Vector3.right;
                if (sample % 13 != 0) direction.Normalize(); else direction *= .1234567f;
                AssertCastMatchesLegacyAndEveryCube(f.World, center, segments[sample / 11 % segments.Length],
                    radii[sample / 7 % radii.Length], direction, distances[sample % distances.Length], "short " + sample);
            }
        }

        [TestCase(0f)] [TestCase(-.0000001f)] [TestCase(.0000001f)]
        public void ShortSimultaneousContactsKeepLowestStableIdentityAcrossColumns(float offset)
        {
            var f = new Fixture(); var first = f.Add(new Vector3Int(1, 0, 0)); f.Add(new Vector3Int(-1, 0, 0));
            Vector3 center = new(.15f + offset, .15f, -.3f);
            AssertCastMatchesLegacyAndEveryCube(f.World, center, 0, .2f, Vector3.forward, .3f, "short stable tie");
            Assert.That(BlockFixedGridQueries.Cast(f.World, center, Vector3.forward, .3f, .2f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(first.Handle));
        }

        [Test] public void ShortSweepsKeepRoundedCornerMissesAndFullVerticalCapsuleContacts()
        {
            var f = new Fixture(); f.Add(Vector3Int.zero);
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(-.2f, .45f, .45f), Vector3.right, .3f, .2f, out _),
                Is.False, "A broadphase box overlap is not an exact rounded-corner collision.");
            AssertCastMatchesLegacyAndEveryCube(f.World, new Vector3(-.2f, .42f, .44f), 0, .2f, Vector3.right, .3f, "rounded edge");
            Assert.That(BlockFixedGridQueries.CastCapsule(f.World, new Vector3(-.2f, .8f, .15f), .6f, .1f,
                Vector3.right, .3f, out var upper), Is.True, "The upright segment still reaches the bottom cube.");
            Assert.That(upper.Distance, Is.EqualTo(.1f).Within(.000001f));
            // The requested path reaches the thin cube even though its start is
            // legal. Movement's separate cast of actual native map displacement
            // remains mandatory and is not replaced by an endpoint-only check.
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(-.01f, .15f, .15f), Vector3.right, .3f, 0, out var ray), Is.True);
            Assert.That(ray.Distance, Is.EqualTo(.01f).Within(.000001f));
        }

        [Test] public void ShortSweepsAllocateNoManagedMemoryAfterWarmup()
        {
            var f = new Fixture(); f.Populate();
            int Consume()
            {
                int hits = 0;
                for (int i = 0; i < 1000; i++)
                {
                    if (BlockFixedGridQueries.CastCapsule(f.World, new Vector3(-.45f, .8f, .15f), .5f, .349f,
                        Vector3.right, .1f, out _)) hits++;
                    if (BlockFixedGridQueries.CastCapsule(f.World, new Vector3(.31f, 2.6f, -.29f), .65f, .279f,
                        Vector3.down, .3f, out _)) hits++;
                }
                return hits;
            }
            int expected = Consume(); long before = GC.GetAllocatedBytesForCurrentThread();
            int actual = Consume(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(actual, Is.EqualTo(expected)); Assert.That(allocated, Is.Zero);
        }

        static void AssertCastMatchesLegacyAndEveryCube(BlockFixedGridWorld world, Vector3 center, float segment, float radius,
            Vector3 direction, float distance, string context)
        {
            bool expected = LegacyCastCapsuleReference(world, center, segment, radius, direction, distance, out var reference);
            bool actual = BlockFixedGridQueries.CastCapsule(world, center, segment, radius, direction, distance, out var hit);
            string diagnostic = $"{context}: center={center:R}, halfSegment={segment:R}, radius={radius:R}, direction={direction:R}, distance={distance:R}";
            Assert.That(actual, Is.EqualTo(expected), "legacy hit/miss " + diagnostic);
            if (actual)
            {
                Assert.That(hit.State.Handle, Is.EqualTo(reference.State.Handle), "legacy identity " + diagnostic);
                Assert.That(hit.Distance, Is.EqualTo(reference.Distance).Within(.000002f), "legacy TOI " + diagnostic);
                Assert.That(Vector3.Distance(hit.Point, reference.Point), Is.LessThan(.00002f), "legacy point " + diagnostic);
                Assert.That(Vector3.Distance(hit.Normal, reference.Normal), Is.LessThan(.00002f), "legacy normal " + diagnostic);
            }
            // A separate complete-distance scan has no broadphase and never narrows
            // its reach using an earlier candidate. Its contact set has no winner.
            // Preserve original DDA epsilon chaining rather than globally sorting it.
            if (direction.magnitude <= .000001f) return; // unchanged OverlapCapsule fallback
            var contacts = EveryCubeContacts(world, center, segment, radius, direction, distance);
            Assert.That(actual, Is.EqualTo(contacts.Count > 0), "exhaustive hit/miss " + diagnostic);
            if (!actual) return;
            var member = contacts.Single(candidate => candidate.State.Handle == hit.State.Handle);
            float minimumToi = contacts.Min(candidate => candidate.Distance);
            Assert.That(hit.Distance, Is.EqualTo(member.Distance).Within(.000002f), "selected contact TOI " + diagnostic);
            Assert.That(minimumToi, Is.LessThanOrEqualTo(hit.Distance + .000002f), "global minimum lower bound " + diagnostic);
            Assert.That(Vector3.Distance(hit.Normal, member.Normal), Is.LessThan(.00002f), "selected contact normal " + diagnostic);
        }

        [Test] public void ShortNonTransitiveEpsilonChainPreservesOriginalDdaVisitOrder()
        {
            var f = new Fixture();
            var first = f.Add(new Vector3Int(0, 0, 0)); f.Add(new Vector3Int(-1, 0, 0));
            var middle = f.Add(new Vector3Int(-1, 1, 0)); f.Add(new Vector3Int(0, 0, -1));
            var last = f.Add(new Vector3Int(0, 1, -1));
            Assert.That(first.Handle.Id, Is.EqualTo(1)); Assert.That(middle.Handle.Id, Is.EqualTo(3)); Assert.That(last.Handle.Id, Is.EqualTo(5));
            Vector3 center = new(.25f, .55f, .25f), direction = new(-.577350269f, -.577345269f, -.577355269f);
            var contacts = EveryCubeContacts(f.World, center, 0, .2f, direction, .1f);
            float firstTime = contacts.Single(h => h.State.Handle == first.Handle).Distance;
            float middleTime = contacts.Single(h => h.State.Handle == middle.Handle).Distance;
            float lastTime = contacts.Single(h => h.State.Handle == last.Handle).Distance;
            Assert.That(lastTime, Is.LessThan(middleTime)); Assert.That(middleTime, Is.LessThan(firstTime));
            Assert.That(firstTime - middleTime, Is.LessThanOrEqualTo(.000001f));
            Assert.That(middleTime - lastTime, Is.LessThanOrEqualTo(.000001f));
            Assert.That(firstTime - lastTime, Is.GreaterThan(.000001f), "The pairwise epsilon relation is not transitive.");
            Assert.That(LegacyCastCapsuleReference(f.World, center, 0, .2f, direction, .1f, out var legacy), Is.True);
            Assert.That(legacy.State.Handle, Is.EqualTo(first.Handle), "Original z/x/Y visits the 5 -> 3 -> 1 contact chain.");
            Assert.That(StableIdOrderedCastDemonstration(f.World, center, 0, .2f, direction, .1f, out var sorted), Is.True);
            Assert.That(sorted.State.Handle, Is.EqualTo(last.Handle), "A global stable-ID order changes the accepted chain.");
            AssertCastMatchesLegacyAndEveryCube(f.World, center, 0, .2f, direction, .1f, "nontransitive epsilon chain");
        }

        [Test] public void ShortNearCellBoundaryPreservesDdaWhenSweptBoundsCannotFitFirstNeighborhood()
        {
            var f = new Fixture(); var target = f.Add(new Vector3Int(2, 0, 0));
            Vector3 center = new(.29999998f, .15f, .15f);
            // firstCell.x=0 and horizontal=2, but double padded bounds reach x=3.
            // The proposed optimization must fall back to the original DDA.
            AssertCastMatchesLegacyAndEveryCube(f.World, center, 0, .3f, Vector3.right, .3f, "containment fallback");
            Assert.That(BlockFixedGridQueries.Cast(f.World, center, Vector3.right, .3f, .3f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(target.Handle));
        }

        [Test] public void SparseOverlapMatchesDistanceToEveryCommittedCube()
        {
            var f = new Fixture(); f.Populate(); var random = new System.Random(632063);
            for (int sample = 0; sample < 600; sample++)
            {
                Vector3 center = Next(random, new Vector3(-2, -.5f, -2), new Vector3(2, 4, 2));
                float radius = (float)random.NextDouble() * .5f, segment = (float)random.NextDouble() * .75f;
                BlockFixedGridState expected = default; float nearest = float.PositiveInfinity;
                foreach (var state in f.World.Blocks.Values)
                {
                    var bounds = BlockFixedGridWorld.GetBounds(state); bounds.Expand(Vector3.up * (segment * 2));
                    float squared = (center - bounds.ClosestPoint(center)).sqrMagnitude;
                    if (squared > radius * radius || squared > nearest || squared == nearest && state.Handle.Id >= expected.Handle.Id) continue;
                    nearest = squared; expected = state;
                }
                bool actual = BlockFixedGridQueries.OverlapCapsule(f.World, center, segment, radius, Vector3.up, out var hit);
                Assert.That(actual, Is.EqualTo(float.IsFinite(nearest)), $"overlap {sample}");
                if (actual) Assert.That(hit.State.Handle, Is.EqualTo(expected.Handle), $"identity {sample}");
            }
        }

        [TestCase(0f)] [TestCase(-.0000001f)] [TestCase(.0000001f)]
        public void SimultaneousContactsChooseTheLowestStableIdentityAcrossColumns(float offset)
        {
            var f = new Fixture(); var first = f.Add(new Vector3Int(1, 0, 0)); f.Add(new Vector3Int(-1, 0, 0));
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(.15f + offset, .15f, -1), Vector3.forward, 2, .2f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(first.Handle));
        }

        [Test] public void FullAndDeltaInstallationPublishTheSameRelocatedCollisionIdentity()
        {
            var source = new Fixture(); var target = new Fixture();
            var bottom = source.Add(Vector3Int.zero); var upper = source.Add(Vector3Int.up);
            Import(target.World, source.World.Capture(true));
            uint revision = source.World.Revision, surface = source.World.Surfaces.Revision;
            AssertHit(target.World, .45f, upper.Handle);
            source.World.Destroy(new[] { bottom.Handle }, 2, 1, 0);
            Import(target.World, source.World.Capture(false, revision, surface));
            AssertHit(target.World, .15f, upper.Handle);
            Assert.That(BlockFixedGridQueries.Cast(target.World, new Vector3(.15f, .45f, -1), Vector3.forward, 2, 0, out _), Is.False);
            Assert.That(target.World.TryGet(bottom.Handle, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Support(target.World, new Vector3(.15f, .3001f, .15f), .15f, .01f, out var support), Is.True);
            Assert.That(support.State.Handle, Is.EqualTo(upper.Handle));
            Assert.That(BlockFixedGridQueries.OverlapBox(target.World, new Vector3(.15f, .15f, .15f), Vector3.one * .02f, Quaternion.Euler(12, 37, 19)), Is.True);
        }

        [Test] public void RecoveryFailureRollsCollisionColumnsBackWithTheWorld()
        {
            var f = new Fixture(); var bottom = f.Add(Vector3Int.zero); var upper = f.Add(Vector3Int.up);
            uint revision = f.World.Revision; f.Env.ThrowRecovery = true;
            Assert.That(f.World.Destroy(new[] { bottom.Handle }, 2, 1, 0).RemovedEnemy, Is.Zero);
            Assert.That(f.World.Revision, Is.EqualTo(revision)); Assert.That(f.World.Count, Is.EqualTo(2));
            AssertHit(f.World, .15f, bottom.Handle); AssertHit(f.World, .45f, upper.Handle);
            f.Env.ThrowRecovery = false;
            Assert.That(f.World.Destroy(new[] { bottom.Handle }, 2, 1, 0).RemovedEnemy, Is.EqualTo(1));
            AssertHit(f.World, .15f, upper.Handle);
        }

        [Test] public void ClearAndNewEpochCannotRetainOldCollisionColumnsOrAliasReusedIds()
        {
            var f = new Fixture(); var old = f.Add(new Vector3Int(-2, 0, 1));
            f.World.Clear(8);
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(-.45f, .15f, -1), Vector3.forward, 3, .1f, out _), Is.False);
            f.World.Surfaces.TrySet(new BlockSurfaceKey(11, 2, -1), 1, out _);
            var replacement = f.Add(new Vector3Int(2, 0, -1));
            Assert.That(replacement.Handle.Id, Is.EqualTo(old.Handle.Id)); Assert.That(replacement.Handle.Epoch, Is.Not.EqualTo(old.Handle.Epoch));
            Assert.That(f.World.TryGet(old.Handle, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(-.45f, .15f, -1), Vector3.forward, 3, .1f, out _), Is.False);
            Assert.That(BlockFixedGridQueries.Cast(f.World, new Vector3(.75f, .15f, -1), Vector3.forward, 3, .1f, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(replacement.Handle));
        }

        [Test] public void UnchangedWorldQueriesAllocateNoManagedMemoryAfterWarmup()
        {
            var f = new Fixture(); f.Populate();
            int Consume()
            {
                int hits = 0;
                for (int i = 0; i < 1000; i++)
                {
                    if (BlockFixedGridQueries.CastCapsule(f.World, new Vector3(-2, .8f, .15f), .5f, .2f, Vector3.right, 4, out _)) hits++;
                    if (BlockFixedGridQueries.Support(f.World, new Vector3(.15f, .9f, .15f), .2f, .1f, out _)) hits++;
                    if (BlockFixedGridQueries.OverlapBox(f.World, new Vector3(.15f, .15f, .15f), Vector3.one * .05f, Quaternion.identity)) hits++;
                }
                return hits;
            }
            int expected = Consume(); long before = GC.GetAllocatedBytesForCurrentThread();
            int actual = Consume(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(actual, Is.EqualTo(expected)); Assert.That(allocated, Is.Zero);
        }

        static void Import(BlockFixedGridWorld target, BlockFixedGridSnapshot snapshot)
        { Assert.That(target.TryPrepareImport(snapshot, out var prepared, out var error), Is.True, error); Assert.That(target.CommitImport(prepared), Is.True, target.LastFailure); }
        static void AssertHit(BlockFixedGridWorld world, float y, BlockFixedGridHandle expected)
        {
            Assert.That(BlockFixedGridQueries.Cast(world, new Vector3(.15f, y, -1), Vector3.forward, 2, 0, out var hit), Is.True);
            Assert.That(hit.State.Handle, Is.EqualTo(expected));
        }
        static Vector3 Next(System.Random random, Vector3 low, Vector3 high)
            => new(Mathf.Lerp(low.x, high.x, (float)random.NextDouble()), Mathf.Lerp(low.y, high.y, (float)random.NextDouble()),
                Mathf.Lerp(low.z, high.z, (float)random.NextDouble()));

        const float Epsilon = .000001f;
        // Frozen baseline CastCapsule behavior, including normalization, DDA traversal,
        // previous-neighborhood deduplication, best-dependent termination and tie chain.
        // Public committed states rebuild identical Y-sorted columns for this reference;
        // the separate test assembly needs no internal production access or test hook.
        static bool LegacyCastCapsuleReference(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 direction, float distance, out BlockFixedGridHit hit)
        {
            hit = default;
            if (world == null || world.Count == 0 || !LegacyFinite(center) || !LegacyFinite(direction) || !float.IsFinite(distance) || distance < 0 ||
                !float.IsFinite(radius) || radius < 0 || !float.IsFinite(halfSegment) || halfSegment < 0) return false;
            float length = direction.magnitude;
            if (length <= Epsilon) return BlockFixedGridQueries.OverlapCapsule(world, center, halfSegment, radius, -direction, out hit);
            direction /= length;
            var referenceColumns = world.Blocks.Values.GroupBy(state => new Vector2Int(state.Cell.x, state.Cell.z))
                .ToDictionary(group => group.Key, group => group.OrderBy(state => state.Cell.y)
                    .Select(state => new KeyValuePair<int, ulong>(state.Cell.y, state.Handle.Id)).ToArray());
            Vector3Int cell = BlockWorld.CellAt(center);
            Vector3Int step = new(Math.Sign(direction.x), Math.Sign(direction.y), Math.Sign(direction.z));
            Vector3 next = new(LegacyNextBoundary(center.x, direction.x, cell.x), LegacyNextBoundary(center.y, direction.y, cell.y), LegacyNextBoundary(center.z, direction.z, cell.z));
            Vector3 interval = new(LegacyInterval(direction.x), LegacyInterval(direction.y), LegacyInterval(direction.z));
            int horizontal = Mathf.CeilToInt(radius / BlockWorld.CellSize) + 1;
            int vertical = Mathf.CeilToInt((radius + halfSegment) / BlockWorld.CellSize) + 1;
            float best = distance + Epsilon;
            bool found = false;
            bool previous = false; Vector3Int previousLow = default, previousHigh = default;
            while (true)
            {
                var low = cell - new Vector3Int(horizontal, vertical, horizontal);
                var high = cell + new Vector3Int(horizontal, vertical, horizontal);
                for (int z = cell.z - horizontal; z <= cell.z + horizontal; z++)
                for (int x = cell.x - horizontal; x <= cell.x + horizontal; x++)
                foreach (var entry in LegacyColumn(referenceColumns, x, z))
                {
                    int y = entry.Key;
                    if (y < low.y) continue;
                    if (y > high.y) break;
                    // A straight DDA walk is monotone in every axis. Any occupied
                    // cell in the previous neighborhood has already had its full
                    // sweep tested; only newly entered slabs need a narrow phase.
                    if (previous && x >= previousLow.x && x <= previousHigh.x &&
                        z >= previousLow.z && z <= previousHigh.z && y >= previousLow.y && y <= previousHigh.y) continue;
                    if (!world.TryGet(entry.Value, out var state)) continue;
                    Bounds bounds = BlockFixedGridWorld.GetBounds(state);
                    Bounds expanded = bounds; expanded.Expand(Vector3.up * (halfSegment * 2));
                    // Keep the existing stable-ID tie band reachable even when a
                    // slightly earlier high-ID candidate was enumerated first.
                    if (!BlockFixedGridQueries.SweepSphereBox(center, direction, Mathf.Min(distance, best + Epsilon), radius, expanded,
                        out float time, out Vector3 normal)) continue;
                    // Moving tangentially along or away from an existing touching face must
                    // not block sliding; a true initial penetration still rejects the move.
                    if (time <= Epsilon && Vector3.Dot(direction, normal) >= -Epsilon &&
                        (center - expanded.ClosestPoint(center)).sqrMagnitude >= radius * radius - Epsilon) continue;
                    if (found && (time > best + Epsilon || Mathf.Abs(time - best) <= Epsilon && state.Handle.Id >= hit.State.Handle.Id)) continue;
                    best = time; found = true;
                    Vector3 sphereCenter = center + direction * time;
                    Vector3 point = bounds.ClosestPoint(sphereCenter - normal * radius);
                    hit = new BlockFixedGridHit(state, point, normal, time);
                }
                previous = true; previousLow = low; previousHigh = high;
                float advance = Mathf.Min(next.x, Mathf.Min(next.y, next.z));
                if (advance > distance || advance > best + Epsilon || float.IsPositiveInfinity(advance)) break;
                if (next.x <= advance + Epsilon) { cell.x += step.x; next.x += interval.x; }
                if (next.y <= advance + Epsilon) { cell.y += step.y; next.y += interval.y; }
                if (next.z <= advance + Epsilon) { cell.z += step.z; next.z += interval.z; }
            }
            return found;
        }

        static KeyValuePair<int, ulong>[] LegacyColumn(Dictionary<Vector2Int, KeyValuePair<int, ulong>[]> columns, int x, int z)
            => columns.TryGetValue(new Vector2Int(x, z), out var entries) ? entries : Array.Empty<KeyValuePair<int, ulong>>();
        static float LegacyNextBoundary(float origin, float direction, int cell) => Mathf.Abs(direction) < 1e-12f ? float.PositiveInfinity :
            (((direction > 0 ? cell + 1 : cell) * BlockWorld.CellSize) - origin) / direction;
        static float LegacyInterval(float direction) => Mathf.Abs(direction) < 1e-12f ? float.PositiveInfinity : BlockWorld.CellSize / Mathf.Abs(direction);
        static bool LegacyFinite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        static List<BlockFixedGridHit> EveryCubeContacts(BlockFixedGridWorld world, Vector3 center, float halfSegment,
            float radius, Vector3 direction, float distance)
        {
            var contacts = new List<BlockFixedGridHit>(); direction /= direction.magnitude;
            foreach (var state in world.Blocks.Values)
            {
                var bounds = BlockFixedGridWorld.GetBounds(state); var expanded = bounds; expanded.Expand(Vector3.up * (halfSegment * 2));
                if (!BlockFixedGridQueries.SweepSphereBox(center, direction, distance, radius, expanded, out float time, out var normal)) continue;
                if (time <= Epsilon && Vector3.Dot(direction, normal) >= -Epsilon &&
                    (center - expanded.ClosestPoint(center)).sqrMagnitude >= radius * radius - Epsilon) continue;
                contacts.Add(new BlockFixedGridHit(state, bounds.ClosestPoint(center + direction * time - normal * radius), normal, time));
            }
            return contacts;
        }

        // Deliberate demonstration of the superseded oracle's different visit order.
        // Only the explicit nontransitive-chain test uses its final selected identity.
        static bool StableIdOrderedCastDemonstration(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 direction, float distance, out BlockFixedGridHit hit)
        {
            hit = default; bool found = false; float best = distance + .000001f;
            direction /= direction.magnitude;
            foreach (var state in world.Blocks.Values.OrderBy(s => s.Handle.Id))
            {
                var bounds = BlockFixedGridWorld.GetBounds(state); var expanded = bounds; expanded.Expand(Vector3.up * (halfSegment * 2));
                if (!BlockFixedGridQueries.SweepSphereBox(center, direction, Mathf.Min(distance, best + .000001f), radius, expanded, out float time, out var normal)) continue;
                if (time <= .000001f && Vector3.Dot(direction, normal) >= -.000001f &&
                    (center - expanded.ClosestPoint(center)).sqrMagnitude >= radius * radius - .000001f) continue;
                if (found && (time > best + .000001f || Mathf.Abs(time - best) <= .000001f && state.Handle.Id >= hit.State.Handle.Id)) continue;
                found = true; best = time;
                hit = new BlockFixedGridHit(state, bounds.ClosestPoint(center + direction * time - normal * radius), normal, time);
            }
            return found;
        }
    }
}
#endif
