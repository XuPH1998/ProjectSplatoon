#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // All searches, retries and publications run through the real authority Tick.
    // Reflection registers the native map floor, observes ownership and advances
    // the completed fixture's round cursor for its guard-lifetime assertion.
    public sealed class BlockSpreadIntegrationTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const int PumpLimit = 3000;
        const float Step = 1f / 60;
        static readonly Vector3Int Bottom = new(2, 0, 2);
        static readonly Vector3Int Top = Bottom + Vector3Int.up * 2;
        static readonly BlockCell BottomValue = new() { Owner = 1, Flags = 0, Durability = 41 };
        static readonly BlockCell MiddleValue = new() { Owner = 2, Flags = 0, Durability = 57 };
        static readonly BlockCell TopValue = new() { Owner = 1, Flags = 0, Durability = 83 };
        readonly HashSet<object> _pendings = new();
        readonly HashSet<Object> _nativeObjects = new();
        GameObject _root;
        BoxCollider _floor;
        BlockTerrain _terrain;
        BlockWorkerQueue _admissions;
        SemaphoreSlim _workers;
        double _now;
        int _mutationEvents, _spreadPublications;
        bool _observeSpread;
        BlockTerrainCommit _lastCommit;
        object _stagedSpread;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _pendings.Clear(); _nativeObjects.Clear();
            _now = 10; _mutationEvents = _spreadPublications = 0; _observeSpread = false; _stagedSpread = null;
            _root = new GameObject("Real conservative block spread integration");
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var ground = new GameObject("Registered native map floor, top zero");
            ground.transform.SetParent(_root.transform, false);
            ground.transform.position = new Vector3(.25f, -.05f, .25f);
            _floor = ground.AddComponent<BoxCollider>(); _floor.size = new Vector3(2, .1f, 2);
            Read<HashSet<Collider>>(_terrain, "_staticColliders").Add(_floor);
            _admissions = ReadStatic<BlockWorkerQueue>("WorkerAdmissions");
            _workers = ReadStatic<SemaphoreSlim>("Workers");
            _terrain.MutationCompleted += ObserveMutation;
            _terrain.PublishedCommit += ObservePublication;
            Physics.SyncTransforms(); RememberOwnership(_terrain);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            _observeSpread = false;
            var terrain = _terrain;
            if (terrain != null)
            {
                terrain.MutationCompleted -= ObserveMutation;
                terrain.PublishedCommit -= ObservePublication;
                RememberOwnership(terrain);
                terrain.FinishRound(_now);
            }
            if (_root != null) Object.DestroyImmediate(_root);
            for (int frame = 0; frame < PumpLimit; frame++)
            {
                RememberOwnership(terrain);
                if (WorkersJoined(terrain) && _nativeObjects.All(value => value == null)) break;
                yield return null;
            }
            bool joined = WorkersJoined(terrain);
            bool destroyed = _nativeObjects.All(value => value == null);
            ObserveTaskFaults();
            int available = _workers?.CurrentCount ?? 2;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(joined, Is.True, "Actual Generate/Scheduled/member/Bake tasks and retired admissions must join before leaving Play Mode.");
            Assert.That(destroyed, Is.True, "Owned native meshes, views and material must finish destruction.");
            Assert.That(available, Is.EqualTo(2), "Spread must return both shared worker permits.");
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator AuthoritySpreadPreservesMixedTeamsAndPublishesDataRenderAndCollisionTogether()
        {
            yield return InstallThreeCellColumn();
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            yield return Pump(true, () => _terrain.SpreadMoves > 0, "A real authority spread must commit.");

            Assert.That(_terrain.SpreadMoves, Is.EqualTo(1));
            Assert.That(_spreadPublications, Is.EqualTo(1));
            Assert.That(_terrain.World, Is.Not.SameAs(source));
            Assert.That(_terrain.Revision, Is.EqualTo(source.Revision + 1));
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(), "The prior published source remains immutable.");
            AssertFirstSpread(_lastCommit);
            Assert.That(_mutationEvents, Is.Zero, "Redistribution must not emit paid mutation/special-charge credit.");
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            _terrain.FinishRound(_now); AssertSpreadSchedulingCleared();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ReceivedColumnGuardsSurviveUntilTheActualSpreadRoundConsumesItsLastSource()
        {
            yield return InstallThreeCellColumn();
            yield return Pump(true, () => _terrain.SpreadMoves > 0, "A real spread must record its received column before checking round cleanup.");
            _observeSpread = false;
            var moved = StableCells().Single(pair => pair.Value.Durability == TopValue.Durability).Key;
            var target = new Vector2Int(moved.x, moved.z);
            var received = Read<HashSet<Vector2Int>>(_terrain, "_spreadReceivedColumns");
            var arrivals = Read<HashSet<Vector3Int>>(_terrain, "_spreadArrivals");
            var round = Read<Queue<Vector2Int>>(_terrain, "_spreadRound");
            Assert.That(round, Is.Not.Null, "The seeded source is in the middle of its nine-column round.");
            Assert.That(round.Count, Is.GreaterThan(1), "At least one intermediate consumption must retain the guards before the final clear.");
            Assert.That(received, Does.Contain(target)); Assert.That(arrivals, Does.Contain(moved));
            var consume = typeof(BlockTerrain).GetMethod("ConsumeSpreadColumn", Fields);
            Assert.That(consume, Is.Not.Null);
            while (round.Count > 1)
            {
                consume.Invoke(_terrain, null);
                Assert.That(Read<Queue<Vector2Int>>(_terrain, "_spreadRound"), Is.SameAs(round));
                Assert.That(received, Does.Contain(target), "An unfinished round must keep its received-column guard.");
                Assert.That(arrivals, Does.Contain(moved));
            }
            consume.Invoke(_terrain, null);
            Assert.That(Read<Queue<Vector2Int>>(_terrain, "_spreadRound"), Is.Null);
            Assert.That(received, Is.Empty); Assert.That(arrivals, Is.Empty);
            AssertCounts();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RemovingFloorAfterSpreadStagingRetriesWithoutChangingPublishedWorldAndRecovers()
        {
            yield return InstallThreeCellColumn();
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            yield return FindStagedSpread();
            var next = Read<BlockWorld>(_stagedSpread, "Next");
            Assert.That(next.TryGet(Top, out _), Is.False, "The actual pending has already moved its draft top block.");
            Assert.That(_terrain.World, Is.SameAs(source)); Assert.That(_terrain.SpreadMoves, Is.Zero);

            _floor.enabled = false; Physics.SyncTransforms();
            yield return Pump(true, () => _terrain.SpreadPublicationRetries > 0,
                "Publish must recheck the native floor and take the real spread retry path.");
            Assert.That(_terrain.World, Is.SameAs(source));
            CollectionAssert.AreEqual(frozen, _terrain.ExportSnapshot(), "Rejected draft must not publish or lose a block.");
            Assert.That(_terrain.SpreadMoves, Is.Zero); Assert.That(_spreadPublications, Is.Zero);
            Assert.That(Read<HashSet<Vector2Int>>(_terrain, "_spreadReceivedColumns"), Is.Empty,
                "A rejected draft must not claim that its destination received a committed move.");
            AssertCounts(); AssertTopContact(Top, TopValue, source.Revision);
            Assert.That(_mutationEvents, Is.Zero);

            _floor.enabled = true; Physics.SyncTransforms();
            yield return Pump(true, () => _terrain.SpreadMoves > 0, "Restored floor must permit a fresh search and publication.");
            Assert.That(_terrain.SpreadMoves, Is.EqualTo(1)); AssertFirstSpread(_lastCommit);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot());
            Assert.That(_mutationEvents, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator FinishRoundCancelsStagedSpreadAndJoinsActualWorkers()
        {
            yield return InstallThreeCellColumn(); yield return FindStagedSpread();
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot();
            _terrain.FinishRound(_now); AssertSpreadSchedulingCleared();
            yield return Pump(true, () => WorkersJoined(_terrain), "FinishRound must retire actual spread worker ownership.");
            Assert.That(_terrain.World, Is.SameAs(source)); CollectionAssert.AreEqual(frozen, _terrain.ExportSnapshot());
            Assert.That(_terrain.SpreadMoves, Is.Zero); AssertCounts();
            Assert.That(_terrain.PendingTransactions, Is.Zero); Assert.That(_mutationEvents, Is.Zero);
            Assert.That(_spreadPublications, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            AssertSpreadSchedulingCleared();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ClearCancelsStagedSpreadWithoutPublishingIntoTheNewRoundOrLeakingWorkers()
        {
            yield return InstallThreeCellColumn(); yield return FindStagedSpread();
            var source = _terrain.World; byte[] frozen = source.EncodeSnapshot(); uint round = _terrain.Round + 1;
            _terrain.Clear(round); AssertSpreadSchedulingCleared();
            yield return Pump(true, () => WorkersJoined(_terrain), "Clear must retire actual spread worker ownership.");
            Assert.That(_terrain.Round, Is.EqualTo(round));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.Zero); Assert.That(_terrain.FallingCount, Is.Zero);
            Assert.That(_terrain.World.Chunks, Is.Empty); Assert.That(_terrain.PendingTransactions, Is.Zero);
            Assert.That(_terrain.SpreadMoves, Is.Zero); Assert.That(_spreadPublications, Is.Zero);
            Assert.That(_mutationEvents, Is.Zero); Assert.That(_terrain.FailedTransactions, Is.Zero);
            CollectionAssert.AreEqual(frozen, source.EncodeSnapshot(), "Cancelling a draft must not mutate the old round's source.");
            AssertSpreadSchedulingCleared();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator SustainedSpreadSearchAllowsTheRealFallingCohortToLandWithinTwoHundredTicks()
        {
            // This fixture deliberately does not install the old three-cell
            // observer. Its exact counts, top-move and mesh assertions remain
            // unchanged for the original fixtures below.
            _observeSpread = false;
            const int authorityTickLimit = 200, sourceWidth = 12, sourceHeight = 8, cohortCells = 8;
            const int sourceCount = sourceWidth * sourceWidth * sourceHeight;
            const int perTeam = (sourceCount + cohortCells) / 2;
            var bottom = new Vector3Int(10, 70, 10);
            var seed = new BlockWorld(new Vector2(32, 64), _terrain.Round);
            Assert.That(seed.InBounds(bottom + Vector3Int.up * (cohortCells - 1)), Is.True,
                "The entire 7m cohort must fit the configured current World.Top.");
            for (int x = -6; x < 6; x++)
                for (int z = -6; z < 6; z++)
                    for (int y = 0; y < sourceHeight; y++)
                        seed.Set(new Vector3Int(x, y, z), new BlockCell
                        { Owner = (byte)(1 + (y & 1)), Flags = 0, Durability = (byte)(50 + y) });
            for (int index = 0; index < cohortCells; index++)
                seed.Set(bottom + Vector3Int.up * index, ContentionCohortValue(index));
            var group = seed.BeginCohort(bottom, cohortCells);
            seed.DetachCohortPiece(group.Id, 4, _now, out _);
            ulong cohortId = group.Id;
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(false, () => _terrain.Revision == seed.Revision && _terrain.PendingTransactions == 0,
                "The contention fixture must install actual meshes, collision, stable tail and falling cells.");
            Assert.That(_terrain.World.Cohorts[cohortId].Phase, Is.EqualTo(FallingCohortPhase.Detaching));
            Assert.That(_terrain.FallingCount, Is.EqualTo(4));
            var retained = _terrain.World; byte[] frozen = retained.EncodeSnapshot();
            string[] payloads = ContentionPayloads();
            int publications = 0, landedAtTick = -1, movedAtTick = -1;
            bool pendingSearchWhileCohortLive = false, sawRealMotion = false;
            long checksAtStart = _terrain.GravityWorkChecks;
            Action<BlockTerrainCommit> observe = commit =>
            {
                publications++;
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                Assert.That(_terrain.PinkCount, Is.EqualTo(perTeam));
                Assert.That(_terrain.BlueCount, Is.EqualTo(perTeam));
                CollectionAssert.AreEqual(payloads, ContentionPayloads(),
                    "Every real publication must conserve owner, flags and durability across stable and falling storage.");
            };
            _terrain.PublishedCommit += observe;
            try
            {
                for (int tick = 1; tick <= authorityTickLimit; tick++)
                {
                    RememberOwnership(_terrain); _now += Step; _terrain.Tick(true, _now, Step);
                    RememberOwnership(_terrain);
                    Assert.That(_terrain.PinkCount, Is.EqualTo(perTeam));
                    Assert.That(_terrain.BlueCount, Is.EqualTo(perTeam));
                    bool live = _terrain.World.Cohorts.TryGetValue(cohortId, out var current);
                    var round = Read<Queue<Vector2Int>>(_terrain, "_spreadRound");
                    if (live && round != null && round.Count > 16) pendingSearchWhileCohortLive = true;
                    if (live && current.Phase == FallingCohortPhase.Falling && current.Offset(_now) > .01f)
                        sawRealMotion = true;
                    var stable = StableCells();
                    if (movedAtTick < 0 && stable.Any(pair => pair.Value.Durability == 57 && pair.Key.y < sourceHeight - 1))
                        movedAtTick = tick; // Actual tower payload redistribution, not a scheduler counter.
                    if (landedAtTick < 0 && !live && _terrain.FallingCount == 0)
                    {
                        landedAtTick = tick;
                        Assert.That(SpreadPressurePending(), Is.True,
                            "Landing must occur while real spread search/seed/dirty work remains outstanding.");
                        for (int index = 0; index < cohortCells; index++)
                        {
                            var at = new Vector3Int(bottom.x, index, bottom.z);
                            Assert.That(_terrain.World.TryGet(at, out var value), Is.True, "Missing actual landed cell " + at);
                            AssertValue(value, ContentionCohortValue(index));
                        }
                        var top = new Vector3Int(bottom.x, cohortCells - 1, bottom.z);
                        AssertTopContact(top, ContentionCohortValue(cohortCells - 1),
                            _terrain.ChunkRevision(BlockWorld.ChunkAt(top)));
                    }
                    yield return null;
                    if (landedAtTick > 0 && movedAtTick > 0) break;
                }
                Assert.That(pendingSearchWhileCohortLive, Is.True,
                    "The fixture must witness a real spread round with at least seventeen remaining sources while the cohort exists.");
                Assert.That(sawRealMotion, Is.True, "The actual cohort must advance through its physical trajectory.");
                Assert.That(landedAtTick, Is.InRange(1, authorityTickLimit),
                    "An actual falling cohort must fully land within 200 unmodified-budget authority ticks under sustained spread search.");
                Assert.That(movedAtTick, Is.InRange(1, authorityTickLimit),
                    "Spread must also publish real tower payload movement within the same finite run.");
                Assert.That(_terrain.GravityWorkChecks, Is.GreaterThan(checksAtStart));
                Assert.That(publications, Is.GreaterThan(0));
                Assert.That(StableCells().Count, Is.EqualTo(sourceCount + cohortCells));
                CollectionAssert.AreEqual(payloads, ContentionPayloads());
                CollectionAssert.AreEqual(frozen, retained.EncodeSnapshot(), "Progress must not mutate the retained published world.");
                Assert.That(_mutationEvents, Is.Zero, "Neither gravity nor spread earns paid mutation credit.");
                Assert.That(_terrain.FailedTransactions, Is.Zero);
            }
            finally { _terrain.PublishedCommit -= observe; }
        }

        static BlockCell ContentionCohortValue(int index)
            => new() { Owner = (byte)(1 + (index & 1)), Flags = 0, Durability = (byte)(200 + index) };

        string[] ContentionPayloads()
            => StableCells().Values.Concat(_terrain.World.Falling.Values.SelectMany(run => run.Cells))
                .Where(value => value.Occupied)
                .Select(value => value.Owner + ":" + value.Flags + ":" + value.Durability)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        bool SpreadPressurePending()
            => Read<object>(_terrain, "_spreadSeed") != null
                || Read<Queue<Vector2Int>>(_terrain, "_spreadRound")?.Count > 0
                || Read<Queue<Vector2Int>>(_terrain, "_spreadDirty").Count > 0;

        IEnumerator InstallThreeCellColumn()
        {
            var seed = new BlockWorld(new Vector2(32, 64), _terrain.Round);
            seed.Set(Bottom, BottomValue); seed.Set(Bottom + Vector3Int.up, MiddleValue); seed.Set(Top, TopValue);
            seed.Seal(_terrain.Revision + 1, seed.RevisionKeys.ToArray());
            Assert.That(_terrain.ApplyRemoteSnapshot(seed.EncodeSnapshot()), Is.True);
            yield return Pump(false, () => _terrain.Revision == seed.Revision && _terrain.PendingTransactions == 0,
                "The remote fixture must publish its actual data, render mesh and native collider first.");
            CollectionAssert.AreEqual(seed.EncodeSnapshot(), _terrain.ExportSnapshot());
            AssertCounts(); AssertTopContact(Top, TopValue, seed.Revision);
            _observeSpread = true;
        }

        IEnumerator FindStagedSpread()
        {
            yield return Pump(true, () =>
            {
                var pending = Read<object>(_terrain, "_pending");
                var journal = pending == null ? null : Read<object>(pending, "Gravity");
                if (journal == null || Read<List<BlockSpreadMove>>(journal, "SpreadMoves").Count == 0) return false;
                _stagedSpread = pending; return true;
            }, "A real spread draft must be staged before fault/cancellation injection.");
            Assert.That(_stagedSpread, Is.Not.Null); _pendings.Add(_stagedSpread);
            Assert.That(_terrain.SpreadMoves, Is.Zero); Assert.That(_spreadPublications, Is.Zero);
            Assert.That(Read<HashSet<Vector2Int>>(_terrain, "_spreadReceivedColumns"), Is.Empty,
                "Staging a draft cannot publish the received-column guard.");
        }

        IEnumerator Pump(bool authority, Func<bool> ready, string reason)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            {
                RememberOwnership(_terrain); _now += Step; _terrain.Tick(authority, _now, Step);
                RememberOwnership(_terrain); yield return null;
            }
            Assert.That(ready(), Is.True, reason);
        }

        void ObserveMutation(BlockMutationResult result, double milliseconds) => _mutationEvents++;
        void ObservePublication(BlockTerrainCommit commit)
        {
            if (!_observeSpread || _terrain.SpreadMoves == 0) return;
            _lastCommit = commit; _spreadPublications++;
            AssertFirstSpread(commit); // Observe atomic data/render/native collision at the real commit event.
        }

        void AssertFirstSpread(BlockTerrainCommit commit)
        {
            AssertCounts();
            Assert.That(commit.Revision, Is.EqualTo(_terrain.Revision));
            Assert.That(commit.Round, Is.EqualTo(_terrain.Round));
            Assert.That(_terrain.World.TryGet(Top, out _), Is.False);
            Assert.That(_terrain.World.TryGet(Bottom, out var bottom), Is.True); AssertValue(bottom, BottomValue);
            Assert.That(_terrain.World.TryGet(Bottom + Vector3Int.up, out var middle), Is.True); AssertValue(middle, MiddleValue);
            var moved = StableCells().Single(pair => pair.Value.Durability == TopValue.Durability);
            AssertValue(moved.Value, TopValue);
            Assert.That(moved.Key.y, Is.Zero);
            Assert.That(Mathf.Max(Mathf.Abs(moved.Key.x - Bottom.x), Mathf.Abs(moved.Key.z - Bottom.z)), Is.EqualTo(1));
            Assert.That(Read<Queue<Vector2Int>>(_terrain, "_spreadRound"), Is.Not.Null,
                "This fixture must observe the committed move before its round is finished.");
            Assert.That(Read<HashSet<Vector2Int>>(_terrain, "_spreadReceivedColumns"),
                Does.Contain(new Vector2Int(moved.Key.x, moved.Key.z)),
                "The actual journal commit must guard its destination column for the rest of this round.");
            Assert.That(commit.ChangedChunks, Does.Contain(BlockWorld.ChunkAt(moved.Key)));
            AssertTopContact(moved.Key, TopValue, commit.Revision);
            AssertTopContact(Bottom + Vector3Int.up, MiddleValue, commit.Revision);
            Assert.That(Physics.Raycast(BlockWorld.Center(Top) + Vector3.up * .08f, Vector3.down, .1f,
                1 << BlockTerrain.TerrainLayer), Is.False, "The removed old top must not leave a native collider face.");
            foreach (var view in Read<IDictionary>(_terrain, "_views").Values.Cast<object>())
            {
                var collision = Read<MeshCollider>(view, "Collider"); var obj = Read<GameObject>(view, "Object");
                Assert.That(collision.enabled && obj.activeInHierarchy, Is.True);
                Assert.That(collision.sharedMesh, Is.SameAs(Read<Mesh>(view, "CollisionMesh")));
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Assert.That(obj.GetComponent<MeshFilter>(), Is.Null, "Headless mode has no render mesh consumer.");
                else
                {
                    var filter = obj.GetComponent<MeshFilter>(); var renderer = obj.GetComponent<MeshRenderer>();
                    Assert.That(filter, Is.Not.Null); Assert.That(renderer != null && renderer.enabled, Is.True);
                    Assert.That(filter.sharedMesh, Is.SameAs(Read<Mesh>(view, "RenderMesh")));
                    Assert.That(filter.sharedMesh.bounds.max.y, Is.EqualTo(.2f).Within(.0001f),
                        "The visible mesh must drop the old .3m top in the same publication as data and collision.");
                    Assert.That(renderer.bounds.Contains(BlockWorld.Center(moved.Key)), Is.True);
                }
                Assert.That(collision.sharedMesh.bounds.max.y, Is.EqualTo(.2f).Within(.0001f));
            }
            Assert.That(_mutationEvents, Is.Zero);
        }

        void AssertCounts()
        {
            Assert.That(_terrain.PinkCount, Is.EqualTo(2)); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.FallingCount, Is.Zero);
            var cells = StableCells().Values.ToArray(); Assert.That(cells.Length, Is.EqualTo(3));
            Assert.That(cells.Count(value => value.Owner == 1), Is.EqualTo(2));
            Assert.That(cells.Count(value => value.Owner == 2), Is.EqualTo(1));
        }

        Dictionary<Vector3Int, BlockCell> StableCells()
        {
            var cells = new Dictionary<Vector3Int, BlockCell>();
            foreach (var pair in _terrain.World.Chunks)
                for (int index = 0; index < pair.Value.Cells.Length; index++)
                    if (pair.Value.Cells[index].Occupied)
                        cells.Add(BlockWorld.ChunkOrigin(pair.Key) + new Vector3Int(index & 15, index >> 4 & 15, index >> 8), pair.Value.Cells[index]);
            return cells;
        }

        void AssertTopContact(Vector3Int cell, BlockCell value, uint revision)
        {
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .08f, Vector3.down,
                out var hit, .1f, 1 << BlockTerrain.TerrainLayer), Is.True, "Missing native top face at " + cell);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
            Assert.That(hit.point.y, Is.EqualTo((cell.y + 1) * BlockWorld.CellSize).Within(.0001f));
            Assert.That(_terrain.TryContact(hit, out var contact), Is.True);
            Assert.That(contact.Cell, Is.EqualTo(cell)); Assert.That(contact.Owner, Is.EqualTo(value.Owner));
            Assert.That(contact.Stable, Is.True); Assert.That(contact.Revision, Is.EqualTo(revision));
            Assert.That(_terrain.ChunkRevision(BlockWorld.ChunkAt(cell)), Is.EqualTo(revision));
        }

        static void AssertValue(BlockCell actual, BlockCell expected)
        {
            Assert.That(actual.Owner, Is.EqualTo(expected.Owner)); Assert.That(actual.Flags, Is.EqualTo(expected.Flags));
            Assert.That(actual.Durability, Is.EqualTo(expected.Durability));
        }

        void AssertSpreadSchedulingCleared()
        {
            foreach (string name in new[] { "_spreadSeed", "_spreadSearch", "_spreadRound", "_spreadCommand" })
                Assert.That(Read<object>(_terrain, name), Is.Null, name);
            Assert.That(Read<bool>(_terrain, "_spreadQueued"), Is.False);
            Assert.That(Read<Queue<Vector2Int>>(_terrain, "_spreadDirty"), Is.Empty);
            Assert.That(Read<HashSet<Vector2Int>>(_terrain, "_spreadDirtySet"), Is.Empty);
            Assert.That(Read<HashSet<Vector3Int>>(_terrain, "_spreadArrivals"), Is.Empty);
            Assert.That(Read<HashSet<Vector2Int>>(_terrain, "_spreadReceivedColumns"), Is.Empty);
        }

        void RememberOwnership(BlockTerrain terrain)
        {
            if (ReferenceEquals(terrain, null)) return;
            var current = Read<object>(terrain, "_pending"); if (current != null) _pendings.Add(current);
            foreach (var retired in (IEnumerable)Read<object>(terrain, "_retired")) _pendings.Add(Read<object>(retired, "Pending"));
            foreach (var pending in _pendings)
            {
                var pieces = Read<Array>(pending, "Pieces"); if (pieces == null) continue;
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    Remember(Read<Mesh>(piece, "RenderMesh")); Remember(Read<Mesh>(piece, "CollisionMesh"));
                    RememberView(Read<object>(piece, "View"));
                }
            }
            foreach (var view in Read<IDictionary>(terrain, "_views").Values) RememberView(view);
            Remember(Read<Mesh>(terrain, "_fallMesh")); Remember(Read<Material>(terrain, "_material"));
        }
        void Remember(Object value) { if (!ReferenceEquals(value, null)) _nativeObjects.Add(value); }
        void RememberView(object view)
        {
            if (view == null) return;
            Remember(Read<GameObject>(view, "Object")); Remember(Read<Mesh>(view, "RenderMesh")); Remember(Read<Mesh>(view, "CollisionMesh"));
        }

        bool WorkersJoined(BlockTerrain terrain)
        {
            if (_admissions != null && !_admissions.WhenIdle.IsCompleted) return false;
            if (!ReferenceEquals(terrain, null) && (Read<object>(terrain, "_pending") != null
                || ((IEnumerable)Read<object>(terrain, "_retired")).Cast<object>().Any())) return false;
            foreach (var pending in _pendings)
            {
                if (!Read<Task>(pending, "Generate").IsCompleted) return false;
                var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
                if (!scheduled.IsCompleted) return false;
                if (scheduled.Status == TaskStatus.RanToCompletion
                    && Property<Task[]>(scheduled, "Result").Any(task => !task.IsCompleted)) return false;
                var pieces = Read<Array>(pending, "Pieces");
                if (pieces != null) foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var bake = Read<Task>(piece, "Bake"); if (bake != null && !bake.IsCompleted) return false;
                }
                if (Read<int>(pending, "AdmissionsDisposed") != 1) return false;
            }
            return true;
        }

        void ObserveTaskFaults()
        {
            foreach (var pending in _pendings)
            {
                var generation = Read<Task>(pending, "Generate"); if (generation.IsFaulted) _ = generation.Exception;
                var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
                if (scheduled.IsFaulted) _ = scheduled.Exception;
                if (scheduled.Status == TaskStatus.RanToCompletion)
                    foreach (var task in Property<Task[]>(scheduled, "Result")) if (task.IsFaulted) _ = task.Exception;
                var pieces = Read<Array>(pending, "Pieces");
                if (pieces != null) foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var bake = Read<Task>(piece, "Bake"); if (bake != null && bake.IsFaulted) _ = bake.Exception;
                }
            }
        }

        static T Read<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null, name); var field = target.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, name); return (T)field.GetValue(target);
        }
        static T ReadStatic<T>(string name) => (T)typeof(BlockTerrain)
            .GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
        static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name).GetValue(target);
    }
}
#endif
