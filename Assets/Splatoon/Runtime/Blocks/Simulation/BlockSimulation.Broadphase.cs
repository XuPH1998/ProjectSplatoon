using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public sealed partial class BlockSimulation
    {
        struct WorkResult
        {
            public int Fault, UnsafePairs, CcdClips, SupportWakes, RangeWakes, NewWakes;
            public int ActiveCount, RestingCount, DynamicContactCount;
            public float MaxPenetration, MaxCorrection;
            public int UnsafeA, UnsafeB;
            public float UnsafeFraction, UnsafeEndPenetration, FaultFraction;
            public float3 UnsafePosition, UnsafeVelocity, UnsafeNormal;
            public float3 FaultPosition, FaultDisplacement, FaultNormal;
            public long AxisPivots, AxisConstraintChecks;
            public long NormalBlockPasses, ObliqueUpdates, FrictionRetractions;
            public float AxisResidual, NormalResidual, FrictionConeExcess;
        }

        struct ContactKey : IEquatable<ContactKey>
        {
            public BlockSimHandle A, B;
            public int Feature, Axis;
            public bool Equals(ContactKey other) => A.Equals(other.A) && B.Equals(other.B) && Feature == other.Feature && Axis == other.Axis;
            public override int GetHashCode() => unchecked(((A.GetHashCode() * 397 ^ B.GetHashCode()) * 397 ^ Feature) * 397 ^ Axis);
        }

        struct CachedContact
        {
            public float3 Normal, Tangent;
            public float NormalImpulse;
        }

        struct SimContact
        {
            public ContactKey Key;
            public int A, B;
            public float3 Normal, TangentImpulse;
            public float Separation, NormalImpulse, StaticFriction, DynamicFriction;
        }

        static int3 Grid(float3 point) => (int3)math.floor(point / CellSize);
        static void SweepBounds(float3 position, float3 velocity, float delta, out float3 minimum, out float3 maximum)
        {
            float3 end = position + velocity * delta;
            float3 extent = HalfSize + ContactSkin + MaxPositionCorrection * PositionIterations;
            minimum = math.min(position, end) - extent;
            maximum = math.max(position, end) + extent;
        }
        static void GridRange(float3 position, float3 velocity, float delta, out int3 lo, out int3 hi)
        {
            SweepBounds(position, velocity, delta, out float3 minimum, out float3 maximum);
            lo = Grid(minimum); hi = Grid(maximum);
        }
        static ulong Pair(int a, int b) => ((ulong)(uint)math.min(a, b) << 32) | (uint)math.max(a, b);
        static int PairA(ulong pair) => (int)(pair >> 32);
        static int PairB(ulong pair) => (int)(pair & uint.MaxValue);

        void BuildPairs(float delta)
        {
            long stage = System.Diagnostics.Stopwatch.GetTimestamp();
            _diagnostics.BroadphaseCalls++;
            EnsureQueryHash();
            int activeCapacity = _broadActive.Capacity, candidateCapacity = _broadCandidates.Capacity;
            int loCapacity = _sweptLo.Capacity, hiCapacity = _sweptHi.Capacity;
            int minimumCapacity = _sweptMinimum.Capacity, maximumCapacity = _sweptMaximum.Capacity;
            new PrepareBroadphaseJob { Positions = _positions.AsArray(), Velocities = _velocities.AsArray(),
                Modes = _modes.AsArray(), ValidateSupport = _validateSupport.AsArray(), Active = _broadActive,
                Candidates = _broadCandidates, Lo = _sweptLo, Hi = _sweptHi,
                Minimum = _sweptMinimum, Maximum = _sweptMaximum, SweepHash = _activeSweepHash,
                Delta = delta, Result = _broadWork }.Schedule().Complete();
            if (_broadActive.Capacity != activeCapacity) _diagnostics.CapacityGrowths++;
            if (_broadCandidates.Capacity != candidateCapacity) _diagnostics.CapacityGrowths++;
            if (_sweptLo.Capacity != loCapacity) _diagnostics.CapacityGrowths++;
            if (_sweptHi.Capacity != hiCapacity) _diagnostics.CapacityGrowths++;
            if (_sweptMinimum.Capacity != minimumCapacity) _diagnostics.CapacityGrowths++;
            if (_sweptMaximum.Capacity != maximumCapacity) _diagnostics.CapacityGrowths++;
            var prepared = _broadWork.Value;
            if (prepared.Fault != 0) throw new InvalidOperationException("Collision sweep capacity exceeds addressable storage; no candidates were omitted.");
            if (prepared.HashFilled == 0)
            {
                _activeSweepHash.Capacity = Grow(prepared.HashRequired); _diagnostics.CapacityGrowths++;
                new FillActiveSweepJob { Active = _broadActive.AsArray(), Lo = _sweptLo.AsArray(), Hi = _sweptHi.AsArray(),
                    Hash = _activeSweepHash, Result = _broadWork }.Schedule().Complete();
                if (_broadWork.Value.Fault != 0) throw new InvalidOperationException("Active sweep hash count mismatch; no partial index was published.");
            }
            // A full-active build has no passive entries to find in CenterHash. This is a mode
            // identity check, never a distance, pair-count or candidate-count approximation.
            bool hasPassive = _broadActive.Length != Count;
            if (!hasPassive) _diagnostics.BroadphaseAllActiveCalls++;
            var pairsJob = new CandidatePairsJob { CenterHash = _hash, SweepHash = _activeSweepHash,
                Candidates = _broadCandidates.AsArray(), Positions = _positions.AsArray(),
                Minimum = _sweptMinimum.AsArray(), Maximum = _sweptMaximum.AsArray(),
                Modes = _modes.AsArray(), ValidateSupport = _validateSupport.AsArray(), Lo = _sweptLo.AsArray(), Hi = _sweptHi.AsArray(),
                Counts = _counts.AsArray(), Offsets = _offsets.AsArray(), Pairs = _pairs.AsArray(), HasPassive = hasPassive };
            _diagnostics.BroadphasePrepareMilliseconds += StageElapsed(ref stage);
            pairsJob.Schedule(_broadCandidates.Length, 64).Complete();
            _diagnostics.BroadphaseCountMilliseconds += StageElapsed(ref stage);
            new PrefixCandidateCountsJob { Count = _broadCandidates.Length, Counts = _counts.AsArray(),
                Offsets = _offsets.AsArray(), Result = _broadWork }.Schedule().Complete();
            var counted = _broadWork.Value;
            if (counted.Fault != 0) throw new InvalidOperationException("Collision candidate capacity exceeds addressable storage; no candidates were omitted.");
            int required = counted.PairRequired;
            if (_pairs.Capacity < required) { _pairs.Capacity = Grow(required); _diagnostics.CapacityGrowths++; }
            _pairs.ResizeUninitialized(required);
            pairsJob.Emit = true; pairsJob.Pairs = _pairs.AsArray();
            _diagnostics.BroadphasePrefixMilliseconds += StageElapsed(ref stage);
            pairsJob.Schedule(_broadCandidates.Length, 64).Complete();
            _diagnostics.BroadphaseEmitMilliseconds += StageElapsed(ref stage);
            new ValidateAndSortPairsJob { Count = _broadCandidates.Length, Counts = _counts.AsArray(), Pairs = _pairs,
                Handles = _handles.AsArray(), Result = _broadWork }.Schedule().Complete();
            if (_broadWork.Value.Fault != 0) throw new InvalidOperationException("Pair emission mismatch or duplicate ownership; no partial candidate list was published.");
            _diagnostics.CandidateRequired = required; _diagnostics.CandidateEmitted = _pairs.Length;
            _diagnostics.BroadphaseSortMilliseconds += StageElapsed(ref stage);
        }

        static int Grow(int required) => required <= 1024 ? math.max(16, math.ceilpow2(math.max(required, 1))) :
            (int)System.Math.Min(int.MaxValue, (long)required + math.max(1024, required / 2));
        void EnsureCacheCapacity(int count)
        {
            if (_cache.Capacity >= count) return;
            _cache.Capacity = Grow(count); _diagnostics.CapacityGrowths++;
        }

        [BurstCompile]
        struct PrepareBroadphaseJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions, Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            [ReadOnly] public NativeArray<byte> ValidateSupport;
            public NativeList<int> Active, Candidates;
            public NativeList<int3> Lo, Hi;
            public NativeList<float3> Minimum, Maximum;
            public NativeParallelMultiHashMap<int3, int> SweepHash;
            public NativeReference<BroadphaseWork> Result;
            public float Delta;
            public void Execute()
            {
                Active.Clear(); Candidates.Clear(); Lo.ResizeUninitialized(Positions.Length); Hi.ResizeUninitialized(Positions.Length);
                // Sparse dense-slot storage: only candidates below are written. A passive body
                // absent from Candidates is queried from its stationary center, never stale cache.
                Minimum.ResizeUninitialized(Positions.Length); Maximum.ResizeUninitialized(Positions.Length);
                var result = new BroadphaseWork(); long total = 0;
                for (int i = 0; i < Positions.Length; i++)
                {
                    bool active = Modes[i] == BlockSimMode.Active;
                    if (!active && ValidateSupport[i] == 0) continue;
                    Candidates.Add(i);
                    if (!Finite(Positions[i]) || (active && !Finite(Velocities[i]))) { result.Fault = 1; Result.Value = result; return; }
                    SweepBounds(Positions[i], active ? Velocities[i] : float3.zero, Delta, out float3 minimum, out float3 maximum);
                    Minimum[i] = minimum; Maximum[i] = maximum;
                    int3 lo = Grid(minimum), hi = Grid(maximum);
                    if (math.any(lo <= int.MinValue) || math.any(hi >= int.MaxValue))
                    { result.Fault = 1; Result.Value = result; return; }
                    Lo[i] = lo; Hi[i] = hi;
                    long nx = (long)hi.x - lo.x + 1, ny = (long)hi.y - lo.y + 1, nz = (long)hi.z - lo.z + 1;
                    if (nx <= 0 || ny <= 0 || nz <= 0 || nx > int.MaxValue || ny > int.MaxValue || nz > int.MaxValue ||
                        nx * ny > int.MaxValue || nx * ny * nz > int.MaxValue)
                    { result.Fault = 1; Result.Value = result; return; }
                    if (!active) continue;
                    Active.Add(i); total += nx * ny * nz;
                    if (total > int.MaxValue) { result.Fault = 1; Result.Value = result; return; }
                }
                result.HashRequired = (int)total;
                if (SweepHash.Capacity >= total)
                {
                    FillActiveHash(SweepHash, Active.AsArray(), Lo.AsArray(), Hi.AsArray());
                    result.HashFilled = 1;
                    if (SweepHash.Count() != total) result.Fault = 1;
                }
                Result.Value = result;
            }
        }

        [BurstCompile]
        struct FillActiveSweepJob : IJob
        {
            [ReadOnly] public NativeArray<int> Active;
            [ReadOnly] public NativeArray<int3> Lo, Hi;
            public NativeParallelMultiHashMap<int3, int> Hash;
            public NativeReference<BroadphaseWork> Result;
            public void Execute()
            {
                FillActiveHash(Hash, Active, Lo, Hi); var result = Result.Value;
                result.HashFilled = 1; if (Hash.Count() != result.HashRequired) result.Fault = 1; Result.Value = result;
            }
        }

        static void FillActiveHash(NativeParallelMultiHashMap<int3, int> hash, NativeArray<int> active, NativeArray<int3> lows, NativeArray<int3> highs)
        {
            hash.Clear();
            for (int a = 0; a < active.Length; a++)
            {
                int i = active[a]; int3 lo = lows[i], hi = highs[i];
                for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
                    hash.Add(new int3(x, y, z), i);
            }
        }

        [BurstCompile]
        struct PrefixCandidateCountsJob : IJob
        {
            public int Count;
            [ReadOnly] public NativeArray<int> Counts;
            public NativeArray<int> Offsets;
            public NativeReference<BroadphaseWork> Result;
            public void Execute()
            {
                var result = Result.Value; long total = 0;
                for (int i = 0; i < Count; i++)
                {
                    if (Counts[i] < 0 || total + Counts[i] > int.MaxValue)
                    { result.Fault = 1; Result.Value = result; return; }
                    Offsets[i] = (int)total; total += Counts[i];
                }
                result.PairRequired = (int)total; Result.Value = result;
            }
        }

        [BurstCompile]
        struct CandidatePairsJob : IJobParallelFor
        {
            [ReadOnly] public NativeParallelMultiHashMap<int3, int> CenterHash, SweepHash;
            [ReadOnly] public NativeArray<int> Candidates;
            [ReadOnly] public NativeArray<float3> Positions, Minimum, Maximum;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            [ReadOnly] public NativeArray<byte> ValidateSupport;
            [ReadOnly] public NativeArray<int3> Lo, Hi;
            [ReadOnly] public NativeArray<int> Offsets;
            public NativeArray<int> Counts;
            [NativeDisableParallelForRestriction] public NativeArray<ulong> Pairs;
            public bool Emit, HasPassive;

            public void Execute(int slot)
            {
                int i = Candidates[slot], first = Emit ? Offsets[slot] : 0, expected = Emit ? Counts[slot] : 0;
                long count = 0; int3 lo = Lo[i], hi = Hi[i];
                float3 minimum = Minimum[i], maximum = Maximum[i];
                for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
                {
                    int3 cell = new int3(x, y, z);
                    if (!SweepHash.TryGetFirstValue(cell, out int j, out var iterator)) continue;
                    do
                    {
                        if (j == i || j < i || !math.all(cell == math.max(lo, Lo[j]))) continue;
                        float3 otherMin = Minimum[j], otherMax = Maximum[j];
                        if (math.any(minimum > otherMax) || math.any(maximum < otherMin)) continue;
                        if (!Append(i, j, first, expected, ref count)) { Counts[slot] = -1; return; }
                    } while (SweepHash.TryGetNextValue(out j, ref iterator));
                }
                if (HasPassive)
                {
                    // Every passive body still has one center entry. Preserve the exact sweep
                    // margin, and derive its static bounds even when it was not a prepared candidate.
                    float3 passiveExtent = HalfSize + ContactSkin + MaxPositionCorrection * PositionIterations;
                    int3 centerLo = Grid(minimum - passiveExtent), centerHi = Grid(maximum + passiveExtent);
                    for (int z = centerLo.z; z <= centerHi.z; z++) for (int y = centerLo.y; y <= centerHi.y; y++) for (int x = centerLo.x; x <= centerHi.x; x++)
                    {
                        if (!CenterHash.TryGetFirstValue(new int3(x, y, z), out int j, out var iterator)) continue;
                        do
                        {
                            if (j == i || Modes[j] == BlockSimMode.Active || (ValidateSupport[j] != 0 && j < i)) continue;
                            float3 otherMin = Positions[j] - passiveExtent, otherMax = Positions[j] + passiveExtent;
                            if (math.any(minimum > otherMax) || math.any(maximum < otherMin)) continue;
                            if (!Append(i, j, first, expected, ref count)) { Counts[slot] = -1; return; }
                        } while (CenterHash.TryGetNextValue(out j, ref iterator));
                    }
                }
                Counts[slot] = Emit ? expected - (int)count : (int)count;
            }

            bool Append(int i, int j, int first, int expected, ref long count)
            {
                if (count >= int.MaxValue) return false;
                if (Emit)
                {
                    if (count >= expected || (long)first + count >= Pairs.Length) return false;
                    Pairs[first + (int)count] = Pair(i, j);
                }
                count++; return true;
            }
        }

        [BurstCompile]
        struct ValidateAndSortPairsJob : IJob
        {
            public int Count;
            [ReadOnly] public NativeArray<int> Counts;
            public NativeList<ulong> Pairs;
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            public NativeReference<BroadphaseWork> Result;
            public void Execute()
            {
                var result = Result.Value;
                if (Pairs.Length != result.PairRequired) { result.Fault = 1; Result.Value = result; return; }
                for (int i = 0; i < Count; i++)
                    if (Counts[i] != 0) { result.Fault = 1; Result.Value = result; return; }
                if (Pairs.Length < 2) return;
                Pairs.AsArray().Sort(new PairComparer { Handles = Handles });
                for (int i = 1; i < Pairs.Length; i++)
                    if (Pairs[i] == Pairs[i - 1]) { result.Fault = 1; Result.Value = result; return; }
            }
        }

        struct PairComparer : System.Collections.Generic.IComparer<ulong>
        {
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            public int Compare(ulong x, ulong y)
            {
                int xa = PairA(x), xb = PairB(x), ya = PairA(y), yb = PairB(y);
                if (CompareHandle(Handles[xa], Handles[xb]) > 0) { int t = xa; xa = xb; xb = t; }
                if (CompareHandle(Handles[ya], Handles[yb]) > 0) { int t = ya; ya = yb; yb = t; }
                int first = CompareHandle(Handles[xa], Handles[ya]);
                return first != 0 ? first : CompareHandle(Handles[xb], Handles[yb]);
            }
        }
        static int CompareHandle(BlockSimHandle a, BlockSimHandle b)
        {
            int value = a.epoch.CompareTo(b.epoch); if (value != 0) return value;
            value = a.round.CompareTo(b.round); if (value != 0) return value;
            value = a.id.CompareTo(b.id); return value != 0 ? value : a.generation.CompareTo(b.generation);
        }

        static bool SweepBoxes(float3 relativePosition, float3 extents, float3 displacement, out float fraction, out float3 normal)
        {
            fraction = 0; normal = float3.zero;
            if (math.all(math.abs(relativePosition) < extents)) return false; // Existing overlap is resolved separately.
            float enter = 0, exit = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                float v = displacement[axis], p = relativePosition[axis], r = extents[axis];
                if (math.abs(v) < 1e-9f) { if (math.abs(p) > r) return false; continue; }
                float t0 = (-r - p) / v, t1 = (r - p) / v;
                float first = math.min(t0, t1), last = math.max(t0, t1);
                if (first >= enter) { enter = first; normal = float3.zero; normal[axis] = -math.sign(v); }
                exit = math.min(exit, last);
                if (enter > exit) return false;
            }
            if (enter < 0 || enter > 1 || exit < 0 || math.lengthsq(normal) == 0) return false;
            // A point/edge graze with no positive-duration overlap does not block motion.
            if (exit - enter <= 1e-7f) return false;
            fraction = enter; return true;
        }

        static int NormalAxis(float3 normal)
        {
            float3 a = math.abs(normal); int axis = a.x > a.y ? (a.x > a.z ? 0 : 2) : (a.y > a.z ? 1 : 2);
            return axis * 2 + (normal[axis] < 0 ? 1 : 0);
        }
        static float InvMass(BlockSimMode mode) => mode == BlockSimMode.Active ? 1f / Mass : 0;
        static float3 BodyVelocity(NativeArray<float3> velocity, NativeArray<BlockSimMode> modes, int index) =>
            index >= 0 && modes[index] == BlockSimMode.Active ? velocity[index] : float3.zero;
    }
}
