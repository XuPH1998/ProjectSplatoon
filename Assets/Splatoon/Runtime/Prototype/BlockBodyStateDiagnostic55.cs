#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Diagnostic input only. No physics policy, native query or simulation belongs here.
    internal static partial class BlockBodyStateDiagnostic55
    {
        const int BodyCap = 8192, PlayerCap = 16, CaptureCap = 3;
        const double DrainSeconds = 10;
        static Run _run;
        internal static string Status => _run == null ? "absent" : _run.Status;

        internal static bool Initialize(string output, string session, string label, string mode)
        {
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "-dynamicProbeBodyState55");
            if (flag < 0) return false;
            if (_run != null) return true; // One request per process; never rearm.
            _run = new Run(output, session, label);
            string source = Argument(args, "-dynamicProbeBodyStateSource55");
            string package = Argument(args, "-dynamicProbeBodyStatePackage55");
            _run.Bundle.sourceManifestSha256 = source; _run.Bundle.packageInventorySha256 = package;
            if (flag + 1 >= args.Length || !double.TryParse(args[flag + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds < 0 || mode != "natural" || !Hash(source) || !Hash(package))
            { _run.Seal("invalid-explicit-request"); return true; }
            _run.Bundle.requestedServerSeconds = seconds;
            if (Application.isEditor) { _run.Seal("editor-is-not-independent-player"); return true; }
            _run.Allocate();
            return true;
        }
        static string Argument(string[] args, string key)
        { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
        static bool Hash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static void WorldEntry(PrototypeMatch match, BlockDynamicWorld world)
        { _run?.Entry(match, world); }
        internal static void MatchTail(PrototypeMatch match, BlockDynamicWorld world, double now)
        { _run?.Tail(match, world, now); }
        internal static void Poll(PrototypeMatch match, bool finished)
        { _run?.Poll(match, finished); }
        internal static void Close() { _run?.Seal("probe-finished-before-complete-window"); }
        internal static bool ReadyToQuit() => _run == null || _run.ReadyToQuit();
        internal static void Destroy()
        {
            if (_run == null) return;
            _run.Seal("probe-destroyed-before-complete-window");
            // Normal finish polls first. Unexpected destruction may only perform a bounded drain outside fixed callbacks.
            if (!Time.inFixedTimeStep) _run.Drain(1000);
        }

        [Serializable] internal struct V3 { public float x, y, z; public V3(Vector3 v) { x = v.x; y = v.y; z = v.z; } }
        [Serializable] internal struct Q4 { public float x, y, z, w; public Q4(Quaternion v) { x = v.x; y = v.y; z = v.z; w = v.w; } }
        [Serializable] internal sealed class Bundle
        {
            public int schema = 1, diagnosticVersion = 1, bodyCap = BodyCap, playerCap = PlayerCap, captureCap = CaptureCap;
            public bool diagnosticOnly = true, complete, sealedWindow;
            public string session, label, requestId, sourceManifestSha256, packageInventorySha256, unity, buildGuid, terminalReason;
            public string role = "unobserved";
            public int processId, simulationVersion;
            public double requestedServerSeconds, actualFirstServerSeconds, latenessServerSeconds;
            public long stopwatchFrequency;
            public int captureCount;
            public ContextNote lastObservedContext;
            public Capture[] captures;
        }
        [Serializable] internal struct ContextNote
        {
            public bool matchPresent, matchSpawned, matchEnabled, matchActive, server, listening;
            public bool terrainPresent, terrainInitialized, worldPresent, worldEnabled, worldActive, authority, frozen;
            public int matchInstance, worldInstance, unityFrame, phase;
            public uint matchRound, worldRound;
            public double realtime, fixedTime;
            public string rejection;
        }
        [Serializable] internal sealed class Capture
        {
            public string boundary;
            public Header start, end;
            public long started, ended;
            public double copyMilliseconds;
            public int bodyVisited, bodyCaptured, bodyReadErrors, bodyIdentityErrors, bodyNonfinite, bodyNotAttempted;
            public int playerVisited, playerCaptured, playerReadErrors, playerNonfinite, playerNotAttempted;
            public int bodyMissingComponents, bodyMissingNative, bodyMissingColliders, bodyExceptions, playerMissingComponents, playerExceptions;
            public bool bodyOverflow, playerOverflow, headerDrift, headerFinite, complete;
            public BodyRow[] bodies;
            public PlayerRow[] players;
        }
        [Serializable] internal struct Header
        {
            public int matchInstance, terrainInstance, worldInstance, voxelWorldIdentity, unityFrame, phase, simulationMode;
            public long fixedTick, entryOrdinal, ngoTick;
            public uint matchRound, worldRound, voxelRound, lifecycle, stateTick;
            public bool fixedContext, hasMatchNow, server, host, listening, matchSpawned, matchEnabled, matchActive;
            public bool worldEnabled, worldActive, authority, frozen, terrainEnabled, terrainActive, terrainInitialized;
            public double fixedTime, fixedDelta, matchNow, terrainTime, ngoServerTime, startsAt, endsAt;
            public float timeScale, dimensionsX, dimensionsZ, bottom, top, cellSize, activationRadius, deactivationRadius;
            public int expectedBodies, expectedPlayers;
        }
        [Serializable] internal struct BodyRow
        {
            public ulong dictionaryKey, id;
            public uint worldRound;
            public byte owner;
            public int componentInstance, nativeInstance, ownerWorldInstance, readMask, error;
            public bool componentEnabled, activeSelf, activeHierarchy, colliderPresent, colliderEnabled, colliderActive;
            public bool sleeping, kinematic, gravity, detectCollisions, parked, finite, identityValid;
            public V3 position, velocity, angularVelocity;
            public Q4 rotation;
        }
        [Serializable] internal struct PlayerRow
        {
            public int slotIndex, componentInstance, error;
            public ulong owner;
            public bool spawned, server, ownerRole, componentEnabled, activeHierarchy, snapshotRead;
            public bool alive, bubble, dead, finite, includedByActivation, authorityScope;
            public int exclusion, hero, lifeState, slot;
            public byte team;
            public uint life, heroRevision, simulationTick;
            public double simulatedAt;
            public float airHumanOffset;
            public V3 position, humanPosition;
        }

        sealed class Run
        {
            internal Bundle Bundle;
            readonly string _path;
            PrototypeMatch _match;
            BlockDynamicWorld _world;
            int _matchId, _worldId;
            uint _round;
            long _aTick, _entryOrdinal, _sealedAt;
            bool _sealed, _timedOut;
            Task<WriteOutcome> _write;
            internal string Status = "armed";

            internal Run(string output, string session, string label)
            {
                _path = Path.GetFullPath(output + ".body-state55.json");
                Bundle = new Bundle { session = session, label = label, requestId = Guid.NewGuid().ToString("N"),
                    unity = Application.unityVersion, buildGuid = Application.buildGUID,
                    processId = Process.GetCurrentProcess().Id, simulationVersion = GameplayContentSignature.WeaponSimulationVersion,
                    stopwatchFrequency = Stopwatch.Frequency };
            }
            internal void Allocate()
            {
                Bundle.captures = new Capture[CaptureCap];
                for (int i = 0; i < CaptureCap; i++) Bundle.captures[i] = new Capture
                    { bodies = new BodyRow[BodyCap], players = new PlayerRow[PlayerCap] };
            }
            internal void Entry(PrototypeMatch match, BlockDynamicWorld world)
            {
                if (_sealed || Bundle.captures == null) return;
                ObserveContext(match, world);
                _entryOrdinal++;
                if (Bundle.captureCount == 0) return;
                string bad = Guard(match, world);
                if (bad != null) { Seal(bad); return; }
                if (!TryTick(out long tick)) { Seal("invalid-fixed-context"); return; }
                if (Bundle.captureCount != 1 || tick != _aTick + 1) { Seal("missing-or-unexpected-world-entry-tick"); return; }
                Copy("world-entry(k+1)", match, world, double.NaN, false);
            }
            internal void Tail(PrototypeMatch match, BlockDynamicWorld world, double now)
            {
                if (_sealed || Bundle.captures == null) return;
                ObserveContext(match, world);
                if (Bundle.captureCount == 0)
                {
                    if (!Normal(match, world)) return;
                    var state = match.State.Value;
                    if (state.Phase != MatchPhase.Playing || !double.IsFinite(now) || now >= state.EndsAt
                        || now - state.StartsAt < Bundle.requestedServerSeconds) return;
                    _match = match; _world = world; _matchId = match.GetInstanceID(); _worldId = world.GetInstanceID(); _round = world.Round;
                    if (state.Round != _round || match.Blocks.World.Round != _round) { Seal("round-mismatch-at-arm"); return; }
                    if (!TryTick(out _aTick)) { Seal("invalid-fixed-context"); return; }
                    Bundle.actualFirstServerSeconds = now - state.StartsAt;
                    Bundle.latenessServerSeconds = Bundle.actualFirstServerSeconds - Bundle.requestedServerSeconds;
                    Bundle.role = match.IsHost ? "host-authority" : "server-authority";
                    Copy("match-tail(k)", match, world, now, true); return;
                }
                string bad = Guard(match, world);
                if (bad != null) { Seal(bad); return; }
                if (!TryTick(out long tick)) { Seal("invalid-fixed-context"); return; }
                if (Bundle.captureCount != 2 || tick != _aTick + 1) { Seal("missing-or-unexpected-match-tail-tick"); return; }
                Copy("match-tail(k+1)", match, world, now, true);
                Seal("window-complete");
            }
            static bool Normal(PrototypeMatch match, BlockDynamicWorld world)
            {
                return match != null && world != null && match == PrototypeMatch.Current && match.enabled && match.gameObject.activeInHierarchy
                    && match.IsSpawned && match.IsServer && match.NetworkManager != null && match.NetworkManager.IsListening
                    && match.Blocks != null && match.Blocks.Initialized && match.Blocks.World != null && match.Blocks.Dynamic == world
                    && world.enabled && world.gameObject.activeInHierarchy && world.IsAuthority && !world.Frozen;
            }
            void ObserveContext(PrototypeMatch match, BlockDynamicWorld world)
            {
                // One replaceable scalar summary only; no extra body/player enumeration or native reads.
                var note = new ContextNote { matchPresent = match != null, worldPresent = world != null,
                    unityFrame = Time.frameCount, realtime = Time.realtimeSinceStartupAsDouble, fixedTime = Time.fixedTimeAsDouble };
                if (match != null)
                {
                    note.matchInstance = match.GetInstanceID(); note.matchSpawned = match.IsSpawned; note.matchEnabled = match.enabled;
                    note.matchActive = match.gameObject.activeInHierarchy; note.server = match.IsServer;
                    note.listening = match.NetworkManager != null && match.NetworkManager.IsListening;
                    var state = match.State.Value; note.phase = (int)state.Phase; note.matchRound = state.Round;
                    note.terrainPresent = match.Blocks != null; note.terrainInitialized = note.terrainPresent && match.Blocks.Initialized;
                }
                if (world != null)
                {
                    note.worldInstance = world.GetInstanceID(); note.worldRound = world.Round; note.worldEnabled = world.enabled;
                    note.worldActive = world.gameObject.activeInHierarchy; note.authority = world.IsAuthority; note.frozen = world.Frozen;
                }
                note.rejection = !note.matchPresent ? "match-absent" : !note.matchSpawned ? "match-unspawned"
                    : !note.matchEnabled || !note.matchActive ? "match-disabled" : !note.server || !note.listening ? "not-listening-server"
                    : !note.terrainInitialized || !note.worldPresent ? "world-unavailable" : !note.worldEnabled || !note.worldActive ? "world-disabled"
                    : !note.authority || note.frozen ? "world-not-running-authority" : Normal(match, world) ? "none" : "unlinked-context";
                Bundle.lastObservedContext = note;
            }
            string Guard(PrototypeMatch match, BlockDynamicWorld world)
            {
                if (!Normal(match, world)) return "lost-normal-authority-context";
                if (match != _match || world != _world || match.GetInstanceID() != _matchId || world.GetInstanceID() != _worldId)
                    return "match-or-world-replaced";
                var state = match.State.Value;
                if (world.Round != _round || state.Round != _round || match.Blocks.World.Round != _round) return "round-replaced";
                if (state.Phase != MatchPhase.Playing) return "playing-phase-ended";
                return null;
            }
            static bool TryTick(out long tick)
            {
                tick = 0;
                double dt = Time.fixedDeltaTime, time = Time.fixedTimeAsDouble;
                if (!Time.inFixedTimeStep || Physics.simulationMode != SimulationMode.FixedUpdate || !double.IsFinite(dt) || dt <= 0
                    || !double.IsFinite(time)) return false;
                double ratio = time / dt;
                if (!double.IsFinite(ratio) || ratio < 0 || ratio >= long.MaxValue) return false;
                tick = (long)Math.Round(ratio); return true;
            }
            Header ReadHeader(PrototypeMatch match, BlockDynamicWorld world, double now, bool hasNow)
            {
                var state = match.State.Value; var terrain = match.Blocks; var voxel = terrain.World;
                TryTick(out long tick);
                return new Header { matchInstance = match.GetInstanceID(), terrainInstance = terrain.GetInstanceID(), worldInstance = world.GetInstanceID(),
                    voxelWorldIdentity = RuntimeHelpers.GetHashCode(voxel), unityFrame = Time.frameCount, phase = (int)state.Phase,
                    simulationMode = (int)Physics.simulationMode, fixedContext = Time.inFixedTimeStep, fixedTick = tick, entryOrdinal = _entryOrdinal,
                    ngoTick = match.NetworkManager.ServerTime.Tick, stateTick = state.Tick, matchRound = state.Round, worldRound = world.Round,
                    voxelRound = voxel.Round, lifecycle = world.LifecycleRevision, hasMatchNow = hasNow, matchNow = now,
                    server = match.IsServer, host = match.IsHost, listening = match.NetworkManager.IsListening, matchSpawned = match.IsSpawned,
                    matchEnabled = match.enabled, matchActive = match.gameObject.activeInHierarchy, worldEnabled = world.enabled,
                    worldActive = world.gameObject.activeInHierarchy, authority = world.IsAuthority, frozen = world.Frozen,
                    terrainEnabled = terrain.enabled, terrainActive = terrain.gameObject.activeInHierarchy, terrainInitialized = terrain.Initialized, fixedTime = Time.fixedTimeAsDouble,
                    fixedDelta = Time.fixedDeltaTime, timeScale = Time.timeScale, terrainTime = terrain.SimulationTime,
                    ngoServerTime = match.NetworkManager.ServerTime.Time, startsAt = state.StartsAt, endsAt = state.EndsAt,
                    dimensionsX = voxel.Dimensions.x, dimensionsZ = voxel.Dimensions.y, bottom = voxel.Bottom, top = BlockWorld.Top,
                    cellSize = BlockWorld.CellSize, activationRadius = BlockDynamicWorld.ActivationRadius,
                    deactivationRadius = BlockDynamicWorld.DeactivationRadius, expectedBodies = world.Count, expectedPlayers = match.Players.Count };
            }
            static bool Same(Header a, Header b)
            {
                return a.matchInstance == b.matchInstance && a.terrainInstance == b.terrainInstance && a.worldInstance == b.worldInstance
                    && a.voxelWorldIdentity == b.voxelWorldIdentity && a.unityFrame == b.unityFrame && a.phase == b.phase
                    && a.fixedTick == b.fixedTick && a.entryOrdinal == b.entryOrdinal && a.fixedTime == b.fixedTime && a.fixedDelta == b.fixedDelta
                    && a.matchRound == b.matchRound && a.worldRound == b.worldRound && a.voxelRound == b.voxelRound && a.lifecycle == b.lifecycle
                    && a.ngoTick == b.ngoTick && a.stateTick == b.stateTick && a.expectedBodies == b.expectedBodies && a.expectedPlayers == b.expectedPlayers
                    && a.server == b.server && a.host == b.host && a.listening == b.listening && a.matchSpawned == b.matchSpawned && a.matchEnabled == b.matchEnabled
                    && a.matchActive == b.matchActive && a.worldEnabled == b.worldEnabled && a.worldActive == b.worldActive && a.authority == b.authority && a.frozen == b.frozen
                    && a.terrainEnabled == b.terrainEnabled && a.terrainActive == b.terrainActive && a.terrainInitialized == b.terrainInitialized
                    && a.simulationMode == b.simulationMode && a.fixedContext == b.fixedContext && a.timeScale == b.timeScale
                    && a.hasMatchNow == b.hasMatchNow && (!a.hasMatchNow || a.matchNow == b.matchNow)
                    && a.terrainTime == b.terrainTime && a.ngoServerTime == b.ngoServerTime && a.startsAt == b.startsAt && a.endsAt == b.endsAt
                    && a.dimensionsX == b.dimensionsX && a.dimensionsZ == b.dimensionsZ && a.bottom == b.bottom && a.top == b.top
                    && a.cellSize == b.cellSize && a.activationRadius == b.activationRadius && a.deactivationRadius == b.deactivationRadius;
            }
            static bool HeaderFinite(Header h) => double.IsFinite(h.fixedTime) && double.IsFinite(h.fixedDelta) && h.fixedDelta > 0
                && (!h.hasMatchNow || double.IsFinite(h.matchNow)) && double.IsFinite(h.terrainTime) && double.IsFinite(h.ngoServerTime)
                && double.IsFinite(h.startsAt) && double.IsFinite(h.endsAt) && float.IsFinite(h.timeScale)
                && float.IsFinite(h.dimensionsX) && float.IsFinite(h.dimensionsZ) && float.IsFinite(h.bottom) && float.IsFinite(h.top)
                && float.IsFinite(h.cellSize) && float.IsFinite(h.activationRadius) && float.IsFinite(h.deactivationRadius);
            void Copy(string boundary, PrototypeMatch match, BlockDynamicWorld world, double now, bool hasNow)
            {
                var capture = Bundle.captures[Bundle.captureCount++];
                capture.boundary = boundary; capture.started = Stopwatch.GetTimestamp();
                try
                {
                    capture.start = ReadHeader(match, world, now, hasNow);
                    capture.bodyOverflow = capture.start.expectedBodies > BodyCap; capture.playerOverflow = capture.start.expectedPlayers > PlayerCap;
                    foreach (var pair in world.Bodies)
                    {
                        if (capture.bodyVisited == BodyCap) break;
                        ref BodyRow row = ref capture.bodies[capture.bodyVisited++];
                        row.dictionaryKey = pair.Key; row.worldRound = world.Round;
                        try
                        {
                            var block = pair.Value;
                            if (block == null) { row.error = 1; capture.bodyReadErrors++; capture.bodyMissingComponents++; continue; }
                            row.id = block.Id; row.owner = block.Owner; row.componentInstance = block.GetInstanceID();
                            row.ownerWorldInstance = block.World != null ? block.World.GetInstanceID() : 0;
                            row.componentEnabled = block.enabled; row.activeSelf = block.gameObject.activeSelf;
                            row.activeHierarchy = block.gameObject.activeInHierarchy; row.parked = block.Parked; row.readMask = 1;
                            row.identityValid = pair.Key == block.Id && block.Id != 0 && block.World == world && block.Owner is 1 or 2;
                            if (!row.identityValid) capture.bodyIdentityErrors++;
                            var body = block.Body;
                            if (body == null) { row.error = 2; capture.bodyReadErrors++; capture.bodyMissingNative++; continue; }
                            row.nativeInstance = body.GetInstanceID(); row.readMask |= 2;
                            Vector3 position = body.position, velocity = body.linearVelocity, angular = body.angularVelocity;
                            Quaternion rotation = body.rotation;
                            row.position = new V3(position); row.velocity = new V3(velocity); row.angularVelocity = new V3(angular); row.rotation = new Q4(rotation);
                            row.finite = Finite(position) && Finite(velocity) && Finite(angular) && Finite(rotation); row.readMask |= 4;
                            if (!row.finite) capture.bodyNonfinite++;
                            row.sleeping = body.IsSleeping(); row.kinematic = body.isKinematic; row.gravity = body.useGravity;
                            row.detectCollisions = body.detectCollisions; row.readMask |= 8;
                            var collider = block.Collider; row.colliderPresent = collider != null;
                            if (collider == null) { row.error = 4; capture.bodyReadErrors++; capture.bodyMissingColliders++; continue; }
                            row.colliderEnabled = collider.enabled; row.colliderActive = collider.gameObject.activeInHierarchy;
                            row.readMask |= 16; capture.bodyCaptured++;
                        }
                        catch (Exception) { row.error = 3; capture.bodyReadErrors++; capture.bodyExceptions++; }
                    }
                    int playerCount = Math.Min(capture.start.expectedPlayers, PlayerCap);
                    for (int i = 0; i < playerCount; i++)
                    {
                        ref PlayerRow row = ref capture.players[capture.playerVisited++]; row.slotIndex = i;
                        try
                        {
                            var player = match.Players[i];
                            if (player == null) { row.error = 1; row.exclusion = 1; capture.playerReadErrors++; capture.playerMissingComponents++; continue; }
                            row.owner = player.PlayerId; row.componentInstance = player.GetInstanceID(); row.spawned = player.IsSpawned;
                            row.server = player.IsServer; row.ownerRole = player.IsOwner; row.componentEnabled = player.enabled;
                            row.activeHierarchy = player.gameObject.activeInHierarchy; row.authorityScope = player.IsAuthorityBlockPushStep;
                            var state = player.Snapshot.Value; row.snapshotRead = true;
                            row.life = state.Revision; row.heroRevision = state.HeroRevision; row.simulationTick = state.SimulationTick;
                            row.simulatedAt = state.SimulatedAt; row.hero = state.HeroId; row.lifeState = (int)state.LifeState;
                            row.slot = state.Slot; row.team = state.Team; row.alive = state.IsAlive; row.bubble = state.IsBubble; row.dead = state.IsDead;
                            row.position = new V3(state.Position); row.airHumanOffset = state.AirHumanOffset;
                            Vector3 human = PlayerMotorSimulation.HumanPosition(state); row.humanPosition = new V3(human);
                            row.finite = Finite(human); // Exact production CollectPlayers predicate, not an alive-only subset.
                            if (!row.finite || !Finite(state.Position) || !float.IsFinite(state.AirHumanOffset) || !double.IsFinite(state.SimulatedAt)) capture.playerNonfinite++;
                            row.includedByActivation = row.spawned && row.finite; row.exclusion = !row.spawned ? 2 : !row.finite ? 3 : 0;
                            capture.playerCaptured++;
                        }
                        catch (Exception) { row.error = 2; row.exclusion = 4; capture.playerReadErrors++; capture.playerExceptions++; }
                    }
                    capture.end = ReadHeader(match, world, now, hasNow);
                    capture.headerDrift = !Same(capture.start, capture.end);
                    capture.headerFinite = HeaderFinite(capture.start) && HeaderFinite(capture.end);
                }
                catch (Exception) { capture.headerDrift = true; }
                capture.ended = Stopwatch.GetTimestamp();
                capture.copyMilliseconds = (capture.ended - capture.started) * 1000.0 / Stopwatch.Frequency;
                capture.bodyNotAttempted = Math.Max(0, capture.start.expectedBodies - capture.bodyVisited);
                capture.playerNotAttempted = Math.Max(0, capture.start.expectedPlayers - capture.playerVisited);
                capture.complete = !capture.headerDrift && capture.headerFinite && !capture.bodyOverflow && !capture.playerOverflow
                    && capture.bodyVisited == capture.start.expectedBodies && capture.bodyCaptured == capture.bodyVisited
                    && capture.playerVisited == capture.start.expectedPlayers && capture.playerCaptured == capture.playerVisited
                    && capture.bodyReadErrors == 0 && capture.playerReadErrors == 0 && capture.bodyIdentityErrors == 0
                    && capture.bodyNonfinite == 0 && capture.playerNonfinite == 0;
                if (capture.headerDrift) Seal("context-drift-during-copy");
            }
            internal void Poll(PrototypeMatch match, bool finished)
            {
                if (!_sealed) ObserveContext(match, match != null ? match.Blocks?.Dynamic : null);
                if (!_sealed && Bundle.captureCount > 0)
                {
                    string bad = Guard(match, _world);
                    if (bad != null) Seal(bad);
                    else if (Time.fixedTimeAsDouble > Bundle.captures[0].start.fixedTime + 2.5 * Bundle.captures[0].start.fixedDelta)
                        Seal("next-fixed-callback-not-observed");
                }
                if (finished) Seal("probe-finished-before-complete-window");
                ReadyToQuit();
            }
            internal void Seal(string reason)
            {
                if (_sealed) return;
                _sealed = true; Bundle.sealedWindow = true;
                Bundle.terminalReason = Bundle.captureCount == 0 && Bundle.lastObservedContext.rejection != null
                    && Bundle.lastObservedContext.rejection != "none" ? reason + ":" + Bundle.lastObservedContext.rejection : reason;
                Bundle.complete = Bundle.captureCount == CaptureCap && reason == "window-complete";
                if (Bundle.complete) for (int i = 0; i < Bundle.captureCount; i++) Bundle.complete &= Bundle.captures[i].complete;
                Status = Bundle.complete ? "writing-complete-window" : "writing-partial-window";
                _sealedAt = Stopwatch.GetTimestamp();
                // The raw DTO has no Unity object references and is immutable after this handoff.
                // The worker derives its separate validation DTO, including duplicate identities.
                _write = StartWrite(Bundle, _path, _sealedAt + (long)(DrainSeconds * Stopwatch.Frequency));
                _match = null; _world = null;
            }
            internal bool ReadyToQuit()
            {
                if (!_sealed) return false;
                if (_write != null && _write.IsCompleted)
                {
                    Status = _timedOut ? "write-deadline-exceeded-incomplete" : _write.Status == TaskStatus.RanToCompletion && _write.Result.completed
                        ? _write.Result.deadlineExceeded ? "write-deadline-exceeded-incomplete"
                            : _write.Result.windowComplete ? "complete-written" : "partial-written" : "write-failed-incomplete";
                    Bundle = null; // The worker completed; release this process's large copied row buffers.
                    return true;
                }
                if ((Stopwatch.GetTimestamp() - _sealedAt) / (double)Stopwatch.Frequency < DrainSeconds) return false;
                _timedOut = true; Status = "write-timeout-incomplete"; return true;
            }
            internal void Drain(int milliseconds)
            {
                if (_write == null || _write.IsCompleted) return;
                try { if (!_write.Wait(milliseconds)) { _timedOut = true; Status = "destroy-drain-timeout-incomplete"; } }
                catch (Exception) { Status = "write-failed-incomplete"; }
            }
        }
        static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);
    }
}
#endif
