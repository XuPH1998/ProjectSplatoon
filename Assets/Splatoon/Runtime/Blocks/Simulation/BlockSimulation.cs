using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public struct BlockSimulationDiagnostics
    {
        public long TickCount, CapacityGrowths, SolverFaults;
        public int BodyCount, ActiveCount, RestingCount, PairCount, ContactCount, DynamicContactCount, HashEntryCount;
        public int CcdClips, SupportWakes, RangeWakes, CandidateRequired, CandidateEmitted;
        public float MaxPenetration, MaxCorrection;
        public double StepMilliseconds;
        public double BeginWakeMilliseconds, BroadphaseMilliseconds, ContactsMilliseconds, VelocityMilliseconds;
        public int BroadphaseCalls, BroadphaseAllActiveCalls;
        public double BroadphasePrepareMilliseconds, BroadphaseCountMilliseconds, BroadphasePrefixMilliseconds;
        public double BroadphaseEmitMilliseconds, BroadphaseSortMilliseconds;
        public double MoveProjectMilliseconds, FinishCommitMilliseconds;
        public int FaultCode, ClosurePasses, UnsafePairs;
        public long AxisPivots, AxisConstraintChecks;
        public long NormalBlockPasses, ObliqueUpdates, FrictionRetractions;
        public float AxisResidual, NormalResidual, FrictionConeExcess;
    }

    public struct BlockSimClosureSample
    {
        public int Substep, Pass, PairCount, ContactCount, UnsafePairs;
        public BlockSimHandle A, B;
        public float Fraction, EndPenetration;
        public float3 RelativePosition, RelativeVelocity, Normal;
    }

    /// <summary>
    /// Independent authoritative, translation-only box simulation. No GameObject, Collider or PhysX state.
    /// Mutations and Step are synchronous: readers never see a partially completed job.
    /// Static snapshots are immutable while Step is running. Call NotifySupportChanged before replacing geometry.
    /// Dynamic CCD uses swept-AABB TOI normals and speculative velocity constraints; refreshed swept pairs close
    /// the set after impulses change velocity. It reaches the available separation rather than stopping at the
    /// start of a future contact. Static motion and positional corrections additionally sweep the exact map.
    /// </summary>
    public sealed partial class BlockSimulation : IDisposable
    {
        public const float Edge = .30f, Mass = .5f, StaticFriction = .35f, DynamicFriction = .2f;
        public const float CellSize = .6f, ContactSkin = .002f, RestGap = .0005f, PenetrationSlop = .0005f;
        public const float MaxPositionCorrection = .005f, SleepSpeed = .03f, SleepDisplacement = .0005f;
        public const float SleepSeconds = .5f, ActivationRadius = 2f, PushSpeed = 3f;
        public const int Substeps = 2, VelocityIterations = 6, PositionIterations = 2;
        const int MaximumClosurePasses = 16;
        static readonly float3 HalfSize = new float3(Edge * .5f);
        static readonly float3 Gravity = new float3(0, -9.81f, 0);

        readonly Dictionary<BlockSimHandle, int> _indices = new();
        NativeList<BlockSimHandle> _handles;
        NativeList<byte> _owners, _external, _near, _backupNear, _validateSupport, _supported;
        NativeList<float3> _positions, _velocities, _tickPositions, _backupVelocities;
        NativeList<BlockSimMode> _modes, _backupModes;
        NativeList<uint> _versions;
        NativeList<float> _quiet, _backupQuiet, _corrections;
        NativeList<int> _counts, _offsets, _parents, _marks, _wakeQueue;
        NativeList<float3> _observers;
        NativeList<ulong> _pairs;
        NativeList<SimContact> _contacts;
        NativeList<BlockStaticHit> _mapScratch;
        NativeList<AxisNode> _axisNodes;
        NativeList<AxisEdge> _axisEdges;
        NativeList<int> _axisQueue;
        NativeParallelMultiHashMap<int3, int> _hash;
        NativeParallelHashMap<ContactKey, CachedContact> _cache;
        NativeReference<WorkResult> _work;
        readonly BlockMapCollisionData _emptyMap;
        bool _disposed, _hashDirty = true, _hasTickBackup;
        int _mark;
        BlockSimulationDiagnostics _diagnostics;
        readonly Stopwatch _clock = new();
        readonly List<BlockSimClosureSample> _closureSamples = new(MaximumClosurePasses * Substeps);

        public int Count => _handles.IsCreated ? _handles.Length : 0;
        public int Capacity => _handles.IsCreated ? _handles.Capacity : 0;
        public int ActiveCount { get { int n = 0; for (int i = 0; i < Count; i++) if (_modes[i] == BlockSimMode.Active) n++; return n; } }
        public int RestingCount { get { int n = 0; for (int i = 0; i < Count; i++) if (_modes[i] == BlockSimMode.Resting) n++; return n; } }
        public BlockSimulationDiagnostics Diagnostics => _diagnostics;
        public IReadOnlyList<BlockSimClosureSample> LastClosure => _closureSamples;

        public BlockSimulation(int initialCapacity = 128)
        {
            int n = math.max(1, initialCapacity);
            _handles = new NativeList<BlockSimHandle>(n, Allocator.Persistent);
            _owners = new NativeList<byte>(n, Allocator.Persistent);
            _external = new NativeList<byte>(n, Allocator.Persistent);
            _near = new NativeList<byte>(n, Allocator.Persistent);
            _backupNear = new NativeList<byte>(n, Allocator.Persistent);
            _validateSupport = new NativeList<byte>(n, Allocator.Persistent);
            _supported = new NativeList<byte>(n, Allocator.Persistent);
            _positions = new NativeList<float3>(n, Allocator.Persistent);
            _velocities = new NativeList<float3>(n, Allocator.Persistent);
            _tickPositions = new NativeList<float3>(n, Allocator.Persistent);
            _backupVelocities = new NativeList<float3>(n, Allocator.Persistent);
            _modes = new NativeList<BlockSimMode>(n, Allocator.Persistent);
            _backupModes = new NativeList<BlockSimMode>(n, Allocator.Persistent);
            _versions = new NativeList<uint>(n, Allocator.Persistent);
            _quiet = new NativeList<float>(n, Allocator.Persistent);
            _backupQuiet = new NativeList<float>(n, Allocator.Persistent);
            _corrections = new NativeList<float>(n, Allocator.Persistent);
            _counts = new NativeList<int>(n, Allocator.Persistent);
            _offsets = new NativeList<int>(n, Allocator.Persistent);
            _parents = new NativeList<int>(n, Allocator.Persistent);
            _marks = new NativeList<int>(n, Allocator.Persistent);
            _wakeQueue = new NativeList<int>(n, Allocator.Persistent);
            _observers = new NativeList<float3>(8, Allocator.Persistent);
            _pairs = new NativeList<ulong>(n, Allocator.Persistent);
            _contacts = new NativeList<SimContact>(n, Allocator.Persistent);
            _mapScratch = new NativeList<BlockStaticHit>(16, Allocator.Persistent);
            _axisNodes = new NativeList<AxisNode>(n * 2, Allocator.Persistent);
            _axisEdges = new NativeList<AxisEdge>(n, Allocator.Persistent);
            _axisQueue = new NativeList<int>(n * 2, Allocator.Persistent);
            InitializeNormalWork(n);
            _hash = new NativeParallelMultiHashMap<int3, int>(n, Allocator.Persistent);
            _cache = new NativeParallelHashMap<ContactKey, CachedContact>(n, Allocator.Persistent);
            _work = new NativeReference<WorkResult>(Allocator.Persistent);
            InitializeSpatialIndex(n);
            InitializeSupportDependencies(n);
            _emptyMap = new BlockMapCollisionBuilder().Build();
        }

        public bool Add(BlockSimState state)
        {
            CheckDisposed(); Validate(state);
            if (_indices.ContainsKey(state.handle)) return false;
            int capacity = Capacity, i = Count;
            _indices.Add(state.handle, i);
            _handles.Add(state.handle); _owners.Add(state.owner); _positions.Add(state.position);
            _velocities.Add(state.mode == BlockSimMode.Active ? state.velocity : float3.zero);
            _modes.Add(state.mode); _versions.Add(state.poseVersion);
            _quiet.Add(state.mode == BlockSimMode.Resting ? SleepSeconds : 0);
            _external.Add(0); _near.Add(0); _backupNear.Add(0); _supported.Add(0);
            _validateSupport.Add(state.mode == BlockSimMode.Resting ? (byte)1 : (byte)0);
            _tickPositions.Add(state.position); _backupVelocities.Add(state.velocity); _backupModes.Add(state.mode);
            _backupQuiet.Add(0); _corrections.Add(0); _counts.Add(0); _offsets.Add(0); _parents.Add(i); _marks.Add(0);
            if (Capacity != capacity) _diagnostics.CapacityGrowths++;
            SpatialBodyAdded(i);
            SupportBodyAdded(i);
            return true;
        }

        public bool Remove(BlockSimHandle handle)
        {
            CheckDisposed();
            if (!_indices.TryGetValue(handle, out int i)) return false;
            SupportBodyRemoving(i);
            int last = Count - 1;
            SpatialBodyRemoving(i, last);
            _indices.Remove(handle);
            if (i != last) _indices[_handles[last]] = i;
            _handles.RemoveAtSwapBack(i); _owners.RemoveAtSwapBack(i); _positions.RemoveAtSwapBack(i);
            _velocities.RemoveAtSwapBack(i); _modes.RemoveAtSwapBack(i); _versions.RemoveAtSwapBack(i);
            _quiet.RemoveAtSwapBack(i); _external.RemoveAtSwapBack(i); _near.RemoveAtSwapBack(i); _backupNear.RemoveAtSwapBack(i);
            _supported.RemoveAtSwapBack(i); _validateSupport.RemoveAtSwapBack(i);
            _tickPositions.RemoveAtSwapBack(i); _backupVelocities.RemoveAtSwapBack(i); _backupModes.RemoveAtSwapBack(i);
            _backupQuiet.RemoveAtSwapBack(i); _corrections.RemoveAtSwapBack(i);
            _counts.RemoveAtSwapBack(i); _offsets.RemoveAtSwapBack(i); _parents.RemoveAtSwapBack(i); _marks.RemoveAtSwapBack(i);
            _contacts.Clear(); _pairs.Clear();
            return true;
        }

        public bool TryGet(BlockSimHandle handle, out BlockSimState state)
        {
            CheckDisposed();
            if (!_indices.TryGetValue(handle, out int i)) { state = default; return false; }
            state = StateAt(i); return true;
        }

        public bool SetState(BlockSimState state)
        {
            CheckDisposed(); Validate(state);
            if (!_indices.TryGetValue(state.handle, out int i)) return false;
            _positions[i] = state.position; _velocities[i] = state.mode == BlockSimMode.Active ? state.velocity : float3.zero;
            _modes[i] = state.mode; _owners[i] = state.owner; _versions[i] = state.poseVersion;
            _quiet[i] = state.mode == BlockSimMode.Resting ? SleepSeconds : 0;
            _validateSupport[i] = state.mode == BlockSimMode.Resting ? (byte)1 : (byte)0;
            _external[i] = state.mode == BlockSimMode.Active ? (byte)1 : (byte)0;
            SpatialBodyChanged(i);
            SupportBodyChanged(i);
            return true;
        }

        public bool ApplyImpulse(BlockSimHandle handle, float3 impulse)
        {
            CheckDisposed();
            if (!Finite(impulse)) throw new ArgumentException("Impulse must be finite.", nameof(impulse));
            if (!_indices.TryGetValue(handle, out int i) || _modes[i] == BlockSimMode.Frozen) return false;
            if (math.lengthsq(impulse) == 0) return true;
            if (_modes[i] == BlockSimMode.Resting) WakeConnected(i, false);
            _velocities[i] += impulse / Mass; _external[i] = 1; _quiet[i] = 0;
            return true;
        }

        /// <summary>One already-budgeted player's impulse. The caller shares its 100 N * dt budget across all contacts.</summary>
        public bool ApplyPush(BlockSimHandle handle, float3 direction, float targetSpeed, float impulseBudget)
        {
            CheckDisposed();
            if (!Finite(direction) || !math.isfinite(targetSpeed) || !math.isfinite(impulseBudget) || impulseBudget < 0)
                throw new ArgumentException("Push parameters must be finite; impulse budget must be nonnegative.");
            if (!_indices.TryGetValue(handle, out int i) || _modes[i] == BlockSimMode.Frozen) return false;
            direction.y = 0; float length = math.length(direction);
            if (length < 1e-8f || impulseBudget == 0) return true;
            direction /= length;
            float impulse = math.min(impulseBudget, Mass * math.max(0, math.min(PushSpeed, math.max(0, targetSpeed)) - math.dot(_velocities[i], direction)));
            return ApplyImpulse(handle, direction * impulse);
        }

        public void Wake(BlockSimHandle handle)
        { CheckDisposed(); if (_indices.TryGetValue(handle, out int i)) WakeConnected(i, false); }

        public void SetObservers(float3[] positions)
        {
            CheckDisposed(); _observers.Clear();
            if (positions == null) return;
            for (int i = 0; i < positions.Length; i++)
            { if (!Finite(positions[i])) throw new ArgumentException("Observer must be finite.", nameof(positions)); _observers.Add(positions[i]); }
        }

        public void NotifySupportChanged(BlockAabb bounds)
        {
            CheckDisposed(); if (!bounds.IsFinite) throw new ArgumentException("Invalid changed bounds.", nameof(bounds));
            SupportMapChanged(bounds);
        }

        public void Query(BlockAabb bounds, List<BlockSimHandle> destination)
        {
            CheckDisposed(); if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!bounds.IsFinite) throw new ArgumentException("Invalid query bounds.", nameof(bounds));
            destination.Clear(); EnsureQueryHash(); NextMark();
            int3 lo = Grid(bounds.Min - HalfSize), hi = Grid(bounds.Max + HalfSize);
            for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
            {
                if (!_hash.TryGetFirstValue(new int3(x, y, z), out int index, out var it)) continue;
                do
                {
                    if (_marks[index] == _mark) continue; _marks[index] = _mark;
                    if (bounds.Overlaps(new BlockAabb(_positions[index], HalfSize))) destination.Add(_handles[index]);
                } while (_hash.TryGetNextValue(out index, ref it));
            }
            destination.Sort(CompareHandle);
        }

        public void CopyStates(List<BlockSimState> destination)
        {
            CheckDisposed(); if (destination == null) throw new ArgumentNullException(nameof(destination));
            ReadCommittedStates(destination);
        }

        public void Step(BlockMapSnapshot map, float dt)
        {
            CheckDisposed();
            if (!math.isfinite(dt) || dt <= 0 || dt > .1f) throw new ArgumentOutOfRangeException(nameof(dt));
            _hasTickBackup = false;
            try { StepCore(map, dt); }
            catch
            {
                if (_hasTickBackup)
                {
                    Rollback();
                    SupportAfterRollback(map.IsCreated ? map : _emptyMap.Snapshot);
                }
                else _diagnostics.SolverFaults++;
                throw;
            }
        }

        void StepCore(BlockMapSnapshot map, float dt)
        {
            if (!map.IsCreated) map = _emptyMap.Snapshot;
            _clock.Restart();
            _closureSamples.Clear(); _diagnostics.FaultCode = 0; _diagnostics.ClosurePasses = 0; _diagnostics.UnsafePairs = 0;
            _normalDiagnostics.Value = default;
            _diagnostics.BroadphaseCalls = _diagnostics.BroadphaseAllActiveCalls = 0;
            _diagnostics.BroadphasePrepareMilliseconds = _diagnostics.BroadphaseCountMilliseconds = 0;
            _diagnostics.BroadphasePrefixMilliseconds = _diagnostics.BroadphaseEmitMilliseconds = _diagnostics.BroadphaseSortMilliseconds = 0;
            long stage = Stopwatch.GetTimestamp();
            double beginTime = 0, broadTime = 0, contactTime = 0, velocityTime = 0, moveTime = 0, finishTime = 0;
            var work = new WorkResult(); _work.Value = work;
            new BeginTickJob { Positions = _positions.AsArray(), Velocities = _velocities.AsArray(), Modes = _modes.AsArray(),
                Quiet = _quiet.AsArray(), TickPositions = _tickPositions.AsArray(), BackupVelocities = _backupVelocities.AsArray(),
                BackupModes = _backupModes.AsArray(), BackupQuiet = _backupQuiet.AsArray(), Corrections = _corrections.AsArray(),
                Near = _near.AsArray(), BackupNear = _backupNear.AsArray(), Observers = _observers.AsArray(), Result = _work }.Schedule().Complete();
            _hasTickBackup = true;
            SupportBeforeTick(map);
            beginTime += StageElapsed(ref stage);
            float h = dt / Substeps;
            for (int substep = 0; substep < Substeps; substep++)
            {
                new GravityJob { Velocities = _velocities.AsArray(), Modes = _modes.AsArray(), Delta = h }.Schedule(Count, 64).Complete();
                beginTime += StageElapsed(ref stage);
                bool safe = false;
                for (int pass = 0; pass < MaximumClosurePasses; pass++)
                {
                    BuildPairs(h);
                    broadTime += StageElapsed(ref stage);
                    GenerateContacts(map, h, pass == 0, pass > 0);
                    contactTime += StageElapsed(ref stage);
                    int woken = 0;
                    while (true)
                    {
                        int queueCapacity = _wakeQueue.Capacity;
                        new WakeContactsJob { Contacts = _contacts, Modes = _modes.AsArray(), Velocities = _velocities.AsArray(),
                            Positions = _positions.AsArray(), Hash = _hash, Queue = _wakeQueue,
                            Quiet = _quiet.AsArray(), Parents = _parents.AsArray(), Seeds = _counts.AsArray(), Delta = h, Result = _work }.Schedule().Complete();
                        if (_wakeQueue.Capacity != queueCapacity) _diagnostics.CapacityGrowths++;
                        beginTime += StageElapsed(ref stage);
                        if (_work.Value.NewWakes == 0) break;
                        woken += _work.Value.NewWakes;
                        if (woken > Count) throw new InvalidOperationException("Wake closure did not make progress; tick was rolled back.");
                        BuildPairs(h); broadTime += StageElapsed(ref stage);
                        GenerateContacts(map, h, pass == 0, true); contactTime += StageElapsed(ref stage);
                    }
                    if (pass == 0)
                        new WarmStartJob { Contacts = _contacts, Velocities = _velocities.AsArray(), Modes = _modes.AsArray() }.Schedule().Complete();
                    SolveAxisNormals(h);
                    ThrowNormalFault(substep, pass, "pre-friction");
                    new VelocityJob { Contacts = _contacts, Velocities = _velocities.AsArray(), Modes = _modes.AsArray(), Delta = h }.Schedule().Complete();
                    SolveAxisNormals(h);
                    ThrowNormalFault(substep, pass, "post-friction");
                    velocityTime += StageElapsed(ref stage);
                    // A solver impulse can introduce a new swept pair. Verify against a freshly rebuilt hash.
                    BuildPairs(h);
                    broadTime += StageElapsed(ref stage);
                    new VerifyMotionJob { Pairs = _pairs, Contacts = _contacts, Handles = _handles.AsArray(),
                        Positions = _positions.AsArray(), Velocities = _velocities.AsArray(),
                        Modes = _modes.AsArray(), Delta = h, Result = _work }.Schedule().Complete();
                    var verification = _work.Value;
                    _closureSamples.Add(new BlockSimClosureSample { Substep = substep, Pass = pass, PairCount = _pairs.Length,
                        ContactCount = _contacts.Length, UnsafePairs = verification.UnsafePairs,
                        A = verification.UnsafeA < 0 ? default : _handles[verification.UnsafeA],
                        B = verification.UnsafeB < 0 ? default : _handles[verification.UnsafeB],
                        Fraction = verification.UnsafeFraction, EndPenetration = verification.UnsafeEndPenetration,
                        RelativePosition = verification.UnsafePosition, RelativeVelocity = verification.UnsafeVelocity,
                        Normal = verification.UnsafeNormal });
                    _diagnostics.ClosurePasses++; _diagnostics.UnsafePairs = verification.UnsafePairs;
                    velocityTime += StageElapsed(ref stage);
                    if (_work.Value.UnsafePairs == 0) { safe = true; break; }
                }
                if (!safe)
                {
                    _diagnostics.FaultCode = 1;
                    BlockSimClosureSample sample = _closureSamples[_closureSamples.Count - 1];
                    throw new InvalidOperationException($"AABB contact closure did not converge; tick={_diagnostics.TickCount}, substep={substep}, pass={sample.Pass}, unsafe={sample.UnsafePairs}, A={sample.A}, B={sample.B}, relativePosition={sample.RelativePosition}, relativeVelocity={sample.RelativeVelocity}, normal={sample.Normal}, fraction={sample.Fraction:R}, endPenetration={sample.EndPenetration:R}; tick was rolled back.");
                }
                EnsureCacheCapacity(_contacts.Length);
                new CacheContactsJob { Contacts = _contacts, Cache = _cache }.Schedule().Complete();
                contactTime += StageElapsed(ref stage);
                new MoveAndProjectJob { Map = map, Pairs = _pairs, Contacts = _contacts, Positions = _positions.AsArray(),
                    Handles = _handles.AsArray(), Hash = _hash, IndexedCells = _indexedCells.AsArray(),
                    Velocities = _velocities.AsArray(), Modes = _modes.AsArray(), Corrections = _corrections.AsArray(),
                    MapScratch = _mapScratch, Delta = h, Result = _work }.Schedule().Complete();
                if (_work.Value.Fault != 0)
                {
                    var fault = _work.Value; _diagnostics.FaultCode = fault.Fault;
                    throw new InvalidOperationException($"AABB motion fault={fault.Fault}, tick={_diagnostics.TickCount}, position={fault.FaultPosition}, displacement={fault.FaultDisplacement}, normal={fault.FaultNormal}, fraction={fault.FaultFraction:R}; tick was rolled back.");
                }
                moveTime += StageElapsed(ref stage);
                // Rebuild at actual positions so support and next warm start are never based on predicted contacts.
                BuildPairs(0);
                broadTime += StageElapsed(ref stage);
                GenerateContacts(map, 0, true);
                EnsureCacheCapacity(_contacts.Length);
                new CacheContactsJob { Contacts = _contacts, Cache = _cache }.Schedule().Complete();
                contactTime += StageElapsed(ref stage);
            }
            SupportBeforeFinish(map);
            new FinishTickJob { Map = map, Contacts = _contacts, MapScratch = _mapScratch, Positions = _positions.AsArray(),
                TickPositions = _tickPositions.AsArray(), Velocities = _velocities.AsArray(), Modes = _modes.AsArray(),
                Versions = _versions.AsArray(), Quiet = _quiet.AsArray(), Corrections = _corrections.AsArray(),
                External = _external.AsArray(), ValidateSupport = _validateSupport.AsArray(), Supported = _supported.AsArray(),
                Parents = _parents.AsArray(), Counts = _counts.AsArray(), Delta = dt, Result = _work }.Schedule().Complete();
            if (_work.Value.Fault != 0)
            {
                var fault = _work.Value; _diagnostics.FaultCode = fault.Fault;
                throw new InvalidOperationException($"AABB solver fault={fault.Fault}, tick={_diagnostics.TickCount}, position={fault.FaultPosition}, displacement={fault.FaultDisplacement}, normal={fault.FaultNormal}, fraction={fault.FaultFraction:R}; tick was rolled back.");
            }
            work = _work.Value;
            _clock.Stop();
            _diagnostics.TickCount++; _diagnostics.BodyCount = Count; _diagnostics.ActiveCount = work.ActiveCount;
            _diagnostics.RestingCount = work.RestingCount; _diagnostics.PairCount = _pairs.Length; _diagnostics.ContactCount = _contacts.Length;
            _diagnostics.DynamicContactCount = work.DynamicContactCount;
            _diagnostics.HashEntryCount = _hash.Count() + _activeSweepHash.Count(); _diagnostics.CcdClips = work.CcdClips;
            _diagnostics.SupportWakes = work.SupportWakes; _diagnostics.RangeWakes = work.RangeWakes;
            _diagnostics.MaxPenetration = work.MaxPenetration; _diagnostics.MaxCorrection = work.MaxCorrection;
            _diagnostics.AxisPivots = work.AxisPivots; _diagnostics.AxisConstraintChecks = work.AxisConstraintChecks;
            _diagnostics.NormalBlockPasses = work.NormalBlockPasses; _diagnostics.ObliqueUpdates = work.ObliqueUpdates;
            _diagnostics.FrictionRetractions = work.FrictionRetractions;
            _diagnostics.AxisResidual = work.AxisResidual; _diagnostics.NormalResidual = work.NormalResidual;
            _diagnostics.FrictionConeExcess = work.FrictionConeExcess;
            _diagnostics.StepMilliseconds = _clock.Elapsed.TotalMilliseconds;
            finishTime += StageElapsed(ref stage);
            _diagnostics.BeginWakeMilliseconds = beginTime; _diagnostics.BroadphaseMilliseconds = broadTime;
            _diagnostics.ContactsMilliseconds = contactTime; _diagnostics.VelocityMilliseconds = velocityTime;
            _diagnostics.MoveProjectMilliseconds = moveTime; _diagnostics.FinishCommitMilliseconds = finishTime;
        }

        public void Clear()
        {
            CheckDisposed(); _indices.Clear(); _handles.Clear(); _owners.Clear(); _positions.Clear(); _velocities.Clear();
            _modes.Clear(); _versions.Clear(); _quiet.Clear(); _external.Clear(); _near.Clear(); _backupNear.Clear(); _supported.Clear(); _validateSupport.Clear();
            _tickPositions.Clear(); _backupVelocities.Clear(); _backupModes.Clear(); _backupQuiet.Clear(); _corrections.Clear();
            _counts.Clear(); _offsets.Clear(); _parents.Clear(); _marks.Clear(); _wakeQueue.Clear();
            _pairs.Clear(); _contacts.Clear(); _cache.Clear(); _hash.Clear(); _hashDirty = true;
            _axisNodes.Clear(); _axisEdges.Clear(); _axisQueue.Clear();
            ClearSpatialIndex();
            ClearSupportDependencies();
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _handles.Dispose(); _owners.Dispose(); _positions.Dispose(); _velocities.Dispose(); _modes.Dispose(); _versions.Dispose();
            _quiet.Dispose(); _external.Dispose(); _near.Dispose(); _backupNear.Dispose(); _supported.Dispose(); _validateSupport.Dispose();
            _tickPositions.Dispose(); _backupVelocities.Dispose(); _backupModes.Dispose(); _backupQuiet.Dispose(); _corrections.Dispose();
            _counts.Dispose(); _offsets.Dispose(); _parents.Dispose(); _marks.Dispose(); _wakeQueue.Dispose(); _observers.Dispose();
            _pairs.Dispose(); _contacts.Dispose(); _mapScratch.Dispose(); _cache.Dispose(); _hash.Dispose(); _work.Dispose(); _emptyMap.Dispose(); _indices.Clear();
            _axisNodes.Dispose(); _axisEdges.Dispose(); _axisQueue.Dispose();
            DisposeNormalWork();
            DisposeSpatialIndex();
            DisposeSupportDependencies();
            DisposeStateReadback();
            DisposeContactEvidenceReadback();
        }

        BlockSimState StateAt(int i) => new BlockSimState { handle = _handles[i], owner = _owners[i], position = _positions[i],
            velocity = _velocities[i], mode = _modes[i], poseVersion = _versions[i] };
        void CheckDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(BlockSimulation)); }
        static bool Finite(float3 value) => math.all(math.isfinite(value));
        static double StageElapsed(ref long start)
        {
            long now = Stopwatch.GetTimestamp(); double elapsed = (now - start) * (1000.0 / Stopwatch.Frequency);
            start = now; return elapsed;
        }
        static void Validate(BlockSimState state)
        {
            if (!state.handle.IsValid || !Finite(state.position) || !Finite(state.velocity) ||
                math.any(math.abs(state.position) > 100000000f) || (byte)state.mode > (byte)BlockSimMode.Frozen)
                throw new ArgumentException("Invalid block state.", nameof(state));
        }
        void Rollback()
        {
            for (int i = 0; i < Count; i++)
            { _positions[i] = _tickPositions[i]; _velocities[i] = _backupVelocities[i]; _modes[i] = _backupModes[i]; _quiet[i] = _backupQuiet[i]; _near[i] = _backupNear[i]; }
            _cache.Clear(); _contacts.Clear(); _pairs.Clear(); _hashDirty = true; _diagnostics.SolverFaults++;
        }
        void NextMark()
        {
            if (++_mark != int.MaxValue) return;
            for (int i = 0; i < _marks.Length; i++) _marks[i] = 0; _mark = 1;
        }
        void WakeConnected(int seed, bool supportChange)
        {
            EnsureQueryHash(); NextMark(); _wakeQueue.Clear();
            if (_modes[seed] == BlockSimMode.Frozen) CollectWakeSeeds(new BlockAabb(_positions[seed], HalfSize + ContactSkin), _mark);
            else { _marks[seed] = _mark; _wakeQueue.Add(seed); }
            DrainWakeQueue(_mark, supportChange);
        }
        void CollectWakeSeeds(BlockAabb bounds, int mark)
        {
            int3 lo = Grid(bounds.Min - HalfSize), hi = Grid(bounds.Max + HalfSize);
            for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
            {
                if (!_hash.TryGetFirstValue(new int3(x, y, z), out int i, out var it)) continue;
                do
                {
                    if (_marks[i] == mark || !bounds.Overlaps(new BlockAabb(_positions[i], HalfSize))) continue;
                    _marks[i] = mark; _wakeQueue.Add(i);
                } while (_hash.TryGetNextValue(out i, ref it));
            }
        }
        void DrainWakeQueue(int mark, bool supportChange)
        {
            int capacity = _wakeQueue.Capacity;
            for (int q = 0; q < _wakeQueue.Length; q++)
            {
                int i = _wakeQueue[q]; if (_modes[i] == BlockSimMode.Frozen) continue;
                if (_modes[i] == BlockSimMode.Resting && supportChange) _diagnostics.SupportWakes++;
                _modes[i] = BlockSimMode.Active; _quiet[i] = 0; _validateSupport[i] = 0;
                CollectWakeSeeds(new BlockAabb(_positions[i], HalfSize + ContactSkin), mark);
            }
            if (capacity != _wakeQueue.Capacity) _diagnostics.CapacityGrowths++;
        }
    }
}
