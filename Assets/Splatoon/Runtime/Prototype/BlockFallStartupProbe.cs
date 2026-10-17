#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Splatoon.Blocks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Prototype
{
    /// <summary>Explicit graphical Player diagnostic; never enabled in an ordinary game.</summary>
    public sealed class BlockFallStartupProbe : MonoBehaviour
    {
        const string OutputFlag = "-blockFallStartupOutput";
        const int Repetitions = 30, FireCommands = 300, FireCells = 32;
        const double TrialDeadline = 15, CleanupDeadline = 15, WholeDeadline = 1800;
        const double FireInterval = .1, FireDrainDeadline = 30;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Vector3Int Origin = new(20000, 0, 20000);
        static BlockFallStartupProbe _instance;

        [Serializable] public sealed class Trial
        {
            public string mode, error, image;
            public int height, repetition, callbacks, added, removedEnemy, removedFriendly, sourceCellsWitnessed;
            public bool completed, actualHeightVerified, sourceUnanchored = true, noBacklogAtQueue, cameraDrawBoundaryObserved;
            public Vector3Int firstCell;
            public ulong cohort;
            public uint sourceRevision, activationRevision;
            public int sourceFrame, activationFrame, drawFrame;
            public double queuedAt, sourcePublishedAt = -1, callbackAt = -1, activatedAt = -1, drawAt = -1;
            public double preparationQueuedAt = -1, preparationCallbackAt = -1, preparationQuietAt = -1;
            public double publicationToActivationMs = -1, activationToDrawMs = -1, publicationToDrawMs = -1;
            public float activationOffset, drawOffset;
            public int pendingAtQueue, unstableAtQueue, fallingAtQueue;
            public long outstandingAtQueue;
            [NonSerialized] public readonly HashSet<Vector3Int> Witnessed = new();
        }
        [Serializable] public sealed class QueueSample
        {
            public double at, tickMs, gravityStageMs, commandStageMs, synchronousMs, headAgeMs, pendingAgeMs;
            public string phase, pendingPhase;
            public int frame, pending, unstable, falling, blocks, priorityColumns, priorityMotions;
            public long queued, completed, gravityChecks, commandChecks, spreadMoves;
        }
        [Serializable] public sealed class PendingSample
        {
            public long id;
            public string scope, phase;
            public double generationMs, uploadMs, bakeTailMs, readyWaitMs, publicationMs, totalMs;
            public bool generationObserved, uploadCompleted, bakeObserved, publishEntered;
        }
        [Serializable] public sealed class Budget
        {
            public int gravityChecksWithCommands = BlockPublicationPolicy.GravityChecksWithCommands;
            public int maximumChecks = BlockPublicationPolicy.MaximumChecks;
            public double gravityMsWithCommands = BlockPublicationPolicy.GravityMillisecondsWithCommands;
            public double gravityMsAlone = BlockPublicationPolicy.GravityMillisecondsAlone;
            public double frameSynchronousMs;
        }
        [Serializable] public sealed class Result
        {
            public string schema = "block-fall-startup-player-v1";
            public string scope = "Opt-in graphical Development Player. Production QueueBuild/QueueDestroy, real map collider, real asynchronous world/collider publication, one authority Tick per natural frame with unmodified wall clock and dt. Not paid weapon input, damage, network or full match acceptance.";
            public string fixture = "Low load: sparse 5000m world, cell (20000,0,20000), physical 0.08m square pedestal top Y=0. No neighbouring floor support and no spread override. New columns start cell Y=20; support-removal columns start Y=1. Every source cell comes from QueueBuild. Independent fresh terrain per trial.";
            public string drawEvidence = "Normal BlockTerrain.LateUpdate -> Graphics.DrawMeshInstanced -> RenderPipelineManager.endCameraRendering for the active main camera. Read-only occupied production matrices match an activated World.Falling cell displaced >1mm. Matrix/draw-boundary evidence, not proof of first visible GPU pixel. One normal-backbuffer PNG per mode/height follows >=1cm actual displacement after timing is recorded.";
            public string sustainedScope = "300 QueueBuild commands of 32 cells, wall schedule 10Hz for 30s with natural hitch catch-up, distinct source columns over a broad real floor, then at most 30s natural drain. No workload/timeout/budget override. This is a bounded diagnostic, not the existing 250k acceptance workload.";
            public string runtimeMvid, unityVersion, graphicsApi, gpu, startedUtc, finishedUtc, error, phase;
            public bool editor, graphical, normalMainCamera, completed, functionalPassed, latencyTargetsPassed, cleanupJoined;
            public int width, height, targetFrameRate, vSyncCount, expectedLowLoadTrials = 240, sustainedCommandsQueued;
            public bool sustainedDrained, sustainedAllCallbacksFull;
            public double elapsedSeconds, trialDeadlineSeconds = TrialDeadline, wholeDeadlineSeconds = WholeDeadline;
            public double sustainedStartedAt = -1, sustainedFiringFinishedAt = -1, sustainedDrainFinishedAt = -1;
            public Budget budget = new();
            public List<Trial> trials = new();
            public List<QueueSample> queueSamples = new();
            public List<PendingSample> pendingSamples = new();
            public List<string> errors = new();
        }

        readonly Result _result = new();
        readonly Dictionary<Vector2Int, Trial> _observed = new();
        readonly HashSet<Vector2Int> _observationComplete = new();
        BlockTerrain _terrain;
        GameObject _root;
        PrototypeApp _app;
        Camera _oldCamera, _camera;
        bool _oldEnabled, _appEnabled, _finished, _ownsOutput, _ticking;
        string _output;
        double _started, _lastTick, _nextSample, _lastSave;
        Trial _trial;
        BlockWorkerQueue _workers;
        SemaphoreSlim _physicalWorkers;
        Matrix4x4[] _matrices;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, OutputFlag);
            if (_instance != null || at < 0) return;
            if (at + 1 >= args.Length || args[at + 1].StartsWith("-", StringComparison.Ordinal))
            { Debug.LogError(OutputFlag + " requires a new output directory"); Application.Quit(2); return; }
            var go = new GameObject("Block fall startup Player probe"); DontDestroyOnLoad(go);
            _instance = go.AddComponent<BlockFallStartupProbe>(); _instance._output = Path.GetFullPath(args[at + 1]);
        }

        IEnumerator Start()
        {
            _started = Time.realtimeSinceStartupAsDouble; _result.startedUtc = DateTime.UtcNow.ToString("O");
            Application.runInBackground = true; Application.logMessageReceived += OnLog;
            RenderPipelineManager.endCameraRendering += CameraRendered;
            _result.runtimeMvid = typeof(BlockTerrain).Assembly.ManifestModule.ModuleVersionId.ToString();
            _result.unityVersion = Application.unityVersion; _result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            _result.gpu = SystemInfo.graphicsDeviceName; _result.editor = Application.isEditor;
            _result.graphical = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && SystemInfo.supportsInstancing;
            _workers = Static<BlockWorkerQueue>("WorkerAdmissions"); _physicalWorkers = Static<SemaphoreSlim>("Workers");
            var stack = new Stack<IEnumerator>(); stack.Push(Run());
            while (!_finished)
            {
                object next = null; bool moved = false;
                try
                {
                    if (stack.Count == 0) break;
                    var run = stack.Peek(); moved = run.MoveNext();
                    if (!moved) { stack.Pop(); continue; }
                    next = run.Current;
                    if (next is IEnumerator nested) { stack.Push(nested); continue; }
                }
                catch (Exception error) { Finish(error.ToString()); }
                if (_finished || !moved) break;
                yield return next;
            }
            if (!_finished) Finish(null);
        }

        void Update()
        {
            if (_finished) return;
            if (Time.realtimeSinceStartupAsDouble - _started >= WholeDeadline)
            { Finish("Probe reached the unchanged 1800 second wall deadline; incomplete trials remain incomplete"); return; }
            if (_ticking && _terrain != null)
            {
                double now = Time.realtimeSinceStartupAsDouble, interval = now - _lastTick; _lastTick = now;
                try { _terrain.Tick(true, now, (float)interval); }
                catch (Exception error) { Finish(error.ToString()); return; }
                double duration = (Time.realtimeSinceStartupAsDouble - now) * 1000;
                if (now >= _nextSample) { AddQueueSample(now, duration); _nextSample = now + (_trial == null ? .25 : 0); }
                if (_terrain.FailedTransactions > 0) Finish("Real terrain transaction failure: " + _terrain.FailedTransactions);
            }
            // Avoid report serialization/disk work inside a low-load latency window.
            if (_ownsOutput && _trial == null && Time.realtimeSinceStartupAsDouble - _lastSave >= 2) SaveProgress();
        }

        IEnumerator Run()
        {
            Need(!Directory.Exists(_output) && !File.Exists(_output), "Existing output must be preserved");
            Directory.CreateDirectory(_output); _ownsOutput = true; SaveProgress();
            Need(_result.graphical, "Graphical Player with instancing is required");
            var args = Environment.GetCommandLineArgs();
            Need(Environment.GetEnvironmentVariable("SPLATOON_DISABLE_SYSTEM_CURSOR") == "1"
                || Array.Exists(args, arg => string.Equals(arg, "-noSystemCursor", StringComparison.OrdinalIgnoreCase)), "Explicit no-system-cursor launch required");
            while (PrototypeApp.Current == null || !PrototypeApp.Current.Ready) yield return null;
            _app = PrototypeApp.Current; Need(!_app.InRoom && !_app.Busy, "Fresh Player must not already be in a room");
            _app.CaptureMouse(false); _appEnabled = _app.enabled; _app.enabled = false;
            _oldCamera = Camera.main; Need(_oldCamera != null, "Boot main camera is required");
            _oldEnabled = _oldCamera.enabled; _camera = new GameObject("Fall startup normal main camera").AddComponent<Camera>();
            _camera.CopyFrom(_oldCamera); _camera.tag = "MainCamera"; _oldCamera.enabled = false;
            _camera.targetTexture = null; _camera.targetDisplay = 0; _camera.rect = new Rect(0, 0, 1, 1);
            _camera.cullingMask = (1 << BlockTerrain.TerrainLayer) | 1;
            _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = new Color(.025f, .025f, .025f, 1);
            _camera.nearClipPlane = .05f; _camera.farClipPlane = 50; _camera.fieldOfView = 45;
            _camera.enabled = true; _result.width = Screen.width; _result.height = Screen.height;
            _result.targetFrameRate = Application.targetFrameRate; _result.vSyncCount = QualitySettings.vSyncCount;
            _result.normalMainCamera = Camera.main == _camera && _camera.targetTexture == null;
            Need(_result.normalMainCamera, "Fixture must use a live normal main camera, with no render texture");
            foreach (string mode in new[] { "new-unsupported", "support-removed" })
                foreach (int height in new[] { 1, 8, 16, 32 })
                    for (int repetition = 0; repetition < Repetitions; repetition++)
                    {
                        _result.phase = mode + "/" + height + "/" + repetition;
                        var trial = new Trial { mode = mode, height = height, repetition = repetition,
                            firstCell = Origin + Vector3Int.up * (mode == "new-unsupported" ? 20 : 1) };
                        _result.trials.Add(trial); _trial = trial;
                        yield return RunTrial(trial);
                        if (!trial.completed) throw new InvalidOperationException("Incomplete trial: " + _result.phase + "; " + trial.error);
                        yield return DestroyTerrain(); _trial = null; SaveProgress();
                    }
            _result.phase = "sustained-fire"; yield return SustainedFire();
            yield return DestroyTerrain(); _result.cleanupJoined = WorkersIdle(); _result.completed = true;
            _result.functionalPassed = _result.trials.Count(t => t.mode != "sustained") == _result.expectedLowLoadTrials
                && _result.trials.All(t => t.completed) && _result.sustainedAllCallbacksFull && _result.sustainedDrained && _result.cleanupJoined;
            // Acceptance is per height/mode; no pooled percentile can hide a failing height.
            _result.latencyTargetsPassed = _result.trials.Where(t => t.mode != "sustained")
                .GroupBy(t => t.mode + "/" + t.height).All(group =>
                {
                    var activation = group.Select(t => t.publicationToActivationMs).OrderBy(v => v).ToArray();
                    var draw = group.Select(t => t.publicationToDrawMs).OrderBy(v => v).ToArray();
                    return activation.Length >= Repetitions && activation.All(v => v >= 0)
                        && draw.All(v => v >= 0) && Percentile(activation, .95) <= 100 && activation.Last() <= 250
                        && Percentile(draw, .95) <= 150 && draw.Last() <= 300;
                });
        }

        IEnumerator RunTrial(Trial trial)
        {
            CreateTerrain(false);
            Vector3 look = BlockWorld.Center(trial.firstCell) + Vector3.up * ((trial.height - 1) * .05f);
            _camera.transform.position = look + new Vector3(1.3f, .8f, -7); _camera.transform.LookAt(look);
            _observed.Add(new Vector2Int(Origin.x, Origin.z), trial);
            if (trial.mode == "support-removed")
            {
                bool prepared = false; BlockMutationResult preparation = default;
                trial.preparationQueuedAt = Time.realtimeSinceStartupAsDouble;
                _terrain.QueueBuild(BuildPoint(Origin), Vector3.up, 2, trial.height + 1, 0, false,
                    value => { preparation = value; prepared = true; trial.preparationCallbackAt = Time.realtimeSinceStartupAsDouble; }, ordinaryMainAttack: true);
                double until = trial.preparationQueuedAt + TrialDeadline;
                while (!prepared && Time.realtimeSinceStartupAsDouble < until) yield return null;
                Need(prepared && preparation.Added == trial.height + 1, "Real support-removal preparation must build the exact full column");
                while (!Quiet() && Time.realtimeSinceStartupAsDouble < until) yield return null;
                Need(Quiet(), "Support-removal preparation did not naturally drain before its unchanged 15s deadline");
                trial.preparationQuietAt = Time.realtimeSinceStartupAsDouble;
                for (int y = 0; y <= trial.height; y++)
                    Need(_terrain.World.TryGet(Origin + Vector3Int.up * y, out var cell) && cell.Owner == 2 && !cell.Anchored,
                        "Support-removal preparation must remain a real contiguous unanchored stable column");
                // Preparation publication is not the measured support-removal event.
                trial.Witnessed.Clear(); trial.sourceCellsWitnessed = 0; trial.sourcePublishedAt = -1;
            }
            trial.pendingAtQueue = _terrain.PendingTransactions; trial.unstableAtQueue = _terrain.UnstableColumns;
            trial.fallingAtQueue = _terrain.FallingCount; trial.outstandingAtQueue = _terrain.CommandsQueued - _terrain.CommandsCompleted;
            trial.noBacklogAtQueue = trial.pendingAtQueue == 0 && trial.unstableAtQueue == 0 && trial.fallingAtQueue == 0 && trial.outstandingAtQueue == 0;
            Need(trial.noBacklogAtQueue && Quiet(), "A low-load measured event requires no pending gravity, spread, commands or real workers");
            trial.queuedAt = Time.realtimeSinceStartupAsDouble;
            if (trial.mode == "new-unsupported")
                _terrain.QueueBuild(BuildPoint(trial.firstCell), Vector3.up, 2, trial.height, 0, false,
                    value => Callback(trial, value), ordinaryMainAttack: true);
            else
                _terrain.QueueDestroy(BlockWorld.Center(Origin), .0001f, 0, 1, true,
                    value => Callback(trial, value), maxEnemyBlocks: 1, maxFriendlyBlocks: 0, ordinaryMainAttack: true);
            double deadline = trial.queuedAt + TrialDeadline;
            bool captured = false;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                bool sourceComplete = trial.callbacks == 1 && trial.Witnessed.Count == trial.height;
                if (sourceComplete && trial.cameraDrawBoundaryObserved && trial.repetition != 0) break;
                yield return new WaitForEndOfFrame();
                sourceComplete = trial.callbacks == 1 && trial.Witnessed.Count == trial.height;
                if (sourceComplete && trial.cameraDrawBoundaryObserved && trial.repetition == 0 && !captured && CurrentOffset(trial) >= .01f)
                {
                    trial.image = trial.mode + "-h" + trial.height + ".png"; Capture(trial.image); captured = true;
                }
                if (sourceComplete && trial.cameraDrawBoundaryObserved && (trial.repetition != 0 || captured)) break;
            }
            trial.actualHeightVerified = trial.Witnessed.Count == trial.height;
            trial.completed = trial.callbacks == 1 && trial.activatedAt >= trial.sourcePublishedAt && trial.sourcePublishedAt >= trial.queuedAt
                && trial.cameraDrawBoundaryObserved && trial.actualHeightVerified && trial.sourceUnanchored
                && (trial.mode == "new-unsupported" ? trial.added == trial.height && trial.removedEnemy == 0 : trial.added == 0 && trial.removedEnemy == 1)
                && trial.removedFriendly == 0 && (trial.repetition != 0 || captured);
            if (!trial.completed) trial.error = "Trial did not meet full source/callback/activation/draw/height observation before 15s deadline";
        }

        IEnumerator SustainedFire()
        {
            CreateTerrain(true); _trial = null; var rows = new List<Trial>();
            _camera.transform.position = BlockWorld.Center(Origin) + new Vector3(8, 10, -14);
            _camera.transform.LookAt(BlockWorld.Center(Origin) + new Vector3(3, 2, 4));
            _result.sustainedStartedAt = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < FireCommands; i++)
            {
                double at = _result.sustainedStartedAt + i * FireInterval;
                while (Time.realtimeSinceStartupAsDouble < at) yield return null;
                var cell = Origin + new Vector3Int(i % 15 * 5, 20, i / 15 * 5);
                var row = new Trial { mode = "sustained", height = FireCells, repetition = i, firstCell = cell, queuedAt = Time.realtimeSinceStartupAsDouble };
                rows.Add(row); _result.trials.Add(row); _observed.Add(new Vector2Int(cell.x, cell.z), row);
                _terrain.QueueBuild(BuildPoint(cell), Vector3.up, 2, FireCells, 0, false,
                    value => Callback(row, value), ordinaryMainAttack: true); _result.sustainedCommandsQueued++;
            }
            while (Time.realtimeSinceStartupAsDouble < _result.sustainedStartedAt + FireCommands * FireInterval) yield return null;
            _result.sustainedFiringFinishedAt = Time.realtimeSinceStartupAsDouble; _result.phase = "sustained-drain";
            double until = _result.sustainedFiringFinishedAt + FireDrainDeadline;
            while (!Quiet() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            _result.sustainedDrained = Quiet(); _result.sustainedDrainFinishedAt = Time.realtimeSinceStartupAsDouble;
            foreach (var row in rows)
            {
                row.actualHeightVerified = row.Witnessed.Count == row.height;
                row.completed = row.callbacks == 1 && row.added == FireCells && row.removedEnemy == 0 && row.removedFriendly == 0
                    && row.actualHeightVerified && row.sourceUnanchored && row.activatedAt >= row.sourcePublishedAt && row.sourcePublishedAt >= row.queuedAt;
            }
            _result.sustainedAllCallbacksFull = rows.All(row => row.callbacks == 1 && row.added == FireCells && row.removedEnemy == 0 && row.removedFriendly == 0);
            AddQueueSample(Time.realtimeSinceStartupAsDouble, 0); SaveProgress();
        }

        void CreateTerrain(bool broadFloor)
        {
            Need(_terrain == null && WorkersIdle(), "Prior real workers must be joined before a fresh fixture");
            _root = new GameObject("Owned fall startup fixture");
            var layout = new GameObject("Inactive registered physical layout"); layout.transform.SetParent(_root.transform, false); layout.SetActive(false);
            var arena = layout.AddComponent<PrototypeArena>(); arena.Dimensions = new Vector2(5000, 5000); arena.SpawnPoints = Array.Empty<Transform>();
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = broadFloor ? "Real broad floor" : "Real narrow pedestal";
            floor.transform.SetParent(layout.transform, false);
            floor.transform.position = BlockWorld.Center(Origin) + new Vector3(broadFloor ? 3.5f : 0, -.15f, broadFloor ? 4.75f : 0);
            floor.transform.localScale = broadFloor ? new Vector3(20, .2f, 24) : new Vector3(.08f, .2f, .08f);
            _terrain = _root.AddComponent<BlockTerrain>(); _terrain.Initialize(arena);
            floor.transform.SetParent(_root.transform, true); Physics.SyncTransforms();
            Need(Math.Abs(floor.GetComponent<Collider>().bounds.max.y) < .00001f, "Real floor top must be exactly Y=0");
            _terrain.PublishedCommit += Published; _terrain.PendingTransactionFinished += PendingFinished;
            _matrices = Read<Matrix4x4[]>(_terrain, "_fallMatrices");
            _result.budget.frameSynchronousMs = _terrain.PublicationPolicy.FrameSynchronousMilliseconds;
            _lastTick = Time.realtimeSinceStartupAsDouble; _nextSample = 0; _ticking = true;
        }

        void Callback(Trial trial, BlockMutationResult value)
        {
            trial.callbacks++; trial.callbackAt = Time.realtimeSinceStartupAsDouble;
            trial.added += value.Added; trial.removedEnemy += value.RemovedEnemy; trial.removedFriendly += value.RemovedFriendly;
        }

        void Published(BlockTerrainCommit commit)
        {
            double wall = Time.realtimeSinceStartupAsDouble;
            foreach (var row in _observed.Values)
            {
                if (row.queuedAt <= 0 || row.completed) continue;
                for (int y = 0; row.Witnessed.Count < row.height && y < row.height; y++)
                {
                    var cell = row.firstCell + Vector3Int.up * y;
                    if (_terrain.World.TryGet(cell, out var value) && value.Owner == 2)
                    { row.Witnessed.Add(cell); row.sourceUnanchored &= !value.Anchored; }
                }
                row.sourceCellsWitnessed = row.Witnessed.Count;
                bool eventObserved = row.mode == "support-removed" ? !_terrain.World.TryGet(Origin, out _) : row.Witnessed.Count > 0;
                if (eventObserved && row.sourcePublishedAt < 0)
                { row.sourcePublishedAt = wall; row.sourceRevision = commit.Revision; row.sourceFrame = Time.frameCount; }
            }
            foreach (var run in _terrain.World.Falling.Values)
            {
                if (!_observed.TryGetValue(new Vector2Int(run.Bottom.x, run.Bottom.z), out var row) || row.queuedAt <= 0 || row.completed) continue;
                for (int i = 0; i < run.Cells.Length; i++)
                {
                    var origin = run.Bottom + Vector3Int.up * i;
                    if (origin.y >= row.firstCell.y && origin.y < row.firstCell.y + row.height && run.Cells[i].Owner == 2)
                    { row.Witnessed.Add(origin); row.sourceUnanchored &= !run.Cells[i].Anchored; }
                }
                if (row.mode != "support-removed" && row.Witnessed.Count > 0 && row.sourcePublishedAt < 0)
                { row.sourcePublishedAt = wall; row.sourceRevision = commit.Revision; row.sourceFrame = Time.frameCount; }
                var cohort = run.Cohort;
                if (cohort != null && row.activatedAt < 0 && row.sourcePublishedAt >= 0
                    && (cohort.Phase == FallingCohortPhase.Falling || cohort.Phase == FallingCohortPhase.Settling))
                {
                    row.activatedAt = wall; row.activationRevision = commit.Revision; row.activationFrame = Time.frameCount;
                    row.cohort = cohort.Id; row.activationOffset = run.Offset(_terrain.SimulationTime);
                    row.publicationToActivationMs = (wall - row.sourcePublishedAt) * 1000;
                }
                row.sourceCellsWitnessed = row.Witnessed.Count;
                if (row.Witnessed.Count == row.height && row.activatedAt >= 0)
                    _observationComplete.Add(new Vector2Int(row.firstCell.x, row.firstCell.z));
            }
            // Output rows stay in Result; old fully witnessed/activated columns
            // stop imposing per-cell observation work on later sustained shots.
            foreach (var column in _observationComplete) _observed.Remove(column);
            _observationComplete.Clear();
        }

        void CameraRendered(ScriptableRenderContext context, Camera camera)
        {
            if (_finished || _terrain == null || camera != _camera || _trial == null || _trial.activatedAt < 0 || _trial.cameraDrawBoundaryObserved) return;
            var row = _trial;
            if (Camera.main != camera || camera.targetTexture != null || !camera.isActiveAndEnabled)
            { Finish("Normal main camera changed"); return; }
            int occupied = _terrain.FallingCount; if (occupied <= 0 || occupied > _matrices.Length) return;
            foreach (var run in _terrain.World.Falling.Values)
            {
                if (run.Cohort == null || run.Cohort.Id != row.cohort) continue;
                float offset = run.Offset(_terrain.SimulationTime); if (offset - row.activationOffset <= .001f) continue;
                int first = Array.FindIndex(run.Cells, cell => cell.Occupied); if (first < 0) continue;
                Vector3 expected = BlockWorld.Center(run.Bottom + Vector3Int.up * first) - Vector3.up * offset;
                Vector3 viewport = camera.WorldToViewportPoint(expected);
                if (viewport.z <= 0 || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) continue;
                for (int i = 0; i < occupied; i++)
                {
                    Vector3 actual = _matrices[i].GetColumn(3); if ((actual - expected).sqrMagnitude > .0000001f) continue;
                    row.cameraDrawBoundaryObserved = true; row.drawAt = Time.realtimeSinceStartupAsDouble; row.drawFrame = Time.frameCount;
                    row.drawOffset = offset; row.activationToDrawMs = (row.drawAt - row.activatedAt) * 1000;
                    row.publicationToDrawMs = (row.drawAt - row.sourcePublishedAt) * 1000; return;
                }
            }
        }

        float CurrentOffset(Trial row) => _terrain.World.Cohorts.TryGetValue(row.cohort, out var group) ? group.Offset(_terrain.SimulationTime) : 0;
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
        bool WorkersIdle() => _workers.WhenIdle.IsCompleted && _physicalWorkers.CurrentCount == 2;
        bool Quiet() => _terrain.PendingTransactions == 0 && _terrain.UnstableColumns == 0 && _terrain.FallingCount == 0
            && _terrain.CommandsQueued == _terrain.CommandsCompleted && WorkersIdle()
            && !Read<bool>(_terrain, "_cohortIndexPending") && Read<ICollection>(_terrain, "_retired").Count == 0
            && Read<object>(_terrain, "_spreadSeed") == null && Read<object>(_terrain, "_spreadRound") == null
            && Read<object>(_terrain, "_spreadSearch") == null && !Read<bool>(_terrain, "_spreadQueued")
            && Read<ICollection>(_terrain, "_spreadDirty").Count == 0;

        IEnumerator DestroyTerrain()
        {
            if (_terrain == null) yield break;
            _ticking = false; var terrain = _terrain; _observed.Clear(); _observationComplete.Clear();
            terrain.PublishedCommit -= Published; terrain.PendingTransactionFinished -= PendingFinished;
            terrain.Clear(terrain.Round + 1); Destroy(_root); _root = null; _terrain = null;
            double until = Time.realtimeSinceStartupAsDouble + CleanupDeadline;
            while (!WorkersIdle() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            yield return null;
            Need(WorkersIdle(), "Real terrain workers did not join before cleanup deadline");
        }

        void AddQueueSample(double now, double tickMs)
        {
            if (_result.queueSamples.Count >= 100000) return;
            var head = _terrain.InspectCommandHead(); var pending = _terrain.InspectPendingTransaction();
            _result.queueSamples.Add(new QueueSample { at = now, frame = Time.frameCount, phase = _result.phase, tickMs = tickMs,
                gravityStageMs = _terrain.LastGravityStageMilliseconds, commandStageMs = _terrain.LastCommandStageMilliseconds,
                synchronousMs = _terrain.TerrainSynchronousMilliseconds, pending = _terrain.PendingTransactions,
                unstable = _terrain.UnstableColumns, falling = _terrain.FallingCount, blocks = _terrain.PinkCount + _terrain.BlueCount,
                queued = _terrain.CommandsQueued, completed = _terrain.CommandsCompleted, headAgeMs = head.ageMs,
                pendingPhase = pending.present ? pending.phase.ToString() : "none", pendingAgeMs = pending.ageMs,
                gravityChecks = _terrain.GravityWorkChecks, commandChecks = _terrain.CommandWorkChecks,
                spreadMoves = _terrain.SpreadMoves, priorityColumns = _terrain.GravityStartupPriorityColumns, priorityMotions = _terrain.GravityStartupPriorityMotions });
        }
        void PendingFinished(BlockTerrain.PendingWallDiagnostic item)
        {
            if (_result.pendingSamples.Count >= 20000) return;
            _result.pendingSamples.Add(new PendingSample { id = item.Id, scope = _result.phase, phase = item.Outcome.ToString(), generationMs = item.GenerationObservedMs,
                uploadMs = item.UploadMs, bakeTailMs = item.BakeObservedTailMs, readyWaitMs = item.ReadyWaitMs,
                publicationMs = item.PublicationMs, totalMs = item.TotalMs, generationObserved = item.GenerationObserved,
                uploadCompleted = item.UploadCompleted, bakeObserved = item.BakeObserved, publishEntered = item.PublishEntered });
        }

        void Capture(string name)
        {
            Need(Camera.main == _camera && _camera.targetTexture == null, "Only normal backbuffer capture is allowed");
            var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0); texture.Apply(false);
            var bytes = texture.EncodeToPNG(); Destroy(texture); WriteNew(name, bytes);
        }
        void SaveProgress()
        {
            _lastSave = Time.realtimeSinceStartupAsDouble; _result.elapsedSeconds = _lastSave - _started;
            File.WriteAllText(Path.Combine(_output, "progress.json"), JsonUtility.ToJson(_result, true));
        }
        void WriteNew(string name, byte[] bytes)
        {
            using var stream = new FileStream(Path.Combine(_output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }
        void OnLog(string message, string stack, LogType type)
        { if (!_finished && (type == LogType.Error || type == LogType.Exception) && _result.errors.Count < 20) _result.errors.Add(message); }
        void Finish(string error)
        {
            if (_finished) return; _finished = true; _ticking = false;
            Application.logMessageReceived -= OnLog; RenderPipelineManager.endCameraRendering -= CameraRendered;
            _result.error = error; _result.finishedUtc = DateTime.UtcNow.ToString("O"); _result.elapsedSeconds = Time.realtimeSinceStartupAsDouble - _started;
            if (_result.errors.Count > 0 || error != null) { _result.functionalPassed = false; _result.latencyTargetsPassed = false; }
            try { if (_ownsOutput) WriteNew("summary.json", System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(_result, true))); }
            catch (Exception failure) { _result.functionalPassed = false; Debug.LogError("Startup report write failed: " + failure.Message); }
            if (_terrain != null) { _terrain.PublishedCommit -= Published; _terrain.PendingTransactionFinished -= PendingFinished; _terrain.Clear(_terrain.Round + 1); }
            if (_root != null) Destroy(_root); if (_camera != null) Destroy(_camera.gameObject);
            if (_oldCamera != null) _oldCamera.enabled = _oldEnabled; if (_app != null) _app.enabled = _appEnabled;
            if (!Application.isEditor) Application.Quit(_result.functionalPassed && _result.latencyTargetsPassed ? 0 : 2);
        }
        static double Percentile(double[] sorted, double quantile) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * quantile) - 1)];
        static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static T Read<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
        static T Static<T>(string name) => (T)typeof(BlockTerrain).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    }
}
#endif
