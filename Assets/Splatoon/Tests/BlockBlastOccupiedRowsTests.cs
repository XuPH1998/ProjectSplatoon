#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Reports-only fixture candidate. Run against the guarded production iterator,
    // not a copied optimized generator; no new runtime seam is required.
    public sealed class BlockBlastOccupiedRowsTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<GameObject> _objects = new();
        BlockTerrain _terrain;
        HashSet<Collider> _map;
        readonly struct Target
        {
            public readonly Vector3Int Cell;
            public readonly byte Owner;
            public readonly ulong Run;
            public readonly int Index;
            public readonly float Distance;
            public Target(Vector3Int cell, byte owner, float distance, ulong run = 0, int index = 0)
            { Cell = cell; Owner = owner; Distance = distance; Run = run; Index = index; }
            public (Vector3Int, byte, ulong, int, int) Exact =>
                (Cell, Owner, Run, Index, BitConverter.SingleToInt32Bits(Distance));
        }
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var obj = new GameObject("Actual blast occupied-row iterator fixture"); _objects.Add(obj);
            _terrain = obj.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            _map = (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", Private).GetValue(_terrain);
        }
        [TearDown] public void Cleanup()
        {
            foreach (var obj in _objects) if (obj != null) Object.DestroyImmediate(obj);
            _objects.Clear(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        object Queue(BlockWorld source, Vector3 point, float enemy, float friendly, bool enemyOnly = false,
            int enemyQuota = int.MaxValue, int friendlyQuota = int.MaxValue, double time = 20)
        {
            _terrain.FinishRound(); _terrain.Clear(source.Round);
            typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, source);
            _terrain.Tick(false, time, .016f);
            _terrain.QueueDestroy(point, enemy, friendly, 1, enemyOnly, maxEnemyBlocks: enemyQuota, maxFriendlyBlocks: friendlyQuota);
            var command = ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private).GetValue(_terrain)).Cast<object>().Single();
            Assert.That(Field(command, "Source"), Is.SameAs(source));
            Assert.That((double)Field(command, "SourceTime"), Is.EqualTo(time));
            return command;
        }
        static Target Read(object item) => new((Vector3Int)Field(item, "Cell"), (byte)Field(item, "Owner"),
            (float)Field(item, "Distance"), (ulong)Field(item, "Run"), (int)Field(item, "Index"));
        IEnumerator ActualIterator(object command) => ((IEnumerable)typeof(BlockTerrain)
            .GetMethod("DestroyCandidates", Private).Invoke(_terrain, new[] { command })).GetEnumerator();
        Target[] ActualStream(object command, out int steps)
        {
            var iterator = ActualIterator(command); var targets = new List<Target>(); steps = 0;
            try
            {
                while (iterator.MoveNext())
                {
                    Assert.That(++steps, Is.LessThan(1000000), "Finite fixture must terminate without truncation");
                    var target = Read(iterator.Current); if (target.Owner != 0) targets.Add(target);
                }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
            return targets.ToArray();
        }
        // Independent frozen visibility implementation, not the production helper.
        bool OracleVisible(Vector3 from, Vector3 to)
        {
            var delta = to - from; float distance = delta.magnitude;
            if (distance < .03f) return true;
            var hits = new RaycastHit[64];
            int count = Physics.RaycastNonAlloc(from + delta / distance * .015f, delta / distance,
                hits, Mathf.Max(0, distance - .03f), PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore);
            return count != hits.Length && !hits.Take(count).Any(hit => _map.Contains(hit.collider));
        }
        bool Include(object command, Vector3 point, BlockCell own, out float distance)
        {
            distance = 0; if (!own.Occupied) return false;
            distance = (point - (Vector3)Field(command, "Point")).sqrMagnitude;
            byte team = (byte)Field(command, "Team");
            if ((bool)Field(command, "EnemyOnly") && own.Owner == team) return false;
            float radius = own.Owner == team ? (float)Field(command, "FriendlyRadius") : (float)Field(command, "Radius");
            return distance <= (radius + .051f) * (radius + .051f) && OracleVisible((Vector3)Field(command, "Point"), point);
        }
        IEnumerable<Target> FallingOracle(object command)
        {
            var source = (BlockWorld)Field(command, "Source"); double time = (double)Field(command, "SourceTime");
            foreach (var run in source.Falling.Values) for (int i = 0; i < run.Cells.Length; i++)
            {
                var cell = run.Bottom + Vector3Int.up * i;
                if (Include(command, BlockWorld.Center(cell) - Vector3.up * run.Offset(time), run.Cells[i], out float distance))
                    yield return new Target(cell, run.Cells[i].Owner, distance, run.Id, i);
            }
        }
        // Former exact x/y/z scan establishes occupied-stream order independently
        // of OccupiedRow, LowestBuildBit, optimized row sentinels, and new masks.
        Target[] LegacyClippedOracle(object command)
        {
            var source = (BlockWorld)Field(command, "Source"); var point = (Vector3)Field(command, "Point");
            float radius = Mathf.Max((float)Field(command, "Radius"), (bool)Field(command, "EnemyOnly") ? 0 : (float)Field(command, "FriendlyRadius")) + BlockWorld.CellSize;
            var low = Vector3.Max(point - Vector3.one * radius, new Vector3(-source.Dimensions.x * .5f, source.Bottom, -source.Dimensions.y * .5f));
            var high = Vector3.Min(point + Vector3.one * radius, new Vector3(source.Dimensions.x * .5f, BlockWorld.Top, source.Dimensions.y * .5f));
            var minCell = BlockWorld.CellAt(low); var maxCell = BlockWorld.CellAt(high);
            var min = BlockWorld.ChunkAt(minCell); var max = BlockWorld.ChunkAt(maxCell); var result = new List<Target>();
            foreach (var pair in source.Chunks)
            {
                var key = pair.Key;
                if (key.x < min.x || key.x > max.x || key.y < min.y || key.y > max.y || key.z < min.z || key.z > max.z) continue;
                var origin = BlockWorld.ChunkOrigin(key);
                var first = Vector3Int.Max(origin, minCell); var last = Vector3Int.Min(origin + Vector3Int.one * 15, maxCell);
                for (int z = first.z; z <= last.z; z++) for (int y = first.y; y <= last.y; y++) for (int x = first.x; x <= last.x; x++)
                {
                    var cell = new Vector3Int(x, y, z); var own = pair.Value.Cells[BlockWorld.Index(cell)];
                    if (Include(command, BlockWorld.Center(cell), own, out float distance)) result.Add(new Target(cell, own.Owner, distance));
                }
            }
            result.AddRange(FallingOracle(command)); return result.ToArray();
        }
        // This second oracle has no clipping: every original Cells slot is read.
        Target[] WholeOccupiedOracle(object command)
        {
            var source = (BlockWorld)Field(command, "Source"); var result = new List<Target>();
            foreach (var pair in source.Chunks) for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
            {
                var cell = BlockWorld.CellFromIndex(pair.Key, index); var own = pair.Value.Cells[index];
                if (Include(command, BlockWorld.Center(cell), own, out float distance)) result.Add(new Target(cell, own.Owner, distance));
            }
            result.AddRange(FallingOracle(command)); return result.ToArray();
        }
        void AssertExact(object command)
        {
            var legacy = LegacyClippedOracle(command); var complete = WholeOccupiedOracle(command);
            var actual = ActualStream(command, out _);
            CollectionAssert.AreEqual(legacy.Select(item => item.Exact), actual.Select(item => item.Exact), "Occupied sequence and float bit patterns remain exact");
            CollectionAssert.AreEquivalent(complete.Select(item => item.Exact), actual.Select(item => item.Exact), "The full occupied source has no clipping omissions or extras");
        }
        static BlockWorld SparseSeed()
        {
            var source = new BlockWorld(new Vector2(8, 8));
            for (int z = -37; z <= 37; z += 4) for (int y = -5; y <= 35; y += 5) for (int x = -39; x <= 39; x += 3)
                source.Set(new Vector3Int(x, y, z), new BlockCell { Owner = (byte)((x + z & 1) == 0 ? 1 : 2), Flags = 1, Durability = 7 });
            for (int x = -32; x < -16; x++) source.Set(new Vector3Int(x, 17, 16), BlockCell.Create((byte)((x & 1) == 0 ? 1 : 2), true));
            return source;
        }
        [TestCase(-1.65f, 1.75f, 1.65f, .24f, .12f, false)]
        [TestCase(-3.95f, -.25f, -3.95f, .23f, .31f, false)]
        [TestCase(3.95f, 1.55f, 3.95f, .21f, .13f, true)]
        [TestCase(-1.55f, 1.75f, 1.65f, 4f, 2f, false)]
        public void NegativePartialAndFullRowsMatchBothOriginalOracles(float x, float y, float z, float enemy, float friendly, bool enemyOnly)
            => AssertExact(Queue(SparseSeed(), new Vector3(x, y, z), enemy, friendly, enemyOnly));

        [Test] public void SnapshotDeltaTombstoneRebuildAndSettleFeedTheExactProductionScan()
        {
            var source = SparseSeed(); source.Seal(11, source.RevisionKeys.ToArray());
            var peer = BlockWorld.DecodeSnapshot(source.EncodeSnapshot());
            var point = new Vector3(-1.65f, 1.75f, -1.65f); AssertExact(Queue(peer, point, 4, 2));
            var next = source.Clone(); var removedKey = source.Chunks.Keys.First();
            var removedCells = Enumerable.Range(0, 4096).Where(i => source.Chunks[removedKey].Cells[i].Occupied)
                .Select(i => BlockWorld.CellFromIndex(removedKey, i)).ToArray();
            Assert.That(removedCells, Is.Not.Empty);
            foreach (var cell in removedCells) next.Set(cell, default);
            next.Seal(12, next.RevisionKeys.ToArray());
            var installed = peer.WithDelta(next.EncodeDelta(11));
            CollectionAssert.AreEqual(next.EncodeSnapshot(), installed.EncodeSnapshot());
            Assert.That(installed.Chunks.ContainsKey(removedKey), Is.False); AssertExact(Queue(installed, point, 4, 2));
            var rebuilt = next.Clone(); rebuilt.Set(removedCells[0], new BlockCell { Owner = 2, Flags = 1, Durability = 11 });
            var bottom = new Vector3Int(-9, 27, -9);
            for (int i = 0; i < 4; i++) rebuilt.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)(i % 2 + 1)));
            var run = rebuilt.Detach(bottom, 4, 20); rebuilt.RemoveFallingCell(run.Id, 1, out _);
            var changed = new HashSet<Vector3Int>(); Assert.That(rebuilt.Settle(run.Id, new Vector3Int(-9, 37, -9), changed), Is.True);
            rebuilt.Seal(13, rebuilt.RevisionKeys.ToArray());
            var final = installed.WithDelta(rebuilt.EncodeDelta(12));
            CollectionAssert.AreEqual(rebuilt.EncodeSnapshot(), final.EncodeSnapshot());
            Assert.That(final.Chunks.ContainsKey(removedKey), Is.True); Assert.That(final.Falling, Is.Empty);
            AssertExact(Queue(final, point, 4, 2));
            CollectionAssert.AreEqual(source.EncodeSnapshot(), peer.EncodeSnapshot(), "Delta replacement and COW must not rewrite either original source");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void EveryEmptyIntersectionOrRejectedChunkConsumesItsOwnMoveNext(int axis)
        {
            // 7m bounds end inside chunk 2. The attack's map-clipped interval
            // is inverted within that same chunk, so key rejection alone fails.
            var source = new BlockWorld(new Vector2(7, 7), bottom: -7.78f);
            for (int z = -34; z <= 34; z += 16) for (int y = -78; y < 40; y += 16) for (int x = -34; x <= 34; x += 16)
                source.Set(new Vector3Int(x, y, z), BlockCell.Create(2, true));
            var edge = axis == 0 ? new Vector3Int(34, 0, 0) : axis == 1 ? new Vector3Int(0, -80, 0) : new Vector3Int(0, 0, 34);
            // axis 1 also retains a distinct zero-count addressable chunk:
            // a private pre-Seal state must not hide an unbounded chunk scan.
            if (axis == 1) { source.Set(new Vector3Int(34, -78, 34), BlockCell.Create(2)); source.Set(new Vector3Int(34, -78, 34), default); }
            else source.Set(edge, BlockCell.Create(2, true));
            var point = axis == 0 ? new Vector3(3.9f, .05f, .05f)
                : axis == 1 ? new Vector3(.05f, -8.10f, .05f) : new Vector3(.05f, .05f, 3.9f);
            var command = Queue(source, point, .01f, .01f);
            Assert.That(LegacyClippedOracle(command), Is.Empty); Assert.That(WholeOccupiedOracle(command), Is.Empty);
            var actual = ActualStream(command, out int steps); Assert.That(actual, Is.Empty);
            Assert.That(steps, Is.EqualTo(source.Chunks.Count), "Each empty-intersection or rejected source chunk must yield one work sentinel");
        }

        [Test] public void BoundaryFloatPredicatesAndLiveStaticOcclusionArePreserved()
        {
            var source = new BlockWorld(new Vector2(8, 8)); var center = new Vector3(-1.65f, .55f, -.05f);
            for (int i = -7; i <= 7; i++) source.Set(new Vector3Int(-17 + i, 5, -1), BlockCell.Create((byte)((i & 1) == 0 ? 2 : 1), true));
            var blocker = new GameObject("Frozen static blast blocker"); _objects.Add(blocker);
            var wall = blocker.AddComponent<BoxCollider>(); wall.size = new Vector3(.035f, 2, 2); wall.transform.position = center + Vector3.right * .25f;
            _map.Add(wall); Physics.SyncTransforms();
            var witness = new Vector3Int(-15, 5, -1);
            int radiusBits = BitConverter.SingleToInt32Bits(Vector3.Distance(center, BlockWorld.Center(witness)) - .051f);
            float below = BitConverter.Int32BitsToSingle(radiusBits - 4), above = BitConverter.Int32BitsToSingle(radiusBits + 4);
            var excluded = Queue(source, center, below, below * .5f); AssertExact(excluded);
            Assert.That(ActualStream(excluded, out _).Any(item => item.Cell == witness), Is.False);
            var included = Queue(source, center, above, above * .5f); AssertExact(included);
            Assert.That(ActualStream(included, out _).Any(item => item.Cell == witness), Is.True, "A few real float ULPs across the threshold change this visible enemy cell");
            foreach (float radius in new[] { .149f, .14900002f, .249f, .24900003f, .349f })
            {
                var command = Queue(source, center, radius, radius * .5f); AssertExact(command);
                var actual = ActualStream(command, out _);
                Assert.That(actual.Any(item => item.Cell.x >= -13), Is.False, "Registered map wall blocks outward cells");
            }
            wall.enabled = false; Physics.SyncTransforms();
            var open = Queue(source, center, .7f, .7f); AssertExact(open);
            Assert.That(ActualStream(open, out _).Any(item => item.Cell.x >= -13), Is.True, "Visibility is evaluated live, not cached in the row mask");
        }

        [Test] public void FallingHolesAndEveryFrozenSourceFieldSurviveResumedScanAndCOWEdits()
        {
            var source = SparseSeed(); var bottom = new Vector3Int(-20, 24, 15);
            for (int i = 0; i < 6; i++) source.Set(bottom + Vector3Int.up * i, new BlockCell { Owner = (byte)(i % 2 + 1), Durability = (ushort)(i + 3) });
            var run = source.Detach(bottom, 6, 20); run.InitialSpeed = .3f;
            source.RemoveFallingCell(run.Id, 1, out _); source.RemoveFallingCell(run.Id, 4, out _);
            var command = Queue(source, new Vector3(-1.95f, 2.45f, 1.55f), 4, 2, time: 20.25);
            var before = source.EncodeSnapshot();
            var cells = source.Chunks.ToDictionary(p => p.Key, p => p.Value.Cells.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray());
            var fallingCells = run.Cells.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray();
            var iterator = ActualIterator(command); var actual = new List<Target>();
            try
            {
                Assert.That(iterator.MoveNext(), Is.True); var first = Read(iterator.Current); if (first.Owner != 0) actual.Add(first);
                var newer = source.Clone(); newer.Set(new Vector3Int(-32, 17, 16), default);
                newer.Set(new Vector3Int(-31, 17, 16), BlockCell.Create(2, true));
                newer.Set(new Vector3Int(-19, 23, 15), BlockCell.Create(2)); newer.Detach(new Vector3Int(-19, 23, 15), 1, 24);
                newer.RemoveFallingCell(run.Id, 2, out _); newer.EditFalling(run.Id).InitialSpeed = 5;
                typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, newer);
                _terrain.Tick(false, 24, .016f);
                while (iterator.MoveNext()) { var item = Read(iterator.Current); if (item.Owner != 0) actual.Add(item); }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
            CollectionAssert.AreEqual(LegacyClippedOracle(command).Select(item => item.Exact), actual.Select(item => item.Exact));
            CollectionAssert.AreEquivalent(WholeOccupiedOracle(command).Select(item => item.Exact), actual.Select(item => item.Exact));
            Assert.That(actual.Count(item => item.Run == run.Id), Is.EqualTo(4));
            Assert.That(actual.Any(item => item.Run == run.Id && (item.Index == 1 || item.Index == 4)), Is.False);
            Assert.That(Field(command, "Source"), Is.SameAs(source)); Assert.That((double)Field(command, "SourceTime"), Is.EqualTo(20.25));
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            foreach (var pair in cells) CollectionAssert.AreEqual(pair.Value, source.Chunks[pair.Key].Cells.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray());
            CollectionAssert.AreEqual(fallingCells, run.Cells.Select(c => (c.Owner, c.Flags, c.Durability)).ToArray());
            Assert.That(run.InitialSpeed, Is.EqualTo(.3f)); Assert.That(run.StartedAt, Is.EqualTo(20));
        }

        [TestCase(.24f, .08f, false)]
        [TestCase(.08f, .30f, false)]
        [TestCase(.24f, .90f, true)]
        public void FallingMixedTeamsKeepBothRadiusOrdersAndEnemyOnly(float enemy, float friendly, bool enemyOnly)
        {
            var source = new BlockWorld(new Vector2(8, 8));
            for (int x = -3; x <= 3; x++)
            {
                var bottom = new Vector3Int(x, 20, 0);
                for (int i = 0; i < 6; i++) source.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)((i & 1) + 1)));
                var run = source.Detach(bottom, 6, 20); run.InitialSpeed = .3f;
                run.LandingKnown = true; run.LandingOffset = 1.3f;
            }
            var command = Queue(source, new Vector3(.05f, 1.75f, .05f), enemy, friendly, enemyOnly, time: 20.25);
            AssertExact(command);
            var actual = ActualStream(command, out _); Assert.That(actual, Is.Not.Empty);
            Assert.That(actual.All(item => item.Run != 0), Is.True);
            if (enemyOnly) Assert.That(actual.All(item => item.Owner == 2), Is.True);
            else Assert.That(actual.Select(item => item.Owner).Distinct().Count(), Is.EqualTo(2));
        }

        [Test] public void FallingHorizontalAndDiagonalFloatEdgesKeepOriginalDistanceBits()
        {
            var source = new BlockWorld(new Vector2(8, 8)); var bottom = new Vector3Int(0, 20, 0);
            source.Set(bottom, BlockCell.Create(2)); var run = source.Detach(bottom, 1, 20);
            var center = BlockWorld.Center(bottom);
            foreach (var delta in new[] { new Vector3(.25f, 0, 0), new Vector3(.15f, 0, .20f), new Vector3(.15f, .05f, .20f) })
            {
                var point = center - delta; int bits = BitConverter.SingleToInt32Bits((center - point).magnitude - .051f);
                bool included = false, excluded = false;
                for (int change = -4; change <= 4; change++)
                {
                    float radius = BitConverter.Int32BitsToSingle(bits + change);
                    var command = Queue(source, point, radius, 0, true); AssertExact(command);
                    bool hit = ActualStream(command, out _).Any(item => item.Run == run.Id);
                    included |= hit; excluded |= !hit;
                }
                Assert.That(included && excluded, Is.True, "Real float ULPs straddle the original spherical boundary");
            }
        }

        [Test] public void FarFallingColumnsYieldOneBoundedStepPerRunWithoutLosingAnyTargets()
        {
            var source = new BlockWorld(new Vector2(32, 64));
            for (int index = 0; index < 128; index++)
            {
                var bottom = new Vector3Int(20 + index % 32, 20, 20 + index / 32 * 8);
                for (int i = 0; i < 16; i++) source.Set(bottom + Vector3Int.up * i, BlockCell.Create((byte)((i & 1) + 1)));
                source.Detach(bottom, 16, 20);
            }
            var command = Queue(source, new Vector3(.05f, 1.25f, .05f), .14f, .07f);
            AssertExact(command); Assert.That(ActualStream(command, out int steps), Is.Empty);
            Assert.That(steps, Is.EqualTo(source.Chunks.Count + source.Falling.Count),
                "Each rejected static chunk and distant falling run yields its own finite work sentinel");
            Assert.That(steps, Is.LessThan(source.Falling.Values.Sum(run => run.Cells.Length) / 4));
        }

        [Test] public void ResumedFallingCaptureKeepsFrozenTimeAndLiveLowerSupportAcrossNewWorldEdits()
        {
            var source = new BlockWorld(new Vector2(8, 8));
            var lowerBottom = new Vector3Int(-1, 20, 2); var upperBottom = new Vector3Int(-1, 27, 2);
            for (int i = 0; i < 4; i++) source.Set(lowerBottom + Vector3Int.up * i, BlockCell.Create(2));
            for (int i = 0; i < 6; i++) source.Set(upperBottom + Vector3Int.up * i, BlockCell.Create((byte)((i & 1) + 1)));
            var lower = source.BeginCohort(lowerBottom, 4); var lowerRun = source.DetachCohortPiece(lower.Id, 4, 20, out _);
            var upper = source.BeginCohort(upperBottom, 6); var upperRun = source.DetachCohortPiece(upper.Id, 6, 20, out _);
            source.SetCohortSupport(upper.Id, lower.Id, (upperBottom.y - lower.TopExclusive) * BlockWorld.CellSize, out _);
            lower = source.Cohorts[lower.Id]; upper = source.Cohorts[upper.Id];
            lower.LandingKnown = upper.LandingKnown = true; lower.LandingOffset = .5f; upper.LandingOffset = 1.5f;
            source.PreparePendingCohortsForPublication(out _); source.Seal(1, source.RevisionKeys.ToArray()); source.ActivateRebasePendingCohorts(20);
            source.EditCohort(upper.Id, out _).InitialSpeed = 2;
            const double capturedAt = 20.25;
            upper = source.Cohorts[upper.Id]; lower = source.Cohorts[lower.Id];
            Assert.That(upper.LowerSupport, Is.SameAs(lower));
            Assert.That(upper.Offset(capturedAt), Is.EqualTo(lower.Offset(capturedAt) + upper.SupportClearance).Within(1e-6));
            Assert.That(upper.Offset(capturedAt + 2), Is.GreaterThan(upper.Offset(capturedAt)));
            var point = BlockWorld.Center(upperBottom + Vector3Int.up * 2) - Vector3.up * upper.Offset(capturedAt);
            var command = Queue(source, point, 2, 1, time: capturedAt); byte[] frozen = source.EncodeSnapshot();
            var iterator = ActualIterator(command); var actual = new List<Target>();
            try
            {
                while (iterator.MoveNext())
                {
                    var target = Read(iterator.Current); if (target.Owner == 0) continue;
                    actual.Add(target); break;
                }
                Assert.That(actual, Is.Not.Empty, "Resume after a real falling target has sampled its trajectory");
                var newer = source.Clone(); newer.EditCohort(lower.Id, out _).LandingOffset = .02f;
                newer.EditCohort(upper.Id, out _).InitialSpeed = 9;
                typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, newer);
                _terrain.Tick(false, 25, .016f);
                while (iterator.MoveNext()) { var target = Read(iterator.Current); if (target.Owner != 0) actual.Add(target); }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
            CollectionAssert.AreEqual(LegacyClippedOracle(command).Select(item => item.Exact), actual.Select(item => item.Exact));
            CollectionAssert.AreEquivalent(WholeOccupiedOracle(command).Select(item => item.Exact), actual.Select(item => item.Exact));
            Assert.That(actual.Count(item => item.Run == lowerRun.Id), Is.EqualTo(4));
            Assert.That(actual.Count(item => item.Run == upperRun.Id), Is.EqualTo(6));
            Assert.That(Field(command, "Source"), Is.SameAs(source)); Assert.That((double)Field(command, "SourceTime"), Is.EqualTo(capturedAt));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
        }

        [UnityTest] public IEnumerator ActualPreparationKeepsOneCheckStagesAndFiniteNearestOrCompleteChunkGroups()
        {
            foreach (var quota in new[] { (enemy: 1, friendly: 2), (enemy: int.MaxValue, friendly: 1), (enemy: 1, friendly: int.MaxValue), (enemy: int.MaxValue, friendly: int.MaxValue) })
            {
                var source = SparseSeed(); var command = Queue(source, new Vector3(-.05f, .55f, .05f), 4, 2, enemyQuota: quota.enemy, friendlyQuota: quota.friendly);
                var expected = WholeOccupiedOracle(command); Assert.That(expected.Length, Is.GreaterThan(64));
                var prepare = typeof(BlockTerrain).GetMethod("PrepareDestruction", Private); bool done = false;
                for (int frame = 0; frame < 100000 && !done; frame++)
                {
                    long before = _terrain.CommandWorkChecks;
                    object[] args = { command, System.Diagnostics.Stopwatch.StartNew(), 0, 10000d, 1 };
                    done = (bool)prepare.Invoke(_terrain, args);
                    Assert.That((int)args[2], Is.InRange(0, 1));
                    Assert.That(_terrain.CommandWorkChecks - before, Is.EqualTo((int)args[2]));
                    if (!done && frame % 257 == 256) yield return null;
                }
                Assert.That(done, Is.True, "The entire source must be captured and sorted, never capped to the quota");
                var actual = ((IEnumerable)Field(command, "Targets")).Cast<object>().Select(Read).ToArray();
                CollectionAssert.AreEquivalent(expected.Select(item => item.Exact), actual.Select(item => item.Exact));
                IEnumerable<Target> order = expected.OrderBy(item => item.Distance).ThenBy(item => item.Cell.x).ThenBy(item => item.Cell.y).ThenBy(item => item.Cell.z).ThenBy(item => item.Run).ThenBy(item => item.Index);
                bool unbounded = quota.enemy == int.MaxValue && quota.friendly == int.MaxValue;
                if (unbounded) order = expected.OrderBy(item => BlockWorld.ChunkAt(item.Cell).x).ThenBy(item => BlockWorld.ChunkAt(item.Cell).y).ThenBy(item => BlockWorld.ChunkAt(item.Cell).z)
                    .ThenBy(item => item.Distance).ThenBy(item => item.Cell.x).ThenBy(item => item.Cell.y).ThenBy(item => item.Cell.z).ThenBy(item => item.Run).ThenBy(item => item.Index);
                CollectionAssert.AreEqual(order.Select(item => item.Exact), actual.Select(item => item.Exact));
                Assert.That(Field(command, "Source"), Is.SameAs(source)); Assert.That((double)Field(command, "SourceTime"), Is.EqualTo(20));
            }
        }
    }
}

namespace Splatoon.Tests
{
    // Focused unit oracles may set Done and invoke the original checkpoint seam.
    // They are not natural gameplay/rollback or Player acceptance; existing real
    // publication, fairness and batch-lifecycle fixtures remain required.
    public sealed class BlockReservationSpatialIndexTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        GameObject _root;
        BlockTerrain _terrain;
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Original reservation predicate versus bounded spatial candidates");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
        }
        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        static object Field(object owner, string name) => owner.GetType().GetField(name, Fields).GetValue(owner);
        static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Fields).SetValue(owner, value);
        object Call(string name, params object[] args) => typeof(BlockTerrain).GetMethod(name, Fields).Invoke(_terrain, args);
        object[] Reservations() => ((IEnumerable)Field(_terrain, "_destructionReservations")).Cast<object>().ToArray();
        object LastCommand() => ((IEnumerable)Field(_terrain, "_commands")).Cast<object>().Last();
        int Count(string field) => ((IEnumerable)Field(_terrain, field)).Cast<object>().Count();
        void ClearCache() => ((IDictionary)Field(_terrain, "_reservedColumns")).Clear();
        static Vector2 ColumnCenter(Vector2Int column) => new((column.x + .5f) * BlockWorld.CellSize, (column.y + .5f) * BlockWorld.CellSize);
        static float Neighbor(float value, int delta) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) + delta);
        object Queue(Vector2 point, float enemy, float friendly, bool enemyOnly = false, Action<BlockMutationResult> callback = null)
        {
            _terrain.QueueDestroy(new Vector3(point.x, 1, point.y), enemy, friendly, 1, enemyOnly, callback);
            return LastCommand();
        }
        bool OriginalLinearOracle(Vector2Int column)
        {
            var center = ColumnCenter(column);
            foreach (var command in Reservations())
            {
                if ((bool)Field(command, "Done") || (bool)Field(command, "Completed")) continue;
                float radius = Mathf.Max((float)Field(command, "Radius"), (bool)Field(command, "EnemyOnly") ? 0 : (float)Field(command, "FriendlyRadius")) + .051f;
                var point = (Vector3)Field(command, "Point");
                if ((center - new Vector2(point.x, point.z)).sqrMagnitude <= radius * radius) return true;
            }
            return false;
        }
        bool AssertOracle(Vector2Int column)
        {
            bool expected = OriginalLinearOracle(column); ClearCache();
            bool actual = (bool)Call("ReservedColumn", column);
            Assert.That(actual, Is.EqualTo(expected), "Original float predicate at column " + column);
            return actual;
        }
        void AssertEmptyIndex()
        {
            Assert.That(Reservations(), Is.Empty);
            Assert.That(Count("_reservationRanges"), Is.Zero);
            Assert.That(Count("_reservationBuckets"), Is.Zero);
            Assert.That(Count("_reservationFallback"), Is.Zero);
        }

        [Test] public void NegativeCoordinatesBucketEdgesTeamOrdersAndEnemyOnlyMatchOriginalLinearOracle()
        {
            int hits = 0, misses = 0;
            foreach (int origin in new[] { -121, -81, -80, -41, -40, -1, 0, 39, 40, 79, 80 })
                foreach (var radii in new[] { (enemy: 0f, friendly: 0f), (enemy: .2f, friendly: 1.2f), (enemy: 1.2f, friendly: .2f) })
                    foreach (bool enemyOnly in new[] { false, true })
                    {
                        _terrain.Clear(_terrain.Round + 1); AssertEmptyIndex();
                        var anchor = new Vector2Int(origin, -origin - 1);
                        Queue(ColumnCenter(anchor), radii.enemy, radii.friendly, enemyOnly);
                        Assert.That(Count("_reservationRanges"), Is.EqualTo(1));
                        foreach (int dx in new[] { -81, -41, -40, -13, -1, 0, 1, 13, 39, 40, 41, 81 })
                            foreach (int dz in new[] { -41, -1, 0, 1, 40, 41 })
                                if (AssertOracle(anchor + new Vector2Int(dx, dz))) hits++; else misses++;
                    }
            Assert.That(hits, Is.GreaterThan(0)); Assert.That(misses, Is.GreaterThan(0));
        }

        [Test] public void AdjacentFloatRadiusEdgesAndSafeCoordinateBoundaryKeepOriginalFloatAnswers()
        {
            foreach (int x in new[] { -655361, -655360, -655359, -81, -80, -41, -40, -1, 0, 39, 40, 79, 80, 655358, 655359, 655360 })
                foreach (float rawRadius in new[] { 0f, .1f, .7f, 2f })
                    foreach (int ulps in new[] { -1, 0, 1 })
                        foreach (bool diagonal in new[] { false, true })
                        {
                            _terrain.Clear(_terrain.Round + 1);
                            var column = new Vector2Int(x, -x - 1); var center = ColumnCenter(column);
                            float edge = Neighbor(rawRadius + .051f, ulps);
                            var point = center - new Vector2(edge, diagonal ? edge : 0);
                            Queue(point, rawRadius, rawRadius, x % 2 == 0);
                            AssertOracle(column);
                            AssertOracle(column + Vector2Int.one);
                            AssertOracle(column - Vector2Int.one);
                        }
        }

        [Test] public void HugeFiniteRadiiDerivedInfinityAndCoordinateRisksUseExactBoundedFallback()
        {
            foreach (var entry in new[] {
                (point: Vector2.zero, radius: float.MaxValue), (point: Vector2.zero, radius: 1e20f),
                (point: Vector2.zero, radius: 1000f), (point: new Vector2(65537f, 0), radius: 0f),
                (point: new Vector2(-65537f, 0), radius: 0f), (point: new Vector2(float.MaxValue, float.MaxValue), radius: .2f) })
            {
                _terrain.Clear(_terrain.Round + 1);
                Queue(entry.point, entry.radius, entry.radius);
                Assert.That(Count("_reservationFallback"), Is.EqualTo(1));
                Assert.That(Count("_reservationRanges"), Is.Zero); Assert.That(Count("_reservationBuckets"), Is.Zero);
                foreach (var column in new[] { Vector2Int.zero, new Vector2Int(-1, 1),
                    new Vector2Int(-655361, 655360), new Vector2Int(655360, -655361),
                    new Vector2Int(int.MinValue, int.MaxValue), new Vector2Int(int.MaxValue, int.MinValue) }) AssertOracle(column);
            }
            // Unsafe query coordinates also use the entire original set, so an
            // indexed command is never silently omitted by the query fallback.
            _terrain.Clear(_terrain.Round + 1); Queue(new Vector2(65536f, -65536f), .2f, .2f);
            Assert.That(Count("_reservationRanges"), Is.EqualTo(1));
            Assert.That(AssertOracle(new Vector2Int(655360, -655360)), Is.True);
            AssertOracle(new Vector2Int(int.MaxValue, int.MinValue));
        }

        [Test] public void RemoteReservationsSkipUnrelatedExactChecksAndExistingPerStageCacheRemains()
        {
            for (int i = 0; i < 512; i++) Queue(new Vector2(100 + i * 4, 100), .2f, .2f);
            Assert.That(Reservations().Length, Is.EqualTo(512));
            Assert.That(Count("_reservationRanges"), Is.EqualTo(512)); Assert.That(Count("_reservationFallback"), Is.Zero);
            foreach (DictionaryEntry item in (IDictionary)Field(_terrain, "_reservationRanges"))
            {
                long width = (int)Field(item.Value, "MaxX") - (long)(int)Field(item.Value, "MinX") + 1;
                long height = (int)Field(item.Value, "MaxZ") - (long)(int)Field(item.Value, "MinZ") + 1;
                Assert.That(width * height, Is.InRange(1L, 64L));
            }
            long before = _terrain.ReservationCommandChecks;
            Assert.That(AssertOracle(Vector2Int.zero), Is.False);
            Assert.That(_terrain.ReservationCommandChecks - before, Is.Zero, "Remote buckets must not visit all512 exact predicates");
            var local = Queue(ColumnCenter(Vector2Int.zero), .2f, .2f);
            before = _terrain.ReservationCommandChecks; Assert.That(AssertOracle(Vector2Int.zero), Is.True);
            Assert.That(_terrain.ReservationCommandChecks - before, Is.EqualTo(1));
            before = _terrain.ReservationCommandChecks;
            Assert.That((bool)Call("ReservedColumn", Vector2Int.zero), Is.True);
            Assert.That(_terrain.ReservationCommandChecks, Is.EqualTo(before), "Same-stage cache hit still skips every command");
            Set(local, "Done", true);
            Assert.That((bool)Call("ReservedColumn", Vector2Int.zero), Is.True, "The existing cache lifetime is intentionally unchanged");
            Assert.That(AssertOracle(Vector2Int.zero), Is.False, "A stage cache clear must observe staged Done");
            Assert.That(Count("_reservationRanges"), Is.EqualTo(513), "Done must not remove rollback membership");
            Set(local, "Done", false); Assert.That(AssertOracle(Vector2Int.zero), Is.True);
        }

        [Test] public void OrdinaryAndCoalescedCheckpointRestoreReactivateExistingIndexedReservations()
        {
            var column = new Vector2Int(-40, 39); var command = Queue(ColumnCenter(column), .2f, .2f);
            object checkpoint = Call("CheckpointCommand", command);
            Set(command, "Done", true); Assert.That(AssertOracle(column), Is.False);
            Assert.That(Count("_reservationRanges"), Is.EqualTo(1));
            Call("RestoreCheckpoint", checkpoint);
            Assert.That((bool)Field(command, "Done"), Is.False); Assert.That(AssertOracle(column), Is.True);
            _terrain.Clear(_terrain.Round + 1); AssertEmptyIndex();

            var commands = new object[8];
            for (int i = 0; i < commands.Length; i++) commands[i] = Queue(ColumnCenter(column + new Vector2Int(40 * i, 0)), .2f, .2f);
            Type type = typeof(BlockTerrain).GetNestedType("UnboundedBlastBatch", BindingFlags.NonPublic);
            object batch = Activator.CreateInstance(type, true);
            var array = Array.CreateInstance(commands[0].GetType(), commands.Length);
            for (int i = 0; i < commands.Length; i++) array.SetValue(commands[i], i);
            Set(batch, "Commands", array); Call("CheckpointCoalescedBlastBatch", batch);
            checkpoint = Field(_terrain, "_stageCommandCheckpoint");
            foreach (var member in commands) Set(member, "Done", true);
            for (int i = 0; i < commands.Length; i++) Assert.That(AssertOracle(column + new Vector2Int(40 * i, 0)), Is.False);
            Assert.That(Count("_reservationRanges"), Is.EqualTo(8));
            Call("RestoreCheckpoint", checkpoint);
            for (int i = 0; i < commands.Length; i++)
            {
                Assert.That((bool)Field(commands[i], "Done"), Is.False);
                Assert.That(AssertOracle(column + new Vector2Int(40 * i, 0)), Is.True);
            }
            CollectionAssert.AreEqual(commands, ((IEnumerable)Field(_terrain, "_commands")).Cast<object>().ToArray());
        }

        [Test] public void CompletionCallbackEnqueueAndNestedClearRetireEachReservationBeforeItsOneCallback()
        {
            var order = new List<string>(); var firstColumn = Vector2Int.zero;
            object first = null, second = null, third = null;
            first = Queue(ColumnCenter(firstColumn), .2f, .2f, callback: _ =>
            {
                order.Add("first"); Assert.That((bool)Field(first, "Completed"), Is.True);
                Assert.That(Reservations().Contains(first), Is.False);
                Assert.That(((IDictionary)Field(_terrain, "_reservationRanges")).Contains(first), Is.False);
                Assert.That(AssertOracle(firstColumn), Is.False);
                third = Queue(ColumnCenter(new Vector2Int(160, 0)), .2f, .2f, callback: __ =>
                {
                    order.Add("third"); Assert.That(Reservations().Contains(third), Is.False);
                    Assert.That(AssertOracle(new Vector2Int(160, 0)), Is.False);
                });
                Assert.That(AssertOracle(new Vector2Int(160, 0)), Is.True);
                _terrain.Clear(_terrain.Round + 1);
            });
            second = Queue(ColumnCenter(new Vector2Int(80, 0)), .2f, .2f, callback: _ =>
            {
                order.Add("second"); Assert.That(Reservations().Contains(second), Is.False);
                Assert.That(AssertOracle(new Vector2Int(80, 0)), Is.False);
            });
            _terrain.Clear(_terrain.Round + 1);
            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, order);
            AssertEmptyIndex();
            Assert.That(_terrain.CommandsQueued, Is.EqualTo(3)); Assert.That(_terrain.CommandsCompleted, Is.EqualTo(3));
        }
    }
}
#endif
