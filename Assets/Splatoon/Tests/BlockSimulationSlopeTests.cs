using System;
using NUnit.Framework;
using Splatoon.Blocks.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace Splatoon.Tests
{
    [Category("BlockSimulationPrototype")]
    public sealed class BlockSimulationSlopeTests
    {
        const float Dt = 1f / 60f;

        static BlockMapCollisionData Ramp(float gradient)
        {
            var builder = new BlockMapCollisionBuilder();
            float3 a = new(-8, -8 * gradient, -4), b = new(8, 8 * gradient, -4),
                c = new(8, 8 * gradient, 4), d = new(-8, -8 * gradient, 4);
            builder.AddTriangle(a, d, c, 1); builder.AddTriangle(a, c, b, 2);
            return builder.Build();
        }

        static BlockSimHandle Add(BlockSimulation world, ulong id, float3 position)
        {
            var handle = new BlockSimHandle(1, 1, id);
            Assert.That(world.Add(new BlockSimState { handle = handle, owner = 1, position = position, mode = BlockSimMode.Active }), Is.True);
            return handle;
        }

        [Test]
        public void AHalfGradientRampExceedsStaticFrictionAndCannotSleepASlidingBody()
        {
            // tan(theta)=.5 exceeds Average(.35,.6)=.475. A real finite slope must slide,
            // even though it supplies an upward contact normal and starts at zero velocity.
            using var map = Ramp(.5f); using var world = new BlockSimulation(1);
            using var contacts = new NativeList<BlockStaticHit>(8, Allocator.TempJob);
            world.SetObservers(new[] { new float3(100, 0, 100) });
            var initial = new float3(2, 1.225f, 0);
            var handle = Add(world, 1, initial);
            for (int tick = 0; tick < 180; tick++)
            {
                world.Step(map.Snapshot, Dt); Assert.That(world.TryGet(handle, out var state), Is.True);
                Assert.That(state.mode, Is.EqualTo(BlockSimMode.Active), "A normal with positive Y alone is not proof of friction stability.");
                contacts.Clear(); BlockMapQueries.CollectContacts(map.Snapshot, state.position, new float3(.15f), contacts);
                for (int c = 0; c < contacts.Length; c++)
                    Assert.That(contacts[c].Penetration, Is.LessThanOrEqualTo(.00101f), "Steep-slope static penetration at tick " + tick);
                Assert.That(state.position.y, Is.EqualTo(.5f * (state.position.x + .15f) + .15f).Within(.001f),
                    "The sliding cube must follow the finite triangle plane without sinking or hovering.");
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired));
            }
            world.TryGet(handle, out var final);
            Assert.That(final.position.x, Is.LessThan(initial.x - .4f));
            Assert.That(final.velocity.x, Is.LessThan(-.1f));
            Assert.That(math.abs(final.position.z), Is.LessThan(.001f));
        }

        [Test]
        public void AHighSpeedImpactOnAnObliquePlaneHasARepresentableFiniteContactSolution()
        {
            using var map = Ramp(.2f); using var world = new BlockSimulation(1);
            using var contacts = new NativeList<BlockStaticHit>(8, Allocator.TempJob);
            world.SetObservers(new[] { new float3(100, 0, 100) });
            var handle = new BlockSimHandle(1, 1, 1);
            Assert.That(world.Add(new BlockSimState { handle = handle, owner = 1, position = new float3(0, 1.3f, 0),
                velocity = new float3(0, -240, 0), mode = BlockSimMode.Active }), Is.True);
            for (int tick = 0; tick < 300; tick++)
            {
                world.Step(map.Snapshot, Dt);
                Assert.That(world.TryGet(handle, out var state), Is.True);
                Assert.That(math.all(math.isfinite(state.position)) && math.all(math.isfinite(state.velocity)), Is.True);
                Assert.That(state.position.y, Is.GreaterThanOrEqualTo(.2f * (state.position.x + .15f) + .15f - .00101f),
                    "A large normal impulse cannot tunnel through a finite oblique plane.");
                contacts.Clear(); BlockMapQueries.CollectContacts(map.Snapshot, state.position, new float3(.15f), contacts);
                for (int c = 0; c < contacts.Length; c++) Assert.That(contacts[c].Penetration, Is.LessThanOrEqualTo(.00101f));
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
                Assert.That(world.Count, Is.EqualTo(1));
            }
            world.TryGet(handle, out var final);
            Assert.That(final.mode, Is.EqualTo(BlockSimMode.Resting));
            Assert.That(math.abs(final.position.x), Is.LessThan(.3f), "A sub-threshold slope cannot sustain sliding after the impact.");
        }

        [Test]
        public void AWeakForceBelowTheTopContactStaticFrictionDoesNotCreepAFullHeightStack()
        {
            var builder = new BlockMapCollisionBuilder(); builder.AddBox(new float3(0, -.1f, 0), new float3(20, .1f, 20));
            using var map = builder.Build(); using var world = new BlockSimulation(1);
            using var contacts = new NativeList<BlockStaticHit>(8, Allocator.TempJob);
            world.SetObservers(new[] { new float3(100, 0, 100) });
            const int layers = 16;
            var handles = new BlockSimHandle[layers]; var states = new BlockSimState[layers];
            for (int i = 0; i < layers; i++) handles[i] = Add(world, (ulong)i + 1, new float3(0, .15f + .3f * i, 0));
            for (int tick = 0; tick < 300; tick++) world.Step(map.Snapshot, Dt);
            for (int i = 0; i < layers; i++)
            { world.TryGet(handles[i], out states[i]); Assert.That(states[i].mode, Is.EqualTo(BlockSimMode.Resting)); }
            // 0.25 N is below the narrowest static interface, .35 * .5 kg * 9.81 = 1.717 N.
            // Repeated external input legitimately keeps the contact group awake, so sleep is not a gate here.
            for (int tick = 0; tick < 300; tick++)
            {
                Assert.That(world.ApplyPush(handles[layers - 1], new float3(1, 0, 0), .2f, .25f * Dt), Is.True);
                world.Step(map.Snapshot, Dt);
                for (int i = 0; i < layers; i++)
                {
                    Assert.That(world.TryGet(handles[i], out states[i]), Is.True);
                    Assert.That(math.length(states[i].position.xz), Is.LessThan(.001f), "Sub-threshold static friction drift at layer " + i + ", tick " + tick);
                    Assert.That(math.length(states[i].velocity.xz), Is.LessThan(.003f));
                    contacts.Clear(); BlockMapQueries.CollectContacts(map.Snapshot, states[i].position, new float3(.15f), contacts);
                    for (int c = 0; c < contacts.Length; c++) Assert.That(contacts[c].Penetration, Is.LessThanOrEqualTo(.00101f));
                }
                for (int a = 0; a < layers; a++)
                for (int b = a + 1; b < layers; b++)
                {
                    float3 overlap = .3f - math.abs(states[a].position - states[b].position);
                    if (math.all(overlap > 0)) Assert.That(math.cmin(overlap), Is.LessThanOrEqualTo(.00201f));
                }
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired));
                Assert.That(world.Count, Is.EqualTo(layers));
            }
        }

        [TestCase(4)]
        [TestCase(16)]
        public void AHorizontalStackOnAnObliqueMapContactSettlesWithoutBreakingItsNormalOrFriction(int layers)
        {
            using var map = Ramp(.2f); using var world = new BlockSimulation(1);
            using var contacts = new NativeList<BlockStaticHit>(8, Allocator.TempJob);
            world.SetObservers(new[] { new float3(100, 0, 100) });
            var handles = new BlockSimHandle[layers]; var states = new BlockSimState[layers];
            for (int i = 0; i < layers; i++) handles[i] = Add(world, (ulong)i + 1, new float3(0, .18f + .3f * i, 0));
            bool previousOverTwoMillimetres = false;
            for (int tick = 1; tick <= 300; tick++)
            {
                world.Step(map.Snapshot, Dt);
                float staticPenetration = 0, dynamicPenetration = 0;
                int resting = 0;
                for (int i = 0; i < layers; i++)
                {
                    Assert.That(world.TryGet(handles[i], out states[i]), Is.True);
                    Assert.That(math.all(math.isfinite(states[i].position)) && math.all(math.isfinite(states[i].velocity)), Is.True);
                    if (states[i].mode == BlockSimMode.Resting) resting++;
                    contacts.Clear(); BlockMapQueries.CollectContacts(map.Snapshot, states[i].position, new float3(.15f), contacts);
                    for (int c = 0; c < contacts.Length; c++) staticPenetration = math.max(staticPenetration, contacts[c].Penetration);
                }
                for (int a = 0; a < layers; a++)
                for (int b = a + 1; b < layers; b++)
                {
                    float3 overlap = .3f - math.abs(states[a].position - states[b].position);
                    if (math.all(overlap > 0)) dynamicPenetration = math.max(dynamicPenetration, math.cmin(overlap));
                }
                float penetration = math.max(staticPenetration, dynamicPenetration);
                Assert.That(penetration, Is.LessThanOrEqualTo(.00501f), "Oblique base contact cannot be broken by an axis-normal projection.");
                bool overTwoMillimetres = penetration > .00201f;
                Assert.That(previousOverTwoMillimetres && overTwoMillimetres, Is.False, "Penetration remained above 2 mm for consecutive ticks.");
                previousOverTwoMillimetres = overTwoMillimetres;
                if (tick == 180) Assert.That(resting, Is.GreaterThanOrEqualTo((int)Math.Ceiling(layers * .95)));
                if (tick == 300)
                {
                    Assert.That(resting, Is.EqualTo(layers));
                    Assert.That(staticPenetration, Is.LessThanOrEqualTo(.00101f));
                    Assert.That(dynamicPenetration, Is.LessThanOrEqualTo(.00201f));
                }
                Assert.That(world.Count, Is.EqualTo(layers));
                Assert.That(world.Diagnostics.SolverFaults, Is.Zero);
                Assert.That(world.Diagnostics.CandidateEmitted, Is.EqualTo(world.Diagnostics.CandidateRequired));
            }
            Assert.That(math.length(states[0].position.xz), Is.LessThan(.01f), "A supported stack below the static-friction threshold cannot creep sideways.");
        }
    }
}
