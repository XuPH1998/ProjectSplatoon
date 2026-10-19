using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splatoon.Blocks.Simulation
{
    public struct BlockNormalDiagnostics
    {
        public long Calls, FastPaths, CertificateChecks, Stalls;
        public long BurstCalls, ManagedCalls, InitialConstraintChecks, AdjacencyChecks, TreeNodeVisits;
        public long AdjacencyVisits, AdjacencyDuplicateSkips;
        public int MaxTreeNodes;
        public int FaultReason, WorstContact;
        public float FeasibilityResidual, ComplementarityResidual, RawComplementarity;
        public float PlaneResidual, ConeResidual, VelocityTolerance, OutputQuantization, AllowedDisplacement;
    }

    public sealed partial class BlockSimulation
    {
        NativeList<float3> _normalPreviousVelocities;
        NativeList<float4> _normalPreviousImpulses;
        NativeList<int> _normalViolationHeap, _normalHeapPositions;
        NativeReference<BlockNormalDiagnostics> _normalDiagnostics;
        public BlockNormalDiagnostics NormalDiagnostics => _normalDiagnostics.Value;

        void InitializeNormalWork(int capacity)
        {
            _normalPreviousVelocities = new NativeList<float3>(capacity, Allocator.Persistent);
            _normalPreviousImpulses = new NativeList<float4>(capacity, Allocator.Persistent);
            _normalViolationHeap = new NativeList<int>(capacity, Allocator.Persistent);
            _normalHeapPositions = new NativeList<int>(capacity, Allocator.Persistent);
            _normalDiagnostics = new NativeReference<BlockNormalDiagnostics>(Allocator.Persistent);
        }
        void DisposeNormalWork()
        {
            _normalPreviousVelocities.Dispose(); _normalPreviousImpulses.Dispose();
            _normalViolationHeap.Dispose(); _normalHeapPositions.Dispose(); _normalDiagnostics.Dispose();
        }

        void ThrowNormalFault(int substep, int pass, string stage)
        {
            if (_work.Value.Fault == 0) return;
            _diagnostics.FaultCode = _work.Value.Fault;
            BlockNormalDiagnostics d = _normalDiagnostics.Value;
            throw new System.InvalidOperationException($"AABB normal solver fault={_work.Value.Fault}, reason={d.FaultReason}, tick={_diagnostics.TickCount}, substep={substep}, pass={pass}, stage={stage}, contact={d.WorstContact}, feasibility={d.FeasibilityResidual:R}, complementarity={d.ComplementarityResidual:R}, rawProduct={d.RawComplementarity:R}, tolerance={d.VelocityTolerance:R}, quantization={d.OutputQuantization:R}, plane={d.PlaneResidual:R}, cone={d.ConeResidual:R}; tick was rolled back.");
        }
        // A normal block solve is separate from the six local friction/velocity sweeps. It solves the
        // mass weighted projection for every cardinal constraint, including inactive/redundant edges.
        // The working set is only a basis: releasing a negative multiplier never removes a constraint.
        struct AxisNode
        {
            public int Head, AllHead, Parent, ParentEdge, Mark;
            public byte Changed;
            public double Value, Direction, Subtree;
        }

        struct AxisEdge
        {
            public int Positive, Negative, Contact, NextPositive, NextNegative, PreviousPositive, PreviousNegative;
            public int NextAllPositive, NextAllNegative;
            public byte Active;
            public double Bound, Lambda, Direction;
        }

        void SolveAxisNormals(float delta)
        {
            int nodeCapacity = _axisNodes.Capacity, edgeCapacity = _axisEdges.Capacity, queueCapacity = _axisQueue.Capacity;
            int velocityCapacity = _normalPreviousVelocities.Capacity, impulseCapacity = _normalPreviousImpulses.Capacity;
            int heapCapacity = _normalViolationHeap.Capacity, heapPositionCapacity = _normalHeapPositions.Capacity;
            // Each axis has at most one inequality per contact. Reuse the indexed heap storage;
            // grow before scheduling so membership changes never resize it inside the solve.
            if (_normalViolationHeap.Capacity < _contacts.Length) _normalViolationHeap.Capacity = Grow(_contacts.Length);
            if (_normalHeapPositions.Capacity < _contacts.Length) _normalHeapPositions.Capacity = Grow(_contacts.Length);
            new AxisNormalJob { Contacts = _contacts, Velocities = _velocities.AsArray(), Modes = _modes.AsArray(),
                Active = _broadActive.AsArray(),
                Parents = _parents.AsArray(), Nodes = _axisNodes, Edges = _axisEdges, Queue = _axisQueue,
                PreviousVelocities = _normalPreviousVelocities, PreviousImpulses = _normalPreviousImpulses,
                ViolationHeap = _normalViolationHeap, HeapPositions = _normalHeapPositions,
                Delta = delta, Result = _work, NormalResult = _normalDiagnostics }.Schedule().Complete();
            if (_axisNodes.Capacity != nodeCapacity) _diagnostics.CapacityGrowths++;
            if (_axisEdges.Capacity != edgeCapacity) _diagnostics.CapacityGrowths++;
            if (_axisQueue.Capacity != queueCapacity) _diagnostics.CapacityGrowths++;
            if (_normalPreviousVelocities.Capacity != velocityCapacity) _diagnostics.CapacityGrowths++;
            if (_normalPreviousImpulses.Capacity != impulseCapacity) _diagnostics.CapacityGrowths++;
            if (_normalViolationHeap.Capacity != heapCapacity) _diagnostics.CapacityGrowths++;
            if (_normalHeapPositions.Capacity != heapPositionCapacity) _diagnostics.CapacityGrowths++;
        }

        [BurstCompile]
        struct AxisNormalJob : IJob
        {
            public NativeList<SimContact> Contacts;
            public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<BlockSimMode> Modes;
            // BuildPairs refreshes this after every contact wake closure. Dense slots remain the
            // addressing scheme; passive bodies cannot change velocity in the normal solver.
            [ReadOnly] public NativeArray<int> Active;
            public NativeArray<int> Parents;
            public NativeList<AxisNode> Nodes;
            public NativeList<AxisEdge> Edges;
            public NativeList<int> Queue;
            public NativeList<float3> PreviousVelocities;
            public NativeList<float4> PreviousImpulses;
            public NativeList<int> ViolationHeap, HeapPositions;
            public NativeReference<WorkResult> Result;
            public NativeReference<BlockNormalDiagnostics> NormalResult;
            public float Delta;
            int _count, _mark;
            long _operations, _limit;
            WorkResult _result;
            BlockNormalDiagnostics _normal;
            const double FeasibilityTolerance = 1e-9;

            public void Execute()
            {
                _count = Velocities.Length; _result = Result.Value; _normal = NormalResult.Value; _normal.Calls++;
                // This tests the actual job invocation. The editor's global Burst-enabled option
                // does not prove that this particular job has finished compiling or ran as Burst.
                bool burst = true; MarkManaged(ref burst);
                if (burst) _normal.BurstCalls++; else _normal.ManagedCalls++;
                if (Certificate()) { _normal.FastPaths++; Commit(); return; }
                if (_result.Fault != 0) { Commit(); return; }
                Nodes.ResizeUninitialized(_count * 2);
                PreviousVelocities.ResizeUninitialized(_count); PreviousImpulses.ResizeUninitialized(Contacts.Length);
                long blockPasses = 0, blockLimit = 32L * (Contacts.Length + 1L) * (_count + 1L);
                while (true)
                {
                    if (++blockPasses > blockLimit) { Fail(4); Commit(); return; }
                    for (int p = 0; p < Active.Length; p++) { int i = Active[p]; PreviousVelocities[i] = Velocities[i]; }
                    for (int c = 0; c < Contacts.Length; c++)
                    { SimContact contact = Contacts[c]; PreviousImpulses[c] = new float4(contact.TangentImpulse, contact.NormalImpulse); }
                    _result.NormalBlockPasses++;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Build(axis);
                        if (Edges.Length == 0) continue;
                        _operations = 0;
                        // Failure is explicit and rolls the tick back. This guard never drops a candidate,
                        // clamps the island size, or silently accepts the current approximation.
                        _limit = 32L * (Edges.Length + 1L) * (_count + 1L);
                        while (ViolationHeap.Length != 0)
                        {
                            // The indexed heap contains exactly the violated inactive inequalities.
                            // Its minimum reproduces the old first-violation scan's contact order.
                            int violated = ViolationHeap[0];
                            if (!Insert(violated)) { Fail(2); Commit(); return; }
                        }
                        // The certificate includes all original inequalities, not merely the forest basis.
                        for (int e = 0; e < Edges.Length; e++)
                        {
                            AxisEdge edge = Edges[e];
                            double difference = Difference(edge);
                            double axisResidual = math.max(0, edge.Bound - difference);
                            _result.AxisResidual = math.max(_result.AxisResidual, (float)axisResidual);
                            if (!math.isfinite(difference) || axisResidual > 1e-7 || edge.Lambda < -1e-9 || !math.isfinite(edge.Lambda) ||
                                (edge.Active != 0 && math.abs(difference - edge.Bound) > 1e-7))
                            { Fail(2); Commit(); return; }
                            SimContact contact = Contacts[edge.Contact];
                            contact.NormalImpulse = (float)math.max(0, edge.Lambda); Contacts[edge.Contact] = contact;
                        }
                        for (int p = 0; p < Active.Length; p++)
                        {
                            int i = Active[p];
                            float3 velocity = Velocities[i]; velocity[axis] = (float)Nodes[i].Value; Velocities[i] = velocity;
                        }
                    }
                    // Oblique normals couple axes. This is a dual block-coordinate normal solve:
                    // cardinal blocks above are exact, and each oblique constraint has its exact
                    // one-dimensional nonnegative update. It is distinct from the six friction sweeps.
                    for (int c = 0; c < Contacts.Length; c++)
                    {
                        SimContact contact = Contacts[c]; if (CardinalAxis(contact.Normal) >= 0) continue;
                        float inverse = InvMass(Modes[contact.A]) + (contact.B < 0 ? 0 : InvMass(Modes[contact.B]));
                        if (inverse == 0) continue;
                        float desired = -math.max(0, contact.Separation - RestGap) / Delta;
                        float normalVelocity = math.dot(Relative(contact), contact.Normal);
                        float lambda = math.max(0, contact.NormalImpulse + (desired - normalVelocity) / inverse);
                        float difference = lambda - contact.NormalImpulse;
                        if (difference != 0) { Apply(contact, contact.Normal * difference); _result.ObliqueUpdates++; }
                        contact.NormalImpulse = lambda; Contacts[c] = contact;
                    }
                    for (int c = 0; c < Contacts.Length; c++)
                    {
                        SimContact contact = Contacts[c]; float length = math.length(contact.TangentImpulse);
                        float3 tangent = contact.TangentImpulse;
                        float plane = math.dot(tangent, contact.Normal), limit = contact.NormalImpulse * contact.StaticFriction;
                        if (math.abs(plane) > ImpulseTolerance(contact)) tangent -= contact.Normal * (plane / math.lengthsq(contact.Normal));
                        length = math.length(tangent);
                        if (length > limit + ImpulseTolerance(contact))
                            tangent *= math.min(1, contact.NormalImpulse * contact.DynamicFriction / math.max(1e-10f, length));
                        if (math.all(tangent == contact.TangentImpulse)) continue;
                        Apply(contact, tangent - contact.TangentImpulse); contact.TangentImpulse = tangent;
                        Contacts[c] = contact; _result.FrictionRetractions++;
                    }
                    if (Certificate()) break;
                    if (_result.Fault != 0) { Commit(); return; }
                    bool changed = false;
                    for (int p = 0; p < Active.Length; p++)
                    { int i = Active[p]; if (math.any(PreviousVelocities[i] != Velocities[i])) { changed = true; break; } }
                    for (int c = 0; !changed && c < Contacts.Length; c++)
                    { SimContact contact = Contacts[c]; changed = math.any(PreviousImpulses[c] != new float4(contact.TangentImpulse, contact.NormalImpulse)); }
                    if (!changed) { _normal.Stalls++; Fail(3); Commit(); return; }
                }
                Commit();
            }

            [BurstDiscard]
            static void MarkManaged(ref bool burst) { burst = false; }

            void Commit() { Result.Value = _result; NormalResult.Value = _normal; }
            void Fail(int reason) { _result.Fault = 4; _normal.FaultReason = reason; }

            // The certificate is in velocity/impulse units. Raw lambda*slack is diagnostic only:
            // multiplying by a large contact impulse cannot change the accuracy float output can
            // represent. CCD and positional penetration gates remain independent.
            bool Certificate()
            {
                _normal.CertificateChecks++; bool valid = true; double worstRatio = 0;
                _normal.WorstContact = -1; _normal.FeasibilityResidual = _normal.ComplementarityResidual = 0;
                _normal.RawComplementarity = _normal.PlaneResidual = _normal.ConeResidual = 0;
                _normal.VelocityTolerance = _normal.OutputQuantization = _normal.AllowedDisplacement = 0;
                for (int p = 0; p < Active.Length; p++) if (!Finite(Velocities[Active[p]])) { Fail(1); return false; }
                for (int c = 0; c < Contacts.Length; c++)
                {
                    SimContact contact = Contacts[c];
                    if (!Finite(contact.Normal) || !Finite(contact.TangentImpulse) || !math.isfinite(contact.NormalImpulse) ||
                        !math.isfinite(contact.Separation) || !math.isfinite(contact.StaticFriction) || !math.isfinite(contact.DynamicFriction))
                    { _normal.WorstContact = c; Fail(1); return false; }
                    if (contact.NormalImpulse < 0) { _normal.WorstContact = c; Fail(5); return false; }
                    double inverse = InvMass(Modes[contact.A]) + (contact.B < 0 ? 0 : InvMass(Modes[contact.B]));
                    if (inverse == 0) continue;
                    float3 a = BodyVelocity(Velocities, Modes, contact.A), b = BodyVelocity(Velocities, Modes, contact.B);
                    double3 normal = (double3)contact.Normal, relative = (double3)a - (double3)b;
                    double desired = -math.max(0, contact.Separation - RestGap) / Delta;
                    double slack = math.dot(relative, normal) - desired;
                    double quantization = math.dot(math.abs(normal), HalfUlp(a) + HalfUlp(b));
                    const double gamma5 = 5.551115123125786e-16;
                    double tolerance = 1e-5 + quantization + gamma5 * (math.dot(math.abs(normal * relative), new double3(1)) + math.abs(desired));
                    double feasibility = math.max(0, -slack);
                    // min(lambda*M^-1, slack) is the nonnegative LCP natural residual, in m/s.
                    double complementarity = math.min(contact.NormalImpulse * inverse, math.max(0, slack));
                    double3 tangent = (double3)contact.TangentImpulse;
                    double plane = math.abs(math.dot(tangent, normal));
                    double cone = math.max(0, math.length(tangent) - contact.NormalImpulse * (double)contact.StaticFriction);
                    double impulseTolerance = ImpulseTolerance(contact);
                    if (!math.isfinite(slack) || !math.isfinite(tolerance) || !math.isfinite(plane) || !math.isfinite(cone))
                    { _normal.WorstContact = c; Fail(1); return false; }
                    _normal.FeasibilityResidual = math.max(_normal.FeasibilityResidual, (float)feasibility);
                    _normal.ComplementarityResidual = math.max(_normal.ComplementarityResidual, (float)complementarity);
                    _normal.RawComplementarity = math.max(_normal.RawComplementarity, (float)(math.abs(slack) * contact.NormalImpulse));
                    _normal.PlaneResidual = math.max(_normal.PlaneResidual, (float)plane);
                    _normal.ConeResidual = math.max(_normal.ConeResidual, (float)cone);
                    _normal.VelocityTolerance = math.max(_normal.VelocityTolerance, (float)tolerance);
                    _normal.OutputQuantization = math.max(_normal.OutputQuantization, (float)quantization);
                    _normal.AllowedDisplacement = math.max(_normal.AllowedDisplacement, (float)(tolerance * Delta));
                    double ratio = math.max(math.max(feasibility, complementarity) / tolerance, math.max(plane, cone) / impulseTolerance);
                    if (ratio > worstRatio) { worstRatio = ratio; _normal.WorstContact = c; }
                    if (ratio > 1) valid = false;
                }
                if (valid)
                {
                    _result.NormalResidual = math.max(_result.NormalResidual, _normal.FeasibilityResidual);
                    _result.FrictionConeExcess = math.max(_result.FrictionConeExcess, _normal.ConeResidual);
                }
                return valid;
            }

            static double HalfUlp(float value)
            {
                value = math.abs(value); uint bits = math.asuint(value);
                float next = math.asfloat(bits + 1u);
                return math.isfinite(next) ? ((double)next - value) * .5 : ((double)value - math.asfloat(bits - 1u)) * .5;
            }
            static double3 HalfUlp(float3 value) => new double3(HalfUlp(value.x), HalfUlp(value.y), HalfUlp(value.z));
            static double ImpulseTolerance(SimContact contact)
            {
                double3 tangent = (double3)contact.TangentImpulse;
                return 1e-7 + math.length(HalfUlp(contact.TangentImpulse)) + contact.StaticFriction * HalfUlp(contact.NormalImpulse) +
                    5.551115123125786e-16 * math.length(tangent);
            }

            float3 Relative(SimContact contact) => BodyVelocity(Velocities, Modes, contact.A) - BodyVelocity(Velocities, Modes, contact.B);
            void Apply(SimContact contact, float3 impulse)
            {
                if (Modes[contact.A] == BlockSimMode.Active) Velocities[contact.A] += impulse / Mass;
                if (contact.B >= 0 && Modes[contact.B] == BlockSimMode.Active) Velocities[contact.B] -= impulse / Mass;
            }

            static int CardinalAxis(float3 normal)
            {
                float3 n = math.abs(normal);
                int axis = n.x > n.y ? (n.x > n.z ? 0 : 2) : (n.y > n.z ? 1 : 2);
                float3 expected = float3.zero; expected[axis] = 1;
                return math.all(n == expected) ? axis : -1;
            }

            void Build(int axis)
            {
                Edges.Clear(); _mark = 0;
                for (int p = 0; p < Active.Length; p++) { int i = Active[p]; Parents[i] = i; }
                for (int c = 0; c < Contacts.Length; c++)
                {
                    SimContact contact = Contacts[c]; if (CardinalAxis(contact.Normal) != axis) continue;
                    bool a = Modes[contact.A] == BlockSimMode.Active;
                    bool b = contact.B >= 0 && Modes[contact.B] == BlockSimMode.Active;
                    if (!a && !b) continue;
                    // All previously applied same-substep normal impulses are withdrawn exactly once.
                    // Tangent and oblique impulses stay in the unconstrained velocities for this solve.
                    float impulse = contact.Normal[axis] * contact.NormalImpulse;
                    if (a) { float3 v = Velocities[contact.A]; v[axis] -= impulse / Mass; Velocities[contact.A] = v; }
                    if (b) { float3 v = Velocities[contact.B]; v[axis] += impulse / Mass; Velocities[contact.B] = v; }
                    contact.NormalImpulse = 0; Contacts[c] = contact;
                    if (a && b) Union(Parents, contact.A, contact.B);
                }
                for (int p = 0; p < Active.Length; p++)
                {
                    int i = Active[p];
                    Nodes[i] = new AxisNode { Head = -1, AllHead = -1, Parent = -1, ParentEdge = -1, Value = Velocities[i][axis] };
                    Nodes[_count + i] = new AxisNode { Head = -1, AllHead = -1, Parent = -1, ParentEdge = -1 };
                }
                for (int c = 0; c < Contacts.Length; c++)
                {
                    SimContact contact = Contacts[c]; if (CardinalAxis(contact.Normal) != axis) continue;
                    bool a = Modes[contact.A] == BlockSimMode.Active;
                    bool b = contact.B >= 0 && Modes[contact.B] == BlockSimMode.Active;
                    if (!a && !b) continue;
                    // Independent dynamic components get independent ground nodes. A static map never
                    // joins distant floor blocks into one collision/support island.
                    int ground = _count + Find(Parents, a ? contact.A : contact.B);
                    int positive = a ? contact.A : ground, negative = b ? contact.B : ground;
                    if (contact.Normal[axis] < 0) { int swap = positive; positive = negative; negative = swap; }
                    AxisNode positiveNode = Nodes[positive], negativeNode = Nodes[negative];
                    int index = Edges.Length;
                    Edges.Add(new AxisEdge { Positive = positive, Negative = negative, Contact = c,
                        Bound = -math.max(0, contact.Separation - RestGap) / Delta,
                        NextAllPositive = positiveNode.AllHead, NextAllNegative = negativeNode.AllHead,
                        NextPositive = -1, NextNegative = -1, PreviousPositive = -1, PreviousNegative = -1 });
                    positiveNode.AllHead = index; negativeNode.AllHead = index;
                    Nodes[positive] = positiveNode; Nodes[negative] = negativeNode;
                }
                ViolationHeap.Clear(); HeapPositions.ResizeUninitialized(Edges.Length);
                for (int e = 0; e < Edges.Length; e++) HeapPositions[e] = -1;
                _normal.InitialConstraintChecks += Edges.Length;
                for (int e = 0; e < Edges.Length; e++) RefreshViolation(e);
            }

            double Difference(AxisEdge edge) => Nodes[edge.Positive].Value - Nodes[edge.Negative].Value;

            bool Insert(int candidateIndex)
            {
                while (true)
                {
                    if (++_operations > _limit) return false;
                    _result.AxisPivots++;
                    AxisEdge candidate = Edges[candidateIndex];
                    double gap = candidate.Bound - Difference(candidate);
                    if (gap <= FeasibilityTolerance)
                    {
                        // A candidate receiving a partial dual step is already present in velocity.
                        // It becomes a binding basis edge only when its primal boundary is reached.
                        if (candidate.Active == 0 && candidate.Lambda > 0) Add(candidateIndex);
                        else RefreshViolation(candidateIndex);
                        return true;
                    }
                    Direction(candidate);
                    double curvature = Nodes[candidate.Positive].Direction - Nodes[candidate.Negative].Direction;
                    double primal = curvature > 1e-14 ? gap / curvature : double.PositiveInfinity;
                    double dual = double.PositiveInfinity; int leaving = -1;
                    // Only edges of the candidate's two working-set trees have a nonzero derivative.
                    for (int q = 0; q < Queue.Length; q++)
                    {
                        int e = Nodes[Queue[q]].ParentEdge; if (e < 0) continue;
                        AxisEdge edge = Edges[e]; if (edge.Direction >= -1e-14) continue;
                        double t = math.max(0, -edge.Lambda / edge.Direction);
                        if (t < dual - 1e-12 || (math.abs(t - dual) <= 1e-12 && (leaving < 0 || e < leaving)))
                        { dual = t; leaving = e; }
                    }
                    double step = math.min(primal, dual);
                    if (!math.isfinite(step) || step < 0) return false;
                    for (int q = 0; q < Queue.Length; q++)
                    {
                        int i = Queue[q]; AxisNode node = Nodes[i];
                        double previous = node.Value;
                        if (i < _count) node.Value += step * node.Direction;
                        node.Changed = node.Value != previous ? (byte)1 : (byte)0;
                        Nodes[i] = node;
                        if (node.ParentEdge < 0) continue;
                        AxisEdge edge = Edges[node.ParentEdge]; edge.Lambda = math.max(0, edge.Lambda + step * edge.Direction);
                        Edges[node.ParentEdge] = edge;
                    }
                    // All values for this pivot are final before any original inequality is refreshed.
                    // If both endpoints changed, only the smaller endpoint owns the refresh. The Mark
                    // check is required: Changed on nodes outside this pivot's Queue may be stale.
                    // Ground and unchanged anchored trees still perform no adjacency work here.
                    long beforeAdjacencyChecks = _result.AxisConstraintChecks;
                    long adjacencyVisits = 0, duplicateSkips = 0;
                    for (int q = 0; q < Queue.Length; q++)
                    {
                        int i = Queue[q]; if (Nodes[i].Changed == 0) continue;
                        int e = Nodes[i].AllHead;
                        while (e >= 0)
                        {
                            AxisEdge edge = Edges[e];
                            int other = edge.Positive == i ? edge.Negative : edge.Positive;
                            adjacencyVisits++;
                            // Short circuit avoids inspecting the other node unless it could own this edge.
                            bool duplicate = other < i && Nodes[other].Mark == _mark && Nodes[other].Changed != 0;
                            if (duplicate) duplicateSkips++;
                            else RefreshViolation(e);
                            // Advance for both outcomes; skipping a refresh must never skip this link.
                            e = edge.Positive == i ? edge.NextAllPositive : edge.NextAllNegative;
                        }
                    }
                    _normal.AdjacencyVisits += adjacencyVisits;
                    _normal.AdjacencyDuplicateSkips += duplicateSkips;
                    _normal.AdjacencyChecks += _result.AxisConstraintChecks - beforeAdjacencyChecks;
                    candidate = Edges[candidateIndex]; candidate.Lambda += step; Edges[candidateIndex] = candidate;
                    if (dual < primal - 1e-12)
                    {
                        if (leaving < 0) return false;
                        Remove(leaving); // A zero dual step still makes structural progress by releasing an edge.
                        continue;
                    }
                    // Positive curvature proves the endpoints are in different working components.
                    // Choosing the primal pivot on exact ties is deterministic and preserves a forest.
                    if (curvature <= 1e-14) return false;
                    Add(candidateIndex); return true;
                }
            }

            void Direction(AxisEdge candidate)
            {
                if (++_mark == int.MaxValue)
                {
                    for (int p = 0; p < Active.Length; p++)
                    {
                        int i = Active[p]; AxisNode node = Nodes[i]; node.Mark = 0; Nodes[i] = node;
                        node = Nodes[_count + i]; node.Mark = 0; Nodes[_count + i] = node;
                    }
                    _mark = 1;
                }
                Queue.Clear();
                Tree(candidate.Positive, candidate);
                if (Nodes[candidate.Negative].Mark != _mark) Tree(candidate.Negative, candidate);
                // q = M*d-a. A tree postorder gives the unique old-multiplier derivative.
                // Ground carries the balancing reaction, with zero physical velocity and no finite mass.
                for (int q = Queue.Length - 1; q >= 0; q--)
                {
                    int i = Queue[q]; AxisNode node = Nodes[i];
                    if (node.ParentEdge < 0) continue;
                    AxisEdge edge = Edges[node.ParentEdge]; edge.Direction = node.Subtree * (edge.Positive == i ? 1 : -1);
                    Edges[node.ParentEdge] = edge;
                    AxisNode parent = Nodes[node.Parent]; parent.Subtree += node.Subtree; Nodes[node.Parent] = parent;
                }
            }

            void Tree(int seed, AxisEdge candidate)
            {
                int start = Queue.Length, ground = -1; double mass = 0;
                AxisNode first = Nodes[seed]; first.Mark = _mark; first.Parent = -1; first.ParentEdge = -1; Nodes[seed] = first;
                Queue.Add(seed);
                for (int q = start; q < Queue.Length; q++)
                {
                    int i = Queue[q]; if (i < _count) mass += Mass; else ground = i;
                    int e = Nodes[i].Head;
                    while (e >= 0)
                    {
                        AxisEdge edge = Edges[e]; int other = edge.Positive == i ? edge.Negative : edge.Positive;
                        if (Nodes[other].Mark != _mark)
                        {
                            AxisNode child = Nodes[other]; child.Mark = _mark; child.Parent = i; child.ParentEdge = e; Nodes[other] = child;
                            Queue.Add(other);
                        }
                        e = edge.Positive == i ? edge.NextPositive : edge.NextNegative;
                    }
                }
                // One aggregate update per traversed tree, not instrumentation inside every edge
                // visit. The solver still performs its original complete forest traversals.
                _normal.TreeNodeVisits += Queue.Length - start;
                _normal.MaxTreeNodes = math.max(_normal.MaxTreeNodes, Queue.Length - start);
                double coefficient = (Nodes[candidate.Positive].Mark == _mark ? 1 : 0) -
                    (Nodes[candidate.Negative].Mark == _mark ? 1 : 0);
                // The second tree must exclude endpoint marks from the previously traversed first tree.
                if (start > 0) coefficient = candidate.Positive == seed ? 1 : -1;
                double direction = ground >= 0 ? 0 : coefficient / mass, sum = 0;
                for (int q = start; q < Queue.Length; q++)
                {
                    int i = Queue[q]; AxisNode node = Nodes[i]; node.Direction = direction;
                    node.Subtree = i < _count ? Mass * direction - (i == candidate.Positive ? 1 : 0) + (i == candidate.Negative ? 1 : 0) : 0;
                    sum += node.Subtree; Nodes[i] = node;
                }
                if (ground >= 0) { AxisNode node = Nodes[ground]; node.Subtree = -sum; node.Direction = 0; Nodes[ground] = node; }
            }

            void Add(int index)
            {
                AxisEdge edge = Edges[index]; if (edge.Active != 0) return;
                edge.Active = 1;
                AxisNode p = Nodes[edge.Positive], n = Nodes[edge.Negative];
                edge.PreviousPositive = edge.PreviousNegative = -1;
                edge.NextPositive = p.Head; edge.NextNegative = n.Head;
                if (p.Head >= 0) SetPrevious(p.Head, edge.Positive, index);
                if (n.Head >= 0) SetPrevious(n.Head, edge.Negative, index);
                p.Head = index; n.Head = index; Nodes[edge.Positive] = p; Nodes[edge.Negative] = n; Edges[index] = edge;
                RefreshViolation(index);
            }

            void Remove(int index)
            {
                AxisEdge edge = Edges[index];
                Unlink(index, edge.Positive, edge.PreviousPositive, edge.NextPositive);
                Unlink(index, edge.Negative, edge.PreviousNegative, edge.NextNegative);
                edge = Edges[index]; edge.Active = 0; edge.Lambda = 0; edge.Direction = 0;
                edge.NextPositive = edge.NextNegative = edge.PreviousPositive = edge.PreviousNegative = -1; Edges[index] = edge;
                RefreshViolation(index);
            }

            void RefreshViolation(int index)
            {
                _result.AxisConstraintChecks++;
                AxisEdge edge = Edges[index];
                bool violated = edge.Active == 0 && edge.Bound - Difference(edge) > FeasibilityTolerance;
                int position = HeapPositions[index];
                if (violated)
                {
                    if (position >= 0) return;
                    position = ViolationHeap.Length; ViolationHeap.Add(index); HeapPositions[index] = position;
                    SiftUp(position);
                }
                else if (position >= 0) RemoveViolation(position);
            }

            void RemoveViolation(int position)
            {
                int removed = ViolationHeap[position], lastPosition = ViolationHeap.Length - 1;
                int last = ViolationHeap[lastPosition]; ViolationHeap.RemoveAtSwapBack(lastPosition); HeapPositions[removed] = -1;
                if (position == lastPosition) return;
                ViolationHeap[position] = last; HeapPositions[last] = position;
                if (position > 0 && last < ViolationHeap[(position - 1) >> 1]) SiftUp(position);
                else SiftDown(position);
            }

            void SiftUp(int position)
            {
                int value = ViolationHeap[position];
                while (position > 0)
                {
                    int parent = (position - 1) >> 1, other = ViolationHeap[parent]; if (other <= value) break;
                    ViolationHeap[position] = other; HeapPositions[other] = position; position = parent;
                }
                ViolationHeap[position] = value; HeapPositions[value] = position;
            }

            void SiftDown(int position)
            {
                int value = ViolationHeap[position], length = ViolationHeap.Length;
                while (position < length / 2)
                {
                    int child = position * 2 + 1;
                    if (child + 1 < length && ViolationHeap[child + 1] < ViolationHeap[child]) child++;
                    int other = ViolationHeap[child]; if (other >= value) break;
                    ViolationHeap[position] = other; HeapPositions[other] = position; position = child;
                }
                ViolationHeap[position] = value; HeapPositions[value] = position;
            }
            void Unlink(int index, int nodeIndex, int previous, int next)
            {
                if (previous < 0) { AxisNode node = Nodes[nodeIndex]; node.Head = next; Nodes[nodeIndex] = node; }
                else { AxisEdge edge = Edges[previous]; if (edge.Positive == nodeIndex) edge.NextPositive = next; else edge.NextNegative = next; Edges[previous] = edge; }
                if (next >= 0) SetPrevious(next, nodeIndex, previous);
            }
            void SetPrevious(int index, int nodeIndex, int previous)
            { AxisEdge edge = Edges[index]; if (edge.Positive == nodeIndex) edge.PreviousPositive = previous; else edge.PreviousNegative = previous; Edges[index] = edge; }
        }
    }
}
