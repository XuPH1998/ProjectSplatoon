#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // New candidate fixture; no existing production tests are replaced.
    public sealed class BlockUnboundedBlastBatchTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject _object;
        BlockTerrain _terrain;
        static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        object[] Commands() => ((IEnumerable)typeof(BlockTerrain).GetField("_commands", Private)
            .GetValue(_terrain)).Cast<object>().ToArray();

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _object = new GameObject("Real unbounded blast batch fixture");
            _terrain = _object.AddComponent<BlockTerrain>();
            // This fixture retains its original eight-key per-frame contract.
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            _terrain.Initialize(null);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_object != null) Object.Destroy(_object);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        IEnumerator Install(BlockWorld seed)
        {
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            for (int tick = 0; tick < 2000 && _terrain.PendingTransactions != 0; tick++)
            { _terrain.Tick(false, tick * .016, .016f); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero);
            _terrain.Tick(false, 40, .016f);
        }
        static Dictionary<Vector3Int, byte> Cells(BlockWorld world)
        {
            var result = new Dictionary<Vector3Int, byte>();
            foreach (var pair in world.Chunks) for (int index = 0; index < pair.Value.Cells.Length; index++)
                if (pair.Value.Cells[index].Occupied)
                    result.Add(BlockWorld.CellFromIndex(pair.Key, index), pair.Value.Cells[index].Owner);
            return result;
        }
        void AssertPerFrameBound(Dictionary<int, int> chunks, BlockTerrainCommit commit)
        {
            chunks.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
            chunks[Time.frameCount] = count;
            Assert.That(count, Is.LessThanOrEqualTo(8));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount,
                Is.EqualTo(Cells(_terrain.World).Count + _terrain.FallingCount));
        }

        [UnityTest, Timeout(600000)] public IEnumerator EightSameSourceMixedTeamBlastsPreserveEarliestClaimsAndAtomicCollision()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            for (int z = -25; z <= 25; z += 3) for (int x = -40; x <= 40; x += 3)
                seed.Set(new Vector3Int(x, 1, z), BlockCell.Create(x < 0 ? (byte)1 : (byte)2, true));
            yield return Install(seed);
            var source = _terrain.World; var before = source.EncodeSnapshot(); var cells = Cells(source);
            var expected = new BlockMutationResult[8]; var callbacks = new int[8]; var actual = new BlockMutationResult[8];
            var claimed = new HashSet<Vector3Int>();
            for (int index = 0; index < 8; index++)
            {
                int owner = index; byte team = index % 2 == 0 ? (byte)1 : (byte)2;
                var point = new Vector3((index % 4 - 1.5f) * 2, .15f, index / 4 == 0 ? -1 : 1);
                foreach (var cell in cells)
                {
                    float radius = cell.Value == team ? .75f : 1.5f;
                    if ((BlockWorld.Center(cell.Key) - point).sqrMagnitude > (radius + .051f) * (radius + .051f)
                        || !claimed.Add(cell.Key)) continue;
                    if (cell.Value == team) expected[index].RemovedFriendly++; else expected[index].RemovedEnemy++;
                }
                _terrain.QueueDestroy(point, 1.5f, .75f, team, false, result =>
                {
                    callbacks[owner]++; actual[owner] = result;
                    // Batch callbacks follow the final actual world/collider publication.
                    foreach (var cell in claimed)
                    {
                        Assert.That(_terrain.World.TryGet(cell, out _), Is.False);
                        Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f,
                            Vector3.down, .16f, 1 << BlockTerrain.TerrainLayer), Is.False);
                    }
                });
            }
            Assert.That(Commands().Length, Is.EqualTo(8));
            foreach (var command in Commands()) Assert.That(Field(command, "Source"), Is.SameAs(source));
            var chunks = new Dictionary<int, int>(); int publications = 0;
            _terrain.PublishedCommit += commit => { publications++; AssertPerFrameBound(chunks, commit); };
            for (int tick = 0; tick < 3000 && callbacks.Any(count => count == 0); tick++)
            { _terrain.Tick(true, 40 + tick * .016, .016f); yield return null; }
            CollectionAssert.AreEqual(Enumerable.Repeat(1, 8), callbacks);
            for (int i = 0; i < 8; i++)
            {
                Assert.That(actual[i].RemovedEnemy, Is.EqualTo(expected[i].RemovedEnemy));
                Assert.That(actual[i].RemovedFriendly, Is.EqualTo(expected[i].RemovedFriendly));
                Assert.That(actual[i].Added, Is.Zero);
            }
            Assert.That(publications, Is.GreaterThan(1)); Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(cells.Count - claimed.Count));
            foreach (var cell in cells.Keys.Except(claimed)) Assert.That(_terrain.World.TryGet(cell, out _), Is.True);
            CollectionAssert.AreEqual(before, source.EncodeSnapshot(), "Every frozen source field survives COW and all stages");
            for (int tick = 0; tick < 20; tick++) { _terrain.Tick(true, 100 + tick * .016, .016f); yield return null; }
            CollectionAssert.AreEqual(Enumerable.Repeat(1, 8), callbacks);
        }

        [UnityTest, Timeout(600000)] public IEnumerator SmallCompleteSetsCloseOnLastWorkUnitAndWaitForRealPublication()
        {
            var seed = new BlockWorld(new Vector2(32, 64));
            foreach (int x in new[] { -16, -15, 16 }) seed.Set(new Vector3Int(x, 1, 0), BlockCell.Create(2, true));
            yield return Install(seed);
            var source = _terrain.World; var before = source.EncodeSnapshot();
            var callbacks = new int[2]; var removals = new int[2];
            for (int i = 0; i < 2; i++)
            {
                int owner = i; var point = BlockWorld.Center(new Vector3Int(i == 0 ? 16 : -15, 1, 0));
                _terrain.QueueDestroy(point, 4, 2, 1, false, result =>
                { callbacks[owner]++; removals[owner] = result.RemovedEnemy; Assert.That(_terrain.BlueCount, Is.Zero); });
            }
            var commands = Commands();
            var prepare = typeof(BlockTerrain).GetMethod("PrepareDestruction", Private);
            foreach (var command in commands)
            {
                object[] arguments = { command, System.Diagnostics.Stopwatch.StartNew(), 0, 10000d, 4096 };
                Assert.That((bool)prepare.Invoke(_terrain, arguments), Is.True);
            }
            // Actual <=64 preparation is nearest-first. Without the batch's
            // complete small-set regrouping, the second attacker claims chunk -1 first.
            var next = source.Clone(); var changed = new HashSet<Vector3Int>();
            var commandType = commands[0].GetType();
            var tupleType = typeof(ValueTuple<,>).MakeGenericType(commandType, typeof(BlockMutationResult));
            var resultList = Activator.CreateInstance(typeof(List<>).MakeGenericType(tupleType));
            object[] stage = { next, changed, resultList, 0, System.Diagnostics.Stopwatch.StartNew(), 10000d, 6 };
            typeof(BlockTerrain).GetMethod("StageCommands", Private).Invoke(_terrain, stage);
            Assert.That((int)stage[3], Is.EqualTo(6), "Three actual removals plus three live-owner skips");
            foreach (var command in commands) Assert.That((bool)Field(command, "Done"), Is.True);
            CollectionAssert.AreEqual(new[] { 0, 0 }, callbacks);
            Assert.That(_terrain.BlueCount, Is.EqualTo(3)); Assert.That(next.BlueCount, Is.Zero);
            next.Seal(_terrain.Revision + 1, changed);
            typeof(BlockTerrain).GetMethod("BeginTransaction", Private).Invoke(_terrain,
                new object[] { next, changed, false, resultList });
            for (int tick = 0; tick < 2000 && callbacks.Any(count => count == 0); tick++)
            { _terrain.Tick(false, 50 + tick * .016, .016f); yield return null; }
            CollectionAssert.AreEqual(new[] { 1, 1 }, callbacks);
            CollectionAssert.AreEqual(new[] { 3, 0 }, removals);
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
        }

        [UnityTest, Timeout(600000)] public IEnumerator FiniteQuotaBuildAndDifferentFrozenTimeRemainBarriers()
        {
            var seed = new BlockWorld(new Vector2(32, 64)); seed.Set(new Vector3Int(0, 1, 0), BlockCell.Create(2, true));
            yield return Install(seed);
            var eligible = typeof(BlockTerrain).GetMethod("HasCoalescedBlastWork", Private);
            void Reset() { _terrain.FinishRound(); _terrain.Clear(seed.Round);
                typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.World)).SetValue(_terrain, seed); _terrain.Tick(false, 40, .016f); }
            void Unbounded() => _terrain.QueueDestroy(Vector3.zero, 4, 2, 1, false);
            Reset(); Unbounded(); _terrain.QueueDestroy(Vector3.zero, 4, 0, 1, true, maxEnemyBlocks: 4, maxFriendlyBlocks: 0); Unbounded();
            Assert.That((bool)eligible.Invoke(_terrain, null), Is.False);
            Reset(); Unbounded(); _terrain.QueueBuild(Vector3.zero, Vector3.up, 1, 1, .1f, false); Unbounded();
            Assert.That((bool)eligible.Invoke(_terrain, null), Is.False);
            Reset(); Unbounded(); _terrain.Tick(false, 41, .016f); Unbounded();
            Assert.That((bool)eligible.Invoke(_terrain, null), Is.False);
            Reset(); Unbounded(); Unbounded(); Assert.That((bool)eligible.Invoke(_terrain, null), Is.True);
        }
    }
}
#endif
