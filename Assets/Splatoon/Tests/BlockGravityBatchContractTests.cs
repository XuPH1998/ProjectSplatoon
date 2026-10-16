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
    // Original-size production publication contract, not a Player timing substitute.
    // Deliberately requires the frozen cohort activation-barrier design.
    public sealed class BlockGravityBatchContractTests
    {
        GameObject _root;
        BlockTerrain _terrain;
        BoxCollider _floor;
        readonly Dictionary<Vector2Int, ColumnTrace> _columns = new();
        readonly HashSet<ulong> _seenRuns = new();
        readonly Dictionary<int, int> _frameChunks = new();
        int _top, _floorCell, _supportRemoved, _publications;
        double _begin, _supportClearMs;
        bool[] _callbacks;

        sealed class ColumnTrace
        {
            public readonly HashSet<int> Sources = new();
            public bool Activated;
            public double Epoch;
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Gravity batch original-scale contract");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            var floor = new GameObject("Physical registered static floor at 3 metres");
            floor.transform.SetParent(_root.transform, false);
            floor.transform.position = new Vector3(0, 2.9f, 0);
            var collider = floor.AddComponent<BoxCollider>(); collider.size = new Vector3(2, .2f, 2); _floor = collider;
            var staticColliders = (HashSet<Collider>)typeof(BlockTerrain).GetField("_staticColliders", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_terrain);
            staticColliders.Add(collider); Physics.SyncTransforms();
            _columns.Clear(); _seenRuns.Clear(); _frameChunks.Clear();
            _supportRemoved = _publications = 0; _supportClearMs = -1; _callbacks = new bool[100];
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null) _terrain.PublishedCommit -= Observe;
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // Allow Editor enter/exit domain reload in the framework timeout.
        // The unchanged 180-second Run boundary and formal 500ms Player gate still apply.
        [UnityTest, Timeout(600000)] public IEnumerator HundredColumnsTo32MetresShareOneTrajectoryAndPreserveAll28900Survivors()
            => Run(320);

        [UnityTest, Timeout(600000)] public IEnumerator HundredColumnTwentyThousandCellCascadeSharesOneTrajectoryAndPreservesAllSurvivors()
            => Run(231);

        [UnityTest, Timeout(600000)] public IEnumerator HundredGroundColumnsHave32MetreBodiesAndPreserveAll31900Survivors()
            => Run(320, 0);

        IEnumerator Run(int top, int floorCell = 30)
        {
            _top = top; _floorCell = floorCell; int perColumn = top - floorCell - 1, survivors = perColumn * 100;
            float floorMeters = floorCell * BlockWorld.CellSize;
            _floor.transform.position = new Vector3(0, floorMeters - _floor.size.y * .5f, 0);
            _floor.name = $"Physical registered static floor at {floorMeters} metres";
            Physics.SyncTransforms();
            Assert.That(_floor.bounds.max.y, Is.EqualTo(floorMeters).Within(.0001));
            int initialBottom = int.MaxValue, initialTop = int.MinValue;
            var seed = new BlockWorld(new Vector2(32, 64));
            for (int z = -5; z < 5; z++) for (int x = -5; x < 5; x++)
            {
                _columns.Add(new Vector2Int(x, z), new ColumnTrace());
                int columnCells = 0;
                for (int y = floorCell; y < top; y++)
                {
                    var cell = new Vector3Int(x, y, z);
                    Assert.That(seed.Set(cell, BlockCell.Create(2, y == floorCell)), Is.True, "every original fixture cell is actually stored");
                    columnCells++; initialBottom = Math.Min(initialBottom, cell.y); initialTop = Math.Max(initialTop, cell.y + 1);
                }
                Assert.That(columnCells, Is.EqualTo(top - floorCell));
                if (floorCell == 0)
                {
                    Assert.That(columnCells, Is.EqualTo(320));
                    var origin = new Vector3((x + .5f) * BlockWorld.CellSize, BlockWorld.Top + 1, (z + .5f) * BlockWorld.CellSize);
                    Assert.That(_floor.Raycast(new Ray(origin, Vector3.down), out var floorHit, BlockWorld.Top + 2), Is.True);
                    Assert.That(floorHit.point.y, Is.Zero.Within(.0001)); Assert.That(floorHit.normal.y, Is.EqualTo(1).Within(.001));
                }
            }
            Assert.That(seed.BlueCount, Is.EqualTo(survivors + 100));
            if (floorCell == 0)
            {
                Assert.That(top, Is.EqualTo(320)); Assert.That(_columns.Count, Is.EqualTo(100));
                Assert.That(seed.PinkCount, Is.Zero); Assert.That(seed.BlueCount, Is.EqualTo(32000));
                Assert.That(survivors, Is.EqualTo(31900));
                Assert.That(initialBottom * BlockWorld.CellSize, Is.Zero);
                Assert.That(initialTop * BlockWorld.CellSize, Is.EqualTo(32));
                Assert.That((initialTop - initialBottom) * BlockWorld.CellSize, Is.EqualTo(32), "actual occupied tower body height, not just its top coordinate");
            }
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 2000 && _terrain.PendingTransactions != 0; tick++)
            { _terrain.Tick(false, tick * .016, .016f); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.BlueCount, Is.EqualTo(survivors + 100));
            _terrain.PublishedCommit += Observe;
            _begin = Time.realtimeSinceStartupAsDouble;
            int index = 0;
            // All original 100 support attacks are queued before the first authority Tick.
            for (int z = -5; z < 5; z++) for (int x = -5; x < 5; x++)
            {
                int callbackIndex = index++;
                _terrain.QueueDestroy(BlockWorld.Center(new Vector3Int(x, floorCell, z)), .0001f, 0, 1, true,
                    result =>
                    {
                        Assert.That(_callbacks[callbackIndex], Is.False, "each original support has exactly one callback");
                        _callbacks[callbackIndex] = true;
                        Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(result.RemovedFriendly, Is.Zero);
                        _supportRemoved += result.RemovedEnemy;
                    }, 1, 0);
            }
            double previous = 20;
            for (int tick = 0; tick < 10000; tick++)
            {
                double now = 20 + Time.realtimeSinceStartupAsDouble - _begin;
                long commandChecks = _terrain.CommandWorkChecks, gravityChecks = _terrain.GravityWorkChecks;
                _terrain.Tick(true, now, (float)Math.Max(0, now - previous)); previous = now;
                Assert.That(_terrain.CommandWorkChecks - commandChecks + _terrain.GravityWorkChecks - gravityChecks,
                    Is.InRange(0L, 4096L), "all command and gravity work retains the original checks cap per authority opportunity");
                Assert.That(_terrain.PinkCount, Is.Zero);
                Assert.That(_terrain.BlueCount, Is.EqualTo(survivors + 100 - _supportRemoved), _terrain.BlueCount == survivors + 100 - _supportRemoved ? "Exact survivor count." : $"top={top}; storedTop={_top}; floorCell={floorCell}; perColumn={perColumn}; survivors={survivors}; removed={_supportRemoved}; callbacks={_callbacks.Count(v => v)}; revision={_terrain.Revision}; pending={_terrain.PendingTransactions}; failed={_terrain.FailedTransactions}; head={JsonUtility.ToJson(_terrain.InspectCommandHead())}");
                CheckTrajectories();
                if (_supportRemoved == 100 && _terrain.LossOfSupportReady && _supportClearMs < 0)
                    _supportClearMs = (Time.realtimeSinceStartupAsDouble - _begin) * 1000;
                if (_supportRemoved == 100 && _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0) break;
                Assert.That(Time.realtimeSinceStartupAsDouble - _begin, Is.LessThan(180), "bounded Editor regression timeout is not a formal 500ms timing gate");
                yield return null;
            }
            Assert.That(_callbacks.All(v => v), Is.True); Assert.That(_supportRemoved, Is.EqualTo(100));
            Assert.That(_supportClearMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(_publications, Is.GreaterThan(0)); Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.UnstableColumns, Is.Zero); Assert.That(_terrain.FallingCount, Is.Zero);
            Assert.That(_terrain.BlueCount, Is.EqualTo(survivors)); Assert.That(_terrain.World.Chunks.Values.Sum(c => c.Count), Is.EqualTo(survivors));
            foreach (var pair in _columns)
            {
                Assert.That(pair.Value.Sources.Count, Is.EqualTo(perColumn), "every original survivor is detached exactly once");
                Assert.That(pair.Value.Activated, Is.True, "all original continuous columns activate as complete cohorts");
                for (int y = floorCell; y < top - 1; y++)
                    Assert.That(_terrain.World.TryGet(new Vector3Int(pair.Key.x, y, pair.Key.y), out var value)
                        && value.Owner == 2 && !value.Anchored, Is.True, "exact ordered final survivor set");
                Assert.That(_terrain.World.TryGet(new Vector3Int(pair.Key.x, top - 1, pair.Key.y), out _), Is.False);
                var topCell = new Vector3Int(pair.Key.x, top - 2, pair.Key.y);
                Assert.That(Physics.Raycast(BlockWorld.Center(topCell) + Vector3.up * .5f, Vector3.down,
                    out var hit, .6f, 1 << BlockTerrain.TerrainLayer), Is.True);
                Assert.That(hit.collider, Is.TypeOf<MeshCollider>()); Assert.That(hit.normal.y, Is.EqualTo(1).Within(.001));
                Assert.That(hit.point.y, Is.EqualTo((top - 1) * BlockWorld.CellSize).Within(.001));
                Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
                Assert.That(contact.Cell, Is.EqualTo(topCell)); Assert.That(contact.Owner, Is.EqualTo(2)); Assert.That(contact.Stable, Is.True);
                Assert.That(contact.Revision, Is.EqualTo(_terrain.ChunkRevision(BlockWorld.ChunkAt(topCell))));
            }
            Debug.Log($"[GRAVITY-CONTRACT-EDITOR-ONLY] floorMeters={floorMeters} initialBottom={initialBottom * BlockWorld.CellSize} initialTop={initialTop * BlockWorld.CellSize} bodyHeight={(initialTop - initialBottom) * BlockWorld.CellSize} initialCells={survivors + 100} top={top} survivors={survivors} publications={_publications} supportClearMs={_supportClearMs:F3}; not formal Player timing evidence");
            for (int i = 0; i < 20; i++)
            { _terrain.Tick(true, previous + .016 * (i + 1), .016f); yield return null; }
            Assert.That(_supportRemoved, Is.EqualTo(100)); Assert.That(_terrain.BlueCount, Is.EqualTo(survivors));
        }

        void Observe(BlockTerrainCommit commit)
        {
            _publications++;
            Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
            _frameChunks.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
            _frameChunks[Time.frameCount] = count; Assert.That(count, Is.LessThanOrEqualTo(8));
            foreach (var run in _terrain.World.Falling.Values)
            {
                if (!_seenRuns.Add(run.Id)) continue;
                Assert.That(run.Cells.Length, Is.InRange(1, 16), "motion cohorts retain bounded storage pieces");
                var column = new Vector2Int(run.Bottom.x, run.Bottom.z);
                Assert.That(_columns.ContainsKey(column), Is.True);
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    if (!run.Cells[i].Occupied) continue;
                    Assert.That(run.Cells[i].Owner, Is.EqualTo(2)); Assert.That(run.Cells[i].Anchored, Is.False);
                    int y = run.Bottom.y + i; Assert.That(y, Is.InRange(_floorCell + 1, _top - 1));
                    Assert.That(_columns[column].Sources.Add(y), Is.True, "source membership cannot detach twice");
                }
            }
            CheckTrajectories();
        }

        void CheckTrajectories()
        {
            int expected = _top - _floorCell - 1;
            var active = _terrain.World.Falling.Values.GroupBy(r => new Vector2Int(r.Bottom.x, r.Bottom.z))
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Bottom.y).ToArray());
            foreach (var pair in _columns)
            {
                var trace = pair.Value;
                if (!active.TryGetValue(pair.Key, out var runs)) continue;
                if (trace.Sources.Count < expected)
                {
                    foreach (var run in runs) Assert.That(run.Offset(_terrain.SimulationTime), Is.Zero.Within(1e-6),
                        "no independently moving lower piece while the continuous cohort still has stable source members");
                }
                else
                {
                    if (!trace.Activated)
                    {
                        Assert.That(runs.Sum(r => r.Cells.Count(c => c.Occupied)), Is.EqualTo(expected),
                            "complete activation occurs before any cohort member settles");
                        trace.Epoch = runs[0].StartedAt; trace.Activated = true;
                    }
                    float offset = runs[0].Offset(_terrain.SimulationTime), speed = runs[0].InitialSpeed;
                    foreach (var run in runs)
                    {
                        Assert.That(run.StartedAt, Is.EqualTo(trace.Epoch), "one exact publication epoch across all surviving pieces");
                        Assert.That(run.InitialSpeed, Is.EqualTo(speed));
                        Assert.That(run.Offset(_terrain.SimulationTime), Is.EqualTo(offset).Within(1e-6), "one common trajectory and landing clamp");
                    }
                }
                for (int i = 1; i < runs.Length; i++)
                {
                    var lower = runs[i - 1]; var upper = runs[i];
                    int last = Array.FindLastIndex(lower.Cells, c => c.Occupied), first = Array.FindIndex(upper.Cells, c => c.Occupied);
                    Assert.That(last, Is.GreaterThanOrEqualTo(0)); Assert.That(first, Is.GreaterThanOrEqualTo(0));
                    float lowerTop = (lower.Bottom.y + last + 1) * BlockWorld.CellSize - lower.Offset(_terrain.SimulationTime);
                    float upperBottom = (upper.Bottom.y + first) * BlockWorld.CellSize - upper.Offset(_terrain.SimulationTime);
                    Assert.That(lowerTop, Is.LessThanOrEqualTo(upperBottom + .0001f), "published occupied spans cannot pass or interpenetrate");
                }
            }
        }
    }

    public sealed class BlockGravityDestinationAlignmentContractTests
    {
        [Test] public void SixteenCellPieceFitsEveryLocalDestinationAlignmentUsingActualIncludeCell()
        {
            var include = typeof(BlockTerrain).GetMethod("IncludeCell", BindingFlags.Static | BindingFlags.NonPublic);
            int maximum = 0;
            for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++)
            {
                var keys = new HashSet<Vector3Int>();
                for (int i = 0; i < 16; i++) include.Invoke(null, new object[] { keys, new Vector3Int(x, y + i, z) });
                maximum = Math.Max(maximum, keys.Count);
                Assert.That(keys.Count, Is.LessThanOrEqualTo(6), $"exact source-independent destination footprint at {x},{y},{z}");
            }
            Assert.That(maximum, Is.EqualTo(6), "all 4096 local alignments include the true worst case, below the unchanged union budget8");
        }
    }
}
#endif
