using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public sealed partial class BlockSimulation
    {
        void GenerateContacts(BlockMapSnapshot map, float delta, bool loadCache, bool preserveExisting = false)
        {
            int capacity = _contacts.Capacity, scratchCapacity = _mapScratch.Capacity;
            new GenerateContactsJob { Map = map, Pairs = _pairs, Handles = _handles.AsArray(), Positions = _positions.AsArray(),
                Velocities = _velocities.AsArray(), Modes = _modes.AsArray(), ValidateSupport = _validateSupport.AsArray(),
                Cache = _cache, Contacts = _contacts, MapScratch = _mapScratch, Delta = delta, LoadCache = loadCache,
                PreserveExisting = preserveExisting }.Schedule().Complete();
            if (capacity != _contacts.Capacity) _diagnostics.CapacityGrowths++;
            if (scratchCapacity != _mapScratch.Capacity) _diagnostics.CapacityGrowths++;
        }

        [BurstCompile]
        struct BeginTickJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions, Velocities, Observers;
            public NativeArray<float> Quiet;
            public NativeArray<BlockSimMode> Modes, BackupModes;
            public NativeArray<float3> TickPositions, BackupVelocities;
            public NativeArray<float> BackupQuiet, Corrections;
            public NativeArray<byte> Near, BackupNear;
            public NativeReference<WorkResult> Result;
            public void Execute()
            {
                WorkResult result = Result.Value;
                for (int i = 0; i < Positions.Length; i++)
                {
                    TickPositions[i] = Positions[i]; BackupVelocities[i] = Velocities[i];
                    BackupModes[i] = Modes[i]; BackupQuiet[i] = Quiet[i]; Corrections[i] = 0;
                    BackupNear[i] = Near[i];
                    bool near = false;
                    for (int j = 0; j < Observers.Length; j++)
                    {
                        float2 d = Positions[i].xz - Observers[j].xz;
                        if (math.lengthsq(d) <= ActivationRadius * ActivationRadius) { near = true; break; }
                    }
                    if (near && Near[i] == 0 && Modes[i] == BlockSimMode.Resting)
                    { Modes[i] = BlockSimMode.Active; Quiet[i] = 0; result.RangeWakes++; }
                    Near[i] = near ? (byte)1 : (byte)0;
                }
                Result.Value = result;
            }
        }

        [BurstCompile]
        struct GravityJob : IJobParallelFor
        {
            public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            public float Delta;
            public void Execute(int i) { if (Modes[i] == BlockSimMode.Active) Velocities[i] += Gravity * Delta; }
        }

        [BurstCompile]
        struct GenerateContactsJob : IJob
        {
            [ReadOnly] public BlockMapSnapshot Map;
            [ReadOnly] public NativeList<ulong> Pairs;
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            [ReadOnly] public NativeArray<float3> Positions, Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            [ReadOnly] public NativeArray<byte> ValidateSupport;
            [ReadOnly] public NativeParallelHashMap<ContactKey, CachedContact> Cache;
            public NativeList<SimContact> Contacts;
            public NativeList<BlockStaticHit> MapScratch;
            public float Delta;
            public bool LoadCache;
            public bool PreserveExisting;
            int _previousLength;

            public void Execute()
            {
                if (!PreserveExisting) Contacts.Clear();
                _previousLength = Contacts.Length;
                for (int p = 0; p < Pairs.Length; p++)
                {
                    int a = PairA(Pairs[p]), b = PairB(Pairs[p]);
                    if (CompareHandle(Handles[a], Handles[b]) > 0) { int swap = a; a = b; b = swap; }
                    float3 d = Positions[a] - Positions[b], separations = math.abs(d) - Edge;
                    float largest = math.cmax(separations); float3 normal;
                    if (largest <= ContactSkin)
                    {
                        int axis = separations.x > separations.y ? (separations.x > separations.z ? 0 : 2) :
                            (separations.y > separations.z ? 1 : 2);
                        normal = float3.zero; normal[axis] = d[axis] < 0 ? -1 : 1;
                    }
                    else
                    {
                        float3 motion = (BodyVelocity(Velocities, Modes, a) - BodyVelocity(Velocities, Modes, b)) * Delta;
                        if (Delta == 0 || !SweepBoxes(d, new float3(Edge), motion, out _, out normal)) continue;
                    }
                    var contact = new SimContact { A = a, B = b, Normal = normal,
                        Separation = math.dot(d, normal) - Edge, StaticFriction = BlockSimulation.StaticFriction,
                        DynamicFriction = BlockSimulation.DynamicFriction,
                        Key = new ContactKey { A = Handles[a], B = Handles[b], Feature = -1, Axis = NormalAxis(normal) } };
                    LoadCached(ref contact); Emit(contact);
                }
                for (int i = 0; Map.IsCreated && i < Positions.Length; i++)
                {
                    if (Modes[i] != BlockSimMode.Active && ValidateSupport[i] == 0) continue;
                    MapScratch.Clear();
                    BlockMapQueries.CollectContacts(Map, Positions[i], HalfSize, MapScratch, ContactSkin);
                    for (int c = 0; c < MapScratch.Length; c++) AddMap(i, MapScratch[c], -MapScratch[c].Penetration);
                    if (Delta == 0 || Modes[i] != BlockSimMode.Active) continue;
                    float3 motion = Velocities[i] * Delta;
                    if (!BlockMapQueries.Sweep(Map, Positions[i], HalfSize, motion, out BlockStaticHit hit)) continue;
                    bool present = false;
                    for (int c = 0; c < MapScratch.Length; c++)
                        if (MapScratch[c].FeatureId == hit.FeatureId && math.dot(MapScratch[c].Normal, hit.Normal) > .99f)
                        { present = true; break; }
                    if (!present)
                    {
                        float separation = hit.Fraction <= 0 ? -math.max(0, hit.Penetration) :
                            math.max(0, -math.dot(motion, hit.Normal) * hit.Fraction);
                        AddMap(i, hit, separation);
                    }
                }
                Contacts.AsArray().Sort(new ContactComparer());
            }
            void AddMap(int a, BlockStaticHit hit, float separation)
            {
                if (math.lengthsq(hit.Normal) < .5f) return;
                var contact = new SimContact { A = a, B = -1, Normal = hit.Normal, Separation = separation,
                    StaticFriction = (BlockSimulation.StaticFriction + hit.StaticFriction) * .5f,
                    DynamicFriction = (BlockSimulation.DynamicFriction + hit.DynamicFriction) * .5f,
                    Key = new ContactKey { A = Handles[a], Feature = hit.FeatureId, Axis = NormalAxis(hit.Normal) } };
                LoadCached(ref contact); Emit(contact);
            }
            void Emit(SimContact contact)
            {
                // Contacts from earlier closure passes already have their impulses applied to velocity.
                // Keep the same-substep accumulated ledger, including constraints no longer found by the
                // changed-velocity sweep. New contacts start at zero; warm starting happens only once.
                int low = 0, high = _previousLength - 1;
                var comparer = new ContactComparer();
                while (low <= high)
                {
                    int middle = (low + high) >> 1, comparison = comparer.Compare(Contacts[middle], contact);
                    if (comparison == 0) return;
                    if (comparison < 0) low = middle + 1; else high = middle - 1;
                }
                Contacts.Add(contact);
            }
            void LoadCached(ref SimContact contact)
            {
                if (LoadCache && contact.Separation <= ContactSkin && Cache.TryGetValue(contact.Key, out CachedContact previous) &&
                    math.dot(previous.Normal, contact.Normal) > .99f)
                {
                    contact.NormalImpulse = previous.NormalImpulse;
                    // A map feature can retain its identity while its SAT normal changes. Cached
                    // tangent impulse is not yet applied, so reproject before warm starting it.
                    contact.TangentImpulse = previous.Tangent - contact.Normal *
                        (math.dot(previous.Tangent, contact.Normal) / math.lengthsq(contact.Normal));
                }
            }
        }

        struct ContactComparer : System.Collections.Generic.IComparer<SimContact>
        {
            public int Compare(SimContact x, SimContact y)
            {
                int result = CompareHandle(x.Key.A, y.Key.A); if (result != 0) return result;
                result = CompareHandle(x.Key.B, y.Key.B); if (result != 0) return result;
                result = x.Key.Feature.CompareTo(y.Key.Feature); return result != 0 ? result : x.Key.Axis.CompareTo(y.Key.Axis);
            }
        }

        static int Find(NativeArray<int> parents, int index)
        {
            int root = index;
            while (parents[root] != root) root = parents[root];
            while (parents[index] != index) { int next = parents[index]; parents[index] = root; index = next; }
            return root;
        }
        static void Union(NativeArray<int> parents, int a, int b)
        {
            int ra = Find(parents, a), rb = Find(parents, b);
            if (ra == rb) return;
            if (ra < rb) parents[rb] = ra; else parents[ra] = rb;
        }

        [BurstCompile]
        struct WakeContactsJob : IJob
        {
            [ReadOnly] public NativeList<SimContact> Contacts;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeParallelMultiHashMap<int3, int> Hash;
            public NativeList<int> Queue;
            public NativeArray<BlockSimMode> Modes;
            public NativeArray<float3> Velocities;
            public NativeArray<float> Quiet;
            public NativeArray<int> Parents;
            public NativeArray<int> Seeds;
            public float Delta;
            public NativeReference<WorkResult> Result;
            public void Execute()
            {
                bool anyResting = false;
                for (int i = 0; i < Modes.Length; i++) if (Modes[i] == BlockSimMode.Resting) { anyResting = true; break; }
                if (!anyResting)
                { var empty = Result.Value; empty.NewWakes = 0; Result.Value = empty; return; }
                for (int i = 0; i < Modes.Length; i++) { Parents[i] = i; Seeds[i] = 0; }
                for (int c = 0; c < Contacts.Length; c++)
                {
                    var contact = Contacts[c];
                    if (contact.B >= 0 && Modes[contact.A] != BlockSimMode.Frozen && Modes[contact.B] != BlockSimMode.Frozen)
                        Union(Parents, contact.A, contact.B);
                }
                for (int i = 0; i < Modes.Length; i++) Find(Parents, i);
                for (int i = 0; i < Modes.Length; i++) if (Modes[i] == BlockSimMode.Active) Seeds[Parents[i]] = 1;
                WorkResult result = Result.Value; result.NewWakes = 0;
                for (int i = 0; i < Modes.Length; i++)
                {
                    if (Modes[i] == BlockSimMode.Resting && Seeds[Parents[i]] != 0)
                    { Modes[i] = BlockSimMode.Active; Quiet[i] = 0; Velocities[i] = Gravity * Delta; result.SupportWakes++; result.NewWakes++; }
                }
                // The broad phase need not generate sleeping/sleeping pairs. Walk only actual touching
                // neighbours of awake seeds to bring the complete affected contact component into this solve.
                Queue.Clear();
                for (int i = 0; i < Modes.Length; i++) if (Modes[i] == BlockSimMode.Active) Queue.Add(i);
                for (int q = 0; q < Queue.Length; q++)
                {
                    int i = Queue[q];
                    // The persistent index stores centers, so include both bodies' half extents.
                    int3 lo = Grid(Positions[i] - Edge - ContactSkin), hi = Grid(Positions[i] + Edge + ContactSkin);
                    for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
                    {
                        if (!Hash.TryGetFirstValue(new int3(x, y, z), out int j, out var it)) continue;
                        do
                        {
                            if (Modes[j] != BlockSimMode.Resting || math.any(math.abs(Positions[i] - Positions[j]) > Edge + ContactSkin)) continue;
                            Modes[j] = BlockSimMode.Active; Quiet[j] = 0; Velocities[j] = Gravity * Delta;
                            result.SupportWakes++; result.NewWakes++; Queue.Add(j);
                        } while (Hash.TryGetNextValue(out j, ref it));
                    }
                }
                Result.Value = result;
            }
        }

        [BurstCompile]
        struct WarmStartJob : IJob
        {
            [ReadOnly] public NativeList<SimContact> Contacts;
            public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            public void Execute()
            {
                for (int c = 0; c < Contacts.Length; c++)
                {
                    SimContact contact = Contacts[c];
                    float3 impulse = contact.Normal * contact.NormalImpulse + contact.TangentImpulse;
                    if (Modes[contact.A] == BlockSimMode.Active) Velocities[contact.A] += impulse / Mass;
                    if (contact.B >= 0 && Modes[contact.B] == BlockSimMode.Active) Velocities[contact.B] -= impulse / Mass;
                }
            }
        }

        [BurstCompile]
        struct VelocityJob : IJob
        {
            public NativeList<SimContact> Contacts;
            public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            public float Delta;
            public void Execute()
            {
                for (int iteration = 0; iteration < VelocityIterations; iteration++)
                for (int c = 0; c < Contacts.Length; c++)
                {
                    var contact = Contacts[c]; float inverse = InvMass(Modes[contact.A]) + (contact.B < 0 ? 0 : InvMass(Modes[contact.B]));
                    if (inverse == 0) continue;
                    float3 relative = BodyVelocity(Velocities, Modes, contact.A) - BodyVelocity(Velocities, Modes, contact.B);
                    float desired = -math.max(0, contact.Separation - RestGap) / Delta;
                    float impulse = math.max(0, contact.NormalImpulse + (desired - math.dot(relative, contact.Normal)) / inverse);
                    Apply(contact, contact.Normal * (impulse - contact.NormalImpulse)); contact.NormalImpulse = impulse;
                    if (contact.Separation <= ContactSkin)
                    {
                        relative = BodyVelocity(Velocities, Modes, contact.A) - BodyVelocity(Velocities, Modes, contact.B);
                        float3 tangent = relative - contact.Normal * math.dot(relative, contact.Normal);
                        float3 candidate = contact.TangentImpulse - tangent / inverse;
                        float magnitude = math.length(candidate), staticLimit = impulse * contact.StaticFriction;
                        if (magnitude > staticLimit)
                            candidate *= math.min(1, impulse * contact.DynamicFriction / math.max(1e-10f, magnitude));
                        Apply(contact, candidate - contact.TangentImpulse); contact.TangentImpulse = candidate;
                    }
                    Contacts[c] = contact;
                }
            }
            void Apply(SimContact contact, float3 impulse)
            {
                if (Modes[contact.A] == BlockSimMode.Active) Velocities[contact.A] += impulse / Mass;
                if (contact.B >= 0 && Modes[contact.B] == BlockSimMode.Active) Velocities[contact.B] -= impulse / Mass;
            }
        }

        [BurstCompile]
        struct VerifyMotionJob : IJob
        {
            [ReadOnly] public NativeList<ulong> Pairs;
            [ReadOnly] public NativeList<SimContact> Contacts;
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            [ReadOnly] public NativeArray<float3> Positions, Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            public float Delta;
            public NativeReference<WorkResult> Result;
            public void Execute()
            {
                WorkResult result = Result.Value; result.UnsafePairs = 0; result.UnsafeA = -1; result.UnsafeB = -1;
                result.UnsafeFraction = 1; result.UnsafeEndPenetration = 0;
                for (int p = 0; p < Pairs.Length; p++)
                {
                    int a = PairA(Pairs[p]), b = PairB(Pairs[p]);
                    float3 relative = Positions[a] - Positions[b];
                    float3 motion = (BodyVelocity(Velocities, Modes, a) - BodyVelocity(Velocities, Modes, b)) * Delta;
                    if (SweepBoxes(relative, new float3(Edge - PenetrationSlop), motion, out float fraction, out float3 normal) && fraction < 1 - 1e-5f)
                    {
                        // Closure is about newly introduced constraints. An already covered normal
                        // with a feasible velocity cannot be repaired by rediscovering the same pair;
                        // existing positional overlap belongs to the unchanged position solver/gates.
                        int ca = a, cb = b;
                        if (CompareHandle(Handles[ca], Handles[cb]) > 0) { int swap = ca; ca = cb; cb = swap; normal = -normal; }
                        var keyContact = new SimContact { Key = new ContactKey { A = Handles[ca], B = Handles[cb], Feature = -1, Axis = NormalAxis(normal) } };
                        int low = 0, high = Contacts.Length - 1; bool covered = false;
                        var comparer = new ContactComparer();
                        while (low <= high)
                        {
                            int middle = (low + high) >> 1, comparison = comparer.Compare(Contacts[middle], keyContact);
                            if (comparison == 0)
                            {
                                SimContact contact = Contacts[middle];
                                float desired = -math.max(0, contact.Separation - RestGap) / Delta;
                                float actual = math.dot(BodyVelocity(Velocities, Modes, contact.A) - BodyVelocity(Velocities, Modes, contact.B), contact.Normal);
                                covered = actual >= desired - 1e-5f; break;
                            }
                            if (comparison < 0) low = middle + 1; else high = middle - 1;
                        }
                        if (covered) continue;
                        result.UnsafePairs++;
                        if (fraction <= result.UnsafeFraction)
                        {
                            result.UnsafeA = a; result.UnsafeB = b; result.UnsafeFraction = fraction;
                            result.UnsafePosition = relative; result.UnsafeVelocity = motion / Delta; result.UnsafeNormal = normal;
                            result.UnsafeEndPenetration = math.max(0, -math.cmax(math.abs(relative + motion) - Edge));
                        }
                    }
                }
                Result.Value = result;
            }
        }

        [BurstCompile]
        struct MoveAndProjectJob : IJob
        {
            [ReadOnly] public BlockMapSnapshot Map;
            [ReadOnly] public NativeList<ulong> Pairs;
            [ReadOnly] public NativeList<SimContact> Contacts;
            [ReadOnly] public NativeArray<BlockSimHandle> Handles;
            public NativeParallelMultiHashMap<int3, int> Hash;
            public NativeArray<int3> IndexedCells;
            public NativeArray<float3> Positions, Velocities;
            public NativeArray<BlockSimMode> Modes;
            public NativeArray<float> Corrections;
            public NativeList<BlockStaticHit> MapScratch;
            public float Delta;
            public NativeReference<WorkResult> Result;
            WorkResult _result;
            public void Execute()
            {
                _result = Result.Value;
                for (int i = 0; i < Positions.Length; i++)
                {
                    if (Modes[i] != BlockSimMode.Active) continue;
                    float3 position = Positions[i], velocity = Velocities[i];
                    MoveStatic(ref position, ref velocity, velocity * Delta, true);
                    Positions[i] = position; Velocities[i] = velocity;
                    if (!RelocateCenter(Hash, IndexedCells, i, position))
                    { _result.Fault = 5; _result.FaultPosition = position; Result.Value = _result; return; }
                }
                for (int iteration = 0; iteration < PositionIterations; iteration++)
                {
                    for (int p = 0; p < Pairs.Length; p++)
                    {
                        int a = PairA(Pairs[p]), b = PairB(Pairs[p]);
                        if (CompareHandle(Handles[a], Handles[b]) > 0) { int swap = a; a = b; b = swap; }
                        float3 d = Positions[a] - Positions[b], s = math.abs(d) - Edge;
                        float largest = math.cmax(s);
                        if (largest >= -PenetrationSlop) continue;
                        if (Modes[a] == BlockSimMode.Resting) Modes[a] = BlockSimMode.Active;
                        if (Modes[b] == BlockSimMode.Resting) Modes[b] = BlockSimMode.Active;
                        float wa = InvMass(Modes[a]), wb = InvMass(Modes[b]), total = wa + wb;
                        if (total == 0) continue;
                        int axis = s.x > s.y ? (s.x > s.z ? 0 : 2) : (s.y > s.z ? 1 : 2);
                        float3 normal = float3.zero; normal[axis] = d[axis] < 0 ? -1 : 1;
                        float amount = math.min(MaxPositionCorrection, -largest - PenetrationSlop);
                        float3 movedA = Correct(a, normal * (amount * wa / total));
                        float3 movedB = Correct(b, -normal * (amount * wb / total));
                        float missing = amount - math.dot(movedA - movedB, normal);
                        if (missing > PenetrationSlop)
                        {
                            float3 extra = Correct(a, normal * missing);
                            missing -= math.dot(extra, normal);
                            if (missing > PenetrationSlop) Correct(b, -normal * missing);
                        }
                    }
                    if (!Map.IsCreated) continue;
                    for (int i = 0; i < Positions.Length; i++)
                    {
                        if (Modes[i] != BlockSimMode.Active) continue;
                        MapScratch.Clear(); BlockMapQueries.CollectContacts(Map, Positions[i], HalfSize, MapScratch);
                        for (int c = 0; c < MapScratch.Length; c++)
                        {
                            var hit = MapScratch[c];
                            float amount = math.min(MaxPositionCorrection, math.max(0, hit.Penetration - PenetrationSlop));
                            if (amount > 0) Correct(i, hit.Normal * amount);
                        }
                    }
                }
                Result.Value = _result;
            }

            float3 Correct(int index, float3 displacement)
            {
                if (Modes[index] != BlockSimMode.Active) return float3.zero;
                float length = math.length(displacement);
                float budget = math.max(0, MaxPositionCorrection * PositionIterations * Substeps - Corrections[index]);
                if (length > budget) displacement *= budget / math.max(length, 1e-10f);
                float3 start = Positions[index], position = start, ignoredVelocity = float3.zero;
                MoveStatic(ref position, ref ignoredVelocity, displacement, false, index);
                Positions[index] = position;
                if (!RelocateCenter(Hash, IndexedCells, index, position))
                { _result.Fault = 5; _result.FaultPosition = position; return float3.zero; }
                float3 moved = position - start; float distance = math.length(moved);
                Corrections[index] += distance; _result.MaxCorrection = math.max(_result.MaxCorrection, distance);
                return moved;
            }

            void MoveStatic(ref float3 position, ref float3 velocity, float3 displacement, bool affectVelocity, int correctionIndex = -1)
            {
                if (!Map.IsCreated && correctionIndex < 0) { position += displacement; return; }
                float3 remaining = displacement;
                while (math.lengthsq(remaining) > 1e-14f)
                {
                    BlockStaticHit hit = default;
                    int bestDynamic = -1;
                    bool found = Map.IsCreated && BlockMapQueries.Sweep(Map, position, HalfSize, remaining, out hit);
                    if (correctionIndex >= 0)
                    {
                        BlockAabb swept = new BlockAabb(position, HalfSize).Swept(remaining);
                        float3 indexedExtent = HalfSize + ContactSkin + MaxPositionCorrection * PositionIterations;
                        int3 lo = Grid(swept.Min - indexedExtent), hi = Grid(swept.Max + indexedExtent);
                        for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
                        {
                            if (!Hash.TryGetFirstValue(new int3(x, y, z), out int j, out var it)) continue;
                            do
                            {
                                if (j == correctionIndex) continue;
                                float3 d = position - Positions[j], separation = math.abs(d) - Edge;
                                float fraction; float3 normal;
                                if (math.cmax(separation) < 0)
                                {
                                    int axis = separation.x > separation.y ? (separation.x > separation.z ? 0 : 2) :
                                        (separation.y > separation.z ? 1 : 2);
                                    normal = float3.zero; normal[axis] = d[axis] < 0 ? -1 : 1;
                                    if (math.dot(remaining, normal) >= -1e-8f) continue;
                                    fraction = 0;
                                }
                                else if (!SweepBoxes(d, new float3(Edge), remaining, out fraction, out normal)) continue;
                                bool better = !found || fraction < hit.Fraction - .000001f;
                                if (!better && found && math.abs(fraction - hit.Fraction) <= .000001f && bestDynamic >= 0)
                                    better = CompareHandle(Handles[j], Handles[bestDynamic]) < 0 ||
                                        (Handles[j].Equals(Handles[bestDynamic]) && NormalAxis(normal) < NormalAxis(hit.Normal));
                                if (better)
                                { hit = new BlockStaticHit { Fraction = fraction, Normal = normal }; found = true; bestDynamic = j; }
                            } while (Hash.TryGetNextValue(out j, ref it));
                        }
                    }
                    if (!found)
                    { position += remaining; return; }
                    float3 before = remaining;
                    float oldLength = math.lengthsq(remaining);
                    float safeFraction = math.max(0, hit.Fraction - .0001f / math.sqrt(oldLength));
                    position += remaining * safeFraction;
                    remaining *= 1 - safeFraction;
                    float entering = math.dot(remaining, hit.Normal);
                    if (entering < 0) remaining -= hit.Normal * entering;
                    if (affectVelocity)
                    { float v = math.dot(velocity, hit.Normal); if (v < 0) velocity -= hit.Normal * v; }
                    _result.CcdClips++;
                    // Squared lengths can round to the same float after removing a tiny normal
                    // component from a large tangent. Actual advancement or vector change is progress.
                    if (safeFraction <= 0 && math.all(remaining == before))
                    {
                        _result.Fault = 2; _result.FaultPosition = position; _result.FaultDisplacement = remaining;
                        _result.FaultNormal = hit.Normal; _result.FaultFraction = hit.Fraction; return;
                    }
                }
            }
        }

        [BurstCompile]
        struct CacheContactsJob : IJob
        {
            [ReadOnly] public NativeList<SimContact> Contacts;
            public NativeParallelHashMap<ContactKey, CachedContact> Cache;
            public void Execute()
            {
                Cache.Clear();
                for (int c = 0; c < Contacts.Length; c++)
                {
                    var contact = Contacts[c];
                    Cache.TryAdd(contact.Key, new CachedContact { Normal = contact.Normal,
                        NormalImpulse = contact.NormalImpulse, Tangent = contact.TangentImpulse });
                }
            }
        }

        [BurstCompile]
        struct FinishTickJob : IJob
        {
            [ReadOnly] public BlockMapSnapshot Map;
            [ReadOnly] public NativeList<SimContact> Contacts;
            [ReadOnly] public NativeArray<float3> Positions, TickPositions;
            [ReadOnly] public NativeArray<float> Corrections;
            public NativeList<BlockStaticHit> MapScratch;
            public NativeArray<float3> Velocities;
            public NativeArray<BlockSimMode> Modes;
            public NativeArray<uint> Versions;
            public NativeArray<float> Quiet;
            public NativeArray<byte> External, ValidateSupport, Supported;
            public NativeArray<int> Parents, Counts;
            public float Delta;
            public NativeReference<WorkResult> Result;
            public void Execute()
            {
                WorkResult result = Result.Value;
                for (int i = 0; i < Positions.Length; i++)
                    if (!Finite(Positions[i]) || !Finite(Velocities[i]))
                    { result.Fault = 3; result.FaultPosition = Positions[i]; result.FaultDisplacement = Velocities[i]; }
                if (result.Fault != 0) { Result.Value = result; return; }
                result.ActiveCount = result.RestingCount = result.DynamicContactCount = 0;
                for (int i = 0; i < Positions.Length; i++)
                {
                    Parents[i] = i; Counts[i] = 1;
                    // Supported is the independently published persistent dependency result.
                }
                for (int c = 0; c < Contacts.Length; c++)
                {
                    var contact = Contacts[c];
                    if (contact.B >= 0) result.DynamicContactCount++;
                    result.MaxPenetration = math.max(result.MaxPenetration, -contact.Separation);
                    if (contact.Separation > ContactSkin) continue; // Future TOI is never a current support.
                    // Map features never union dynamic islands; contact connectivity remains
                    // independent from the persistent directed support graph.
                    if (contact.B >= 0 && Modes[contact.A] != BlockSimMode.Frozen && Modes[contact.B] != BlockSimMode.Frozen)
                        Union(Parents, contact.A, contact.B);
                }
                for (int i = 0; i < Positions.Length; i++)
                {
                    if (Modes[i] == BlockSimMode.Frozen) continue;
                    if (Modes[i] == BlockSimMode.Resting && ValidateSupport[i] != 0 && Supported[i] == 0)
                    { Modes[i] = BlockSimMode.Active; Quiet[i] = 0; result.SupportWakes++; }
                    if (Modes[i] == BlockSimMode.Active)
                    {
                        bool quiet = Supported[i] != 0 && External[i] == 0 && math.lengthsq(Velocities[i]) < SleepSpeed * SleepSpeed &&
                            math.lengthsq(Positions[i] - TickPositions[i]) < SleepDisplacement * SleepDisplacement && Corrections[i] < SleepDisplacement;
                        Quiet[i] = quiet ? Quiet[i] + Delta : 0;
                    }
                    int root = Find(Parents, i);
                    if (Modes[i] == BlockSimMode.Active && (Quiet[i] < SleepSeconds || Supported[i] == 0)) Counts[root] = 0;
                }
                for (int i = 0; i < Positions.Length; i++)
                {
                    if (Modes[i] == BlockSimMode.Active && Counts[Find(Parents, i)] != 0 && Supported[i] != 0)
                    { Modes[i] = BlockSimMode.Resting; Velocities[i] = float3.zero; }
                    if (!math.all(Positions[i] == TickPositions[i])) Versions[i]++;
                    External[i] = 0; ValidateSupport[i] = 0;
                    if (Modes[i] == BlockSimMode.Active) result.ActiveCount++;
                    else if (Modes[i] == BlockSimMode.Resting) result.RestingCount++;
                }
                Result.Value = result;
            }
        }
    }
}
