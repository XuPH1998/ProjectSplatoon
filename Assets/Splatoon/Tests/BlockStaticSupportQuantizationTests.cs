#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Native Collider geometry and public authority commands drive every phase.
    // No synthetic falling/cohort state, proof helper calls or private-field writes.
    // One Play Mode lifecycle covers all four independent map controls.
    public sealed class BlockStaticSupportQuantizationTests
    {
        const float Delta = 1f / 60;
        const int StableObservationFrames = 120;
        const double PhaseSeconds = 40;
        static readonly Vector2Int Column = new(2, 2);
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _platform;
        Mesh _slopeMesh;
        double _now, _phaseWallUntil, _phaseSimulationUntil;
        string _phase;
        int _callbacks, _mutationEvents, _builtPink, _builtBlue;

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            DisposePhase();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator NativeNonAlignedPlatformSlopeAlignedBoundaryAndRemovedSupportKeepQuantizedGravityConsistent()
        {
            StartPhase("non-aligned native platform", .301f, false);
            AssertNativeHeight(Column, .301f, _platform);
            Build(Cell(Column, 7), 2);
            yield return PumpUntil(() => _callbacks == 1 && Quiet());
            CheckCell(Cell(Column, 4), 2);
            Assert.That(_terrain.World.TryGet(Cell(Column, 7), out _), Is.False);
            var firstWorld = _terrain.World; var firstBytes = firstWorld.EncodeSnapshot();
            Build(Cell(Column, 5), 1);
            yield return PumpUntil(() => _callbacks == 2 && Quiet());
            AssertPopulation(1, 1, 2);
            CheckCell(Cell(Column, 4), 2); CheckCell(Cell(Column, 5), 1);
            CollectionAssert.AreEqual(firstBytes, firstWorld.EncodeSnapshot(), "Published source worlds remain immutable.");
            yield return ObserveQuiet(1, 1, 2);
            Debug.Log("[BlockStaticSupportQuantization] phase=non-aligned-platform status=passed");

            StartPhase("aligned native platform rejects a whole extra row", .300f, false);
            AssertNativeHeight(Column, .300f, _platform);
            // This source center is .150m above the native top. Merely extending
            // the short ray would incorrectly stabilize row 4 instead of row 3.
            Build(Cell(Column, 4), 2);
            yield return PumpUntil(() => _callbacks == 1 && Quiet());
            CheckCell(Cell(Column, 3), 2);
            Assert.That(_terrain.World.TryGet(Cell(Column, 4), out _), Is.False);
            Build(Cell(Column, 4), 1);
            yield return PumpUntil(() => _callbacks == 2 && Quiet());
            AssertPopulation(1, 1, 2);
            CheckCell(Cell(Column, 3), 2); CheckCell(Cell(Column, 4), 1);
            yield return ObserveQuiet(1, 1, 2);
            Debug.Log("[BlockStaticSupportQuantization] phase=aligned-extra-row status=passed");

            StartPhase("non-aligned native slope across positive and negative chunk boundaries", null, true);
            var columns = new[] { new Vector2Int(-1, 4), new Vector2Int(0, 4), new Vector2Int(15, 4), new Vector2Int(16, 4) };
            for (int i = 0; i < columns.Length; i++)
            {
                // The authored slope is y=2.076+.5*z. All sampled z centers
                // are .45m, so the independently known native height is 2.301m.
                AssertNativeHeight(columns[i], 2.301f, null);
                Build(Cell(columns[i], 29), (byte)(1 + (i & 1)));
            }
            yield return PumpUntil(() => _callbacks == 4 && Quiet());
            AssertPopulation(2, 2, 4);
            for (int i = 0; i < columns.Length; i++)
            {
                CheckCell(Cell(columns[i], 24), (byte)(1 + (i & 1)));
                Assert.That(_terrain.World.TryGet(Cell(columns[i], 29), out _), Is.False);
                Assert.That(CollisionAt(Cell(columns[i], 29)), Is.False, "Source collision must be withdrawn after actual falling.");
            }
            yield return ObserveQuiet(2, 2, 4);
            Debug.Log("[BlockStaticSupportQuantization] phase=non-aligned-slope-chunk-boundaries status=passed");

            StartPhase("disabling real map support releases unanchored settled cells", .301f, false);
            Build(Cell(Column, 7), 2);
            yield return PumpUntil(() => _callbacks == 1 && Quiet());
            Build(Cell(Column, 5), 1);
            yield return PumpUntil(() => _callbacks == 2 && Quiet());
            AssertPopulation(1, 1, 2); CheckCell(Cell(Column, 4), 2); CheckCell(Cell(Column, 5), 1);
            var beforeRemoval = _terrain.World; var beforeRemovalBytes = beforeRemoval.EncodeSnapshot();
            _platform.enabled = false; Physics.SyncTransforms();
            AssertNativeHeight(Column, 0f, null);
            // A real new build queues the same column for a fresh support scan.
            // All previous cells stay unanchored; no synthetic scan is injected.
            Build(Cell(Column, 6), 2);
            yield return PumpUntil(() => _callbacks == 3 && Quiet());
            AssertPopulation(1, 2, 3);
            CheckCell(Cell(Column, 0), 2); CheckCell(Cell(Column, 1), 1); CheckCell(Cell(Column, 2), 2);
            for (int y = 4; y <= 7; y++)
            {
                Assert.That(_terrain.World.TryGet(Cell(Column, y), out _), Is.False);
                Assert.That(CollisionAt(Cell(Column, y)), Is.False, "Removed native support cannot leave a ghost stable character collider.");
            }
            CollectionAssert.AreEqual(beforeRemovalBytes, beforeRemoval.EncodeSnapshot());
            yield return ObserveQuiet(1, 2, 3);
            Debug.Log("[BlockStaticSupportQuantization] phase=disabled-real-platform status=passed");
        }

        void StartPhase(string name, float? platformTop, bool slope)
        {
            DisposePhase(); _phase = name; _now = 10; _callbacks = _mutationEvents = _builtPink = _builtBlue = 0;
            _phaseWallUntil = Time.realtimeSinceStartupAsDouble + PhaseSeconds;
            _phaseSimulationUntil = _now + PhaseSeconds;
            _root = new GameObject("Native support quantization: " + name);
            var layout = new GameObject("Inactive registered map layout");
            layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64); arena.SpawnPoints = Array.Empty<Transform>();
            MakeBox(layout.transform, "Baseline map floor top zero", new Vector3(0, -.05f, 0), new Vector3(4, .1f, 4));
            if (platformTop.HasValue)
                _platform = MakeBox(layout.transform, "Native map platform", new Vector3(.25f, platformTop.Value - .05f, .25f), new Vector3(2, .1f, 2));
            if (slope)
            {
                var surface = new GameObject("Real unquantized slope MeshCollider");
                surface.transform.SetParent(layout.transform, false); surface.transform.position = new Vector3(0, 2.076f, 0);
                _slopeMesh = new Mesh { name = "Authored slope y=2.076+.5z" };
                _slopeMesh.vertices = new[] { new Vector3(-2, -.5f, -1), new Vector3(-2, .5f, 1), new Vector3(2, .5f, 1), new Vector3(2, -.5f, -1) };
                _slopeMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; _slopeMesh.RecalculateNormals(); _slopeMesh.RecalculateBounds();
                surface.AddComponent<MeshCollider>().sharedMesh = _slopeMesh;
            }
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena);
            foreach (var collider in layout.GetComponentsInChildren<Collider>(true)) collider.transform.SetParent(_root.transform, true);
            _terrain.MutationCompleted += ObserveMutation;
            Physics.SyncTransforms();
        }

        static BoxCollider MakeBox(Transform parent, string name, Vector3 position, Vector3 size)
        {
            var map = new GameObject(name); map.transform.SetParent(parent, false); map.transform.position = position;
            var collider = map.AddComponent<BoxCollider>(); collider.size = size; return collider;
        }
        void Build(Vector3Int cell, byte owner)
        {
            _terrain.QueueBuild(BlockWorld.Center(cell) - Vector3.up * .051f, Vector3.up, owner, 1, 0, false, result =>
            {
                _callbacks++;
                Assert.That(result.Added, Is.EqualTo(1), _phase + ": each real command must add exactly one cell.");
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                if (owner == 1) _builtPink++; else _builtBlue++;
            });
        }
        void ObserveMutation(BlockMutationResult result, double _)
        {
            _mutationEvents++;
            Assert.That(result.Added, Is.EqualTo(1)); Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
        }
        IEnumerator PumpUntil(Func<bool> done)
        {
            while (!done())
            {
                AssertPhaseBudget(); _now += Delta; _terrain.Tick(true, _now, Delta);
                Assert.That(_terrain.FailedTransactions, Is.Zero, _phase);
                yield return null;
                AssertConservationPrefix();
            }
            AssertPhaseBudget();
        }
        IEnumerator ObserveQuiet(int pink, int blue, int commands)
        {
            Assert.That(Quiet(), Is.True); uint revision = _terrain.Revision;
            var snapshot = _terrain.World.EncodeSnapshot();
            for (int i = 0; i < StableObservationFrames; i++)
            {
                AssertPhaseBudget(); _now += Delta; _terrain.Tick(true, _now, Delta); yield return null;
                Assert.That(Quiet(), Is.True, _phase + ": a settled native landing cannot start a fresh detach/settle cycle.");
                Assert.That(_terrain.Revision, Is.EqualTo(revision), _phase + ": no revision churn after normal completion.");
                AssertPopulation(pink, blue, commands);
            }
            CollectionAssert.AreEqual(snapshot, _terrain.World.EncodeSnapshot());
        }
        void AssertPhaseBudget()
        {
            Assert.That(_now, Is.LessThanOrEqualTo(_phaseSimulationUntil), _phase + ": 40-second simulation completion limit.");
            Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(_phaseWallUntil), _phase + ": 40-second native work limit.");
        }
        bool Quiet() => _terrain.PendingTransactions == 0 && _terrain.FallingCount == 0 && _terrain.UnstableColumns == 0
            && _terrain.CommandsCompleted == _terrain.CommandsQueued;
        void AssertConservationPrefix()
        {
            var occupied = _terrain.World.Chunks.Values.SelectMany(chunk => chunk.Cells)
                .Concat(_terrain.World.Falling.Values.SelectMany(run => run.Cells)).Where(cell => cell.Occupied).ToArray();
            Assert.That(_terrain.PinkCount, Is.EqualTo(_builtPink)); Assert.That(_terrain.BlueCount, Is.EqualTo(_builtBlue));
            Assert.That(occupied.Count(cell => cell.Owner == 1), Is.EqualTo(_builtPink));
            Assert.That(occupied.Count(cell => cell.Owner == 2), Is.EqualTo(_builtBlue));
            Assert.That(occupied.All(cell => !cell.Anchored && cell.Durability == 100), Is.True);
            Assert.That(_mutationEvents, Is.EqualTo(_callbacks), "Every published paid result occurs once; gravity has no separate result.");
        }
        void AssertPopulation(int pink, int blue, int commands)
        {
            AssertConservationPrefix();
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink)); Assert.That(_terrain.BlueCount, Is.EqualTo(blue));
            Assert.That(_terrain.World.Chunks.Values.Sum(chunk => chunk.Count), Is.EqualTo(pink + blue));
            Assert.That(_terrain.FallingCount, Is.Zero); Assert.That(_terrain.World.Falling.Count, Is.Zero);
            Assert.That(_callbacks, Is.EqualTo(commands)); Assert.That(_mutationEvents, Is.EqualTo(commands), "Gravity must produce no free attack result.");
            Assert.That(_terrain.CommandsQueued, Is.EqualTo(commands)); Assert.That(_terrain.CommandsCompleted, Is.EqualTo(commands));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }
        void CheckCell(Vector3Int cell, byte owner)
        {
            Assert.That(_terrain.World.TryGet(cell, out var own), Is.True, _phase + ": missing stable cell " + cell);
            Assert.That(own.Owner, Is.EqualTo(owner)); Assert.That(own.Anchored, Is.False);
            Assert.That(own.Durability, Is.EqualTo(100)); Assert.That(CollisionAt(cell), Is.True, _phase + ": native published collision must match " + cell);
        }
        static bool CollisionAt(Vector3Int cell) => Physics.Raycast(BlockWorld.Center(cell) + Vector3.forward * .2f,
            Vector3.back, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);
        void AssertNativeHeight(Vector2Int column, float expected, Collider expectedCollider)
        {
            var center = BlockWorld.Center(Cell(column, 0));
            Assert.That(Physics.Raycast(new Vector3(center.x, 4, center.z), Vector3.down, out var hit, 4.1f,
                PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer), QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.point.y, Is.EqualTo(expected).Within(.00001f)); Assert.That(hit.normal.y, Is.GreaterThan(.2f));
            if (expectedCollider != null) Assert.That(hit.collider, Is.SameAs(expectedCollider));
        }
        static Vector3Int Cell(Vector2Int column, int y) => new(column.x, y, column.y);
        void DisposePhase()
        {
            if (_terrain != null) _terrain.MutationCompleted -= ObserveMutation;
            if (_root != null) Object.DestroyImmediate(_root);
            if (_slopeMesh != null) Object.DestroyImmediate(_slopeMesh);
            _root = null; _terrain = null; _platform = null; _slopeMesh = null;
        }
    }
}
#endif
