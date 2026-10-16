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
    public sealed class BlockUnboundedBlastChunkTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly struct FrozenTarget
        {
            public readonly Vector3Int Cell;
            public readonly byte Owner;
            public readonly ulong Run;
            public readonly int Index;
            public readonly float Distance;
            public FrozenTarget(Vector3Int cell, byte owner, ulong run, int index, float distance)
            { Cell = cell; Owner = owner; Run = run; Index = index; Distance = distance; }
            public (Vector3Int, ulong, int, byte) Key => (Cell, Run, Index, Owner);
        }
        [UnitySetUp] public IEnumerator Setup()
        { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); yield return new EnterPlayMode(); }
        [UnityTearDown] public IEnumerator Cleanup()
        { yield return new ExitPlayMode(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        static object Head(BlockTerrain terrain) => ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private)
            .GetValue(terrain)).Cast<object>().First();
        static IEnumerator Install(BlockTerrain terrain, BlockWorld world)
        {
            world.Seal(terrain.Revision + 1, world.RevisionKeys.ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && terrain.PendingTransactions != 0; tick++)
            { terrain.Tick(false, tick * .016, .016f); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero);
        }
        static IEnumerator PrepareActual(BlockTerrain terrain, object command)
        {
            var prepare = typeof(BlockTerrain).GetMethod("PrepareDestruction", Private);
            bool done = false;
            for (int frame = 0; frame < 2000 && !done; frame++)
            {
                object[] args = { command, System.Diagnostics.Stopwatch.StartNew(), 0, 10000d, 257 };
                done = (bool)prepare.Invoke(terrain, args);
                Assert.That((int)args[2], Is.LessThanOrEqualTo(257), "Actual preparation still obeys its work-check bound");
                if (!done) yield return null;
            }
            Assert.That(done, Is.True);
        }
        static FrozenTarget[] ActualTargets(object command) => ((IEnumerable)command.GetType().GetField("Targets")
            .GetValue(command)).Cast<object>().Select(target =>
            {
                var type = target.GetType();
                return new FrozenTarget((Vector3Int)type.GetField("Cell").GetValue(target),
                    (byte)type.GetField("Owner").GetValue(target), (ulong)type.GetField("Run").GetValue(target),
                    (int)type.GetField("Index").GetValue(target), (float)type.GetField("Distance").GetValue(target));
            }).ToArray();
        static bool FrozenVisible(Vector3 from, Vector3 to, HashSet<Collider> map)
        {
            var delta = to - from; float distance = delta.magnitude; if (distance < .03f) return true;
            var hits = new RaycastHit[64];
            int count = Physics.RaycastNonAlloc(from + delta / distance * .015f, delta / distance, hits,
                Mathf.Max(0, distance - .03f), PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore);
            return count != hits.Length && !hits.Take(count).Any(hit => map.Contains(hit.collider));
        }
        // Independent full occupied-snapshot scan: no production candidate iterator,
        // source-chunk clipping, comparator or preparation list is used by this oracle.
        static FrozenTarget[] FrozenCompleteSet(BlockWorld source, double time, Vector3 point,
            float enemyRadius, float friendlyRadius, byte team, HashSet<Collider> map)
        {
            var result = new List<FrozenTarget>();
            void Include(Vector3Int cell, BlockCell own, Vector3 center, ulong run = 0, int index = 0)
            {
                if (!own.Occupied) return;
                float radius = own.Owner == team ? friendlyRadius : enemyRadius;
                float distance = (center - point).sqrMagnitude;
                if (distance <= (radius + .051f) * (radius + .051f) && FrozenVisible(point, center, map))
                    result.Add(new FrozenTarget(cell, own.Owner, run, index, distance));
            }
            foreach (var chunk in source.Chunks) for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
            {
                var cell = BlockWorld.CellFromIndex(chunk.Key, index);
                Include(cell, chunk.Value.Cells[index], BlockWorld.Center(cell));
            }
            foreach (var run in source.Falling.Values) for (int index = 0; index < run.Cells.Length; index++)
            {
                var cell = run.Bottom + Vector3Int.up * index;
                Include(cell, run.Cells[index], BlockWorld.Center(cell) - Vector3.up * run.Offset(time), run.Id, index);
            }
            return result.ToArray();
        }
        static FrozenTarget[] NearestOrder(IEnumerable<FrozenTarget> targets) => targets.OrderBy(target => target.Distance)
            .ThenBy(target => target.Cell.x).ThenBy(target => target.Cell.y).ThenBy(target => target.Cell.z)
            .ThenBy(target => target.Run).ThenBy(target => target.Index).ToArray();
        static Dictionary<Vector3Int, byte> StableCells(BlockWorld world)
        {
            var result = new Dictionary<Vector3Int, byte>();
            foreach (var chunk in world.Chunks) for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
                if (chunk.Value.Cells[index].Occupied) result.Add(BlockWorld.CellFromIndex(chunk.Key, index), chunk.Value.Cells[index].Owner);
            return result;
        }
        sealed class FrozenRunState
        {
            public ulong Id;
            public Vector3Int Bottom;
            public double StartedAt;
            public float InitialSpeed, LandingOffset;
            public bool LandingKnown;
            public (byte Owner, byte Flags, ushort Durability)[] Cells;
        }
        static (byte Owner, byte Flags, ushort Durability)[] FrozenCellStates(BlockCell[] cells)
            => cells.Select(cell => (cell.Owner, cell.Flags, cell.Durability)).ToArray();
        static Dictionary<Vector3Int, (byte Owner, byte Flags, ushort Durability)[]> FrozenStableStates(BlockWorld world)
            => world.Chunks.ToDictionary(pair => pair.Key, pair => FrozenCellStates(pair.Value.Cells));
        static Dictionary<ulong, FrozenRunState> FrozenFallingStates(BlockWorld world)
            => world.Falling.ToDictionary(pair => pair.Key, pair => new FrozenRunState
            {
                Id = pair.Value.Id, Bottom = pair.Value.Bottom, StartedAt = pair.Value.StartedAt,
                InitialSpeed = pair.Value.InitialSpeed, LandingOffset = pair.Value.LandingOffset,
                LandingKnown = pair.Value.LandingKnown, Cells = FrozenCellStates(pair.Value.Cells)
            });
        static void AssertSourceUnchanged(BlockWorld source,
            Dictionary<Vector3Int, (byte Owner, byte Flags, ushort Durability)[]> stable,
            Dictionary<ulong, FrozenRunState> falling, int pink, int blue)
        {
            CollectionAssert.AreEquivalent(stable.Keys, source.Chunks.Keys);
            foreach (var pair in stable)
                CollectionAssert.AreEqual(pair.Value, FrozenCellStates(source.Chunks[pair.Key].Cells),
                    "The frozen source keeps every stable owner, flags and durability, including empty slots");
            CollectionAssert.AreEquivalent(falling.Keys, source.Falling.Keys);
            foreach (var pair in falling)
            {
                var expected = pair.Value; var actual = source.Falling[pair.Key];
                Assert.That(actual.Id, Is.EqualTo(expected.Id)); Assert.That(actual.Bottom, Is.EqualTo(expected.Bottom));
                Assert.That(actual.StartedAt, Is.EqualTo(expected.StartedAt)); Assert.That(actual.InitialSpeed, Is.EqualTo(expected.InitialSpeed));
                Assert.That(actual.LandingOffset, Is.EqualTo(expected.LandingOffset)); Assert.That(actual.LandingKnown, Is.EqualTo(expected.LandingKnown));
                CollectionAssert.AreEqual(expected.Cells, FrozenCellStates(actual.Cells),
                    "The frozen falling array keeps all owners, flags, durability and holes");
            }
            Assert.That(source.PinkCount, Is.EqualTo(pink)); Assert.That(source.BlueCount, Is.EqualTo(blue));
        }
        static void AssertBudget(BlockTerrain terrain, Dictionary<int, int> chunksPerFrame, BlockTerrainCommit commit)
        {
            chunksPerFrame.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
            chunksPerFrame[Time.frameCount] = count; Assert.That(count, Is.LessThanOrEqualTo(8));
            Assert.That(terrain.Revision, Is.EqualTo(commit.Revision));
        }
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator UnboundedBlastKeepsTheFrozenCompleteSetOcclusionTeamsFallingHolesAndOneCallback()
        {
            var obj = new GameObject("Unbounded blast chunk publication regression");
            var wallObject = new GameObject("Registered static blast occluder");
            try
            {
                var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                var wall = wallObject.AddComponent<BoxCollider>(); wall.size = new Vector3(.15f, 5, 8);
                wall.transform.position = new Vector3(.85f, 2, 0);
                var map = (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", Private).GetValue(terrain);
                map.Add(wall); Physics.SyncTransforms();
                var incoming = new BlockWorld(new Vector2(32, 64));
                for (int z = -32; z < 32; z += 2) for (int x = -48; x < 48; x += 2)
                    incoming.Set(new Vector3Int(x, 5, z), BlockCell.Create((byte)(((x / 2 + z / 2) & 1) == 0 ? 1 : 2), true));
                var falling = new Vector3Int(-10, 12, 0);
                for (int i = 0; i < 5; i++) incoming.Set(falling + Vector3Int.up * i, BlockCell.Create((byte)(i == 0 ? 1 : 2)));
                var run = incoming.Detach(falling, 5, 20); run.LandingKnown = true; run.LandingOffset = .6f;
                incoming.RemoveFallingCell(run.Id, 1, out _); incoming.RemoveFallingCell(run.Id, 3, out _);
                yield return Install(terrain, incoming); terrain.Tick(false, 20, .016f);
                var source = terrain.World; var sourceStable = StableCells(source);
                var sourceStableState = FrozenStableStates(source); var sourceFallingState = FrozenFallingStates(source);
                int sourcePink = source.PinkCount, sourceBlue = source.BlueCount;
                var point = new Vector3(-.05f, .55f, .05f); int callbacks = 0; BlockMutationResult total = default;
                terrain.QueueDestroy(point, 4, 2, 1, false, result => { callbacks++; total = result; });
                var command = Head(terrain); double sourceTime = (double)command.GetType().GetField("SourceTime").GetValue(command);
                Assert.That(sourceTime, Is.EqualTo(20)); Assert.That(command.GetType().GetField("Source").GetValue(command), Is.SameAs(source));
                var frozen = FrozenCompleteSet(source, sourceTime, point, 4, 2, 1, map);
                Assert.That(frozen.Length, Is.GreaterThan(64));
                var hidden = new Vector3Int(14, 5, 0); var smallerFriendly = new Vector3Int(-40, 5, 0);
                Assert.That(sourceStable[hidden], Is.EqualTo(2)); Assert.That(frozen.Any(target => target.Cell == hidden), Is.False);
                Assert.That(sourceStable[smallerFriendly], Is.EqualTo(1)); Assert.That(frozen.Any(target => target.Cell == smallerFriendly), Is.False);
                Assert.That(frozen.Count(target => target.Run == run.Id), Is.EqualTo(3));
                Assert.That(frozen.Any(target => target.Run == run.Id && (target.Index == 1 || target.Index == 3)), Is.False);
                Assert.That(source.Falling[run.Id].Offset(sourceTime + 2), Is.GreaterThan(source.Falling[run.Id].Offset(sourceTime)));
                yield return PrepareActual(terrain, command);
                var actual = ActualTargets(command); CollectionAssert.AreEquivalent(frozen.Select(target => target.Key), actual.Select(target => target.Key));
                var chunks = actual.Select(target => BlockWorld.ChunkAt(target.Cell)).ToArray(); var seen = new HashSet<Vector3Int>();
                for (int i = 0; i < chunks.Length; i++) if (i == 0 || chunks[i] != chunks[i - 1])
                    Assert.That(seen.Add(chunks[i]), Is.True, "Each source chunk is one contiguous deletion group");
                foreach (var group in actual.GroupBy(target => BlockWorld.ChunkAt(target.Cell)))
                    CollectionAssert.AreEqual(NearestOrder(group).Select(target => target.Key), group.Select(target => target.Key));
                Assert.That(seen.Count, Is.GreaterThan(8), "The real fixture spans multiple atomic publications");
                var future = new Vector3Int(-5, 5, 1); var replaced = new Vector3Int(-2, 5, 0);
                Assert.That(frozen.Any(target => target.Run == 0 && target.Cell == replaced), Is.True);
                var newer = source.Clone(); newer.Set(future, BlockCell.Create(2, true)); newer.Set(replaced, BlockCell.Create(1, true));
                uint newerRevision = terrain.Revision + 1; newer.Seal(newerRevision, newer.RevisionKeys.ToArray());
                Assert.That(terrain.ApplyRemoteSnapshot(newer.EncodeSnapshot()), Is.True);
                // The already queued attack remains present: wait for this remote
                // publication by revision, not for PendingTransactions to become zero.
                for (int tick = 0; tick < 1000 && terrain.Revision != newerRevision; tick++)
                { terrain.Tick(false, 20 + tick * .016, .016f); yield return null; }
                Assert.That(terrain.Revision, Is.EqualTo(newerRevision));
                AssertSourceUnchanged(source, sourceStableState, sourceFallingState, sourcePink, sourceBlue);
                Assert.That(callbacks, Is.Zero); Assert.That(command.GetType().GetField("Source").GetValue(command), Is.SameAs(source));
                Assert.That((double)command.GetType().GetField("SourceTime").GetValue(command), Is.EqualTo(sourceTime));
                var deleted = new HashSet<(Vector3Int, byte)>(); var previous = terrain.World;
                var chunksPerFrame = new Dictionary<int, int>(); int publications = 0;
                terrain.PublishedCommit += commit =>
                {
                    publications++; AssertBudget(terrain, chunksPerFrame, commit);
                    foreach (var cell in StableCells(previous)) if (!terrain.World.TryGet(cell.Key, out _))
                        Assert.That(deleted.Add((cell.Key, cell.Value)), Is.True, "No stable removal can be counted twice");
                    previous = terrain.World;
                };
                for (int tick = 0; tick < 2000 && (callbacks == 0 || terrain.PendingTransactions != 0 || terrain.UnstableColumns != 0); tick++)
                { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                var removed = frozen.Where(target => target.Run != 0 || target.Cell != replaced).ToArray();
                CollectionAssert.AreEquivalent(removed.Where(target => target.Run == 0).Select(target => (target.Cell, target.Owner)), deleted);
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(publications, Is.GreaterThan(1));
                Assert.That(total.RemovedEnemy, Is.EqualTo(removed.Count(target => target.Owner != 1)));
                Assert.That(total.RemovedFriendly, Is.EqualTo(removed.Count(target => target.Owner == 1)));
                Assert.That(terrain.World.Falling, Is.Empty); Assert.That(terrain.FallingCount, Is.Zero);
                Assert.That(terrain.World.TryGet(future, out var futureOwn) && futureOwn.Owner == 2, Is.True);
                Assert.That(terrain.World.TryGet(replaced, out var changedOwn) && changedOwn.Owner == 1, Is.True);
                Assert.That(terrain.World.TryGet(hidden, out _), Is.True); Assert.That(terrain.World.TryGet(smallerFriendly, out _), Is.True);
                foreach (var target in removed.Where(target => target.Run == 0).Take(3))
                    Assert.That(Physics.Raycast(BlockWorld.Center(target.Cell) + Vector3.up * .15f, Vector3.down,
                        .25f, 1 << BlockTerrain.TerrainLayer), Is.False, "Deleted source cells must have no published ghost collision");
                Assert.That(Physics.Raycast(BlockWorld.Center(hidden) + Vector3.up * .15f, Vector3.down,
                    .25f, 1 << BlockTerrain.TerrainLayer), Is.True, "The occluded survivor keeps its actual published collider");
                Assert.That(StableCells(source).Count, Is.EqualTo(sourceStable.Count));
                Assert.That(source.Falling[run.Id].Cells.Count(cell => cell.Occupied), Is.EqualTo(3));
                for (int tick = 0; tick < 20; tick++) { terrain.Tick(true, 60 + tick * .016, .016f); yield return null; }
                Assert.That(callbacks, Is.EqualTo(1)); Assert.That(terrain.PendingTransactions, Is.Zero);
                AssertSourceUnchanged(source, sourceStableState, sourceFallingState, sourcePink, sourceBlue);
            }
            finally { Object.DestroyImmediate(wallObject); Object.DestroyImmediate(obj); }
        }
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AnyFiniteTeamQuotaRetainsTheOriginalNearestOrderAndExactRemovalSet()
        {
            foreach (var quota in new[] { (enemy: 1, friendly: 2), (enemy: 1, friendly: int.MaxValue), (enemy: int.MaxValue, friendly: 1) })
            {
                var obj = new GameObject("Finite blast quota nearest regression");
                try
                {
                    var terrain = obj.AddComponent<BlockTerrain>(); terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); terrain.Initialize(null);
                    var incoming = new BlockWorld(new Vector2(32, 64));
                    for (int z = -10; z < 10; z += 2) for (int x = -40; x < 40; x += 2)
                        incoming.Set(new Vector3Int(x, 5, z), BlockCell.Create((byte)(((x / 2 + z / 2) & 1) == 0 ? 1 : 2), true));
                    yield return Install(terrain, incoming); terrain.Tick(false, 20, .016f);
                    var source = terrain.World; var point = new Vector3(1.55f, .55f, .05f); int callbacks = 0; BlockMutationResult total = default;
                    var map = new HashSet<Collider>(); var frozen = FrozenCompleteSet(source, 20, point, 4, 4, 1, map);
                    Assert.That(frozen.Length, Is.GreaterThan(64));
                    terrain.QueueDestroy(point, 4, 4, 1, false, result => { callbacks++; total = result; }, quota.enemy, quota.friendly);
                    var command = Head(terrain); yield return PrepareActual(terrain, command);
                    var ordered = NearestOrder(frozen);
                    CollectionAssert.AreEqual(ordered.Select(target => target.Key), ActualTargets(command).Select(target => target.Key));
                    var selected = ordered.Where(target => target.Owner != 1).Take(quota.enemy)
                        .Concat(ordered.Where(target => target.Owner == 1).Take(quota.friendly)).ToArray();
                    var expected = new HashSet<Vector3Int>(selected.Select(target => target.Cell));
                    var chunksPerFrame = new Dictionary<int, int>();
                    terrain.PublishedCommit += commit => AssertBudget(terrain, chunksPerFrame, commit);
                    for (int tick = 0; tick < 2000 && (callbacks == 0 || terrain.PendingTransactions != 0 || terrain.UnstableColumns != 0); tick++)
                    { terrain.Tick(true, 20 + tick * .016, .016f); yield return null; }
                    var deleted = StableCells(source).Keys.Where(cell => !terrain.World.TryGet(cell, out _)).ToArray();
                    CollectionAssert.AreEquivalent(expected, deleted); Assert.That(callbacks, Is.EqualTo(1));
                    Assert.That(total.RemovedEnemy, Is.EqualTo(selected.Count(target => target.Owner != 1)));
                    Assert.That(total.RemovedFriendly, Is.EqualTo(selected.Count(target => target.Owner == 1)));
                    Assert.That(terrain.PendingTransactions, Is.Zero);
                }
                finally { Object.DestroyImmediate(obj); }
            }
        }
    }
}
#endif
