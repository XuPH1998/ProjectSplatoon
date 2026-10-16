#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Public construction, support attack, native collision and actual default
    // scheduling. Reflection only reads naturally created pending windows.
    // This is a functional contract, not the 20k/32m Player timing acceptance.
    public sealed class BlockGravityDefaultWindowTests
    {
        const int Height = 256;
        GameObject _root;
        BlockTerrain _terrain;
        Vector2Int _column;
        double _now;
        int _supportCallbacks, _commits;
        bool _largePublishedGravityWindow;
        readonly Dictionary<BlockWorld, int> _observedGravityWindows = new();
        readonly Dictionary<int, int> _publishedFrameKeys = new();
        BlockWorld _seedSource;
        byte[] _seedBytes;

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Default gravity window public contract");
            var layout = new GameObject("Inactive registered map layout");
            layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64); arena.SpawnPoints = Array.Empty<Transform>();
            var floor = new GameObject("Real native floor with top zero");
            floor.transform.SetParent(layout.transform, false); floor.transform.position = new Vector3(0, -.1f, 0);
            var collider = floor.AddComponent<BoxCollider>(); collider.size = new Vector3(4, .2f, 4);
            _terrain = _root.AddComponent<BlockTerrain>();
            // Exercise the production default. Do not select CompatibilityEight
            // or increase the production timer/check allowance to get a witness.
            _terrain.Initialize(arena);
            Assert.That(_terrain.PublicationPolicy.GravityKeys, Is.EqualTo(32));
            Assert.That(_terrain.PublicationPolicy.DestructionKeys, Is.EqualTo(32));
            Assert.That(_terrain.PublicationPolicy.OrdinaryAttackKeys, Is.EqualTo(8));
            floor.transform.SetParent(_root.transform, true); Physics.SyncTransforms();
            Assert.That(floor.activeInHierarchy, Is.True);
            Assert.That(collider.bounds.max.y, Is.Zero.Within(.000001));
            _now = 10; _supportCallbacks = _commits = 0; _largePublishedGravityWindow = false;
            _observedGravityWindows.Clear(); _publishedFrameKeys.Clear(); _seedSource = null; _seedBytes = null;
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null) _terrain.PublishedCommit -= ObserveCommit;
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PositiveCornerDefaultWindowPublishesMoreThanEightKeysAndSettlesTheWholeMixedColumn()
            => Run(new Vector2Int(15, 15));

        [UnityTest, Timeout(600000)]
        public IEnumerator NegativeCornerDefaultWindowPublishesMoreThanEightKeysAndSettlesTheWholeMixedColumn()
            => Run(new Vector2Int(-1, -1));

        IEnumerator Run(Vector2Int column)
        {
            _column = column;
            int supportBuildCallbacks = 0;
            _terrain.QueueBuild(BuildPoint(Cell(0)), Vector3.up, 2, 1, 0, false, result =>
            {
                Assert.That(++supportBuildCallbacks, Is.EqualTo(1));
                Assert.That(result.Added, Is.EqualTo(1)); Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
            });
            yield return PumpUntil(Quiet);
            Assert.That(supportBuildCallbacks, Is.EqualTo(1));
            for (int first = 1; first <= Height; first += 16)
            {
                int start = first, callbacks = 0;
                _terrain.QueueBuild(BuildPoint(Cell(start)), Vector3.up, SourceOwner(start), 16, 0, false, result =>
                {
                    Assert.That(++callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(16));
                    Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                });
                yield return PumpUntil(Quiet);
                Assert.That(callbacks, Is.EqualTo(1));
                for (int y = start; y < start + 16; y++) CheckCell(Cell(y), SourceOwner(y));
            }
            Assert.That(_terrain.PinkCount, Is.EqualTo(Height / 2));
            Assert.That(_terrain.BlueCount, Is.EqualTo(Height / 2 + 1));
            _seedSource = _terrain.World; _seedBytes = _seedSource.EncodeSnapshot();
            _terrain.PublishedCommit += ObserveCommit;
            _terrain.QueueDestroy(BlockWorld.Center(Cell(0)), .0001f, 0, 1, true, result =>
            {
                Assert.That(++_supportCallbacks, Is.EqualTo(1));
                Assert.That(result.Added, Is.Zero); Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                Assert.That(result.RemovedFriendly, Is.Zero);
            });
            yield return PumpUntil(() => _supportCallbacks == 1 && Quiet());
            Assert.That(_largePublishedGravityWindow, Is.True,
                "A real default gravity window must publish more than eight complete data/halo keys; profile constants alone do not prove this.");
            Assert.That(_commits, Is.GreaterThan(0)); Assert.That(_terrain.FallingCount, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(Height / 2)); Assert.That(_terrain.BlueCount, Is.EqualTo(Height / 2));
            Assert.That(_terrain.World.Chunks.Values.Sum(chunk => chunk.Count), Is.EqualTo(Height));
            // Every initial cell falls exactly one cell to the real floor. The
            // independently calculated source owner remains attached to its cell.
            for (int y = 0; y < Height; y++)
            {
                CheckCell(Cell(y), SourceOwner(y + 1));
                Assert.That(Physics.Raycast(BlockWorld.Center(Cell(y)) + Vector3.right * .2f,
                    Vector3.left, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True,
                    $"The settled published column needs real collision at y={y}.");
            }
            Assert.That(_terrain.World.TryGet(Cell(Height), out _), Is.False);
            CollectionAssert.AreEqual(_seedBytes, _seedSource.EncodeSnapshot());
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(_terrain.CommandsQueued));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        bool Quiet() => _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0
            && _terrain.CommandsCompleted == _terrain.CommandsQueued;

        IEnumerator PumpUntil(Func<bool> complete)
        {
            double until = Time.realtimeSinceStartupAsDouble + 240;
            for (int frame = 0; frame < 12000; frame++)
            {
                ObservePending();
                long before = _terrain.CommandWorkChecks + _terrain.GravityWorkChecks;
                _now += .016; _terrain.Tick(true, _now, .016f);
                Assert.That(_terrain.CommandWorkChecks + _terrain.GravityWorkChecks - before, Is.InRange(0L, 4096L));
                ObservePending();
                Assert.That(_terrain.FailedTransactions, Is.Zero);
                if (_supportCallbacks != 0)
                {
                    Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(Height),
                        "Detachment, moving pieces and landing must preserve the entire surviving population.");
                    Assert.That(_terrain.PinkCount, Is.EqualTo(Height / 2)); Assert.That(_terrain.BlueCount, Is.EqualTo(Height / 2));
                }
                if (complete()) yield break;
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until), "Public terrain work did not drain.");
                yield return null;
            }
            Assert.Fail("Public terrain work exceeded the bounded contract pumping allowance.");
        }

        void ObservePending()
        {
            var pending = Field<object>(_terrain, "_pending");
            if (pending == null || Field<bool>(pending, "Remote")) return;
            var window = Field<BlockPublicationWindow>(pending, "Window");
            Assert.That(window, Is.Not.Null);
            if (!window.Includes(BlockPublicationOperation.Gravity) || window.Includes(BlockPublicationOperation.OrdinaryAttack)) return;
            var changed = Field<Vector3Int[]>(pending, "Changed");
            Assert.That(window.MaximumChangedKeys, Is.EqualTo(32));
            Assert.That(changed.Length, Is.LessThanOrEqualTo(32));
            var next = Field<BlockWorld>(pending, "Next");
            if (changed.Length > 8 && next.Falling.Values.Count(run => run.Cohort != null) >= 2)
                _observedGravityWindows[next] = changed.Length;
        }

        void ObserveCommit(BlockTerrainCommit commit)
        {
            _commits++;
            Assert.That(commit.Revision, Is.EqualTo(_terrain.World.Revision));
            Assert.That(_terrain.PinkCount, Is.EqualTo(_terrain.World.PinkCount));
            Assert.That(_terrain.BlueCount, Is.EqualTo(_terrain.World.BlueCount));
            _publishedFrameKeys.TryGetValue(Time.frameCount, out int keys);
            keys += commit.ChangedChunks.Length; _publishedFrameKeys[Time.frameCount] = keys;
            Assert.That(keys, Is.LessThanOrEqualTo(32), "The default includes all complete changed/neighbor keys per frame.");
            if (_observedGravityWindows.TryGetValue(_terrain.World, out int witnessed))
            {
                Assert.That(commit.ChangedChunks.Length, Is.EqualTo(witnessed));
                _largePublishedGravityWindow = true;
            }
            if (_seedSource != null) CollectionAssert.AreEqual(_seedBytes, _seedSource.EncodeSnapshot());
        }

        void CheckCell(Vector3Int cell, byte expectedOwner)
        {
            Assert.That(_terrain.World.TryGet(cell, out var value), Is.True, $"Missing stable cell {cell}.");
            Assert.That(value.Owner, Is.EqualTo(expectedOwner)); Assert.That(value.Durability, Is.EqualTo(100));
        }
        static T Field<T>(object instance, string name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Read-only natural witness {name} must exist.");
            return (T)field.GetValue(instance);
        }
        Vector3Int Cell(int y) => new(_column.x, y, _column.y);
        static byte SourceOwner(int y) => (byte)(1 + ((y - 1) / 16) % 2);
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
    }
}
#endif
