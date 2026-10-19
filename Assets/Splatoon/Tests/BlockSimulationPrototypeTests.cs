#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    /// <summary>Behaviour oracles for the data-only prototype; these never simulate the live match or PhysX.</summary>
    [Category("BlockSimulationPrototype")]
    public sealed class BlockSimulationPrototypeTests
    {
        const float Dt = 1f / 60f;
        const float Edge = .30f;
        const float Half = .15f;
        static readonly float3[] FarObservers = { new(100, 0, 100) };

        static BlockSimState State(ulong id, float3 position, float3 velocity = default,
            BlockSimMode mode = BlockSimMode.Active, uint generation = 1, uint epoch = 1, uint round = 1)
            => new()
            {
                handle = new BlockSimHandle(epoch, round, id, generation), owner = 1,
                position = position, velocity = velocity, mode = mode
            };

        static BlockSimHandle Add(BlockSimulation world, BlockSimState state)
        {
            Assert.That(world.Add(state), Is.True, "Fixture must create the requested stable identity.");
            return state.handle;
        }

        static BlockSimState Read(BlockSimulation world, BlockSimHandle handle)
        {
            Assert.That(world.TryGet(handle, out var state), Is.True, $"Missing live body {handle}");
            Assert.That(math.all(math.isfinite(state.position)) && math.all(math.isfinite(state.velocity)), Is.True,
                $"Non-finite state for {handle}");
            return state;
        }

        static void Steps(BlockSimulation world, BlockMapSnapshot map, int count)
        {
            for (int tick = 0; tick < count; tick++) world.Step(map, Dt);
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero, "No bounded buffer or solver failure may be hidden.");
        }

        static BlockMapCollisionBuilder Floor(float extent = 40)
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, -.1f, 0), new float3(extent, .1f, extent), 1);
            return builder;
        }

        static BlockSimHandle[] Column(BlockSimulation world, int height)
        {
            var handles = new BlockSimHandle[height];
            for (int y = 0; y < height; y++) handles[y] = Add(world, State((ulong)y + 1, new float3(0, Half + y * Edge, 0)));
            return handles;
        }

        static void SettleUnderStrictGates(BlockSimulation world, BlockMapSnapshot map, BlockSimHandle[] handles, string label,
            Action<int> observe = null)
        {
            bool previousOverTwoMillimetres = false;
            float peak = 0;
            var states = new List<BlockSimState>(handles.Length);
            using var contacts = new NativeList<BlockStaticHit>(16, Allocator.TempJob);
            for (int tick = 1; tick <= 300; tick++)
            {
                world.Step(map, Dt);
                observe?.Invoke(tick);
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero, label + " solver fault");
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired), label + " candidate loss");
                states.Clear();
                foreach (var handle in handles) states.Add(Read(world, handle));
                float staticPenetration = 0, dynamicPenetration = 0;
                foreach (var state in states)
                {
                    contacts.Clear(); BlockMapQueries.CollectContacts(map, state.position, new float3(Half), contacts);
                    for (int c = 0; c < contacts.Length; c++) staticPenetration = math.max(staticPenetration, contacts[c].Penetration);
                }
                for (int a = 0; a < states.Count; a++)
                for (int b = a + 1; b < states.Count; b++)
                {
                    float3 overlap = Edge - math.abs(states[a].position - states[b].position);
                    if (math.all(overlap > 0)) dynamicPenetration = math.max(dynamicPenetration, math.cmin(overlap));
                }
                float penetration = math.max(staticPenetration, dynamicPenetration);
                peak = math.max(peak, penetration);
                Assert.That(penetration, Is.LessThanOrEqualTo(.005f + .00001f), $"{label}: tick {tick} exceeded the 5 mm transient limit.");
                bool overTwoMillimetres = penetration > .002f + .00001f;
                Assert.That(previousOverTwoMillimetres && overTwoMillimetres, Is.False,
                    $"{label}: penetration over 2 mm persisted for two committed ticks at {tick}.");
                previousOverTwoMillimetres = overTwoMillimetres;
                int resting = 0;
                foreach (var state in states) if (state.mode == BlockSimMode.Resting) resting++;
                if (handles.Length == 1 && tick == 60)
                    Assert.That(resting, Is.EqualTo(1), label + ": a single controlled cube must sleep within 1 s.");
                if (tick == 180)
                {
                    TestContext.WriteLine($"{label}: at 3 s resting {resting}/{handles.Length}, peak penetration {peak:R} m.");
                    Assert.That(resting, Is.GreaterThanOrEqualTo((int)Math.Ceiling(handles.Length * .95)), label + ": at least 95% must sleep within 3 s.");
                }
                if (tick == 300)
                {
                    TestContext.WriteLine($"{label}: at 5 s resting {resting}/{handles.Length}, final static {staticPenetration:R} m, dynamic {dynamicPenetration:R} m.");
                    Assert.That(resting, Is.EqualTo(handles.Length), label + ": every controlled cube must sleep within 5 s.");
                    Assert.That(staticPenetration, Is.LessThanOrEqualTo(.001f + .00001f), label + ": final static penetration exceeds 1 mm.");
                    Assert.That(dynamicPenetration, Is.LessThanOrEqualTo(.002f + .00001f), label + ": final inter-block penetration exceeds 2 mm.");
                }
            }
        }

        [Test]
        public void ThinWallSweepReturnsTheFirstContinuousContact()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, 1, 0), new float3(.004f, 2, 2), 17);
            using var map = builder.Build(Allocator.Persistent);
            bool hit = BlockMapQueries.Sweep(map.Snapshot, new float3(-2, 1, 0), new float3(Half),
                new float3(4, 0, 0), out var contact);
            Assert.That(hit, Is.True, "An 8 mm wall lies strictly between both endpoints.");
            Assert.That(contact.FeatureId, Is.EqualTo(17));
            Assert.That(contact.Fraction, Is.EqualTo((2 - Half - .004f) / 4).Within(.001f));
            Assert.That(contact.Normal.x, Is.LessThan(-.99f));
        }

        [Test]
        public void AFiniteTriangleNeverBlocksOutsideItsActualFootprint()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddTriangle(new float3(0, 0, 0), new float3(0, 0, 1), new float3(1, 0, 0), 19);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Sweep(map.Snapshot, new float3(.8f, 1, .8f), new float3(.05f),
                new float3(0, -2, 0), out _), Is.False, "Inside the triangle AABB but outside the triangle is empty space.");
            Assert.That(BlockMapQueries.Sweep(map.Snapshot, new float3(.2f, 1, .2f), new float3(.05f),
                new float3(0, -2, 0), out _), Is.True);
        }

        [Test]
        public void ABoxContainingTheEntireCubeReturnsASolidExitDistance()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(0, new float3(1), 20);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Overlap(map.Snapshot, new float3(.8f, 0, 0), new float3(.1f), out var hit), Is.True);
            Assert.That(hit.Penetration, Is.EqualTo(.3f).Within(.00001f), "Intersection volume is not the minimum distance needed to leave a solid.");
            Assert.That(hit.Normal.x, Is.GreaterThan(.99f));
        }

        [Test]
        public void AClosedConvexContainingTheCubeIsSolid()
        {
            var vertices = new[]
            {
                new float3(-1,-1,-1), new float3(1,-1,-1), new float3(1,1,-1), new float3(-1,1,-1),
                new float3(-1,-1,1), new float3(1,-1,1), new float3(1,1,1), new float3(-1,1,1)
            };
            var triangles = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            var builder = new BlockMapCollisionBuilder(); builder.AddConvex(vertices, triangles, 20);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Overlap(map.Snapshot, new float3(.8f, 0, 0), new float3(.1f), out var hit), Is.True);
            Assert.That(hit.Penetration, Is.EqualTo(.3f).Within(.00001f));
            Assert.That(hit.Normal.x, Is.GreaterThan(.99f));
        }

        [Test]
        public void CoplanarTriangleDiagonalDoesNotSnagATangentialSweep()
        {
            var builder = new BlockMapCollisionBuilder();
            float3 a = new(-2, 0, -2), b = new(2, 0, -2), c = new(2, 0, 2), d = new(-2, 0, 2);
            builder.AddTriangle(a, d, c, 1); builder.AddTriangle(a, c, b, 2);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Sweep(map.Snapshot, new float3(-1, Half, 0), new float3(Half),
                new float3(2, 0, 0), out _), Is.False, "An internal triangulation edge is not a vertical obstacle.");
        }

        [Test]
        public void LeavingAnInitialOverlapStillFindsASecondWall()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(0, new float3(.5f, 1, 1), 1);
            builder.AddBox(new float3(2, 0, 0), new float3(.01f, 1, 1), 2);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Sweep(map.Snapshot, new float3(.55f, 0, 0), new float3(Half),
                new float3(3, 0, 0), out var hit), Is.True);
            Assert.That(hit.FeatureId, Is.EqualTo(2));
            Assert.That(hit.Fraction, Is.EqualTo((2 - .01f - Half - .55f) / 3).Within(.00001f));
        }

        [Test]
        public void GeometryHashChangesWhenOnlyTriangleTopologyChanges()
        {
            float3 a = new(-1, 0, -1), b = new(1, 0, -1), c = new(1, 0, 1), d = new(-1, 0, 1);
            var first = new BlockMapCollisionBuilder(); first.AddTriangle(a, b, c, 1); first.AddTriangle(a, c, d, 2);
            var second = new BlockMapCollisionBuilder(); second.AddTriangle(a, b, d, 1); second.AddTriangle(b, c, d, 2);
            using var map1 = first.Build(Allocator.Persistent); using var map2 = second.Build(Allocator.Persistent);
            Assert.That(map1.GeometryHash, Is.Not.EqualTo(map2.GeometryHash));
        }

        [Test]
        public void A240MetresPerSecondBlockCannotCrossAnEightMillimetreWall()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, 1, 0), new float3(.004f, 2, 2), 17);
            using var map = builder.Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            var handle = Add(world, State(1, new float3(-2, 1, 0), new float3(240, 0, 0)));
            Steps(world, map.Snapshot, 1);
            var state = Read(world, handle);
            Assert.That(state.position.x, Is.LessThanOrEqualTo(-Half - .004f + .003f),
                "Discrete end-overlap alone would miss this wall.");
            Assert.That(state.position.x, Is.GreaterThan(-2.01f), "CCD must advance to contact, not reset the whole tick.");
            Assert.That(state.velocity.x, Is.LessThanOrEqualTo(.01f));
        }

        [Test]
        public void FiniteTriangleSlopeSupportsACubeWithoutAHeightfieldApproximation()
        {
            var builder = new BlockMapCollisionBuilder();
            float3 a = new(-3, 0, -2), b = new(3, 1.2f, -2), c = new(3, 1.2f, 2), d = new(-3, 0, 2);
            builder.AddTriangle(a, d, c, 21); builder.AddTriangle(a, c, b, 22);
            using var map = builder.Build(Allocator.Persistent);
            Assert.That(BlockMapQueries.Sweep(map.Snapshot, new float3(0, 2, .4f), new float3(Half),
                new float3(0, -3, 0), out var hit), Is.True);
            Assert.That(hit.Normal.y, Is.GreaterThan(.97f));
            Assert.That(hit.Normal.x, Is.LessThan(-.18f));
            Assert.That(hit.Fraction, Is.EqualTo((2 - (.6f + .2f * Half + Half)) / 3).Within(.003f));
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            var handle = Add(world, State(1, new float3(0, 1.3f, .4f)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { handle }, "finite triangle slope");
            var state = Read(world, handle);
            float supportedHeight = .6f + .2f * (state.position.x + Half) + Half;
            Assert.That(state.position.y, Is.EqualTo(supportedHeight).Within(.012f));
            Assert.That(state.position.x, Is.InRange(-.25f, .25f), "The shallow slope is below the static-friction threshold.");
            Assert.That(math.length(state.velocity), Is.LessThan(.05f));
        }

        [Test]
        public void BridgeTopAndUndersideRemainDistinctCollisionSurfaces()
        {
            var builder = Floor();
            builder.AddBox(new float3(0, 2, 0), new float3(3, .15f, 2), 30);
            using var map = builder.Build(Allocator.Persistent);
            using var world = new BlockSimulation(4);
            world.SetObservers(FarObservers);
            var below = Add(world, State(1, new float3(-1, .6f, 0)));
            var above = Add(world, State(2, new float3(0, 2.8f, 0)));
            var rising = Add(world, State(3, new float3(1, 1.2f, 0), new float3(0, 8, 0)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { below, above, rising }, "bridge top and underside", tick =>
            {
                Assert.That(Read(world, rising).position.y, Is.LessThanOrEqualTo(1.85f - Half + .003f),
                    "A block rising under the bridge cannot be snapped onto its top.");
            });
            Assert.That(Read(world, below).position.y, Is.EqualTo(Half).Within(.008f));
            Assert.That(Read(world, above).position.y, Is.EqualTo(2.15f + Half).Within(.008f));
            Assert.That(Read(world, rising).position.y, Is.EqualTo(Half).Within(.008f));
            Assert.That(world.Count, Is.EqualTo(3));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(26)]
        public void AFullHeightColumnRestsWithoutFallingThroughOrLosingBodies(int layers)
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            var handles = Column(world, layers);
            SettleUnderStrictGates(world, map.Snapshot, handles, layers + "-layer column");
            Assert.That(world.Count, Is.EqualTo(layers));
            for (int layer = 0; layer < layers; layer++)
            {
                var state = Read(world, handles[layer]);
                // The direct penetration oracle above is authoritative; this height check also rejects whole-stack drift.
                Assert.That(state.position.y, Is.EqualTo(Half + layer * Edge).Within(.003f + .002f * layer));
                Assert.That(math.length(state.position.xz), Is.LessThan(.01f));
                Assert.That(math.length(state.velocity), Is.LessThan(.05f));
                Assert.That(state.mode, Is.EqualTo(BlockSimMode.Resting), "Far, genuinely supported blocks must be allowed to sleep.");
                if (layer != 0)
                    Assert.That(state.position.y - Read(world, handles[layer - 1]).position.y,
                        Is.GreaterThanOrEqualTo(Edge - .00201f), "Adjacent layers cannot be silently compressed together.");
            }
        }

        [TestCase(4)]
        [TestCase(16)]
        [TestCase(26)]
        public void AStaggeredFourByFourPileSettlesWithMultipleSupportsPerBody(int layers)
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            const int width = 4, bodiesPerLayer = width * width;
            var handles = new BlockSimHandle[bodiesPerLayer * layers];
            var initial = new float3[handles.Length];
            for (int layer = 0; layer < layers; layer++)
            for (int z = 0; z < width; z++)
            for (int x = 0; x < width; x++)
            {
                int index = layer * bodiesPerLayer + z * width + x;
                // Alternate which horizontal axis is offset. Adjacent layers therefore overlap four
                // lower footprints in the interior, while every initial volume only touches its peers.
                float offsetX = (layer & 1) != 0 ? Half : 0;
                float offsetZ = (layer & 1) == 0 ? Half : 0;
                initial[index] = new float3((x - 1.5f) * Edge + offsetX,
                    Half + layer * Edge, (z - 1.5f) * Edge + offsetZ);
                handles[index] = Add(world, State((ulong)index + 1, initial[index]));
                Assert.That(Read(world, handles[index]).mode, Is.EqualTo(BlockSimMode.Active));
            }
            Assert.That(Half + (layers - 1) * Edge + Half, Is.LessThanOrEqualTo(8f),
                "The complete 26-layer fixture must fit below the eight-metre world ceiling.");
            for (int a = 0; a < initial.Length; a++)
            for (int b = a + 1; b < initial.Length; b++)
                Assert.That(math.cmin(new float3(Edge) - math.abs(initial[a] - initial[b])),
                    Is.LessThanOrEqualTo(.000001f), "The fixture cannot begin with positive-volume overlap.");
            for (int layer = 1; layer < layers; layer++)
            {
                int fourWaySupported = 0;
                for (int upper = layer * bodiesPerLayer; upper < (layer + 1) * bodiesPerLayer; upper++)
                {
                    int supports = 0;
                    for (int lower = (layer - 1) * bodiesPerLayer; lower < layer * bodiesPerLayer; lower++)
                    {
                        float2 overlap = new float2(Edge) - math.abs((initial[upper] - initial[lower]).xz);
                        if (math.all(overlap > .000001f)) supports++;
                    }
                    Assert.That(supports, Is.GreaterThanOrEqualTo(1), "Every upper body begins on real support.");
                    if (supports == 4) fourWaySupported++;
                }
                Assert.That(fourWaySupported, Is.GreaterThanOrEqualTo(9),
                    "Each layer transition must contain branching, shared support rather than isolated columns.");
            }
            var identities = new HashSet<BlockSimHandle>(handles);
            Assert.That(identities.Count, Is.EqualTo(handles.Length));
            string label = layers + "-layer staggered 4x4 pile";
            SettleUnderStrictGates(world, map.Snapshot, handles, label, tick =>
                Assert.That(world.Count, Is.EqualTo(handles.Length), label + ": changed body count at tick " + tick));
            var committed = new List<BlockSimState>(handles.Length);
            world.CopyStates(committed);
            foreach (var state in committed)
            {
                Assert.That(identities.Remove(state.handle), Is.True, "A stable identity was duplicated or replaced.");
                Assert.That(state.owner, Is.EqualTo(1));
                Assert.That(state.mode, Is.EqualTo(BlockSimMode.Resting));
            }
            Assert.That(identities, Is.Empty, "Every original stable identity must survive settlement.");
        }

        [Test]
        public void AOneCentimetreCornerFootprintCountsAsSupportForAnAxisAlignedCube()
        {
            var builder = new BlockMapCollisionBuilder();
            builder.AddBox(new float3(0, -.1f, 0), new float3(.15f, .1f, .15f), 41);
            using var map = builder.Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            var handle = Add(world, State(1, new float3(.29f, .4f, .29f)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { handle }, "corner-supported cube");
            var state = Read(world, handle);
            Assert.That(state.position.y, Is.EqualTo(Half).Within(.008f));
            Assert.That(state.position.x, Is.EqualTo(.29f).Within(.005f));
            Assert.That(state.position.z, Is.EqualTo(.29f).Within(.005f));
            Assert.That(state.mode, Is.EqualTo(BlockSimMode.Resting));
        }

        [Test]
        public void RemovingTheBottomOfASleepingColumnWakesAndDropsEveryDependentLayer()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(4);
            world.SetObservers(FarObservers);
            var handles = Column(world, 16);
            SettleUnderStrictGates(world, map.Snapshot, handles, "before bottom removal");
            Assert.That(Read(world, handles[15]).mode, Is.EqualTo(BlockSimMode.Resting));
            var before = new BlockSimState[handles.Length];
            for (int i = 0; i < handles.Length; i++) before[i] = Read(world, handles[i]);
            Assert.That(world.Remove(handles[0]), Is.True);
            var remaining = new BlockSimHandle[handles.Length - 1];
            Array.Copy(handles, 1, remaining, 0, remaining.Length);
            SettleUnderStrictGates(world, map.Snapshot, remaining, "after bottom removal");
            Assert.That(world.Count, Is.EqualTo(15));
            Assert.That(world.TryGet(handles[0], out _), Is.False);
            for (int i = 1; i < handles.Length; i++)
            {
                var state = Read(world, handles[i]);
                Assert.That(before[i].position.y - state.position.y, Is.GreaterThan(.26f), $"Layer {i} remained suspended.");
                Assert.That(state.position.y, Is.EqualTo(Half + (i - 1) * Edge).Within(.008f + .002f * i));
            }
        }

        [Test]
        public void ASharedBudgetSidePushMovesTheBaseAndDisturbsItsSleepingStack()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(4);
            world.SetObservers(FarObservers);
            var handles = Column(world, 4);
            SettleUnderStrictGates(world, map.Snapshot, handles, "before side push");
            float baseX = Read(world, handles[0]).position.x;
            Assert.That(world.ApplyPush(handles[0], new float3(1, 0, 0), 3, 100 * Dt), Is.True);
            bool dependentMoved = false; float maximumBaseAdvance = 0;
            SettleUnderStrictGates(world, map.Snapshot, handles, "after side push", tick =>
            {
                maximumBaseAdvance = math.max(maximumBaseAdvance, Read(world, handles[0]).position.x - baseX);
                for (int i = 1; i < handles.Length; i++)
                {
                    var state = Read(world, handles[i]);
                    dependentMoved |= math.length(state.velocity) > .03f || math.abs(state.position.x) > .005f;
                }
            });
            Assert.That(maximumBaseAdvance, Is.GreaterThan(.02f));
            Assert.That(dependentMoved, Is.True, "Sleeping dependents cannot remain an inert island after their support is pushed.");
            Assert.That(world.Count, Is.EqualTo(4));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void AnIncomingCubeWakesAndMovesADistantSleepingHeap()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(4);
            world.SetObservers(FarObservers);
            var target = Add(world, State(1, new float3(0, Half, 0)));
            var neighbour = Add(world, State(2, new float3(Edge, Half, 0)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { target, neighbour }, "before sleeping-heap impact");
            Assert.That(Read(world, target).mode, Is.EqualTo(BlockSimMode.Resting));
            Assert.That(Read(world, neighbour).mode, Is.EqualTo(BlockSimMode.Resting));
            var incoming = Add(world, State(3, new float3(-1.5f, Half, 0), new float3(6, 0, 0)));
            bool hitSleepingBody = false;
            SettleUnderStrictGates(world, map.Snapshot, new[] { target, neighbour, incoming }, "after sleeping-heap impact", tick =>
            {
                var state = Read(world, target);
                hitSleepingBody |= state.position.x > .01f || state.velocity.x > .1f;
            });
            Assert.That(hitSleepingBody, Is.True, "Range management must not remove sleeping bodies from contact generation.");
            Assert.That(world.Count, Is.EqualTo(3));
            Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
        }

        [Test]
        public void ABodyFarOutsideTwoMetresContinuesItsRealAirborneFall()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(new[] { new float3(0, 0, 0) });
            var handle = Add(world, State(1, new float3(20, 5, 0)));
            Steps(world, map.Snapshot, 30);
            var state = Read(world, handle);
            Assert.That(state.mode, Is.EqualTo(BlockSimMode.Active));
            Assert.That(state.position.y, Is.InRange(3.70f, 3.85f));
            Assert.That(state.velocity.y, Is.InRange(-5.0f, -4.8f));
            Steps(world, map.Snapshot, 180);
            state = Read(world, handle);
            Assert.That(state.position.y, Is.EqualTo(Half).Within(.008f));
            Assert.That(state.mode, Is.EqualTo(BlockSimMode.Resting), "Only after real landing and settling may the distant body sleep.");
        }

        [Test]
        public void AnEnteringTwoMetreRangeWakesOnceAndDoesNotKeepAQuietBodyAwake()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            var handle = Add(world, State(1, new float3(0, Half, 0)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { handle }, "before entering range");
            world.SetObservers(new[] { new float3(2, 0, 0) });
            world.Step(map.Snapshot, Dt);
            Assert.That(Read(world, handle).mode, Is.EqualTo(BlockSimMode.Active), "The inclusive 2 m entry must wake the body.");
            int wakes = world.Diagnostics.RangeWakes;
            for (int tick = 0; tick < 120; tick++) { world.Step(map.Snapshot, Dt); wakes += world.Diagnostics.RangeWakes; }
            Assert.That(wakes, Is.EqualTo(1), "A stationary observer must not re-wake the same quiet body every tick.");
            Assert.That(Read(world, handle).mode, Is.EqualTo(BlockSimMode.Resting));
            world.SetObservers(new[] { new float3(2.01f, 0, 0) }); world.Step(map.Snapshot, Dt);
            Assert.That(Read(world, handle).mode, Is.EqualTo(BlockSimMode.Resting));
            world.SetObservers(new[] { new float3(1.99f, 0, 0) }); world.Step(map.Snapshot, Dt);
            Assert.That(world.Diagnostics.RangeWakes, Is.EqualTo(1), "Leaving and entering creates a new range-entry event.");
        }

        [Test]
        public void ALongSleepingRowTransmitsAPushBeyondTheTwoMetrePlayerRange()
        {
            const int count = 40;
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(4);
            world.SetObservers(FarObservers);
            var handles = new BlockSimHandle[count];
            for (int i = 0; i < count; i++) handles[i] = Add(world, State((ulong)i + 1, new float3(i * Edge, Half, 0)));
            SettleUnderStrictGates(world, map.Snapshot, handles, "long row before push");
            world.SetObservers(new[] { new float3(0, 0, 0) });
            bool remoteWoke = false, remoteMoved = false;
            float initial = Read(world, handles[count - 1]).position.x;
            for (int tick = 0; tick < 180; tick++)
            {
                Assert.That(world.ApplyPush(handles[0], new float3(1, 0, 0), 3, 100 * Dt), Is.True);
                world.Step(map.Snapshot, Dt);
                var remote = Read(world, handles[count - 1]);
                remoteWoke |= remote.mode == BlockSimMode.Active;
                remoteMoved |= remote.position.x > initial + .02f;
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired));
            }
            Assert.That(remoteWoke && remoteMoved, Is.True, "Contact momentum and wake propagation cannot stop at the player activation boundary.");
            Assert.That(world.Count, Is.EqualTo(count));
        }

        [Test]
        public void RemovingOneOfTwoSupportsKeepsTheTopButRemovingBothLetsItFall()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            world.SetObservers(FarObservers);
            var left = Add(world, State(1, new float3(-Half, Half, 0)));
            var right = Add(world, State(2, new float3(Half, Half, 0)));
            var top = Add(world, State(3, new float3(0, Half + Edge, 0)));
            SettleUnderStrictGates(world, map.Snapshot, new[] { left, right, top }, "two supports");
            Assert.That(world.Remove(left), Is.True);
            SettleUnderStrictGates(world, map.Snapshot, new[] { right, top }, "one support remains");
            Assert.That(Read(world, top).position.y, Is.EqualTo(Half + Edge).Within(.003f));
            Assert.That(world.Remove(right), Is.True);
            SettleUnderStrictGates(world, map.Snapshot, new[] { top }, "both supports removed");
            Assert.That(Read(world, top).position.y, Is.EqualTo(Half).Within(.0011f));
        }

        [Test]
        public void RemovingASwappedSlotDoesNotChangeStableIdentitySolveOrder()
        {
            using var map = Floor().Build(Allocator.Persistent);
            using var first = new BlockSimulation(8); using var second = new BlockSimulation(8);
            first.SetObservers(FarObservers); second.SetObservers(FarObservers);
            var discard = Add(first, State(9999, new float3(20, 2, 20)));
            for (ulong id = 1; id <= 8; id++)
            {
                var state = State(id, new float3((id % 2) * Edge, Half + ((id - 1) / 2) * Edge, 0));
                Add(first, state); Add(second, state);
            }
            Assert.That(first.Remove(discard), Is.True, "Swap-back moves the last live body into a different array slot.");
            for (int tick = 0; tick < 300; tick++)
            {
                first.Step(map.Snapshot, Dt); second.Step(map.Snapshot, Dt);
                for (ulong id = 1; id <= 8; id++)
                {
                    var a = Read(first, new BlockSimHandle(1, 1, id)); var b = Read(second, new BlockSimHandle(1, 1, id));
                    Assert.That(math.distance(a.position, b.position), Is.LessThan(.000001f), $"Slot-dependent pose at tick {tick}, ID {id}.");
                    Assert.That(math.distance(a.velocity, b.velocity), Is.LessThan(.000001f), $"Slot-dependent velocity at tick {tick}, ID {id}.");
                    Assert.That(a.mode, Is.EqualTo(b.mode));
                }
            }
        }

        [Test]
        public void GrowingBuffersAndPreallocatedBuffersProduceTheSameCommittedBodies()
        {
            const int count = 257;
            using var map = Floor().Build(Allocator.Persistent);
            using var growing = new BlockSimulation(1); using var preallocated = new BlockSimulation(4096);
            growing.SetObservers(FarObservers); preallocated.SetObservers(FarObservers);
            for (int i = 0; i < count; i++)
            {
                var state = State((ulong)i + 1, new float3((i % 17 - 8) * Edge, Half, (i / 17 - 7) * Edge));
                Add(growing, state); Add(preallocated, state);
            }
            for (int tick = 0; tick < 90; tick++)
            {
                growing.Step(map.Snapshot, Dt); preallocated.Step(map.Snapshot, Dt);
                Assert.That(growing.Diagnostics.CandidateEmitted, Is.EqualTo(growing.Diagnostics.CandidateRequired));
                Assert.That(preallocated.Diagnostics.CandidateEmitted, Is.EqualTo(preallocated.Diagnostics.CandidateRequired));
                Assert.That(growing.Diagnostics.SolverFaults + preallocated.Diagnostics.SolverFaults, Is.Zero);
                for (int i = 0; i < count; i++)
                {
                    var handle = new BlockSimHandle(1, 1, (ulong)i + 1);
                    var a = Read(growing, handle); var b = Read(preallocated, handle);
                    Assert.That(math.distance(a.position, b.position), Is.LessThan(.000001f));
                    Assert.That(math.distance(a.velocity, b.velocity), Is.LessThan(.000001f));
                    Assert.That(a.mode, Is.EqualTo(b.mode));
                }
            }
            Assert.That(growing.Diagnostics.CapacityGrowths, Is.GreaterThan(0));
        }

        [Test]
        public void ReusedStorageNeverLetsAnOldGenerationOrRoundMutateANewBody()
        {
            using var world = new BlockSimulation(1);
            var old = Add(world, State(7, new float3(0, 1, 0)));
            Assert.That(world.Remove(old), Is.True);
            var replacement = State(7, new float3(3, 2, 1), generation: 2);
            Add(world, replacement);
            Assert.That(world.TryGet(old, out _), Is.False);
            Assert.That(world.ApplyImpulse(old, new float3(100, 0, 0)), Is.False);
            Assert.That(world.ApplyPush(old, new float3(1, 0, 0), 3, 100 * Dt), Is.False);
            Assert.That(world.Remove(old), Is.False);
            Assert.That(math.distance(Read(world, replacement.handle).position, replacement.position), Is.Zero);
            Assert.That(math.length(Read(world, replacement.handle).velocity), Is.Zero);
            world.Clear();
            var nextRound = State(7, new float3(-3, 2, 1), generation: 1, epoch: 2, round: 2);
            Add(world, nextRound);
            Assert.That(world.TryGet(replacement.handle, out _), Is.False);
            Assert.That(world.ApplyImpulse(replacement.handle, new float3(100, 0, 0)), Is.False);
            Assert.That(world.Count, Is.EqualTo(1));
            Assert.That(Read(world, nextRound.handle).handle, Is.EqualTo(nextRound.handle));
        }

        [Test]
        public void BodyHashContactAndQueryBuffersGrowWithoutSilentlyDroppingCandidates()
        {
            const int count = 513;
            using var map = Floor().Build(Allocator.Persistent);
            using var world = new BlockSimulation(2);
            var snapshots = new List<BlockSimState>(count);
            var queried = new List<BlockSimHandle>(1);
            for (int i = 0; i < count; i++)
                Add(world, State((ulong)i + 1, new float3((i % 27 - 13) * .302f, Half, (i / 27 - 9) * .302f)));
            Steps(world, map.Snapshot, 1);
            world.CopyStates(snapshots);
            Assert.That(world.Count, Is.EqualTo(count));
            Assert.That(world.Capacity, Is.GreaterThanOrEqualTo(count));
            Assert.That(world.Diagnostics.CapacityGrowths, Is.GreaterThan(0));
            Assert.That(world.Diagnostics.ContactCount, Is.GreaterThanOrEqualTo(count),
                "Each of the 513 initially active cubes contacts the floor, beyond a 512-entry buffer.");
            var boxes = new[]
            {
                new BlockAabb(new float3(0, Half, 0), new float3(20)),
                new BlockAabb(new float3(.123f, Half, -.176f), new float3(.71f, .17f, 1.13f)),
                new BlockAabb(new float3(-3.8f, Half, 1.4f), new float3(.2f, .16f, 1.8f))
            };
            foreach (var query in boxes)
            {
                queried.Clear(); world.Query(query, queried);
                var actual = new HashSet<BlockSimHandle>(queried);
                var expected = new HashSet<BlockSimHandle>();
                foreach (var state in snapshots)
                    if (query.Overlaps(new BlockAabb(state.position, new float3(Half)))) expected.Add(state.handle);
                Assert.That(queried.Count, Is.EqualTo(actual.Count), "No duplicate hash-bucket result may consume capacity.");
                CollectionAssert.AreEquivalent(expected, actual, "Independent brute-force AABB oracle found missing or extra query bodies.");
            }
        }
    }
}
#endif
