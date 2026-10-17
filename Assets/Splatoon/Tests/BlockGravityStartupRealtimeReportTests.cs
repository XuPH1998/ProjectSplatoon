#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Opt-in, reports-only Editor wall-clock observation. The latency has no
    // pass/fail threshold. Real authority Tick, worker generation, native Bake
    // and world/collider publication remain in production code, unchanged.
    public sealed class BlockGravityStartupRealtimeReportTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const int Repetitions = 6;
        static readonly Vector3Int Support = new(2, 0, 2);
        GameObject _root;
        BlockTerrain _terrain;
        BlockWorkerQueue _admissions;
        SemaphoreSlim _workers;
        readonly HashSet<object> _pending = new();
        readonly HashSet<Task> _sorts = new();
        readonly HashSet<Mesh> _meshes = new();
        readonly HashSet<GameObject> _views = new();
        readonly HashSet<Material> _materials = new();
        Report _report;
        Trial _trial;
        string _reportPath;
        int _lastTickFrame = -1;
        double _lastTickAt;
        bool _measuring;

        [Serializable] public sealed class TickObservation
        {
            public int frame;
            public double at, actualIntervalMs, cpuWallMs;
        }
        [Serializable] public sealed class PublicationObservation
        {
            public uint revision;
            public double wallAt, tickAt;
            public bool supportAbsent, fallingOrSettling;
            public int changedKeys;
        }
        [Serializable] public sealed class Trial
        {
            public int height, repetition;
            public bool firstRepetition, completed, noBacklogBeforeRemoval, workerJoinedAfter;
            public int pendingBefore, unstableBefore, fallingBefore, workerPermitsBefore, commandQueueBefore;
            public long commandsOutstandingBefore, spreadMovesBefore, spreadMovesAfter;
            public double queuedWallAt, supportRemovedWallAt = -1, activatedWallAt = -1;
            public double activationEventLatencyMs = -1, drawDiagnosticObservedWallAt = -1;
            public uint removalRevision, activationRevision;
            public int supportRemovalCallbacks;
            public ulong cohort;
            public string drawSubmissionObservation;
            public List<TickObservation> ticks = new();
            public List<PublicationObservation> publications = new();
            public BlockTerrain.GravityStartupSample[] nativeSamples = Array.Empty<BlockTerrain.GravityStartupSample>();
        }
        [Serializable] public sealed class HeightSummary
        {
            public int height, count, atOrBelow100Ms;
            public double minimumMs, medianMs, maximumMs;
        }
        [Serializable] public sealed class Report
        {
            public string schema = "block-gravity-startup-realtime-v1";
            public string environment = "Unity Editor, isolated empty scene; not packaged Player or device acceptance";
            public string clock = "Time.realtimeSinceStartupAsDouble; once per natural frame; dt is the measured interval";
            public string latency = "Wall time observed inside PublishedCommit after atomic world/collider commit: first support-absent publication to first Falling/Settling cohort publication";
            public string nativeClock = "InspectGravityStartupSamples uses the real clock value injected at Tick entry; publication callbacks separately observe actual wall time";
            public string fixture = "Public QueueSeed diagnostic: central unanchored column on one anchored removable cell, eight anchored neighbouring columns of equal height, registered physical map floor at y=0";
            public string scope = "No paid attack, player input, network, screen visibility or pixel evidence; no runtime rule/budget/config overrides; 100ms is reported only";
            public string ordering = "1,8,16,32 cells; six fresh-terrain repetitions per height; firstRepetition is not a cold-process claim";
            public string status = "DIAGNOSTIC_INCOMPLETE";
            public string utc, unityVersion, graphicsDevice, operatingSystem, cpu, lastPhase;
            public int processorCount, targetFrameRate, vSyncCount;
            public bool instancing, completed, cleanupJoined;
            public float worldTop, spreadInterval;
            public List<Trial> trials = new();
            public HeightSummary[] summaries = Array.Empty<HeightSummary>();
        }

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return HistoricalBlockWorldHeightScope.WaitForBootstrap();
            _admissions = Static<BlockWorkerQueue>("WorkerAdmissions");
            _workers = Static<SemaphoreSlim>("Workers");
            _report = new Report { utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), instancing = SystemInfo.supportsInstancing,
                operatingSystem = SystemInfo.operatingSystem, cpu = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount, worldTop = BlockWorld.Top,
                spreadInterval = BlockGameplaySettings.SpreadInterval, targetFrameRate = Application.targetFrameRate,
                vSyncCount = QualitySettings.vSyncCount, lastPhase = "setup" };
            _reportPath = Path.GetFullPath(Path.Combine("Reports", "BlockGravityStartupRealtime",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(_reportPath));
            SaveReport();
            yield return WaitFor(() => _admissions.WhenIdle.IsCompleted && _workers.CurrentCount == 2, "initial worker idle", false);
        }

        [UnityTest, Timeout(900000), Explicit("Reports-only real-time gravity startup measurement; select this test explicitly.")]
        public IEnumerator IsolatedColumnsOf1_8_16_32CellsReportActualStartupTime()
        {
            foreach (int height in new[] { 1, 8, 16, 32 })
                for (int repetition = 0; repetition < Repetitions; repetition++)
                {
                    _trial = new Trial { height = height, repetition = repetition, firstRepetition = repetition == 0 };
                    _report.trials.Add(_trial);
                    CreateTerrain(height);
                    _report.lastPhase = $"seed h={height} repetition={repetition}"; SaveReport();
                    yield return WaitFor(Quiet, "seed publication and all initial work", true);
                    AssertInitialColumn(height);
                    _trial.pendingBefore = _terrain.PendingTransactions;
                    _trial.unstableBefore = _terrain.UnstableColumns;
                    _trial.fallingBefore = _terrain.FallingCount;
                    _trial.workerPermitsBefore = _workers.CurrentCount;
                    _trial.commandQueueBefore = Property<int>(Read<object>(_terrain, "_commands"), "Count");
                    _trial.commandsOutstandingBefore = _terrain.CommandsQueued - _terrain.CommandsCompleted;
                    _trial.noBacklogBeforeRemoval = Quiet();
                    Assert.That(_trial.noBacklogBeforeRemoval, Is.True);
                    _trial.spreadMovesBefore = _terrain.SpreadMoves;
                    Assert.That(_trial.spreadMovesBefore, Is.Zero, "The anchored sleeve must prevent movement through normal spread geometry.");
                    _terrain.PublishedCommit += ObservePublication;
                    _trial.queuedWallAt = Time.realtimeSinceStartupAsDouble;
                    _measuring = true;
                    _terrain.QueueDestroy(BlockWorld.Center(Support), .0001f, 0, 1, true, result =>
                    {
                        _trial.supportRemovalCallbacks++;
                        Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                        Assert.That(result.RemovedFriendly + result.Added, Is.Zero);
                    }, maxEnemyBlocks: 1, maxFriendlyBlocks: 0);
                    _report.lastPhase = $"measure h={height} repetition={repetition}";
                    yield return WaitFor(() => _trial.activatedWallAt >= 0 && Quiet(), "support removal, activation and complete landing", true);
                    _measuring = false;
                    CaptureNativeSamples();
                    Assert.That(_trial.supportRemovalCallbacks, Is.EqualTo(1));
                    Assert.That(_trial.supportRemovedWallAt, Is.GreaterThanOrEqualTo(_trial.queuedWallAt));
                    Assert.That(_trial.activatedWallAt, Is.GreaterThanOrEqualTo(_trial.supportRemovedWallAt));
                    Assert.That(_trial.nativeSamples.Length, Is.EqualTo(1), "Retain the real runtime activation diagnostic.");
                    var native = _trial.nativeSamples[0];
                    Assert.That(native.cohort, Is.EqualTo(_trial.cohort));
                    Assert.That(native.removalRevision, Is.EqualTo(_trial.removalRevision));
                    Assert.That(native.activationRevision, Is.EqualTo(_trial.activationRevision));
                    _trial.activationEventLatencyMs = (_trial.activatedWallAt - _trial.supportRemovedWallAt) * 1000;
                    _trial.spreadMovesAfter = _terrain.SpreadMoves;
                    Assert.That(_trial.spreadMovesAfter, Is.EqualTo(_trial.spreadMovesBefore));
                    Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(9 * (height + 1) - 1));
                    _trial.drawSubmissionObservation = native.firstPresentedAt >= 0
                        ? "Runtime observed a displaced instanced draw submission; no visibility or pixel claim."
                        : SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsInstancing
                            ? "No graphics device/instancing; draw submission unobserved."
                            : "No displaced draw submission recorded before landing; startup activation remains independently observed.";
                    _report.lastPhase = $"cleanup h={height} repetition={repetition}"; SaveReport();
                    yield return CleanupTerrain();
                    _trial.workerJoinedAfter = true;
                    _trial.completed = true;
                    _report.lastPhase = $"complete h={height} repetition={repetition}"; SaveReport();
                }
            _report.completed = true;
            _report.status = "DIAGNOSTIC_COMPLETE";
            _report.lastPhase = "all trials completed";
            SaveReport();
            TestContext.Progress.WriteLine("[BLOCK GRAVITY STARTUP REALTIME] " + _reportPath);
        }

        void CreateTerrain(int height)
        {
            Assert.That(_admissions.WhenIdle.IsCompleted && _workers.CurrentCount == 2, Is.True);
            _root = new GameObject("Gravity startup realtime report terrain");
            var layout = new GameObject("Inactive registered map layout");
            layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>();
            arena.Dimensions = new Vector2(32, 64); arena.SpawnPoints = Array.Empty<Transform>();
            var floor = new GameObject("Registered physical floor with top zero");
            floor.transform.SetParent(layout.transform, false); floor.transform.position = new Vector3(0, -.1f, 0);
            var collider = floor.AddComponent<BoxCollider>(); collider.size = new Vector3(4, .2f, 4);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena);
            floor.transform.SetParent(_root.transform, true); Physics.SyncTransforms();
            Assert.That(collider.enabled && collider.gameObject.activeInHierarchy, Is.True);
            Assert.That(collider.bounds.max.y, Is.Zero.Within(.000001));
            var seed = new BlockWorld(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++) for (int y = 0; y <= height; y++)
                Assert.That(seed.Set(Support + new Vector3Int(x, y, z), BlockCell.Create(2, y == 0 || x != 0 || z != 0)), Is.True);
            _lastTickFrame = -1; _lastTickAt = Time.realtimeSinceStartupAsDouble;
            Assert.That(_terrain.QueueSeed(seed), Is.True);
        }

        void AssertInitialColumn(int height)
        {
            Assert.That(_terrain.InspectGravityStartupSamples(), Is.Empty);
            for (int y = 0; y <= height; y++)
            {
                Assert.That(_terrain.World.TryGet(Support + Vector3Int.up * y, out var cell), Is.True);
                Assert.That(cell.Anchored, Is.EqualTo(y == 0));
            }
        }

        void ObservePublication(BlockTerrainCommit commit)
        {
            double wall = Time.realtimeSinceStartupAsDouble;
            bool absent = !_terrain.World.TryGet(Support, out _);
            var group = _terrain.World.Cohorts.Values.FirstOrDefault(cohort => cohort.Bottom.x == Support.x && cohort.Bottom.z == Support.z
                && (cohort.Phase == FallingCohortPhase.Falling || cohort.Phase == FallingCohortPhase.Settling));
            _trial.publications.Add(new PublicationObservation { revision = commit.Revision, wallAt = wall,
                tickAt = _terrain.SimulationTime, supportAbsent = absent, fallingOrSettling = group != null,
                changedKeys = commit.ChangedChunks.Length });
            if (absent && _trial.supportRemovedWallAt < 0)
            { _trial.supportRemovedWallAt = wall; _trial.removalRevision = commit.Revision; }
            if (group != null && _trial.activatedWallAt < 0)
            {
                _trial.activatedWallAt = wall; _trial.activationRevision = commit.Revision; _trial.cohort = group.Id;
                if (_trial.supportRemovedWallAt >= 0) _trial.activationEventLatencyMs = (wall - _trial.supportRemovedWallAt) * 1000;
            }
        }

        bool Quiet() => _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0
            && _terrain.CommandsQueued == _terrain.CommandsCompleted && _admissions.WhenIdle.IsCompleted && _workers.CurrentCount == 2
            && !Read<bool>(_terrain, "_cohortIndexPending") && Read<ICollection>(_terrain, "_retired").Count == 0
            && Read<object>(_terrain, "_spreadSeed") == null && Read<object>(_terrain, "_spreadRound") == null
            && Read<object>(_terrain, "_spreadSearch") == null && !Read<bool>(_terrain, "_spreadQueued")
            && Read<ICollection>(_terrain, "_spreadDirty").Count == 0;

        IEnumerator WaitFor(Func<bool> done, string phase, bool tick)
        {
            double until = Time.realtimeSinceStartupAsDouble + 90;
            do
            {
                if (tick) TickOnce();
                if (done()) yield break;
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(until), phase);
                yield return null;
            } while (true);
        }

        void TickOnce()
        {
            CaptureNativeSamples();
            if (_lastTickFrame == Time.frameCount) return;
            RememberOwnedWork();
            double now = Time.realtimeSinceStartupAsDouble, interval = now - _lastTickAt;
            _lastTickFrame = Time.frameCount; _lastTickAt = now;
            _terrain.Tick(true, now, (float)interval);
            double end = Time.realtimeSinceStartupAsDouble;
            if (_measuring) _trial.ticks.Add(new TickObservation { frame = Time.frameCount, at = now,
                actualIntervalMs = interval * 1000, cpuWallMs = (end - now) * 1000 });
            RememberOwnedWork(); CaptureNativeSamples();
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        void CaptureNativeSamples()
        {
            if (_trial == null || _terrain == null) return;
            _trial.nativeSamples = _terrain.InspectGravityStartupSamples();
            if (_trial.drawDiagnosticObservedWallAt < 0 && _trial.nativeSamples.Any(sample => sample.firstPresentedAt >= 0))
                _trial.drawDiagnosticObservedWallAt = Time.realtimeSinceStartupAsDouble;
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (!Application.isPlaying) yield break;
            _measuring = false;
            if (_report != null && !ReferenceEquals(_terrain, null)) _report.lastPhase += "; teardown cleanup";
            SaveReport(); // Preserve an incomplete report if a functional assertion failed.
            yield return CleanupTerrain();
            if (_report != null) { _report.cleanupJoined = true; SaveReport(); }
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        IEnumerator CleanupTerrain()
        {
            _measuring = false;
            var terrain = _terrain;
            if (!ReferenceEquals(terrain, null))
            {
                terrain.PublishedCommit -= ObservePublication;
                RememberOwnedWork(); terrain.Clear(terrain.Round + 1); RememberOwnedWork();
            }
            if (_root != null) Object.DestroyImmediate(_root);
            yield return WaitFor(() => CleanupJoined(terrain), "actual worker tasks and native resources joined/destroyed", false);
            foreach (var task in _sorts) Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            foreach (var pending in _pending) foreach (var task in PendingTasks(pending))
                Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            _terrain = null; _root = null;
            _pending.Clear(); _sorts.Clear(); _meshes.Clear(); _views.Clear(); _materials.Clear();
        }

        void RememberOwnedWork()
        {
            if (ReferenceEquals(_terrain, null)) return;
            var current = Read<object>(_terrain, "_pending"); if (current != null) _pending.Add(current);
            foreach (var entry in (IEnumerable)Read<object>(_terrain, "_retired")) _pending.Add(Read<object>(entry, "Pending"));
            foreach (var pending in _pending) RememberPendingResources(pending);
            foreach (var view in Read<IDictionary>(_terrain, "_views").Values) RememberView(view);
            var mesh = Read<Mesh>(_terrain, "_fallMesh"); var material = Read<Material>(_terrain, "_material");
            if (!ReferenceEquals(mesh, null)) _meshes.Add(mesh);
            if (!ReferenceEquals(material, null)) _materials.Add(material);
            foreach (var command in (IEnumerable)Read<object>(_terrain, "_commands"))
            { var task = Read<Task>(command, "Sorting"); if (task != null) _sorts.Add(task); }
        }

        bool CleanupJoined(BlockTerrain terrain)
        {
            foreach (var pending in _pending) RememberPendingResources(pending);
            if (_admissions != null && !_admissions.WhenIdle.IsCompleted || _workers != null && _workers.CurrentCount != 2) return false;
            if (_sorts.Any(task => !task.IsCompleted)) return false;
            if (!ReferenceEquals(terrain, null) && (Read<object>(terrain, "_pending") != null || Read<ICollection>(terrain, "_retired").Count != 0)) return false;
            foreach (var pending in _pending)
                if (PendingTasks(pending).Any(task => !task.IsCompleted) || Read<int>(pending, "AdmissionsDisposed") != 1) return false;
            return _meshes.All(mesh => mesh == null) && _views.All(view => view == null) && _materials.All(material => material == null);
        }

        static IEnumerable<Task> PendingTasks(object pending)
        {
            yield return Read<Task>(pending, "Generate");
            var scheduled = Property<Task>(Read<object>(pending, "Scheduled"), "Task"); yield return scheduled;
            if (scheduled.Status == TaskStatus.RanToCompletion)
                foreach (var task in Property<Task[]>(scheduled, "Result")) yield return task;
            var pieces = Read<Array>(pending, "Pieces");
            if (pieces != null) foreach (var piece in pieces)
                if (piece != null) { var bake = Read<Task>(piece, "Bake"); if (bake != null) yield return bake; }
        }

        void RememberPendingResources(object pending)
        {
            var pieces = Read<Array>(pending, "Pieces"); if (pieces == null) return;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                var render = Read<Mesh>(piece, "RenderMesh"); var collision = Read<Mesh>(piece, "CollisionMesh");
                if (!ReferenceEquals(render, null)) _meshes.Add(render);
                if (!ReferenceEquals(collision, null)) _meshes.Add(collision);
                RememberView(Read<object>(piece, "View"));
            }
        }
        void RememberView(object view)
        {
            if (view == null) return;
            var obj = Read<GameObject>(view, "Object");
            var render = Read<Mesh>(view, "RenderMesh"); var collision = Read<Mesh>(view, "CollisionMesh");
            if (!ReferenceEquals(obj, null)) _views.Add(obj);
            if (!ReferenceEquals(render, null)) _meshes.Add(render);
            if (!ReferenceEquals(collision, null)) _meshes.Add(collision);
        }

        void SaveReport()
        {
            if (_report == null || _reportPath == null) return;
            _report.summaries = _report.trials.Where(trial => trial.completed).GroupBy(trial => trial.height).Select(group =>
            {
                var values = group.Select(trial => trial.activationEventLatencyMs).OrderBy(value => value).ToArray();
                return new HeightSummary { height = group.Key, count = values.Length, minimumMs = values[0],
                    medianMs = (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2,
                    maximumMs = values[values.Length - 1], atOrBelow100Ms = values.Count(value => value <= 100) };
            }).ToArray();
            File.WriteAllText(_reportPath, JsonUtility.ToJson(_report, true));
        }
        static T Read<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
        static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name).GetValue(target);
        static T Static<T>(string name) => (T)typeof(BlockTerrain).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    }
}
#endif
