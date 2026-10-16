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
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Natural public-command scenario for last-reserved-peer window reuse.
    // Public commands create both columns, reservations, work and Pending.
    // The exact next-stage last-peer witnesses are explicit diagnostics. The
    // normal-time functional cases retain the complete original terrain oracle.
    // This functional fixture does not replace formal100-column/20k/32m benchmarks.
    public sealed class BlockGravityLastPeerSkipTests
    {
        const int Height = 319, PeerShots = 64;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Vector2Int Main = new(-145, -287), Peer = new(145, 285);
        GameObject _root;
        BlockTerrain _terrain;
        double _now;
        bool _advanceTime, _witnessPublished, _requireNaturalWitness;
        int _expectedWindow, _supportCallbacks, _commits;
        int _captureAttempts, _entryCaptures, _staleStageRejects, _endedReservationRejects, _pieceRejects, _visitRejects;
        enum CaptureGate { Pending, Order, Cursor, MainWork, Cohort, Tail, Reservation }
        readonly int[] _captureGateCounts = new int[7];
        readonly TerrainState[] _captureGateSamples = new TerrainState[7];
        enum ObserveGate
        {
            NoPending, Remote, NoMatchingSource, AlreadyRecorded, StageMismatch, NoJournal, JournalNextMismatch,
            WindowIdentityMismatch, WindowLimitMismatch, WindowGravityMissing, PolicyGravityLimitMismatch,
            NewColumns, MainMissing, MainTerminal, MainNotReady, BottomMismatch, TopMismatch, CohortZero,
            CohortMismatch, ScanInsufficient, GroupMissing, GroupPhaseMismatch, GroupTopMismatch,
            VisitsInsufficient, CapturedReservationsEnded, PeerCacheMissing, PeerCacheUnreserved,
            PiecesInsufficient, FirstPieceBottomMismatch, FirstPieceLengthMismatch,
            SecondPieceBottomMismatch, SecondPieceLengthMismatch, Accepted
        }
        const int ObserveGateCount = (int)ObserveGate.Accepted + 1, MaximumEntryDiagnostics = 8;
        int _observeAttempts;
        readonly int[] _observeGateCounts = new int[ObserveGateCount];
        readonly ObserveState[] _observeGateSamples = new ObserveState[ObserveGateCount];
        // Pending is a sealed reference type without an Equals override. These
        // sets count real object identities, separately from repeated observations.
        readonly HashSet<object> _observeDistinctPending = new();
        readonly HashSet<object>[] _observeGatePending = new HashSet<object>[ObserveGateCount];
        readonly List<EntryDiagnostic> _entryDiagnostics = new();
        readonly int[] _shotCallbacks = new int[PeerShots];
        readonly List<BlockMutationResult> _results = new();
        readonly Dictionary<BlockWorld, Entry> _entries = new();
        readonly Dictionary<BlockWorld, WindowWitness> _windows = new();
        readonly Dictionary<BlockWorld, PublicationWitness> _publications = new();
        readonly Dictionary<int, int> _frameKeys = new();
        BlockWorld _seed;
        byte[] _seedBytes;
        const int CleanupFrames = 3000;
        SemaphoreSlim _workers;
        BlockWorkerQueue _admissions;
        readonly HashSet<object> _observedPending = new();
        readonly HashSet<Task> _observedCommandTasks = new();
        readonly HashSet<Mesh> _observedMeshes = new();
        readonly HashSet<GameObject> _observedViews = new();
        bool _tearingDown;

        sealed class Entry
        {
            public BlockWorld World;
            public byte[] Bytes;
            public int Cursor, Start, Top, ColumnBottom;
            public ulong Cohort;
            public long StageOrdinal;
            public object[] Reservations;
        }
        sealed class WindowWitness
        {
            public Entry Source;
            public BlockWorld Next;
            public Vector3Int[] Keys;
            public FallingRun[] FirstPieces;
            public int ColumnVisits;
            public bool Published;
        }
        sealed class PublicationWitness
        {
            public BlockWorld Source;
            public byte[] Bytes;
            public BlockPublicationWindow Window;
            public Vector3Int[] Keys;
            public bool Published;
        }
        struct ColumnState
        {
            public bool Present, Terminal, Ready, Proving;
            public int Scan, Bottom, Top;
            public ulong Cohort;
        }
        struct GroupState
        {
            public bool Present;
            public FallingCohortPhase Phase;
            public int Cursor, Top;
        }
        struct HeadState
        {
            public bool Present, SourceIsSeed, Preparing, HasSorting, Targets, Done, Completed;
            public Vector3 Point;
            public float Radius;
            public TaskStatus Sorting;
            public int TargetIndex;
        }
        sealed class TerrainState
        {
            public BlockWorld World;
            public uint Revision;
            public int Frame, Cursor, SupportCallbacks, ShotCallbacks, QueueCount, ReservationCount,
                LiveReservations, SeedReservations, LivePeerReservations;
            public long Stage, Queued, Completed, CommandChecks, GravityChecks;
            public double Now;
            public bool Pending;
            public Vector2Int[] Order;
            public ColumnState Main;
            public GroupState Group;
            public HeadState Head;
        }
        sealed class EntryDiagnostic
        {
            public Entry Source;
            public TerrainState Captured;
            public ObserveState FirstAdvance;
        }
        sealed class ObserveState
        {
            public TerrainState Current;
            public Entry Source;
            public object Pending;
            public bool Remote, PrivateGravity, HasJournal, JournalNextMatches, WindowMatches,
                HasWindow, PeerCachePresent, PeerCacheReserved, PiecesObserved;
            public int WindowLimit, PolicyGravityLimit, PolicyVersion, ChangedCount,
                NewColumnCount, JournalCursor, Visits, CapturedReservationsLive = -1,
                PieceCount, FirstBottom, FirstLength, SecondBottom, SecondLength;
            public BlockPublicationOperation Operations;
            public ColumnState Main;
            public GroupState Group;
        }

        IEnumerator InitializeScenario()
        {
            // Construct this scenario only after the wrapper's EnterPlayMode
            // domain transition, rather than restoring a pre-created Run cursor.
            _tearingDown = false;
            _workers = Static<SemaphoreSlim>("Workers");
            _admissions = Static<BlockWorkerQueue>("WorkerAdmissions");
            yield return AwaitCleanup(() => _admissions.WhenIdle.IsCompleted && _workers.CurrentCount == 2,
                "Prior actual worker Tasks must drain before this serial public terrain fixture begins.");
            _root = new GameObject("Natural last reserved peer window reuse");
            var layout = new GameObject("Inactive public arena collider registration");
            layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64); arena.SpawnPoints = Array.Empty<Transform>();
            var floor = new GameObject("Real map floor top zero");
            floor.transform.SetParent(layout.transform, false); floor.transform.position = new Vector3(0, -.1f, 0);
            var collider = floor.AddComponent<BoxCollider>(); collider.size = new Vector3(32, .2f, 64);
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(arena);
            floor.transform.SetParent(_root.transform, true); Physics.SyncTransforms();
            Assert.That(collider.bounds.max.y, Is.Zero.Within(.000001));
            _now = 10; _advanceTime = _witnessPublished = false; _supportCallbacks = _commits = 0;
            _captureAttempts = _entryCaptures = _staleStageRejects = _endedReservationRejects = _pieceRejects = _visitRejects = 0;
            Array.Clear(_captureGateCounts, 0, _captureGateCounts.Length);
            Array.Clear(_captureGateSamples, 0, _captureGateSamples.Length);
            _observeAttempts = 0;
            Array.Clear(_observeGateCounts, 0, _observeGateCounts.Length);
            Array.Clear(_observeGateSamples, 0, _observeGateSamples.Length);
            foreach (var identities in _observeGatePending) identities?.Clear();
            _observeDistinctPending.Clear(); _entryDiagnostics.Clear();
            Array.Clear(_shotCallbacks, 0, _shotCallbacks.Length);
            _results.Clear(); _entries.Clear(); _windows.Clear(); _publications.Clear(); _frameKeys.Clear(); _seed = null; _seedBytes = null;
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            // Keep the actual managed Terrain and every observed Pending across
            // Unity's fake-null destruction. Queue idle is NOT a Task-group or
            // async OnDestroy completion gate.
            _tearingDown = true;
            var terrain = _terrain;
            if (!ReferenceEquals(terrain, null))
            { terrain.PublishedCommit -= OnCommit; terrain.MutationCompleted -= OnMutation; }
            RememberActualPending(terrain);
            if (_root != null) Object.DestroyImmediate(_root);
            for (int frame = 0; frame < CleanupFrames && !FailureCleanupJoined(terrain); frame++)
            { yield return null; }
            Assert.That(FailureCleanupJoined(terrain), Is.True,
                "Failure teardown must join actual Generate, scheduled/member and Bake Tasks, CTS disposal, and complete retired cleanup before exiting Play Mode.");
            // Unity destruction is deferred even after native tasks have joined.
            // Retain actual piece references through this independent gate.
            yield return AwaitCleanup(PendingResourcesDestroyed,
                "Every actually observed Pending Mesh/View must be destroyed after its joined retirement.");
            ObserveJoinedPendingFaults();
            if (_admissions != null) Assert.That(_admissions.WhenIdle.IsCompleted, Is.True);
            if (_workers != null) Assert.That(_workers.CurrentCount, Is.EqualTo(2), "Return exactly the original two permits.");
            _observedPending.Clear(); _observedCommandTasks.Clear(); _observedMeshes.Clear(); _observedViews.Clear();
            _terrain = null; _root = null;
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void RememberActualPending(BlockTerrain terrain)
        {
            if (ReferenceEquals(terrain, null)) return;
            var current = Read<object>(terrain, "_pending");
            if (current != null) _observedPending.Add(current);
            foreach (var entry in (IEnumerable)Read<object>(terrain, "_retired"))
                _observedPending.Add(Read<object>(entry, "Pending"));
            foreach (var pending in _observedPending) RememberPendingResources(pending);
            // Publish transfers View ownership and clears Prepared mesh/view
            // fields. Capture actual published views before OnDestroy clears them.
            foreach (var view in Read<IDictionary>(terrain, "_views").Values.Cast<object>())
                RememberViewResources(view);
            // Complete clears Command.Sorting. Preserve the genuine Task before
            // destruction; its queue lease can end before its Task continuation.
            foreach (var command in ((IEnumerable)Read<object>(terrain, "_commands")).Cast<object>()
                .Concat(((IEnumerable)Read<object>(terrain, "_destructionReservations")).Cast<object>()))
            {
                var sorting = Read<Task>(command, "Sorting");
                if (sorting != null) _observedCommandTasks.Add(sorting);
            }
        }
        bool FailureCleanupJoined(BlockTerrain terrain)
        {
            if (_admissions != null && !_admissions.WhenIdle.IsCompleted) return false;
            if (_observedCommandTasks.Any(task => !task.IsCompleted)) return false;
            if (!ReferenceEquals(terrain, null))
            {
                if (Read<object>(terrain, "_pending") != null) return false;
                if (((IEnumerable)Read<object>(terrain, "_retired")).Cast<object>().Any()) return false;
            }
            foreach (var pending in _observedPending)
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
                    var bake = Read<Task>(piece, "Bake");
                    if (bake != null && !bake.IsCompleted) return false;
                }
                if (Read<int>(pending, "AdmissionsDisposed") != 1) return false;
            }
            return true;
        }
        bool PendingResourcesDestroyed()
            => _observedMeshes.All(mesh => mesh == null) && _observedViews.All(view => view == null);
        void RememberPendingResources(object pending)
        {
            var pieces = Read<Array>(pending, "Pieces");
            if (pieces == null) return;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var render = Read<Mesh>(piece, "RenderMesh"); var collision = Read<Mesh>(piece, "CollisionMesh");
                if (!ReferenceEquals(render, null)) _observedMeshes.Add(render);
                if (!ReferenceEquals(collision, null)) _observedMeshes.Add(collision);
                RememberViewResources(Read<object>(piece, "View"));
            }
        }
        void RememberViewResources(object view)
        {
            if (view == null) return;
            var obj = Read<GameObject>(view, "Object");
            var render = Read<Mesh>(view, "RenderMesh"); var collision = Read<Mesh>(view, "CollisionMesh");
            if (!ReferenceEquals(obj, null)) _observedViews.Add(obj);
            if (!ReferenceEquals(render, null)) _observedMeshes.Add(render);
            if (!ReferenceEquals(collision, null)) _observedMeshes.Add(collision);
        }
        void ObserveJoinedPendingFaults()
        {
            foreach (var task in _observedCommandTasks) if (task.IsFaulted) _ = task.Exception;
            foreach (var pending in _observedPending)
            {
                var generate = Read<Task>(pending, "Generate");
                if (generate.IsFaulted) _ = generate.Exception;
                var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task");
                if (scheduled.IsFaulted) _ = scheduled.Exception;
                if (scheduled.Status == TaskStatus.RanToCompletion)
                    foreach (var task in Property<Task[]>(scheduled, "Result")) if (task.IsFaulted) _ = task.Exception;
                var pieces = Read<Array>(pending, "Pieces");
                if (pieces == null) continue;
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var bake = Read<Task>(piece, "Bake"); if (bake != null && bake.IsFaulted) _ = bake.Exception;
                }
                var stop = Read<CancellationTokenSource>(pending, "AdmissionStop");
                Assert.Throws<ObjectDisposedException>(() => { _ = stop.Token; });
            }
        }

        static IEnumerator AwaitCleanup(Func<bool> ready, string reason)
        {
            for (int frame = 0; frame < CleanupFrames && !ready(); frame++) yield return null;
            Assert.That(ready(), Is.True, reason);
        }
        static T Static<T>(string name)
        {
            var field = typeof(BlockTerrain).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, name); return (T)field.GetValue(null);
        }
        static T Property<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null, name); var property = target.GetType().GetProperty(name);
            Assert.That(property, Is.Not.Null, name); return (T)property.GetValue(target);
        }

        [UnityTest, Timeout(600000), Explicit("Diagnostic: requires the exact next-stage natural last-peer window-reuse combination.")]
        public IEnumerator ActualDefaultThirtyTwoWindowReusesAfterLastNaturallyReservedPeer()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return InitializeScenario();
            yield return Run(false);
        }

        [UnityTest, Timeout(600000), Explicit("Diagnostic: requires the exact next-stage natural last-peer window-reuse combination.")]
        public IEnumerator CompatibilityEightWindowReusesAfterLastNaturallyReservedPeer()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return InitializeScenario();
            yield return Run(true);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ActualDefaultThirtyTwoWindowPreservesCompletePublicAttacksAndLanding()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return InitializeScenario();
            yield return Run(false, false);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator CompatibilityEightWindowPreservesCompletePublicAttacksAndLanding()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return InitializeScenario();
            yield return Run(true, false);
        }

        IEnumerator Run(bool compatibility, bool requireNaturalWitness = true)
        {
            _requireNaturalWitness = requireNaturalWitness;
            if (compatibility) _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(61);
            _expectedWindow = compatibility ? 8 : 32;
            Assert.That(_terrain.PublicationPolicy.GravityKeys, Is.EqualTo(_expectedWindow));
            Assert.That(_terrain.PublicationPolicy.DestructionKeys, Is.EqualTo(_expectedWindow));
            Assert.That(_terrain.PublicationPolicy.OrdinaryAttackKeys, Is.EqualTo(8));
            yield return SeedPublicColumn(Main, true);
            yield return SeedPublicColumn(Peer, false);
            Assert.That(_terrain.World.Chunks.Values.Sum(c => c.Count), Is.EqualTo(640));
            Assert.That(_terrain.PinkCount, Is.EqualTo(160)); Assert.That(_terrain.BlueCount, Is.EqualTo(480));
            _seed = _terrain.World; _seedBytes = _seed.EncodeSnapshot();
            // Functional landing must not wait for an optional scheduling witness.
            _advanceTime = !requireNaturalWitness;
            _terrain.PublishedCommit += OnCommit; _terrain.MutationCompleted += OnMutation;
            long queued = _terrain.CommandsQueued, completed = _terrain.CommandsCompleted;

            // Queue both support deletions in insertion order. Their actual
            // publication creates A/B column work; the observer verifies order.
            foreach (var column in new[] { Main, Peer })
                _terrain.QueueDestroy(BlockWorld.Center(Cell(column, 0)), 0, 0, 1, true, result =>
                {
                    _supportCallbacks++; if (!_tearingDown) AssertOneEnemy(result);
                }, 1, 0);
            // Real finite attacks keep the peer horizontally reserved while
            // their immutable large source queries/sorts spend original budgets.
            // Different zero-distance targets make all64 removals unambiguous.
            // The two towers are >16m apart, so none reserves the main column.
            for (int shot = 0; shot < PeerShots; shot++)
            {
                int index = shot;
                _terrain.QueueDestroy(BlockWorld.Center(Cell(Peer, 128 + index)), 16, 0, 1, true, result =>
                {
                    _shotCallbacks[index]++;
                    if (!_tearingDown) { Assert.That(_shotCallbacks[index], Is.EqualTo(1)); AssertOneEnemy(result); }
                }, 1, 0);
            }

            double deadline = Time.realtimeSinceStartupAsDouble + 240;
            bool complete = false;
            for (int frame = 0; frame < 12000 && Time.realtimeSinceStartupAsDouble < deadline; frame++)
            {
                ObserveScenario();
                long checks = _terrain.CommandWorkChecks + _terrain.GravityWorkChecks;
                if (_advanceTime) _now += .016;
                _terrain.Tick(true, _now, .016f);
                Assert.That(_terrain.CommandWorkChecks + _terrain.GravityWorkChecks - checks, Is.InRange(0L, 4096L));
                ObserveScenario(false);
                if (_witnessPublished) _advanceTime = true;
                if (Quiet()) { complete = true; break; }
                yield return null;
            }
            if (requireNaturalWitness)
            {
                string captureDiagnostics = _witnessPublished ? string.Empty : CaptureDiagnostics();
                Assert.That(_captureAttempts, Is.EqualTo(_entryCaptures + _captureGateCounts.Sum()),
                    "Every capture attempt must have exactly one accepted entry or exclusive rejection gate." + captureDiagnostics);
                Assert.That(_observeAttempts, Is.EqualTo(_observeGateCounts.Sum()),
                    "Every Pending observation must have exactly one exclusive gate, including Accepted." + captureDiagnostics);
                Assert.That(_witnessPublished, Is.True,
                    "No actual last-reserved-peer, two column-detach pieces, exact-window publication witness. Fail rather than infer from constants or fresh runs alone. "
                    + $"Capture attempts={_captureAttempts}, captured entries={_entryCaptures}, stale stage rejects={_staleStageRejects}, "
                    + $"all captured reservations ended={_endedReservationRejects}, two-piece rejects={_pieceRejects}, insufficient visits={_visitRejects}."
                    + captureDiagnostics);
            }
            Assert.That(complete, Is.True, "Public attacks and both complete original columns must drain within the functional fixture deadline.");
            Assert.That(_supportCallbacks, Is.EqualTo(2)); Assert.That(_shotCallbacks.All(c => c == 1), Is.True);
            Assert.That(_terrain.CommandsQueued - queued, Is.EqualTo(66));
            Assert.That(_terrain.CommandsCompleted - completed, Is.EqualTo(66));
            Assert.That(_results.Count, Is.EqualTo(66)); Assert.That(_results.Sum(r => r.RemovedEnemy), Is.EqualTo(66));
            Assert.That(_results.Sum(r => r.Added + r.RemovedFriendly), Is.Zero);
            Assert.That(_terrain.PinkCount, Is.EqualTo(160)); Assert.That(_terrain.BlueCount, Is.EqualTo(414));
            Assert.That(_terrain.FailedTransactions, Is.Zero);
            for (int y = 0; y < Height; y++) CheckFinalCell(Cell(Main, y), MainOwner(y + 1));
            for (int y = 0; y < Height - PeerShots; y++) CheckFinalCell(Cell(Peer, y), 2);
            Assert.That(_terrain.World.TryGet(Cell(Main, Height), out _), Is.False);
            Assert.That(_terrain.World.TryGet(Cell(Peer, Height - PeerShots), out _), Is.False);
            Assert.That(_terrain.World.Chunks.Values.Sum(c => c.Count), Is.EqualTo(574));
            CheckCow();
            if (!requireNaturalWitness)
            {
                Assert.That(_commits, Is.GreaterThan(0));
                Assert.That(_publications.Values.Count(w => w.Published), Is.EqualTo(_commits),
                    "Every actual functional commit must have its own captured source and publication window.");
                foreach (var publication in _publications.Values)
                    CollectionAssert.AreEqual(publication.Bytes, publication.Source.EncodeSnapshot());
            }
            long done = _terrain.CommandsCompleted;
            for (int i = 0; i < 20; i++) { _now += .016; _terrain.Tick(true, _now, .016f); yield return null; }
            Assert.That(_terrain.CommandsCompleted, Is.EqualTo(done)); Assert.That(_results.Count, Is.EqualTo(66));
            Assert.That(_supportCallbacks, Is.EqualTo(2)); Assert.That(_shotCallbacks.All(c => c == 1), Is.True);
        }

        void ObserveScenario(bool captureEntry = true)
        {
            if (_requireNaturalWitness)
            { if (captureEntry) CaptureEntry(); ObservePending(); return; }
            var pending = Read<object>(_terrain, "_pending");
            if (pending == null) return;
            _observedPending.Add(pending); RememberPendingResources(pending);
            Assert.That(Read<bool>(pending, "Remote"), Is.False);
            var next = Read<BlockWorld>(pending, "Next");
            var window = Read<BlockPublicationWindow>(pending, "Window");
            var keys = Read<Vector3Int[]>(pending, "Changed");
            Assert.That(window, Is.Not.Null);
            Assert.That(window.Policy, Is.SameAs(_terrain.PublicationPolicy));
            Assert.That(keys.Length, Is.LessThanOrEqualTo(window.MaximumChangedKeys));
            Assert.That(window.MaximumChangedKeys, Is.InRange(1, _expectedWindow));
            if (window.Includes(BlockPublicationOperation.OrdinaryAttack))
                Assert.That(window.MaximumChangedKeys, Is.LessThanOrEqualTo(8));
            if (_publications.TryGetValue(next, out var prior))
            {
                Assert.That(window, Is.SameAs(prior.Window));
                CollectionAssert.AreEquivalent(prior.Keys, keys);
                return;
            }
            _publications.Add(next, new PublicationWitness { Source = _terrain.World,
                Bytes = _terrain.World.EncodeSnapshot(), Window = window, Keys = (Vector3Int[])keys.Clone() });
        }

        IEnumerator SeedPublicColumn(Vector2Int column, bool mixed)
        {
            for (int first = 0; first <= Height; first += first == 0 ? 1 : 16)
            {
                int start = first, length = first == 0 ? 1 : Math.Min(16, Height - first + 1), callbacks = 0;
                byte owner = first == 0 || !mixed ? (byte)2 : MainOwner(first);
                _terrain.QueueBuild(BlockWorld.Center(Cell(column, start)) - Vector3.up * .051f, Vector3.up, owner, length, 0, false, result =>
                {
                    callbacks++;
                    if (!_tearingDown)
                    {
                        Assert.That(callbacks, Is.EqualTo(1)); Assert.That(result.Added, Is.EqualTo(length));
                        Assert.That(result.RemovedEnemy + result.RemovedFriendly, Is.Zero);
                    }
                });
                double until = Time.realtimeSinceStartupAsDouble + 180;
                for (int frame = 0; frame < 6000 && !Quiet() && Time.realtimeSinceStartupAsDouble < until; frame++)
                { _terrain.Tick(true, _now, .016f); yield return null; }
                Assert.That(Quiet(), Is.True); Assert.That(callbacks, Is.EqualTo(1));
                for (int y = start; y < start + length; y++) AssertStable(_terrain.World, Cell(column, y), owner);
            }
        }

        void CaptureEntry()
        {
            _captureAttempts++;
            // Tick has one StageAuthority invocation. A matching source capture
            // also comes from OnCommit after actual world/journal publication.
            if (Read<object>(_terrain, "_pending") != null) { RejectCapture(CaptureGate.Pending); return; }
            var order = Read<List<Vector2Int>>(_terrain, "_cohortColumnOrder");
            if (order.Count != 2 || order[0] != Main || order[1] != Peer) { RejectCapture(CaptureGate.Order); return; }
            int cursor = Read<int>(_terrain, "_cohortColumnCursor");
            if (cursor % 2 != 0) { RejectCapture(CaptureGate.Cursor); return; }
            var works = Read<IDictionary>(_terrain, "_cohortColumnWork");
            if (!works.Contains(Main) || works[Main] == null
                || !Read<bool>(works[Main], "Ready") && !Read<bool>(works[Main], "Proving")) { RejectCapture(CaptureGate.MainWork); return; }
            ulong id = Read<ulong>(works[Main], "Cohort");
            int columnBottom = Read<int>(works[Main], "Bottom"), start = columnBottom, top = Read<int>(works[Main], "Top");
            if (id != 0)
            {
                if (!_terrain.World.Cohorts.TryGetValue(id, out var group) || group.Phase != FallingCohortPhase.Detaching)
                { RejectCapture(CaptureGate.Cohort); return; }
                start = group.DetachCursor; top = group.TopExclusive;
            }
            else
            {
                // Proving can become Ready and detach multiple pieces within
                // one draft. Derive its complete source span independently from
                // published payloads; the matching journal must prove Ready.
                int sourceTop = PublishedStableTop(_terrain.World, start);
                if (Read<bool>(works[Main], "Ready")) Assert.That(top, Is.EqualTo(sourceTop), "Committed Ready top matches the actual complete stable source span.");
                top = sourceTop;
            }
            if (top - start < 48) { RejectCapture(CaptureGate.Tail); return; } // Leave a stable tail; activation cannot mimic this witness.
            var reservations = ((IEnumerable)Read<object>(_terrain, "_destructionReservations")).Cast<object>()
                .Where(LivePeerReservation).ToArray();
            if (reservations.Length == 0) { RejectCapture(CaptureGate.Reservation); return; }
            _entryCaptures++;
            var entry = new Entry { World = _terrain.World, Bytes = _terrain.World.EncodeSnapshot(),
                Cursor = cursor, Start = start, Top = top, ColumnBottom = columnBottom, Cohort = id,
                StageOrdinal = _terrain.AuthorityStageCount, Reservations = reservations };
            _entries[_terrain.World] = entry;
            if (_entryDiagnostics.Count < MaximumEntryDiagnostics)
                _entryDiagnostics.Add(new EntryDiagnostic { Source = entry, Captured = CaptureState() });
        }

        static int PublishedStableTop(BlockWorld world, int start)
        {
            int top = start;
            for (int remaining = 0; remaining <= Height; remaining++)
            {
                var cell = Cell(Main, top);
                if (!world.InBounds(cell) || !world.TryGet(cell, out var value) || value.Anchored) return top;
                Assert.That(value.Occupied, Is.True, "Independent published source occupancy.");
                Assert.That(value.Owner, Is.EqualTo(MainOwner(top)), "Independent published source ownership.");
                Assert.That(value.Durability, Is.EqualTo(100), "Independent published source durability.");
                top++;
            }
            Assert.Fail("The actual published source span exceeds the unchanged map-height bound.");
            return top;
        }

        void RejectCapture(CaptureGate gate)
        {
            int index = (int)gate;
            _captureGateCounts[index]++;
            // Only the first real state for each gate, plus one final failure
            // state, is retained. Never log or sample these every frame.
            if (_captureGateSamples[index] == null) _captureGateSamples[index] = CaptureState();
        }

        string CaptureDiagnostics()
        {
            string counts = string.Join(", ", Enumerable.Range(0, _captureGateCounts.Length)
                .Select(i => $"{(CaptureGate)i}={_captureGateCounts[i]}"));
            string samples = string.Join("; ", Enumerable.Range(0, _captureGateSamples.Length)
                .Where(i => _captureGateSamples[i] != null).Select(i => $"{(CaptureGate)i} first=[{FormatTerrainState(_captureGateSamples[i])}]"));
            return $" Exclusive capture gates: {counts}. First rejected states: {samples}. Final state=[{FormatTerrainState(CaptureState())}]."
                + ObserveDiagnostics();
        }

        TerrainState CaptureState()
        {
            var order = Read<List<Vector2Int>>(_terrain, "_cohortColumnOrder");
            int cursor = Read<int>(_terrain, "_cohortColumnCursor");
            var works = Read<IDictionary>(_terrain, "_cohortColumnWork");
            var main = ReadColumnState(works);
            var reservations = ((IEnumerable)Read<object>(_terrain, "_destructionReservations")).Cast<object>().ToArray();
            int live = reservations.Count(r => !Read<bool>(r, "Done") && !Read<bool>(r, "Completed"));
            int seedSources = reservations.Count(r => ReferenceEquals(Read<BlockWorld>(r, "Source"), _seed));
            int livePeer = reservations.Count(LivePeerReservation);
            var commands = ((IEnumerable)Read<object>(_terrain, "_commands")).Cast<object>().ToArray();
            object head = commands.FirstOrDefault();
            var headState = new HeadState { Present = head != null };
            if (head != null)
            {
                var sorting = Read<Task>(head, "Sorting");
                headState.Point = Read<Vector3>(head, "Point"); headState.Radius = Read<float>(head, "Radius");
                headState.SourceIsSeed = ReferenceEquals(Read<BlockWorld>(head, "Source"), _seed);
                headState.Preparing = Read<object>(head, "Preparing") != null;
                headState.HasSorting = sorting != null; if (sorting != null) headState.Sorting = sorting.Status;
                headState.Targets = Read<object>(head, "Targets") != null; headState.TargetIndex = Read<int>(head, "TargetIndex");
                headState.Done = Read<bool>(head, "Done"); headState.Completed = Read<bool>(head, "Completed");
            }
            return new TerrainState { World = _terrain.World, Frame = Time.frameCount, Now = _now,
                Revision = _terrain.Revision, Stage = _terrain.AuthorityStageCount,
                CommandChecks = _terrain.CommandWorkChecks, GravityChecks = _terrain.GravityWorkChecks,
                Pending = Read<object>(_terrain, "_pending") != null, Order = order.ToArray(), Cursor = cursor,
                Main = main, Group = ReadGroupState(_terrain.World, main.Cohort), SupportCallbacks = _supportCallbacks,
                ShotCallbacks = _shotCallbacks.Count(c => c == 1), Queued = _terrain.CommandsQueued,
                Completed = _terrain.CommandsCompleted, QueueCount = commands.Length, ReservationCount = reservations.Length,
                LiveReservations = live, SeedReservations = seedSources, LivePeerReservations = livePeer, Head = headState };
        }

        static ColumnState ReadColumnState(IDictionary columns)
        {
            var state = new ColumnState { Present = columns.Contains(Main) };
            if (!state.Present) return state;
            var work = columns[Main]; state.Terminal = work == null; if (state.Terminal) return state;
            state.Scan = Read<int>(work, "Scan"); state.Bottom = Read<int>(work, "Bottom"); state.Top = Read<int>(work, "Top");
            state.Ready = Read<bool>(work, "Ready"); state.Proving = Read<bool>(work, "Proving"); state.Cohort = Read<ulong>(work, "Cohort");
            return state;
        }
        static GroupState ReadGroupState(BlockWorld world, ulong id)
        {
            if (world == null || id == 0 || !world.Cohorts.TryGetValue(id, out var group)) return default;
            return new GroupState { Present = true, Phase = group.Phase, Cursor = group.DetachCursor, Top = group.TopExclusive };
        }
        static string FormatColumnState(ColumnState state)
            => !state.Present ? "missing" : state.Terminal ? "terminal" :
                $"scan={state.Scan}, bottom={state.Bottom}, rawTop={state.Top}, proving={state.Proving}, ready={state.Ready}, cohort={state.Cohort}";
        static string FormatGroupState(GroupState state)
            => !state.Present ? "none" : $"phase={state.Phase}, cursor={state.Cursor}, top={state.Top}";
        static string ReferenceLabel(object value)
            => value == null ? "none" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value).ToString("X8");
        static string FormatTerrainState(TerrainState state)
        {
            var h = state.Head;
            string head = !h.Present ? "none" : $"point={h.Point}, radius={h.Radius}, sourceIsSeed={h.SourceIsSeed}, "
                + $"preparing={h.Preparing}, sorting={(h.HasSorting ? h.Sorting.ToString() : "none")}, targets={h.Targets}, "
                + $"targetIndex={h.TargetIndex}, done={h.Done}, completed={h.Completed}";
            return $"frame={state.Frame}, now={state.Now:R}, world={ReferenceLabel(state.World)}, revision={state.Revision}, stage={state.Stage}, "
                + $"pending={state.Pending}, order=[{string.Join(",", state.Order)}], cursor={state.Cursor}, "
                + $"mainWork=[{FormatColumnState(state.Main)}], cohort=[{FormatGroupState(state.Group)}], "
                + $"supportCallbacks={state.SupportCallbacks}, shotCallbacks={state.ShotCallbacks}, queued={state.Queued}, completed={state.Completed}, "
                + $"queueCount={state.QueueCount}, reservations={state.ReservationCount}, live={state.LiveReservations}, "
                + $"sourceIsSeed={state.SeedReservations}, livePeer={state.LivePeerReservations}, head=[{head}]";
        }

        bool LivePeerReservation(object command)
        {
            if (Read<bool>(command, "Done") || Read<bool>(command, "Completed")) return false;
            if (!ReferenceEquals(Read<BlockWorld>(command, "Source"), _seed)) return false;
            if (Read<float>(command, "Radius") != 16 || !Read<bool>(command, "EnemyOnly")) return false;
            Vector3 point = Read<Vector3>(command, "Point");
            var peer = BlockWorld.Center(Cell(Peer, 0)); var main = BlockWorld.Center(Cell(Main, 0));
            float radius = Read<float>(command, "Radius") + .051f;
            return new Vector2(point.x - peer.x, point.z - peer.z).sqrMagnitude <= radius * radius
                && new Vector2(point.x - main.x, point.z - main.z).sqrMagnitude > radius * radius;
        }

        ObserveState CaptureObserveState(object pending, Entry source, FallingRun[] pieces = null)
        {
            var state = new ObserveState { Current = CaptureState(), Pending = pending, Source = source };
            if (source != null)
            {
                var actual = ((IEnumerable)Read<object>(_terrain, "_destructionReservations")).Cast<object>().ToArray();
                state.CapturedReservationsLive = source.Reservations.Count(captured =>
                    actual.Any(current => ReferenceEquals(current, captured)) && LivePeerReservation(captured));
            }
            var reserved = Read<IDictionary>(_terrain, "_reservedColumns");
            state.PeerCachePresent = reserved.Contains(Peer);
            state.PeerCacheReserved = state.PeerCachePresent && (bool)reserved[Peer];
            if (pending == null) return state;
            state.Remote = Read<bool>(pending, "Remote"); state.PrivateGravity = Read<bool>(pending, "PrivateGravity");
            state.ChangedCount = Read<Vector3Int[]>(pending, "Changed").Length;
            var window = Read<BlockPublicationWindow>(pending, "Window");
            state.HasWindow = window != null;
            if (window != null)
            {
                state.WindowLimit = window.MaximumChangedKeys; state.Operations = window.Operations;
                state.PolicyGravityLimit = window.Policy.GravityKeys; state.PolicyVersion = window.Policy.Version;
            }
            var journal = Read<object>(pending, "Gravity"); state.HasJournal = journal != null;
            if (journal != null)
            {
                var next = Read<BlockWorld>(pending, "Next");
                state.JournalNextMatches = ReferenceEquals(Read<BlockWorld>(journal, "Next"), next);
                state.WindowMatches = ReferenceEquals(Read<BlockPublicationWindow>(journal, "Window"), window);
                state.NewColumnCount = Read<List<Vector2Int>>(journal, "NewColumns").Count;
                state.JournalCursor = Read<int>(journal, "ColumnCursor");
                state.Visits = source == null ? 0 : state.JournalCursor - source.Cursor;
                state.Main = ReadColumnState(Read<IDictionary>(journal, "Columns"));
                state.Group = ReadGroupState(next, state.Main.Cohort);
            }
            // Piece details come only from the existing witness query. Diagnostic
            // sampling never runs another Falling traversal or source oracle.
            if (pieces != null)
            {
                state.PiecesObserved = true; state.PieceCount = pieces.Length;
                if (pieces.Length > 0) { state.FirstBottom = pieces[0].Bottom.y; state.FirstLength = pieces[0].Cells.Length; }
                if (pieces.Length > 1) { state.SecondBottom = pieces[1].Bottom.y; state.SecondLength = pieces[1].Cells.Length; }
            }
            return state;
        }
        void ObserveEntryAdvances(object pending)
        {
            // These first eight records remain independent of _entries lookups:
            // metadata-only stages and obsolete source Worlds must remain visible.
            foreach (var entry in _entryDiagnostics)
                if (entry.FirstAdvance == null && _terrain.AuthorityStageCount > entry.Source.StageOrdinal)
                    entry.FirstAdvance = CaptureObserveState(pending, entry.Source);
        }
        void RecordObserve(ObserveGate gate, object pending, Entry source = null, FallingRun[] pieces = null)
        {
            int index = (int)gate; _observeGateCounts[index]++;
            if (pending != null)
            {
                _observeGatePending[index] ??= new HashSet<object>();
                _observeGatePending[index].Add(pending);
            }
            if (_observeGateSamples[index] == null) _observeGateSamples[index] = CaptureObserveState(pending, source, pieces);
        }
        static string FormatObserveState(ObserveState state)
        {
            string source = state.Source == null ? "none" : $"world={ReferenceLabel(state.Source.World)}, stage={state.Source.StageOrdinal}, "
                + $"cursor={state.Source.Cursor}, bottom={state.Source.ColumnBottom}, start={state.Source.Start}, top={state.Source.Top}, cohort={state.Source.Cohort}";
            string pieces = !state.PiecesObserved ? "not queried" : $"count={state.PieceCount}, first={state.FirstBottom}/{state.FirstLength}, "
                + $"second={state.SecondBottom}/{state.SecondLength}";
            return $"source=[{source}], current=[{FormatTerrainState(state.Current)}], pendingIdentity={ReferenceLabel(state.Pending)}, "
                + $"remote={state.Remote}, private={state.PrivateGravity}, journal={state.HasJournal}, journalNextMatches={state.JournalNextMatches}, "
                + $"windowPresent={state.HasWindow}, windowMatches={state.WindowMatches}, operations={state.Operations}, "
                + $"windowLimit={state.WindowLimit}, policyGravity={state.PolicyGravityLimit}, policyVersion={state.PolicyVersion}, changed={state.ChangedCount}, "
                + $"newColumns={state.NewColumnCount}, journalCursor={state.JournalCursor}, visits={state.Visits}, "
                + $"journalMain=[{FormatColumnState(state.Main)}], journalGroup=[{FormatGroupState(state.Group)}], "
                + $"capturedReservationsLive={state.CapturedReservationsLive}, peerCachePresent={state.PeerCachePresent}, "
                + $"peerCacheReserved={state.PeerCacheReserved}, pieces=[{pieces}]";
        }
        string ObserveDiagnostics()
        {
            string counts = string.Join(", ", Enumerable.Range(0, ObserveGateCount).Select(i =>
                $"{(ObserveGate)i}={_observeGateCounts[i]}(distinctPending={_observeGatePending[i]?.Count ?? 0})"));
            string samples = string.Join("; ", Enumerable.Range(0, ObserveGateCount).Where(i => _observeGateSamples[i] != null)
                .Select(i => $"{(ObserveGate)i} first=[{FormatObserveState(_observeGateSamples[i])}]"));
            string entries = string.Join("; ", _entryDiagnostics.Select((entry, index) =>
            {
                string advanced = "no later authority stage observed";
                if (entry.FirstAdvance != null)
                {
                    var next = entry.FirstAdvance.Current;
                    advanced = $"stageDelta={next.Stage - entry.Source.StageOrdinal}, sameWorld={ReferenceEquals(next.World, entry.Source.World)}, "
                        + $"sameRevision={next.Revision == entry.Captured.Revision}, commandChecksDelta={next.CommandChecks - entry.Captured.CommandChecks}, "
                        + $"gravityChecksDelta={next.GravityChecks - entry.Captured.GravityChecks}, state=[{FormatObserveState(entry.FirstAdvance)}]";
                }
                return $"entry{index + 1} sourceStart={entry.Source.Start}, sourceTop={entry.Source.Top}, "
                    + $"capturedReservationCount={entry.Source.Reservations.Length}, original=[{FormatTerrainState(entry.Captured)}], firstAdvance=[{advanced}]";
            }));
            return $" Observe attempts={_observeAttempts}, distinct Pending objects={_observeDistinctPending.Count}. "
                + $"Exclusive Observe gates: {counts}. First Observe states: {samples}. First {_entryDiagnostics.Count}/{_entryCaptures} accepted Entry records: {entries}.";
        }

        void ObservePending()
        {
            _observeAttempts++;
            var pending = Read<object>(_terrain, "_pending");
            if (pending != null) { _observeDistinctPending.Add(pending); _observedPending.Add(pending); RememberPendingResources(pending); }
            ObserveEntryAdvances(pending);
            if (pending == null) { RecordObserve(ObserveGate.NoPending, pending); return; }
            if (Read<bool>(pending, "Remote")) { RecordObserve(ObserveGate.Remote, pending); return; }
            if (!_entries.TryGetValue(_terrain.World, out var source)) { RecordObserve(ObserveGate.NoMatchingSource, pending); return; }
            var next = Read<BlockWorld>(pending, "Next");
            if (_windows.ContainsKey(next)) { RecordObserve(ObserveGate.AlreadyRecorded, pending, source); return; }
            // The same World can survive preparation/retry stages, where Done
            // may be restored. Only this capture's next authority stage qualifies.
            if (_terrain.AuthorityStageCount != source.StageOrdinal + 1)
            { _staleStageRejects++; RecordObserve(ObserveGate.StageMismatch, pending, source); return; }
            var journal = Read<object>(pending, "Gravity");
            if (journal == null) { RecordObserve(ObserveGate.NoJournal, pending, source); return; }
            if (!ReferenceEquals(Read<BlockWorld>(journal, "Next"), next)) { RecordObserve(ObserveGate.JournalNextMismatch, pending, source); return; }
            var window = Read<BlockPublicationWindow>(pending, "Window");
            if (!ReferenceEquals(Read<BlockPublicationWindow>(journal, "Window"), window))
            { RecordObserve(ObserveGate.WindowIdentityMismatch, pending, source); return; }
            if (window.MaximumChangedKeys != _expectedWindow) { RecordObserve(ObserveGate.WindowLimitMismatch, pending, source); return; }
            if (!window.Includes(BlockPublicationOperation.Gravity)) { RecordObserve(ObserveGate.WindowGravityMissing, pending, source); return; }
            if (window.Policy.GravityKeys != _expectedWindow) { RecordObserve(ObserveGate.PolicyGravityLimitMismatch, pending, source); return; }
            if (Read<List<Vector2Int>>(journal, "NewColumns").Count != 0) { RecordObserve(ObserveGate.NewColumns, pending, source); return; }
            var columns = Read<IDictionary>(journal, "Columns");
            if (!columns.Contains(Main)) { RecordObserve(ObserveGate.MainMissing, pending, source); return; }
            if (columns[Main] == null) { RecordObserve(ObserveGate.MainTerminal, pending, source); return; }
            if (!Read<bool>(columns[Main], "Ready")) { RecordObserve(ObserveGate.MainNotReady, pending, source); return; }
            if (Read<int>(columns[Main], "Bottom") != source.ColumnBottom) { RecordObserve(ObserveGate.BottomMismatch, pending, source); return; }
            if (Read<int>(columns[Main], "Top") != source.Top) { RecordObserve(ObserveGate.TopMismatch, pending, source); return; }
            ulong id = Read<ulong>(columns[Main], "Cohort");
            if (id == 0) { RecordObserve(ObserveGate.CohortZero, pending, source); return; }
            if (source.Cohort != 0 && id != source.Cohort) { RecordObserve(ObserveGate.CohortMismatch, pending, source); return; }
            if (Read<int>(columns[Main], "Scan") < source.Start + 32) { RecordObserve(ObserveGate.ScanInsufficient, pending, source); return; }
            if (!next.Cohorts.TryGetValue(id, out var group)) { RecordObserve(ObserveGate.GroupMissing, pending, source); return; }
            if (group.Phase != FallingCohortPhase.Detaching) { RecordObserve(ObserveGate.GroupPhaseMismatch, pending, source); return; }
            if (group.TopExclusive != source.Top) { RecordObserve(ObserveGate.GroupTopMismatch, pending, source); return; }
            int visits = Read<int>(journal, "ColumnCursor") - source.Cursor;
            if (visits < 4) { _visitRejects++; RecordObserve(ObserveGate.VisitsInsufficient, pending, source); return; }
            // One paid command may finish while another captured command still
            // genuinely reserves Peer. Membership and identity are both required.
            bool capturedReservationLive = ((IEnumerable)Read<object>(_terrain, "_destructionReservations")).Cast<object>()
                .Any(r => source.Reservations.Any(captured => ReferenceEquals(captured, r)) && LivePeerReservation(r));
            if (!capturedReservationLive) { _endedReservationRejects++; RecordObserve(ObserveGate.CapturedReservationsEnded, pending, source); return; }
            var reserved = Read<IDictionary>(_terrain, "_reservedColumns");
            if (!reserved.Contains(Peer)) { RecordObserve(ObserveGate.PeerCacheMissing, pending, source); return; }
            if (!(bool)reserved[Peer]) { RecordObserve(ObserveGate.PeerCacheUnreserved, pending, source); return; }
            var fresh = next.Falling.Values.Where(r => At(r.Bottom, Main) && r.Cohort != null && r.Cohort.Id == id
                && !source.World.Falling.ContainsKey(r.Id)).OrderBy(r => r.Bottom.y).ToArray();
            if (fresh.Length < 2) { _pieceRejects++; RecordObserve(ObserveGate.PiecesInsufficient, pending, source, fresh); return; }
            if (fresh[0].Bottom.y != source.Start) { _pieceRejects++; RecordObserve(ObserveGate.FirstPieceBottomMismatch, pending, source, fresh); return; }
            if (fresh[0].Cells.Length != 16) { _pieceRejects++; RecordObserve(ObserveGate.FirstPieceLengthMismatch, pending, source, fresh); return; }
            if (fresh[1].Bottom.y != source.Start + 16) { _pieceRejects++; RecordObserve(ObserveGate.SecondPieceBottomMismatch, pending, source, fresh); return; }
            if (fresh[1].Cells.Length != 16) { _pieceRejects++; RecordObserve(ObserveGate.SecondPieceLengthMismatch, pending, source, fresh); return; }
            Assert.That(Read<bool>(pending, "PrivateGravity"), Is.False);
            Assert.That(Read<object>(pending, "BuildGuard"), Is.Null); Assert.That(Read<object>(pending, "RecoveryGuard"), Is.Null);
            CollectionAssert.AreEqual(source.Bytes, source.World.EncodeSnapshot());
            foreach (var run in fresh)
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    int y = run.Bottom.y + i;
                    Assert.That(run.Cells[i].Occupied, Is.True); Assert.That(run.Cells[i].Owner, Is.EqualTo(MainOwner(y)));
                    Assert.That(run.Cells[i].Anchored, Is.False); Assert.That(run.Cells[i].Durability, Is.EqualTo(100));
                }
            var keys = Oracle(source.World, next).ToArray();
            CollectionAssert.AreEquivalent(keys, Read<Vector3Int[]>(pending, "Changed"));
            Assert.That(keys.Length, Is.LessThanOrEqualTo(_expectedWindow));
            _windows.Add(next, new WindowWitness { Source = source, Next = next, Keys = keys,
                FirstPieces = fresh.Take(2).ToArray(), ColumnVisits = visits });
            RecordObserve(ObserveGate.Accepted, pending, source, fresh);
        }

        void OnCommit(BlockTerrainCommit commit)
        {
            _commits++; Assert.That(commit.Revision, Is.EqualTo(_terrain.Revision));
            _frameKeys.TryGetValue(Time.frameCount, out int total); total += commit.ChangedChunks.Length;
            _frameKeys[Time.frameCount] = total; Assert.That(total, Is.LessThanOrEqualTo(_terrain.PublicationPolicy.FrameKeys));
            if (!_requireNaturalWitness)
            {
                Assert.That(_publications.TryGetValue(_terrain.World, out var publication), Is.True,
                    "Published terrain must be the actual observed Pending.Next, not an inferred window.");
                Assert.That(publication.Published, Is.False); publication.Published = true;
                CollectionAssert.AreEquivalent(publication.Keys, commit.ChangedChunks);
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(publication.Window.MaximumChangedKeys));
                CollectionAssert.AreEqual(publication.Bytes, publication.Source.EncodeSnapshot());
                foreach (var column in new[] { Main, Peer }) for (int y = 0; y <= Height; y++)
                {
                    var cell = Cell(column, y);
                    bool before = publication.Source.TryGet(cell, out _), after = _terrain.World.TryGet(cell, out _);
                    if (before != after) Assert.That(NativeSide(cell), Is.EqualTo(after),
                        "Actual native stable collision must change at the same commit as cell occupancy: " + cell);
                }
            }
            if (_windows.TryGetValue(_terrain.World, out var witness))
            {
                Assert.That(witness.Published, Is.False); witness.Published = true;
                CollectionAssert.AreEquivalent(witness.Keys, commit.ChangedChunks);
                Assert.That(witness.ColumnVisits, Is.GreaterThanOrEqualTo(4));
                foreach (var run in witness.FirstPieces)
                    for (int i = 0; i < run.Cells.Length; i++)
                    {
                        var cell = run.Bottom + Vector3Int.up * i;
                        Assert.That(_terrain.World.TryGet(cell, out _), Is.False);
                        Assert.That(NativeSide(cell), Is.False, "Published detached source still has a stable character collider.");
                    }
                var group = witness.FirstPieces[0].Cohort;
                Assert.That(group.Phase, Is.EqualTo(FallingCohortPhase.Detaching));
                Assert.That(group.Offset(_now), Is.Zero.Within(.000001));
                Assert.That(NativeSide(Cell(Main, group.DetachCursor)), Is.True, "Deferred source tail loses stable collision.");
                _witnessPublished = true;
            }
            CheckCounts(); CheckCow(); if (_requireNaturalWitness) CaptureEntry();
        }

        static HashSet<Vector3Int> Oracle(BlockWorld before, BlockWorld after)
        {
            var keys = new HashSet<Vector3Int>();
            foreach (var column in new[] { Main, Peer }) for (int y = 0; y <= Height; y++)
            {
                var cell = Cell(column, y);
                bool a = before.TryGet(cell, out var old), b = after.TryGet(cell, out var value);
                if (a != b || a && (old.Owner != value.Owner || old.Flags != value.Flags || old.Durability != value.Durability)) Include(keys, cell);
            }
            foreach (var run in after.Falling.Values)
                if (!before.Falling.ContainsKey(run.Id))
                    for (int i = 0; i < run.Cells.Length; i++) if (run.Cells[i].Occupied) Include(keys, run.Bottom + Vector3Int.up * i);
            return keys;
        }
        static void Include(HashSet<Vector3Int> keys, Vector3Int cell)
        {
            var key = BlockWorld.ChunkAt(cell); var local = cell - BlockWorld.ChunkOrigin(key); keys.Add(key);
            if (local.x == 0) keys.Add(key + Vector3Int.left); if (local.x == 15) keys.Add(key + Vector3Int.right);
            if (local.y == 0) keys.Add(key + Vector3Int.down); if (local.y == 15) keys.Add(key + Vector3Int.up);
            if (local.z == 0) keys.Add(key + new Vector3Int(0, 0, -1)); if (local.z == 15) keys.Add(key + new Vector3Int(0, 0, 1));
        }
        void CheckCounts()
        {
            var all = _terrain.World.Chunks.Values.SelectMany(c => c.Cells)
                .Concat(_terrain.World.Falling.Values.SelectMany(r => r.Cells)).Where(c => c.Occupied).ToArray();
            Assert.That(_terrain.PinkCount, Is.EqualTo(all.Count(c => c.Owner == 1)));
            Assert.That(_terrain.BlueCount, Is.EqualTo(all.Count(c => c.Owner == 2)));
        }
        void CheckCow()
        {
            CollectionAssert.AreEqual(_seedBytes, _seed.EncodeSnapshot());
            foreach (var window in _windows.Values)
                CollectionAssert.AreEqual(window.Source.Bytes, window.Source.World.EncodeSnapshot());
        }
        void OnMutation(BlockMutationResult result, double _) => _results.Add(result);
        static void AssertOneEnemy(BlockMutationResult result)
        { Assert.That(result.RemovedEnemy, Is.EqualTo(1)); Assert.That(result.Added + result.RemovedFriendly, Is.Zero); }
        void CheckFinalCell(Vector3Int cell, byte owner)
        { AssertStable(_terrain.World, cell, owner); Assert.That(NativeSide(cell), Is.True); }
        static void AssertStable(BlockWorld world, Vector3Int cell, byte owner)
        {
            Assert.That(world.TryGet(cell, out var value), Is.True, "Missing exact stable final cell " + cell);
            Assert.That(value.Owner, Is.EqualTo(owner)); Assert.That(value.Anchored, Is.False); Assert.That(value.Durability, Is.EqualTo(100));
        }
        static bool NativeSide(Vector3Int cell) => Physics.Raycast(BlockWorld.Center(cell) + Vector3.right * .2f,
            Vector3.left, .4f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore);
        bool Quiet() => _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0
            && _terrain.CommandsCompleted == _terrain.CommandsQueued;
        static T Read<T>(object owner, string name)
        {
            Assert.That(owner, Is.Not.Null);
            var field = owner.GetType().GetField(name, Fields); Assert.That(field, Is.Not.Null, owner.GetType().Name + "." + name);
            return (T)field.GetValue(owner);
        }
        static Vector3Int Cell(Vector2Int column, int y) => new(column.x, y, column.y);
        static bool At(Vector3Int cell, Vector2Int column) => cell.x == column.x && cell.z == column.y;
        static byte MainOwner(int y) => (byte)(1 + ((y - 1) / 16) % 2);
    }
}
#endif
