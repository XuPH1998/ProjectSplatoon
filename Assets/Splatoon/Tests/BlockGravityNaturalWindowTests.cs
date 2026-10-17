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
    // Reports-only source preparation. Every tested body and attack is public
    // QueueBuild/QueueDestroy/Tick work. Reflection only reads natural witnesses.
    // Missing a real target window fails; these are not Player timing tests.
    public sealed class BlockGravityNaturalWindowTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        GameObject _root;
        BlockTerrain _terrain;
        Vector2Int _column;
        int _height, _supportCallbacks, _publications;
        double _now;
        bool _supportDeletionPublished, _activated;
        double _activationEpoch;
        readonly HashSet<int> _detachedSources = new();
        readonly HashSet<ulong> _seenRuns = new();
        readonly HashSet<object> _seenPending = new();
        readonly List<Filler> _fillers = new();
        readonly List<FrozenWorld> _retained = new();
        readonly List<WindowWitness> _windows = new();
        readonly List<BlockMutationResult> _outcomes = new();
        readonly Dictionary<int, int> _frameChunks = new();

        sealed class Filler
        {
            public Vector3Int Cell;
            public int Callbacks;
        }
        sealed class FrozenWorld
        {
            public BlockWorld World;
            public byte[] Bytes;
        }
        sealed class WindowWitness
        {
            public BlockWorld Next;
            public Vector3Int[] Keys;
            public int Pieces, ShortPrefixBottom = -1, ShortPrefixLength, DeferredCursor;
            public bool Published;
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            _historicalHeight = new HistoricalBlockWorldHeightScope();
            _root = new GameObject("Natural gravity window public command contract");
            var layout = new GameObject("Inactive layout used only for normal collider registration");
            layout.transform.SetParent(_root.transform, false);
            layout.SetActive(false); // Do not invoke the production map/Luban Awake validation.
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64);
            arena.SpawnPoints = Array.Empty<Transform>();
            var floor = new GameObject("Real static native floor with top exactly zero");
            floor.transform.SetParent(layout.transform, false);
            floor.transform.position = new Vector3(0, -.1f, 0);
            var collider = floor.AddComponent<BoxCollider>();
            collider.size = new Vector3(4, .2f, 4);
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight();
            _terrain.Initialize(arena); // Real public registration, no private HashSet edits.
            floor.transform.SetParent(_root.transform, true);
            Physics.SyncTransforms();
            Assert.That(floor.activeInHierarchy, Is.True);
            Assert.That(collider.bounds.max.y, Is.EqualTo(0).Within(.000001));
            _now = 10; _supportCallbacks = _publications = 0;
            _supportDeletionPublished = _activated = false; _activationEpoch = 0;
            _detachedSources.Clear(); _seenRuns.Clear(); _seenPending.Clear();
            _fillers.Clear(); _retained.Clear(); _windows.Clear(); _outcomes.Clear(); _frameChunks.Clear();
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            if (_terrain != null)
            {
                _terrain.PublishedCommit -= ObservePublication;
                _terrain.MutationCompleted -= ObserveMutation;
            }
            if (_root != null) Object.DestroyImmediate(_root);
            _historicalHeight?.Dispose(); _historicalHeight = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PublicBuildAndSupportAttackNaturallyDetachMultiplePiecesInOneExactEightKeyWindow()
            => Run(new Vector2Int(15, 15), 128, false);

        [UnityTest, Timeout(600000)]
        public IEnumerator NegativeChunkBoundaryShortPrefixPreservesEveryDeferredMixedOwnerSourceUntilCompleteActivation()
            => Run(new Vector2Int(-1, -1), 96, true);

        IEnumerator Run(Vector2Int column, int height, bool requireShortPrefix)
        {
            _column = column; _height = height;
            yield return SeedWithPublicCommands();
            Assert.That(_terrain.FallingCount, Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(Enumerable.Range(1, height).Count(y => SourceOwner(y) == 1)));
            Assert.That(_terrain.BlueCount, Is.EqualTo(1 + Enumerable.Range(1, height).Count(y => SourceOwner(y) == 2)));
            Retain(_terrain.World);
            _terrain.PublishedCommit += ObservePublication;
            _terrain.MutationCompleted += ObserveMutation;
            long queuedBefore = _terrain.CommandsQueued, completedBefore = _terrain.CommandsCompleted;
            _terrain.QueueDestroy(BlockWorld.Center(Cell(0)), .0001f, 0, 1, true, result =>
            {
                Assert.That(++_supportCallbacks, Is.EqualTo(1), "one original support attack has one final callback");
                Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                Assert.That(result.RemovedFriendly, Is.Zero); Assert.That(result.Added, Is.Zero);
            }, 1, 0);

            double begin = Time.realtimeSinceStartupAsDouble;
            bool complete = false;
            for (int frame = 0; frame < 6000; frame++)
            {
                // A real, stable ground build contributes its exact lower-y halo.
                // We neither edit changed keys nor hold/replace a production task.
                // Queue during a naturally observed real pending, or a public idle
                // opportunity; Tick may publish and stage the next gravity window
                // before returning, so waiting only for an empty queue misses it.
                // At most two real filler commands may still await their callback.
                bool naturalPending = Read<object>(_terrain, "_pending") != null;
                if ((naturalPending || _terrain.PendingTransactions == 0)
                    && _detachedSources.Count < height && _fillers.Count < 64
                    && _fillers.Count(f => f.Callbacks == 0) < 2)
                    QueueOneGroundFiller();
                TickAndObserve();
                if (_supportCallbacks == 1 && _terrain.PendingTransactions == 0
                    && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0)
                { complete = true; break; }
                Assert.That(Time.realtimeSinceStartupAsDouble - begin, Is.LessThan(180),
                    "bounded Editor fixture timeout, never the formal 500ms Player gate");
                yield return null;
            }
            Assert.That(complete, Is.True, "all public commands and gravity must naturally drain");
            Assert.That(_windows.Any(w => w.Published && w.Pieces >= 2 && w.Keys.Length == 8), Is.True,
                "missing natural exact eight-key, multi-piece pending/publication witness is a failure");
            if (requireShortPrefix)
                Assert.That(_windows.Any(w => w.Published && w.ShortPrefixLength > 0), Is.True,
                    "missing naturally refused-full-span/accepted-short-prefix witness is a failure");
            Assert.That(_detachedSources.Count, Is.EqualTo(height));
            CollectionAssert.AreEquivalent(Enumerable.Range(1, height).ToArray(), _detachedSources.ToArray(),
                "every original mixed-owner survivor detaches once, including every shortened-piece remainder");
            Assert.That(_activated, Is.True); Assert.That(_supportCallbacks, Is.EqualTo(1));
            Assert.That(_fillers.All(f => f.Callbacks == 1), Is.True);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_terrain.UnstableColumns, Is.Zero);
            Assert.That(_terrain.FallingCount, Is.Zero); Assert.That(_publications, Is.GreaterThan(0));
            Assert.That(_terrain.CommandsQueued - queuedBefore, Is.EqualTo(1 + _fillers.Count));
            Assert.That(_terrain.CommandsCompleted - completedBefore, Is.EqualTo(1 + _fillers.Count));
            Assert.That(_outcomes.Count, Is.EqualTo(1 + _fillers.Count), "gravity transfers generate no attack result");
            Assert.That(_outcomes.Sum(r => r.Added), Is.EqualTo(_fillers.Count));
            Assert.That(_outcomes.Sum(r => r.RemovedEnemy), Is.EqualTo(1));
            Assert.That(_outcomes.Sum(r => r.RemovedFriendly), Is.Zero);
            AssertFinalCellsAndNativeCollision();
            CheckRetainedWorlds();
            long completed = _terrain.CommandsCompleted;
            for (int i = 0; i < 12; i++) { TickAndObserve(); yield return null; }
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(completed));
            Assert.That(_supportCallbacks, Is.EqualTo(1));
            Assert.That(_fillers.All(f => f.Callbacks == 1), Is.True);
            Debug.Log($"[GRAVITY-NATURAL-WINDOW-EDITOR-ONLY] column={column}; survivors={height}; "
                + $"windows8={_windows.Count}; shortPrefixes={_windows.Count(w => w.ShortPrefixLength > 0)}; "
                + $"publications={_publications}; fillers={_fillers.Count}; no Player timing or last-column skip claim");
        }

        IEnumerator SeedWithPublicCommands()
        {
            int supportCallbacks = 0;
            _terrain.QueueBuild(BuildPoint(Cell(0)), Vector3.up, 2, 1, 0, false, result =>
            {
                Assert.That(++supportCallbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(1));
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
            });
            yield return WaitForQuiet();
            Assert.That(supportCallbacks, Is.EqualTo(1));
            for (int first = 1; first <= _height; first += 16)
            {
                int start = first, count = Math.Min(16, _height - first + 1), callbacks = 0;
                _terrain.QueueBuild(BuildPoint(Cell(start)), Vector3.up, SourceOwner(start), count, 0, false, result =>
                {
                    Assert.That(++callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(count));
                    Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                });
                yield return WaitForQuiet();
                Assert.That(callbacks, Is.EqualTo(1));
                for (int y = start; y < start + count; y++) AssertStableCell(_terrain.World, Cell(y), SourceOwner(y));
            }
            Assert.That(_terrain.World.Chunks.Values.Sum(c => c.Count), Is.EqualTo(_height + 1));
            for (int y = 0; y <= _height; y++) AssertStableCell(_terrain.World, Cell(y), y == 0 ? (byte)2 : SourceOwner(y));
            Assert.That(_terrain.World.TryGet(Cell(_height + 1), out _), Is.False);
            Assert.That(_terrain.CommandsQueued, Is.EqualTo(_terrain.CommandsCompleted));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        IEnumerator WaitForQuiet()
        {
            double begin = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 6000; i++)
            {
                _now += .016; _terrain.Tick(true, _now, .016f);
                if (_terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0) yield break;
                Assert.That(Time.realtimeSinceStartupAsDouble - begin, Is.LessThan(180));
                yield return null;
            }
            Assert.Fail("public seed commands did not naturally publish and become stable");
        }

        void QueueOneGroundFiller()
        {
            int i = _fillers.Count;
            var origin = BlockWorld.ChunkOrigin(BlockWorld.ChunkAt(Cell(0)));
            var filler = new Filler { Cell = new Vector3Int(origin.x + 2 + i % 8, 0, origin.z + 2 + i / 8) };
            Assert.That(filler.Cell.x == _column.x && filler.Cell.z == _column.y, Is.False);
            Assert.That(_terrain.World.TryGet(filler.Cell, out _), Is.False);
            _fillers.Add(filler);
            _terrain.QueueBuild(BuildPoint(filler.Cell), Vector3.up, 1, 1, 0, false, result =>
            {
                Assert.That(++filler.Callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(1));
                Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
            });
        }

        void TickAndObserve()
        {
            long before = _terrain.CommandWorkChecks + _terrain.GravityWorkChecks;
            _now += .016; _terrain.Tick(true, _now, .016f);
            Assert.That(_terrain.CommandWorkChecks + _terrain.GravityWorkChecks - before, Is.InRange(0L, 4096L));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            ObserveNaturalPending(); CheckPublishedState(); CheckRetainedWorlds();
        }

        void ObserveNaturalPending()
        {
            object pending = Read<object>(_terrain, "_pending");
            if (pending == null || !_seenPending.Add(pending)) return;
            if (Read<bool>(pending, "Remote")) return;
            var next = Read<BlockWorld>(pending, "Next");
            var keys = Read<Vector3Int[]>(pending, "Changed");
            var journal = Read<object>(pending, "Gravity");
            if (journal == null) return; // Other real private preparation is not this witness.
            Assert.That(Read<BlockWorld>(journal, "Next"), Is.SameAs(next));
            Assert.That(next, Is.Not.SameAs(_terrain.World));
            Assert.That(keys.Length, Is.LessThanOrEqualTo(8));
            foreach (var group in next.Cohorts.Values.Where(g => InMainColumn(g.Bottom)))
            {
                Assert.That(group.TopExclusive, Is.EqualTo(_height + 1), "a short storage piece never truncates the continuous body");
                if (group.DetachCursor >= group.TopExclusive) continue;
                Assert.That(group.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
                foreach (ulong id in group.MemberIds)
                    Assert.That(next.Falling[id].Offset(_now), Is.Zero.Within(1e-6), "no draft early activation with a remaining source tail");
                for (int y = group.DetachCursor; y < group.TopExclusive; y++) AssertStableCell(next, Cell(y), SourceOwner(y));
            }
            var fresh = next.Falling.Values.Where(r => InMainColumn(r.Bottom) && !_terrain.World.Falling.ContainsKey(r.Id))
                .OrderBy(r => r.Bottom.y).ToArray();
            if (keys.Length != 8 || fresh.Length < 2) return;
            Assert.That(fresh.All(r => r.Cohort != null && r.Cohort.Id == fresh[0].Cohort.Id), Is.True);
            foreach (var run in fresh) AssertRunCells(run);
            var exact = ActualMutationHalo(_terrain.World, next, fresh, null);
            CollectionAssert.AreEquivalent(exact.ToArray(), keys,
                "eight keys are the complete original source/side/vertical mutation halo, not an injected or omitted set");
            var witness = new WindowWitness { Next = next, Keys = (Vector3Int[])keys.Clone(), Pieces = fresh.Length };
            foreach (var shortRun in fresh)
            {
                if (shortRun.Cells.Length >= 16 || shortRun.Bottom.y + shortRun.Cells.Length >= _height + 1) continue;
                // Reconstruct the actual window before this piece independently,
                // including the real floor build/support attack and earlier pieces.
                var beforePiece = ActualMutationHalo(_terrain.World, next, fresh, shortRun);
                var full = new HashSet<Vector3Int>(beforePiece);
                int fullLength = Math.Min(16, _height + 1 - shortRun.Bottom.y);
                for (int i = 0; i < fullLength; i++) IncludeOracle(full, shortRun.Bottom + Vector3Int.up * i);
                if (full.Count <= 8) continue; // Not the footprint-constrained branch.
                var prefix = new HashSet<Vector3Int>(beforePiece);
                for (int i = 0; i < shortRun.Cells.Length; i++) IncludeOracle(prefix, shortRun.Bottom + Vector3Int.up * i);
                CollectionAssert.AreEquivalent(keys, prefix.ToArray());
                Assert.That(prefix.Count, Is.EqualTo(8)); Assert.That(fullLength, Is.EqualTo(16));
                int cursor = shortRun.Bottom.y + shortRun.Cells.Length;
                Assert.That(shortRun.Cohort.DetachCursor, Is.EqualTo(cursor), "the refused cell remains the true detach cursor");
                for (int y = cursor; y < _height + 1; y++) AssertStableCell(next, Cell(y), SourceOwner(y));
                foreach (ulong id in shortRun.Cohort.MemberIds)
                    Assert.That(next.Falling[id].Offset(_now), Is.Zero.Within(1e-6));
                witness.ShortPrefixBottom = shortRun.Bottom.y; witness.ShortPrefixLength = shortRun.Cells.Length;
                witness.DeferredCursor = cursor;
            }
            _windows.Add(witness);
        }

        HashSet<Vector3Int> ActualMutationHalo(BlockWorld before, BlockWorld after, FallingRun[] fresh, FallingRun excluded)
        {
            var keys = new HashSet<Vector3Int>();
            for (int y = 0; y <= _height; y++)
            {
                var cell = Cell(y);
                if (excluded != null && y >= excluded.Bottom.y && y < excluded.Bottom.y + excluded.Cells.Length) continue;
                if (!SameStableCell(before, after, cell)) IncludeOracle(keys, cell);
            }
            foreach (var filler in _fillers)
                if (!SameStableCell(before, after, filler.Cell)) IncludeOracle(keys, filler.Cell);
            foreach (var run in fresh)
                if (!ReferenceEquals(run, excluded))
                    for (int i = 0; i < run.Cells.Length; i++) if (run.Cells[i].Occupied) IncludeOracle(keys, run.Bottom + Vector3Int.up * i);
            return keys;
        }

        void ObserveMutation(BlockMutationResult result, double milliseconds) => _outcomes.Add(result);

        void ObservePublication(BlockTerrainCommit commit)
        {
            _publications++;
            Assert.That(commit.Revision, Is.EqualTo(_terrain.Revision)); Assert.That(commit.Round, Is.EqualTo(_terrain.Round));
            Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
            _frameChunks.TryGetValue(Time.frameCount, out int count); count += commit.ChangedChunks.Length;
            _frameChunks[Time.frameCount] = count; Assert.That(count, Is.LessThanOrEqualTo(8));
            foreach (var witness in _windows.Where(w => ReferenceEquals(w.Next, _terrain.World)))
            {
                Assert.That(witness.Published, Is.False); witness.Published = true;
                CollectionAssert.AreEquivalent(witness.Keys, commit.ChangedChunks);
                if (witness.ShortPrefixLength > 0)
                {
                    for (int y = witness.DeferredCursor; y <= _height; y++) AssertStableCell(_terrain.World, Cell(y), SourceOwner(y));
                    Assert.That(_detachedSources.Any(y => y >= witness.DeferredCursor), Is.False,
                        "deferred source cells were not counted as previously detached");
                }
            }
            foreach (var run in _terrain.World.Falling.Values.Where(r => InMainColumn(r.Bottom)))
            {
                if (!_seenRuns.Add(run.Id)) continue;
                AssertRunCells(run);
                for (int i = 0; i < run.Cells.Length; i++)
                    Assert.That(_detachedSources.Add(run.Bottom.y + i), Is.True, "source membership cannot detach twice");
                if (run.Cohort.DetachCursor < run.Cohort.TopExclusive)
                {
                    for (int i = 0; i < run.Cells.Length; i++)
                    {
                        var at = BlockWorld.Center(run.Bottom + Vector3Int.up * i);
                        Assert.That(Physics.Raycast(at + Vector3.left * .2f, Vector3.right, out _, .4f,
                            1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.False,
                            "a published dormant detached source has no lingering stable native collision");
                    }
                    AssertStableSide(new Vector3Int(_column.x, run.Cohort.DetachCursor, _column.y));
                }
            }
            CheckPublishedState(); CheckRetainedWorlds();
            var bytes = _terrain.ExportSnapshot(); var decoded = BlockWorld.DecodeSnapshot(bytes);
            Assert.That(decoded.PinkCount, Is.EqualTo(_terrain.PinkCount)); Assert.That(decoded.BlueCount, Is.EqualTo(_terrain.BlueCount));
            CollectionAssert.AreEqual(bytes, decoded.EncodeSnapshot(), "published snapshot round trip retains owners, positions and motion metadata");
            Retain(_terrain.World);
        }

        void CheckPublishedState()
        {
            var world = _terrain.World;
            if (!world.TryGet(Cell(0), out var support) || support.Owner != 2) _supportDeletionPublished = true;
            int fillers = _fillers.Count(f => world.TryGet(f.Cell, out var value) && value.Owner == 1);
            int pink = Enumerable.Range(1, _height).Count(y => SourceOwner(y) == 1) + fillers;
            int blue = Enumerable.Range(1, _height).Count(y => SourceOwner(y) == 2) + (_supportDeletionPublished ? 0 : 1);
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink)); Assert.That(_terrain.BlueCount, Is.EqualTo(blue),
                "detaching, activation and settling do not change either live-owner score");
            int stable = world.Chunks.Values.Sum(c => c.Count);
            Assert.That(stable + _terrain.FallingCount, Is.EqualTo(pink + blue));
            var runs = world.Falling.Values.Where(r => InMainColumn(r.Bottom)).OrderBy(r => r.Bottom.y).ToArray();
            if (runs.Length == 0) return;
            foreach (var run in runs) AssertRunCells(run);
            if (_detachedSources.Count < _height)
            {
                foreach (var run in runs)
                {
                    Assert.That(run.Cohort.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
                    Assert.That(run.Offset(_now), Is.Zero.Within(1e-6), "partial published body cannot move before its complete source tail detaches");
                }
            }
            else
            {
                if (!_activated)
                {
                    Assert.That(runs.Sum(r => r.Cells.Length), Is.EqualTo(_height), "complete activation precedes any member settling");
                    _activated = true; _activationEpoch = runs[0].StartedAt;
                }
                foreach (var run in runs)
                {
                    Assert.That(run.StartedAt, Is.EqualTo(_activationEpoch));
                    Assert.That(run.InitialSpeed, Is.EqualTo(runs[0].InitialSpeed));
                    Assert.That(run.Offset(_now), Is.EqualTo(runs[0].Offset(_now)).Within(1e-6));
                    Assert.That(run.LandingKnown, Is.True); Assert.That(run.LandingOffset, Is.EqualTo(.1f).Within(1e-6));
                }
            }
            for (int i = 1; i < runs.Length; i++)
            {
                float lowerTop = (runs[i - 1].Bottom.y + runs[i - 1].Cells.Length) * BlockWorld.CellSize - runs[i - 1].Offset(_now);
                float upperBottom = runs[i].Bottom.y * BlockWorld.CellSize - runs[i].Offset(_now);
                Assert.That(lowerTop, Is.LessThanOrEqualTo(upperBottom + .0001f));
            }
        }

        void AssertRunCells(FallingRun run)
        {
            Assert.That(run.Cells.Length, Is.InRange(1, 16)); Assert.That(run.Cohort, Is.Not.Null);
            Assert.That(run.Cohort.TopExclusive, Is.EqualTo(_height + 1));
            for (int i = 0; i < run.Cells.Length; i++)
            {
                int y = run.Bottom.y + i; Assert.That(y, Is.InRange(1, _height));
                Assert.That(run.Cells[i].Occupied, Is.True); Assert.That(run.Cells[i].Owner, Is.EqualTo(SourceOwner(y)));
                Assert.That(run.Cells[i].Anchored, Is.False); Assert.That(run.Cells[i].Durability, Is.EqualTo(100));
            }
        }

        void AssertFinalCellsAndNativeCollision()
        {
            for (int y = 0; y < _height; y++) AssertStableCell(_terrain.World, Cell(y), SourceOwner(y + 1));
            Assert.That(_terrain.World.TryGet(Cell(_height), out _), Is.False);
            foreach (var filler in _fillers) AssertStableCell(_terrain.World, filler.Cell, 1);
            Assert.That(_terrain.World.Chunks.Values.Sum(c => c.Count), Is.EqualTo(_height + _fillers.Count));
            Physics.SyncTransforms();
            var top = Cell(_height - 1);
            Assert.That(Physics.Raycast(BlockWorld.Center(top) + Vector3.up * .5f, Vector3.down, out var hit, .6f,
                1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>()); Assert.That(hit.normal.y, Is.EqualTo(1).Within(.001));
            Assert.That(hit.point.y, Is.EqualTo(_height * BlockWorld.CellSize).Within(.001));
            AssertContact(hit, top, SourceOwner(_height));
            AssertStableSide(Cell(0));
        }

        void AssertStableSide(Vector3Int cell)
        {
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.left * .2f, Vector3.right, out var hit, .4f,
                1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>()); Assert.That(hit.normal.x, Is.EqualTo(-1).Within(.001));
            Assert.That(_terrain.World.TryGet(cell, out var value), Is.True);
            AssertContact(hit, cell, value.Owner);
        }

        void AssertContact(RaycastHit hit, Vector3Int cell, byte owner)
        {
            Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Owner, Is.EqualTo(owner));
            Assert.That(contact.Stable, Is.True); Assert.That(contact.Normal, Is.EqualTo(hit.normal));
            Assert.That(contact.Revision, Is.EqualTo(_terrain.ChunkRevision(BlockWorld.ChunkAt(cell))));
        }

        void Retain(BlockWorld world) => _retained.Add(new FrozenWorld { World = world, Bytes = world.EncodeSnapshot() });
        void CheckRetainedWorlds()
        {
            foreach (var frozen in _retained)
                CollectionAssert.AreEqual(frozen.Bytes, frozen.World.EncodeSnapshot(), "subsequent public work cannot mutate a retained published source world");
        }
        static void AssertStableCell(BlockWorld world, Vector3Int at, byte owner)
        {
            Assert.That(world.TryGet(at, out var value), Is.True, $"missing exact stable cell {at}");
            Assert.That(value.Owner, Is.EqualTo(owner)); Assert.That(value.Anchored, Is.False);
            Assert.That(value.Durability, Is.EqualTo(100));
        }
        static bool SameStableCell(BlockWorld before, BlockWorld after, Vector3Int at)
        {
            bool hasA = before.TryGet(at, out var a), hasB = after.TryGet(at, out var b);
            return hasA == hasB && (!hasA || a.Owner == b.Owner && a.Flags == b.Flags && a.Durability == b.Durability);
        }
        static void IncludeOracle(HashSet<Vector3Int> keys, Vector3Int at)
        {
            var key = BlockWorld.ChunkAt(at); var local = at - BlockWorld.ChunkOrigin(key);
            keys.Add(key);
            if (local.x == 0) keys.Add(key + Vector3Int.left);
            if (local.x == 15) keys.Add(key + Vector3Int.right);
            if (local.y == 0) keys.Add(key + Vector3Int.down);
            if (local.y == 15) keys.Add(key + Vector3Int.up);
            if (local.z == 0) keys.Add(key + new Vector3Int(0, 0, -1));
            if (local.z == 15) keys.Add(key + new Vector3Int(0, 0, 1));
        }
        static T Read<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"readonly natural witness field {name} must exist");
            return (T)field.GetValue(target);
        }
        Vector3Int Cell(int y) => new(_column.x, y, _column.y);
        bool InMainColumn(Vector3Int at) => at.x == _column.x && at.z == _column.y;
        static byte SourceOwner(int y) => (byte)(1 + ((y - 1) / 16) % 2);
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
    }
}
#endif
