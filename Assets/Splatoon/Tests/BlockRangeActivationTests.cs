#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Blocks;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BlockRangeActivationTests
    {
        GameObject _root;
        BlockDynamicWorld _world;
        SimulationMode _simulation;
        bool[,] _ignored;
        static readonly Vector3[] Far = { new(10, 0, 0) };

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _simulation = Physics.simulationMode; Physics.simulationMode = SimulationMode.Script;
            _ignored = new bool[32, 32];
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) _ignored[a, b] = Physics.GetIgnoreLayerCollision(a, b);
            _root = new GameObject("Range activation physics fixture");
            var terrain = _root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
            _world = terrain.Dynamic; _world.SetAuthority(true);
        }

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            Physics.simulationMode = _simulation;
            for (int a = 0; a < 32; a++) for (int b = a; b < 32; b++) Physics.IgnoreLayerCollision(a, b, _ignored[a, b]);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        BoxCollider Support(Vector3 center, Vector3 size)
        {
            var floor = new GameObject("Static support"); floor.transform.SetParent(_root.transform);
            floor.transform.position = center; var collider = floor.AddComponent<BoxCollider>(); collider.size = size;
            Physics.SyncTransforms(); return collider;
        }

        void Floor() => Support(new Vector3(0, -.1f, 0), new Vector3(12, .2f, 12));
        BlockDynamicBody Spawn(Vector3 position, byte owner = 1)
        {
            Assert.That(_world.TrySpawn(owner, position, Quaternion.identity, out var body), Is.True);
            return body;
        }
        static void Steps(int count)
        {
            for (int i = 0; i < count; i++) Physics.Simulate(.02f);
            Physics.SyncTransforms();
        }
        void Qualify(IReadOnlyList<Vector3> observers = null)
        {
            for (int i = 0; i < 3; i++) _world.PrepareForPhysics(observers ?? Far, .02f);
        }

        static void CenterSettledBoundaryFixture(BlockDynamicBody body)
        {
            Assert.That(body.Body.IsSleeping(), Is.True, "Boundary setup must begin with naturally settled sleep");
            // Natural floor contact can drift by a fraction of a micrometre.
            // Pin only this test's XZ origin so exactly 2 m is representable;
            // the runtime range comparisons remain strict and unchanged.
            body.Body.position = new Vector3(0, body.Body.position.y, 0);
            Physics.SyncTransforms(); body.Body.Sleep();
        }

        sealed class SecondaryFixture
        {
            public BlockDynamicBody A, B, C, D;
            // D is a disconnected native-sleep witness; compare interacting
            // A/B/C against an unmanaged dynamic control with identical sleep.
            public BlockDynamicBody[] Ordered => new[] { A, B, C };
        }
        struct BodySample
        {
            public Vector3 Position, Velocity, AngularVelocity;
            public Quaternion Rotation;
            public Vector3 ColliderSize;
            public float Mass;
            public bool Kinematic, Gravity, Sleeping;
            public CollisionDetectionMode CollisionDetection;
        }
        sealed class SecondaryRun
        {
            public BodySample[] Initial, AfterOne, AfterEight;
            public int FirstAB, FirstBC;
            public bool TargetDynamicAtFirstContact;
            public bool RemoteSleepingBeforeFirstSolve, RemoteSleepingAfterEight;
            public long FallbackWakesBeforeFirstSolve;
            public Vector3 FirstBCReceiverVelocity;
        }

        // Observe native contact generation, including CCD, without changing a
        // contact, a Rigidbody, or the world's activation state from the callback.
        // These callbacks may execute on workers: only immutable IDs, copied
        // native values and fields guarded by _gate are accessed here.
        sealed class SecondaryContactEvidence : IDisposable
        {
            readonly int _a, _b, _c;
            readonly object _gate = new();
            int _step;
            bool _targetDynamic;
            PhysicsScene _scene = Physics.defaultPhysicsScene;
            public int FirstAB, FirstBC;
            public bool TargetDynamicAtFirstContact;
            public Vector3 FirstBCReceiverVelocity;
            public SecondaryContactEvidence(SecondaryFixture fixture)
            {
                _a = fixture.A.Collider.GetInstanceID(); _b = fixture.B.Collider.GetInstanceID(); _c = fixture.C.Collider.GetInstanceID();
                Physics.ContactModifyEvent += Observe;
                Physics.ContactModifyEventCCD += Observe;
            }
            public void BeginStep(int step, bool targetDynamic)
            {
                lock (_gate) { _step = step; _targetDynamic = targetDynamic; }
            }
            public void LimitToScene(PhysicsScene scene) { lock (_gate) _scene = scene; }
            void Observe(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
            {
                lock (_gate) if (!scene.Equals(_scene)) return;
                for (int i = 0; i < pairs.Length; i++)
                {
                    var pair = pairs[i];
                    if (pair.contactCount == 0) continue;
                    int a = pair.colliderInstanceID, b = pair.otherColliderInstanceID;
                    bool ab = a == _a && b == _b || a == _b && b == _a;
                    bool bc = a == _b && b == _c || a == _c && b == _b;
                    if (!ab && !bc) continue;
                    lock (_gate)
                    {
                        if (ab && FirstAB == 0) FirstAB = _step;
                        if (bc && FirstBC == 0)
                        {
                            FirstBC = _step; TargetDynamicAtFirstContact = _targetDynamic;
                            FirstBCReceiverVelocity = a == _b ? pair.bodyVelocity : pair.otherBodyVelocity;
                        }
                    }
                }
            }
            public void Dispose()
            {
                Physics.ContactModifyEvent -= Observe;
                Physics.ContactModifyEventCCD -= Observe;
            }
        }

        SecondaryFixture SecondaryImpactSetup(bool parkTargets, bool sleepingReceiver = false)
        {
            // Each control/candidate gets the same native actor creation order.
            // Reset deliberately retains a LIFO pool, which can exchange the
            // actors playing A/B/C/D and confound sensitive CCD comparisons.
            Object.DestroyImmediate(_root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("Fresh secondary-impact physics fixture");
            var terrain = _root.AddComponent<BlockTerrain>(); terrain.Initialize(null); terrain.EnableSurfaceMode();
            _world = terrain.Dynamic; _world.SetAuthority(true);
            Floor();
            var c = Spawn(new Vector3(.15f, 1, .46f));
            var d = Spawn(new Vector3(3, 1, 0));
            Assert.That(_world.TrySpawn(1, new Vector3(0, 1, 0), Quaternion.Euler(0, 45, 0), out var b), Is.True);
            c.Collider.hasModifiableContacts = true;
            Steps(350);
            Assert.That(c.Body.IsSleeping(), Is.True, "C must first settle through real PhysX");
            Assert.That(d.Body.IsSleeping(), Is.True, "Remote D must first settle through real PhysX");
            Assert.That(b.Body.IsSleeping(), Is.True, "B must first settle through real PhysX");
            // Exact matched actual native initial conditions in both paths,
            // removing only natural floor-contact micrometre drift in the test.
            c.Body.position = new Vector3(.15f, BlockWorld.CellSize * .5f, .46f);
            d.Body.position = new Vector3(3, BlockWorld.CellSize * .5f, 0);
            b.Body.position = new Vector3(0, BlockWorld.CellSize * .5f, 0);
            c.Body.rotation = d.Body.rotation = Quaternion.identity;
            b.Body.rotation = Quaternion.Euler(0, 45, 0);
            Physics.SyncTransforms(); c.Body.Sleep(); d.Body.Sleep();
            if (sleepingReceiver) b.Body.Sleep(); else b.Body.WakeUp();
            if (parkTargets)
            {
                Qualify(); Assert.That(c.Parked, Is.True); Assert.That(d.Parked, Is.True);
            }
            // Keep A/B separated but inside their native contact envelope.
            // The active collider now uses its authored 2 mm offset; at the
            // previous x=-.266 the 6.978 mm gap exceeded the combined 4 mm
            // envelope and changed the CCD phase this fixture exercises.
            var a = Spawn(new Vector3(-.260f, BlockWorld.CellSize * .5f, -.256f), 2);
            a.Collider.hasModifiableContacts = b.Collider.hasModifiableContacts = true;
            a.Body.linearVelocity = Vector3.right * 90;
            Physics.SyncTransforms();
            Assert.That(a.Body.IsSleeping(), Is.False); Assert.That(b.Body.IsSleeping(), Is.EqualTo(sleepingReceiver));
            Assert.That(Physics.ComputePenetration(a.Collider, a.Body.position, a.Body.rotation,
                b.Collider, b.Body.position, b.Body.rotation, out _, out _), Is.False, "A and B begin separated, without an overlap-resolution impulse");
            Vector3 aCorner = a.Body.position + new Vector3(BlockWorld.CellSize * .5f, 0, BlockWorld.CellSize * .5f);
            float abGap = Vector3.Distance(aCorner, b.Collider.ClosestPoint(aCorner));
            Assert.That(abGap, Is.InRange(.0027f, .0028f), "A/B geometric separation is about 2.735 mm");
            Assert.That(abGap, Is.LessThan(a.Collider.contactOffset + b.Collider.contactOffset), "A/B start within the native contact envelope");
            Assert.That(Physics.ComputePenetration(b.Collider, b.Body.position, b.Body.rotation,
                c.Collider, c.Body.position, c.Body.rotation, out _, out _), Is.False);
            Assert.That(b.Collider.bounds.max.z, Is.LessThan(c.Collider.bounds.min.z), "B and C are not an existing resting contact component");
            // A travels 1.8 m in this step. D lies beyond even a 2.25 m
            // all-direction reach from either A/B starting centre and is
            // disconnected from C. Activity in A/B/C must leave D asleep.
            Assert.That(d.Collider.bounds.min.x - Mathf.Max(a.Body.position.x, b.Body.position.x), Is.GreaterThan(2.25f), "Remote D lies outside the direct A/B motion envelope");
            Assert.That(d.Collider.bounds.min.x - c.Collider.bounds.max.x, Is.GreaterThan(2f), "Remote D is not part of C's resting contact component");
            Assert.That(_world.Count, Is.EqualTo(4));
            return new SecondaryFixture { A = a, B = b, C = c, D = d };
        }

        static BodySample[] Samples(SecondaryFixture fixture)
        {
            var bodies = fixture.Ordered; var result = new BodySample[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                var body = bodies[i].Body;
                result[i] = new BodySample { Position = body.position, Rotation = body.rotation,
                    Velocity = body.isKinematic ? Vector3.zero : body.linearVelocity,
                    AngularVelocity = body.isKinematic ? Vector3.zero : body.angularVelocity,
                    ColliderSize = bodies[i].Collider.size, Mass = body.mass, Kinematic = body.isKinematic,
                    Gravity = body.useGravity, Sleeping = body.IsSleeping(), CollisionDetection = body.collisionDetectionMode };
            }
            return result;
        }

        SecondaryRun RunSecondaryImpact(bool parking, bool sleepingReceiver)
        {
            var fixture = SecondaryImpactSetup(parking, sleepingReceiver);
            var result = new SecondaryRun { Initial = Samples(fixture) };
            using var contacts = new SecondaryContactEvidence(fixture);
            if (parking)
            {
                Assert.That(fixture.D.Parked, Is.True);
                _world.PrepareForPhysics(Far, .02f);
                result.RemoteSleepingBeforeFirstSolve = fixture.D.Parked && fixture.D.Body.IsSleeping() && !fixture.D.Body.isKinematic;
                result.FallbackWakesBeforeFirstSolve = _world.InspectActivation().secondaryFallbackWakes;
            }
            else result.RemoteSleepingBeforeFirstSolve = fixture.D.Body.IsSleeping() && !fixture.D.Body.isKinematic;
            var prepared = Samples(fixture);
            // Admission/support queries above deliberately use the production
            // default PhysicsScene. Compare the dynamic actors' subsequent
            // motion in a fresh local scene, avoiding default-scene CCD/contact
            // history that also makes two all-dynamic controls differ. The
            // separate integration test retains the default-scene same-step
            // contact and actual native wake assertions.
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                SceneManager.MoveGameObjectToScene(_root, scene);
                var physics = scene.GetPhysicsScene();
                Assert.That(physics.IsValid(), Is.True); Assert.That(physics.Equals(Physics.defaultPhysicsScene), Is.False);
                contacts.LimitToScene(physics);
                Physics.SyncTransforms();
                // Moving actors to another PhysicsScene can wake them. Restore
                // the exact naturally settled initial sleep of both fixtures;
                // no runtime activation or callback forces a mass to sleep.
                fixture.C.Body.Sleep(); fixture.D.Body.Sleep();
                if (sleepingReceiver) fixture.B.Body.Sleep();
                AssertMatchingBodies(prepared, Samples(fixture), .000001f, .000001f, "Scene transfer preserves prepared native state", true);
                for (int step = 1; step <= 8; step++)
                {
                    bool ready = !fixture.C.Body.isKinematic && fixture.C.Body.useGravity && fixture.C.Body.mass == BlockDynamicWorld.Mass;
                    contacts.BeginStep(step, ready);
                    physics.Simulate(.02f); Physics.SyncTransforms();
                    if (step == 1) result.AfterOne = Samples(fixture);
                }
                result.AfterEight = Samples(fixture);
                result.RemoteSleepingAfterEight = fixture.D.Body.IsSleeping() && (!parking || fixture.D.Parked);
            }
            finally
            {
                contacts.Dispose();
                Object.DestroyImmediate(_root); _root = null;
                EditorSceneManager.ClosePreviewScene(scene);
            }
            result.FirstAB = contacts.FirstAB; result.FirstBC = contacts.FirstBC;
            result.TargetDynamicAtFirstContact = contacts.TargetDynamicAtFirstContact;
            result.FirstBCReceiverVelocity = contacts.FirstBCReceiverVelocity;
            TestContext.WriteLine($"Secondary impact managed={parking}, sleeping B={sleepingReceiver}; first A-B={result.FirstAB}, first B-C={result.FirstBC}, C dynamic={result.TargetDynamicAtFirstContact}, remote D sleeping before/after={result.RemoteSleepingBeforeFirstSolve}/{result.RemoteSleepingAfterEight}, fallback wakes before solve={result.FallbackWakesBeforeFirstSolve}; B velocity at first B-C={result.FirstBCReceiverVelocity.ToString("R")}; C after step1 p={result.AfterOne[2].Position.ToString("R")} v={result.AfterOne[2].Velocity.ToString("R")}");
            return result;
        }

        static void AssertMatchingBodies(BodySample[] expected, BodySample[] actual, float positionTolerance, float velocityTolerance, string label, bool configurations = false)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                string role = i == 0 ? "A" : i == 1 ? "B" : "C";
                if (configurations)
                {
                    Assert.That(actual[i].ColliderSize, Is.EqualTo(expected[i].ColliderSize), $"{label}: {role} collider size");
                    Assert.That(actual[i].Mass, Is.EqualTo(expected[i].Mass), $"{label}: {role} mass");
                    Assert.That(actual[i].Kinematic, Is.EqualTo(expected[i].Kinematic), $"{label}: {role} kinematic");
                    Assert.That(actual[i].Gravity, Is.EqualTo(expected[i].Gravity), $"{label}: {role} gravity");
                    Assert.That(actual[i].Sleeping, Is.EqualTo(expected[i].Sleeping), $"{label}: {role} native sleep");
                    Assert.That(actual[i].CollisionDetection, Is.EqualTo(expected[i].CollisionDetection), $"{label}: {role} CCD");
                }
                Assert.That(Vector3.Distance(actual[i].Position, expected[i].Position), Is.LessThanOrEqualTo(positionTolerance), $"{label}: {role} position");
                Assert.That(Vector3.Distance(actual[i].Velocity, expected[i].Velocity), Is.LessThanOrEqualTo(velocityTolerance), $"{label}: {role} linear velocity");
                Assert.That(Vector3.Distance(actual[i].AngularVelocity, expected[i].AngularVelocity), Is.LessThanOrEqualTo(velocityTolerance), $"{label}: {role} angular velocity");
                foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    Assert.That(Vector3.Distance(actual[i].Rotation * axis, expected[i].Rotation * axis), Is.LessThanOrEqualTo(positionTolerance * 4), $"{label}: {role} orientation basis");
            }
        }

        [TestCase(false), TestCase(true)]
        public void SecondaryImpactPreservesSameSolveMomentumThenProductionPrepareMovesCAndRetainsRemoteSleep(bool sleepingReceiver)
        {
            var fixture = SecondaryImpactSetup(true, sleepingReceiver);
            var initial = Samples(fixture); ulong id = fixture.C.Id, remoteId = fixture.D.Id; uint revision = _world.LifecycleRevision;
            var remotePosition = fixture.D.Body.position; var remoteRotation = fixture.D.Body.rotation;
            Assert.That(fixture.D.Parked, Is.True);
            using var contacts = new SecondaryContactEvidence(fixture);
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(fixture.C.Parked && fixture.C.Body.IsSleeping(), Is.True, "Preparation retains C's native sleep and management tag");
            Assert.That(fixture.C.Body.isKinematic, Is.False); Assert.That(fixture.C.Body.useGravity, Is.True);
            Assert.That(fixture.C.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass)); Assert.That(fixture.C.Id, Is.EqualTo(id));
            Assert.That(fixture.D.Parked && fixture.D.Body.IsSleeping(), Is.True, "Disconnected D stays naturally asleep before simulation");
            Assert.That(fixture.D.Body.isKinematic, Is.False); Assert.That(fixture.D.Body.useGravity, Is.True);
            Assert.That(fixture.D.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass)); Assert.That(fixture.D.Id, Is.EqualTo(remoteId));
            Assert.That(fixture.D.Body.position, Is.EqualTo(remotePosition)); Assert.That(fixture.D.Body.rotation, Is.EqualTo(remoteRotation));
            Assert.That(fixture.D.Body.linearVelocity, Is.EqualTo(Vector3.zero)); Assert.That(fixture.D.Body.angularVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(_world.LifecycleRevision, Is.EqualTo(revision)); Assert.That(_world.Count, Is.EqualTo(4));
            AssertMatchingBodies(initial, Samples(fixture), .000001f, .000001f, "Preparation preserves actual motion state");
            var metrics = _world.InspectActivation();
            Assert.That(metrics.motionWakes, Is.Zero); Assert.That(metrics.supportGraphBuilds, Is.Zero, "Ordinary preparation builds no contact graph");
            Assert.That(metrics.secondaryFallbackSteps, Is.Zero); Assert.That(metrics.secondaryFallbackWakes, Is.Zero);
            Assert.That(metrics.secondaryGuardCalls, Is.Zero);
            contacts.BeginStep(1, !fixture.C.Body.isKinematic);
            Physics.Simulate(.02f); Physics.SyncTransforms();
            TestContext.WriteLine($"First-solve native contacts A-B={contacts.FirstAB}, B-C={contacts.FirstBC}; B current velocity={fixture.B.Body.linearVelocity.ToString("R")}; C raw sleep={fixture.C.Body.IsSleeping()}, at rest={fixture.C.IsAtRest}, velocity={fixture.C.Body.linearVelocity.ToString("R")}");
            Assert.That(contacts.FirstAB, Is.EqualTo(1), "The actual A-B impact must occur in this solve");
            Assert.That(contacts.FirstBC, Is.EqualTo(1), "Require a real same-step secondary B-C contact, not only a broadphase prediction");
            Assert.That(contacts.TargetDynamicAtFirstContact, Is.True, "The first native B-C contact was admitted with C already dynamic");
            bool rawSleepingAfterImpact = fixture.C.Body.IsSleeping();
            Assert.That(fixture.C.IsAtRest, Is.False, "A delivered native impulse cannot be classified as at rest even if the raw sleep flag lags");
            Assert.That(fixture.C.Body.linearVelocity.z, Is.GreaterThan(.01f), "C receives a real positive-Z secondary impulse");
            var captured = new List<BlockDynamicState>(); _world.CaptureStates(captured);
            var cState = captured.Find(state => state.Id == id);
            Assert.That(cState.Sleeping, Is.False, "A stale management tag must not hide the just-simulated native wake");
            Assert.That(cState.Velocity, Is.EqualTo(fixture.C.Body.linearVelocity));
            Assert.That(cState.AngularVelocity, Is.EqualTo(fixture.C.Body.angularVelocity));
            Assert.That(_world.InspectActivation().active, Is.GreaterThanOrEqualTo(3));
            var pendingVelocity = fixture.C.Body.linearVelocity; var pendingAngular = fixture.C.Body.angularVelocity;
            var beforeSecondSolve = fixture.C.Body.position;
            long pendingWakes = _world.InspectActivation().pendingMotionWakes;
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(_world.InspectActivation().pendingMotionWakes, Is.EqualTo(pendingWakes + (rawSleepingAfterImpact ? 1 : 0)), "Only C's proven pending impulse needs repair");
            Assert.That(fixture.C.Body.linearVelocity, Is.EqualTo(pendingVelocity)); Assert.That(fixture.C.Body.angularVelocity, Is.EqualTo(pendingAngular));
            Assert.That(fixture.C.Body.IsSleeping() || fixture.C.Parked, Is.False);
            Assert.That(fixture.D.Parked && fixture.D.IsAtRest, Is.True);
            contacts.BeginStep(2, !fixture.C.Body.isKinematic); Steps(1);
            Assert.That(fixture.C.Body.position.z, Is.GreaterThan(beforeSecondSolve.z + .01f), "The next production solve must integrate the preserved secondary impulse into actual motion");
            for (int step = 3; step <= 8; step++)
            {
                _world.PrepareForPhysics(Far, .02f); contacts.BeginStep(step, !fixture.C.Body.isKinematic); Steps(1);
                Assert.That(fixture.D.Parked && fixture.D.IsAtRest, Is.True, "Unrelated impact must preserve remote D's native sleep");
            }
            Assert.That(fixture.C.Parked, Is.False); Assert.That(fixture.D.Body.position, Is.EqualTo(remotePosition));
            Assert.That(_world.InspectActivation().nativeWakeClears, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(false), TestCase(true)]
        public void SecondaryImpactMatchesUnmanagedNativeSleepInFreshPhysicsSceneAtOneAndEightSteps(bool sleepingReceiver)
        {
            var baseline = RunSecondaryImpact(false, sleepingReceiver);
            var repeatedBaseline = RunSecondaryImpact(false, sleepingReceiver);
            var candidate = RunSecondaryImpact(true, sleepingReceiver);
            var trailingBaseline = RunSecondaryImpact(false, sleepingReceiver);
            foreach (var control in new[] { repeatedBaseline, trailingBaseline })
            {
                AssertMatchingBodies(baseline.Initial, control.Initial, .000001f, .000001f, "Repeated all-dynamic initial conditions");
                AssertMatchingBodies(baseline.AfterOne, control.AfterOne, .001f, .02f, "Repeated all-dynamic first solve");
                AssertMatchingBodies(baseline.AfterEight, control.AfterEight, .003f, .03f, "Repeated all-dynamic eight solves");
                Assert.That(control.FirstAB, Is.EqualTo(1)); Assert.That(control.FirstBC, Is.EqualTo(1));
                Assert.That(control.TargetDynamicAtFirstContact, Is.True);
            }
            AssertMatchingBodies(baseline.Initial, candidate.Initial, .000001f, .000001f, "Matched actual native initial conditions", true);
            Assert.That(baseline.FirstAB, Is.EqualTo(1)); Assert.That(baseline.FirstBC, Is.EqualTo(1), "The all-dynamic control must prove this is a real same-step secondary impact");
            Assert.That(candidate.FirstAB, Is.EqualTo(1)); Assert.That(candidate.FirstBC, Is.EqualTo(1));
            Assert.That(baseline.TargetDynamicAtFirstContact && candidate.TargetDynamicAtFirstContact, Is.True);
            Assert.That(baseline.AfterOne[2].Velocity.z, Is.GreaterThan(.01f), "The control must transfer a real positive-Z impulse into C");
            AssertMatchingBodies(baseline.AfterOne, candidate.AfterOne, .001f, .02f, "First solve versus all-dynamic control");
            AssertMatchingBodies(baseline.AfterEight, candidate.AfterEight, .003f, .03f, "Eight solves versus all-dynamic control");
            foreach (var run in new[] { baseline, repeatedBaseline, candidate, trailingBaseline })
            {
                Assert.That(run.RemoteSleepingBeforeFirstSolve && run.RemoteSleepingAfterEight, Is.True, "Remote D remains a sleeping dynamic mass throughout the comparison");
                Assert.That(run.FallbackWakesBeforeFirstSolve, Is.Zero);
            }
        }

        [Test] public void UnrelatedAwakeBodiesRetainRemoteNativeSleepWithoutPerStepContactGraphs()
        {
            Floor(); var remote = Spawn(new Vector3(-3, 1, 0)); Steps(350); Qualify();
            Assert.That(remote.Parked, Is.True);
            var a = Spawn(new Vector3(0, 1, 0)); var b = Spawn(new Vector3(3, 1, 0));
            a.Body.linearVelocity = b.Body.linearVelocity = Vector3.right;
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(remote.Parked, Is.True, "Separated awake motion must not globally restore a distant pile");
            var observed = _world.InspectActivation();
            Assert.That(remote.Body.IsSleeping(), Is.True); Assert.That(remote.Body.isKinematic, Is.False);
            Assert.That(observed.secondaryGuardCalls, Is.Zero); Assert.That(observed.secondaryFallbackSteps, Is.Zero);
            Assert.That(observed.supportGraphBuilds, Is.Zero); Assert.That(observed.restingNodesVisited, Is.Zero);
            Steps(5); _world.PrepareForPhysics(Far, .02f);
            Assert.That(remote.Parked && remote.Body.IsSleeping(), Is.True);
            Assert.That(_world.DestroyIds(new[] { remote.Id }, 2), Is.EqualTo(1));
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(_world.InspectActivation().secondaryGuardCalls, Is.Zero);
        }

        [Test] public void AllPlayerUnionUsesHorizontalTwoMetreBoundaryAndRetainsIdentity()
        {
            Floor(); var body = Spawn(new Vector3(0, 1, 0)); Steps(350);
            Assert.That(body.Body.IsSleeping(), Is.True, "Eligibility begins with actual naturally settled PhysX sleep");
            CenterSettledBoundaryFixture(body);
            ulong id = body.Id; uint revision = _world.LifecycleRevision;
            for (int sample = 0; sample < 2; sample++) _world.PrepareForPhysics(Far, .02f);
            Assert.That(body.Parked, Is.False); Assert.That(_world.InspectActivation().supportChecks, Is.Zero, "Support proof waits for the third unchanged sleeping sample");
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(body.Parked, Is.True); Assert.That(body.Body.isKinematic, Is.False);
            Assert.That(body.Body.IsSleeping(), Is.True); Assert.That(body.Body.useGravity, Is.True);
            Assert.That(body.Body.collisionDetectionMode, Is.EqualTo(CollisionDetectionMode.ContinuousDynamic));
            long proofs = _world.InspectActivation().supportChecks;
            Qualify(); Assert.That(_world.InspectActivation().supportChecks, Is.EqualTo(proofs), "A managed sleeper needs no repeated support proof");
            Assert.That(body.Collider.enabled, Is.True); Assert.That(body.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
            var states = new List<BlockDynamicState>(); _world.CaptureStates(states);
            Assert.That(states[0].Sleeping, Is.True); Assert.That(states[0].Id, Is.EqualTo(id));
            _world.PrepareForPhysics(new[] { new Vector3(3, 0, 0) }, .02f);
            Assert.That(body.Parked, Is.True, "A managed sleeper remains managed beyond 2 m");
            _world.PrepareForPhysics(new[] { Far[0], new Vector3(2, 90, 0) }, .02f);
            Assert.That(body.Parked, Is.False, $"Any player at 2 m activates its entire vertical column; actual body={body.Body.position.ToString("R")}, horizontal squared distance={new Vector2(body.Body.position.x - 2, body.Body.position.z).sqrMagnitude:R}");
            Assert.That(body.Body.IsSleeping(), Is.False, "The inclusive activation boundary must actually wake the managed native sleeper");
            Assert.That(body.Body.isKinematic, Is.False); Assert.That(body.Body.useGravity, Is.True);
            Assert.That(body.Body.constraints, Is.EqualTo(RigidbodyConstraints.None));
            Steps(350); CenterSettledBoundaryFixture(body); Qualify(new[] { new Vector3(2, 0, 0) });
            Assert.That(body.Parked, Is.False, "Exactly 2 m never admits a sleeper to range management");
            Assert.That(body.Body.IsSleeping(), Is.True, "An untagged native sleeper inside range may still naturally sleep");
            var justOutside = new[] { new Vector3(2.01f, 0, 0) };
            for (int sample = 0; sample < 2; sample++) _world.PrepareForPhysics(justOutside, .02f);
            Assert.That(body.Parked, Is.False, "Crossing 2 m still requires all three unchanged natural-sleep samples");
            _world.PrepareForPhysics(justOutside, .02f); Assert.That(body.Parked, Is.True, "A settled sleeper at 2.01 m qualifies without the former 2-4 m buffer");
            _world.PrepareForPhysics(new[] { new Vector3(2, -90, 0), Far[0] }, .02f);
            Assert.That(body.Parked || body.Body.IsSleeping(), Is.False, "The union also activates when the near player appears first; vertical separation is ignored");
            Steps(350); CenterSettledBoundaryFixture(body); Qualify(justOutside);
            Assert.That(body.Parked, Is.True); Assert.That(body.Body.IsSleeping(), Is.True);
            Assert.That(body.Id, Is.EqualTo(id)); Assert.That(_world.Count, Is.EqualTo(1));
            Assert.That(_world.LifecycleRevision, Is.EqualTo(revision), "Activation does not create a lifecycle or lose material");
            var diagnostics = _world.InspectActivation();
            Assert.That(diagnostics.active + diagnostics.sleeping + diagnostics.parked, Is.EqualTo(diagnostics.total));
            Assert.That(diagnostics.parked, Is.EqualTo(1));
        }

        [Test] public void MovingAndUnsupportedSleepingBodiesNeverParkAndNoPlayersRestoreDynamics()
        {
            var airborne = Spawn(new Vector3(0, 2, 0));
            var unsupportedUpper = Spawn(airborne.Body.position + Vector3.up * BlockWorld.CellSize);
            airborne.Body.Sleep(); unsupportedUpper.Body.Sleep(); Qualify();
            Assert.That(airborne.Parked, Is.False, "Even a sleeping flag cannot pin an airborne body");
            Assert.That(unsupportedUpper.Parked, Is.False, "A sleeping bridge must itself have a support path");
            Assert.That(airborne.Body.IsSleeping() || unsupportedUpper.Body.IsSleeping(), Is.False, "Failed support proof wakes both airborne masses");
            float previousHeight = airborne.Body.position.y; Steps(1);
            Assert.That(airborne.Body.position.y, Is.LessThan(previousHeight), "Unsupported sleep must actually resume gravity");
            airborne.Body.WakeUp(); airborne.Body.linearVelocity = Vector3.right; Qualify();
            Assert.That(airborne.Parked, Is.False); Assert.That(airborne.Body.isKinematic, Is.False);
            var movingVelocity = airborne.Body.linearVelocity; var movingAngular = airborne.Body.angularVelocity;
            Qualify(new[] { airborne.Body.position + Vector3.right * 2.01f });
            Assert.That(airborne.Parked, Is.False, "A moving body just outside 2 m must retain real physics");
            Assert.That(airborne.Body.useGravity, Is.True); Assert.That(airborne.Body.IsSleeping(), Is.False);
            Assert.That(airborne.Body.linearVelocity, Is.EqualTo(movingVelocity));
            Assert.That(airborne.Body.angularVelocity, Is.EqualTo(movingAngular));
            Floor(); var settled = Spawn(new Vector3(2, 1, 0)); Steps(350); Qualify();
            Assert.That(settled.Parked, Is.True);
            _world.PrepareForPhysics(System.Array.Empty<Vector3>(), .02f);
            Assert.That(settled.Parked, Is.False); Assert.That(settled.Body.isKinematic, Is.False);
            Steps(350); Qualify(System.Array.Empty<Vector3>());
            Assert.That(settled.Parked, Is.False, "A missing match/player set cannot silently immobilize the world");
            Assert.That(settled.Body.IsSleeping(), Is.True, "An empty observer set must not repeatedly wake untagged natural sleepers");
        }

        [TestCase("twoStaticLedges")]
        [TestCase("twoDynamicSupportsOffset")]
        [TestCase("ramp20")]
        [TestCase("sideCradle60")]
        public void NaturalNonCentralSupportRetainsSleepUntilActualSupportIsRemoved(string geometry)
        {
            const float dt = 1f / 60f;
            var supports = new List<BoxCollider>();
            BlockDynamicBody target;
            switch (geometry)
            {
                case "twoStaticLedges":
                    supports.Add(Support(new Vector3(-.115f, -.1f, 0), new Vector3(.07f, .2f, 1)));
                    supports.Add(Support(new Vector3(.115f, -.1f, 0), new Vector3(.07f, .2f, 1)));
                    target = Spawn(new Vector3(0, .5f, 0));
                    break;
                case "twoDynamicSupportsOffset":
                    supports.Add(Support(new Vector3(0, -.1f, 0), new Vector3(4, .2f, 4)));
                    Spawn(new Vector3(-.23f, .5f, 0));
                    Spawn(new Vector3(.23f, .5f, 0));
                    target = Spawn(new Vector3(.02f, 1.3f, .025f));
                    break;
                case "ramp20":
                    var slope = Quaternion.Euler(0, 0, 20);
                    var ramp = Support(-(slope * Vector3.up) * .1f, new Vector3(2, .2f, 2));
                    ramp.transform.rotation = slope; supports.Add(ramp); Physics.SyncTransforms();
                    Assert.That(_world.TrySpawn(1, (slope * Vector3.up) * .20f, slope, out target), Is.True);
                    break;
                case "sideCradle60":
                    foreach (int sign in new[] { -1, 1 })
                    {
                        var rotation = Quaternion.Euler(0, 0, sign * 60);
                        var midpoint = new Vector3(sign * .275f, .47631397f, 0);
                        var side = Support(midpoint - (rotation * Vector3.up) * .05f, new Vector3(1.1f, .1f, 1));
                        side.transform.rotation = rotation; supports.Add(side);
                    }
                    Physics.SyncTransforms(); target = Spawn(new Vector3(0, .8f, 0));
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(geometry));
            }

            var bodies = new List<BlockDynamicBody>(_world.Bodies.Values);
            var positions = new Vector3[bodies.Count]; var rotations = new Quaternion[bodies.Count];
            int consecutive = 0; bool qualified = false;
            // These geometries reproduce the native support diagnostic. Never
            // pin a pose, call Sleep or force a wake to obtain natural eligibility.
            for (int step = 1; step <= 1500; step++)
            {
                Physics.Simulate(dt); Physics.SyncTransforms();
                bool unchanged = true;
                for (int i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    unchanged &= body.IsAtRest && body.Body.position.Equals(positions[i]) && body.Body.rotation.Equals(rotations[i]);
                    positions[i] = body.Body.position; rotations[i] = body.Body.rotation;
                }
                consecutive = unchanged ? consecutive + 1 : 0;
                if (step >= 250 && consecutive >= 30) { qualified = true; break; }
            }
            Assert.That(qualified, Is.True, "All bodies must first settle into thirty unchanged, exact-zero native sleeping steps");
            long wakes = _world.InspectActivation().unsupportedWakes;
            uint revision = _world.LifecycleRevision; ulong id = target.Id;
            for (int step = 0; step < 300; step++)
            {
                _world.PrepareForPhysics(Far, dt); Physics.Simulate(dt); Physics.SyncTransforms();
                for (int i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    Assert.That(body.IsAtRest && body.Body.IsSleeping(), Is.True, $"{geometry}: body {body.Id} at step {step}");
                    Assert.That(body.Body.position.Equals(positions[i]) && body.Body.rotation.Equals(rotations[i]), Is.True,
                        "An unchanged real supporting geometry must retain the native resting pose exactly");
                    Assert.That(body.Body.isKinematic, Is.False); Assert.That(body.Body.useGravity, Is.True);
                    Assert.That(body.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
                }
            }
            Assert.That(_world.InspectActivation().unsupportedWakes, Is.EqualTo(wakes), "An incomplete centre-ray proof must not repeatedly wake genuine support");
            Assert.That(_world.LifecycleRevision, Is.EqualTo(revision)); Assert.That(target.Id, Is.EqualTo(id));

            float height = target.Body.position.y;
            var changed = new List<Bounds>(); foreach (var support in supports) changed.Add(support.bounds);
            _world.NotifySupportGeometryChanging(changed);
            foreach (var body in bodies) Assert.That(body.Body.IsSleeping(), Is.False, "Wake the actual contact component before removing its support");
            foreach (var support in supports) support.enabled = false;
            Physics.SyncTransforms();
            for (int step = 0; step < 50; step++) Physics.Simulate(dt);
            Assert.That(target.Body.position.y, Is.LessThan(height - BlockWorld.CellSize * .5f), "The same naturally settled mass must really fall after support removal");
            Assert.That(_world.Count, Is.EqualTo(bodies.Count)); Assert.That(target.Id, Is.EqualTo(id));
        }

        [TestCase(1)]
        [TestCase(3)]
        public void CompleteSleepingAirborneComponentResumesGravityAndRetainsIdentity(int count)
        {
            var bodies = new List<BlockDynamicBody>();
            for (int i = 0; i < count; i++) bodies.Add(Spawn(new Vector3(0, 2 + i * BlockWorld.CellSize, 0)));
            // Explicit artificial positive control, separate from natural support.
            foreach (var body in bodies) body.Body.Sleep();
            uint revision = _world.LifecycleRevision; float initialHeight = bodies[0].Body.position.y;
            Qualify();
            foreach (var body in bodies)
            {
                Assert.That(body.Parked || body.Body.IsSleeping(), Is.False, "A complete unsupported sleeping component must wake");
                Assert.That(body.Body.isKinematic, Is.False); Assert.That(body.Body.useGravity, Is.True);
                Assert.That(_world.Contains(body), Is.True);
            }
            Steps(1);
            Assert.That(bodies[0].Body.position.y, Is.LessThan(initialHeight));
            Assert.That(_world.Count, Is.EqualTo(count)); Assert.That(_world.LifecycleRevision, Is.EqualTo(revision));
        }

        [Test] public void RemovingLooseSupportRestoresItsStackBeforeRemovalAndKeepsOtherPileParked()
        {
            Floor(); var lower = Spawn(new Vector3(0, 1, 0));
            var upper = Spawn(new Vector3(0, 1.6f, 0)); var remote = Spawn(new Vector3(3, 1, 0));
            Steps(450); Qualify();
            Assert.That(lower.Parked && upper.Parked && remote.Parked, Is.True);
            float previousHeight = upper.Body.position.y; ulong upperId = upper.Id;
            Assert.That(_world.DestroyIds(new[] { lower.Id }, 2), Is.EqualTo(1));
            Assert.That(upper.Parked, Is.False, "Support deletion restores the touching tower synchronously before the solve");
            Assert.That(upper.Body.isKinematic, Is.False); Assert.That(remote.Parked, Is.True, "A disconnected distant pile stays parked");
            Steps(50);
            Assert.That(upper.Body.position.y, Is.LessThan(previousHeight - BlockWorld.CellSize * .5f));
            Assert.That(upper.Id, Is.EqualTo(upperId)); Assert.That(_world.Count, Is.EqualTo(2));
        }

        [Test] public void BatchedTerrainSupportChangeRestoresContactTowerBeforeStaticColliderDisappears()
        {
            var platform = Support(new Vector3(0, .9f, 0), new Vector3(1, .2f, 1));
            Support(new Vector3(3, .9f, 0), new Vector3(1, .2f, 1));
            var falling = Spawn(new Vector3(0, 2, 0)); var remote = Spawn(new Vector3(3, 2, 0));
            Steps(350); Qualify();
            Assert.That(falling.Parked && remote.Parked, Is.True);
            _world.NotifySupportGeometryChanging(new[] { platform.bounds });
            Assert.That(falling.Parked, Is.False); Assert.That(remote.Parked, Is.True);
            platform.enabled = false; Steps(20);
            Assert.That(falling.Body.position.y, Is.LessThan(.8f), "A removed platform must not leave a mass suspended");
        }

        [Test] public void IncomingMotionNativelyWakesManagedObstacleThroughSleepingContactBridge()
        {
            Floor(); var target = Spawn(new Vector3(0, 1, 0));
            var bridge = Spawn(new Vector3(-BlockWorld.CellSize, 1, 0));
            target.Collider.hasModifiableContacts = bridge.Collider.hasModifiableContacts = true;
            Steps(350);
            var observers = new[] { new Vector3(-2.15f, 0, 0) };
            Qualify(observers);
            Assert.That(target.Parked, Is.True); Assert.That(bridge.Parked, Is.False);
            Assert.That(bridge.Body.IsSleeping(), Is.True, "The bridge remains a native sleeping dynamic mass inside 2 m");
            var incoming = Spawn(new Vector3(-1.1f, BlockWorld.CellSize * .5f, 0), 2);
            incoming.Collider.hasModifiableContacts = true;
            incoming.Body.linearVelocity = Vector3.right * 30;
            using var contacts = new SecondaryContactEvidence(new SecondaryFixture { A = incoming, B = bridge, C = target });
            _world.PrepareForPhysics(observers, .02f);
            Assert.That(target.Parked && target.Body.IsSleeping(), Is.True, "No motion envelope or global wake intervenes before the solve");
            Assert.That(target.Body.isKinematic, Is.False); Assert.That(target.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
            Assert.That(_world.InspectActivation().motionWakes, Is.Zero);
            Assert.That(_world.InspectActivation().secondaryGuardCalls, Is.Zero);
            Assert.That(_world.InspectActivation().supportGraphBuilds, Is.Zero);
            float previousX = target.Body.position.x;
            contacts.BeginStep(1, true); Steps(1); Assert.That(_world.Count, Is.EqualTo(3));
            Assert.That(contacts.FirstAB, Is.EqualTo(1)); Assert.That(contacts.FirstBC, Is.Zero, "This fixture reaches the sleeping target in the second solve");
            Assert.That(target.IsAtRest, Is.True, "Do not assert a target wake before a real B-C contact exists");
            _world.PrepareForPhysics(observers, .02f); contacts.BeginStep(2, true); Steps(1);
            Assert.That(contacts.FirstBC, Is.EqualTo(2), "Require the actual native bridge-target contact in its measured second solve");
            Assert.That(target.Body.IsSleeping(), Is.False, "Native contact transfers the impact through the sleeping bridge");
            Assert.That(target.Body.linearVelocity.x, Is.GreaterThan(.01f));
            Assert.That(target.Body.position.x, Is.GreaterThan(previousX + .001f));
            _world.PrepareForPhysics(observers, .02f); Assert.That(target.Parked, Is.False);
            Assert.That(_world.InspectActivation().pendingMotionWakes, Is.Zero, "The ordinary native wake path needs no pending-momentum correction");
        }

        [TestCase(false), TestCase(true)]
        public void CcdPendingImpulseWithProductionCollisionFlagsSurvivesPrepareEvenWithoutObservers(bool noObservers)
        {
            // The matched pin-and-sleep initialization deliberately reproduces
            // the verified CCD phase edge case. It is not evidence that this
            // particular edge case occurs after unmodified natural settling.
            var fixture = SecondaryImpactSetup(false, true);
            foreach (var block in _world.Bodies.Values) block.Collider.hasModifiableContacts = false;
            IReadOnlyList<Vector3> observers = noObservers ? Array.Empty<Vector3>() : Far;
            _world.PrepareForPhysics(observers, .02f); Steps(1);
            Assert.That(fixture.C.Body.IsSleeping(), Is.True, "This regression must reproduce the native pending-momentum sleep flag");
            Assert.That(fixture.C.Body.linearVelocity.z, Is.GreaterThan(1));
            Assert.That(fixture.C.IsAtRest, Is.False); Assert.That(fixture.D.IsAtRest, Is.True);
            Assert.That(fixture.C.Collider.hasModifiableContacts || fixture.D.Collider.hasModifiableContacts, Is.False);
            var velocity = fixture.C.Body.linearVelocity; var angular = fixture.C.Body.angularVelocity;
            var position = fixture.C.Body.position; var remotePosition = fixture.D.Body.position;
            var states = new List<BlockDynamicState>(); _world.CaptureStates(states);
            var state = states.Find(value => value.Id == fixture.C.Id);
            Assert.That(state.Sleeping, Is.False); Assert.That(state.Velocity, Is.EqualTo(velocity));
            Assert.That(state.AngularVelocity, Is.EqualTo(angular));
            Assert.That(_world.InspectActivation().active, Is.GreaterThanOrEqualTo(3));
            _world.PrepareForPhysics(observers, .02f);
            Assert.That(_world.InspectActivation().pendingMotionWakes, Is.EqualTo(1));
            Assert.That(fixture.C.Body.IsSleeping(), Is.False);
            Assert.That(fixture.C.Body.linearVelocity, Is.EqualTo(velocity)); Assert.That(fixture.C.Body.angularVelocity, Is.EqualTo(angular));
            Assert.That(fixture.D.IsAtRest, Is.True, "A repair without observers must not wake an unrelated native sleeper");
            Steps(1);
            Assert.That(fixture.C.Body.position.z, Is.GreaterThan(position.z + .01f));
            Assert.That(fixture.D.IsAtRest, Is.True); Assert.That(fixture.D.Body.position, Is.EqualTo(remotePosition));
        }

        [Test] public void NaturallySleepingBridgeTransfersMomentumOnSecondSolveWithProductionCollisionFlags()
        {
            Floor(); var target = Spawn(new Vector3(0, 1, 0));
            var bridge = Spawn(new Vector3(-BlockWorld.CellSize, 1, 0)); Steps(350);
            Assert.That(target.IsAtRest && bridge.IsAtRest, Is.True, "Both receiver poses and native sleep come from actual settling, without pinning or Sleep calls");
            var observers = new[] { new Vector3(-2.15f, 0, 0) }; Qualify(observers);
            var incoming = Spawn(new Vector3(-1.1f, BlockWorld.CellSize * .5f, 0), 2);
            incoming.Body.linearVelocity = Vector3.right * 30;
            foreach (var block in _world.Bodies.Values) Assert.That(block.Collider.hasModifiableContacts, Is.False);
            float previousX = target.Body.position.x;
            _world.PrepareForPhysics(observers, .02f); Steps(1);
            Assert.That(incoming.Body.linearVelocity.x, Is.InRange(1f, 29f), "The first solve delivers a real native collision response to the incoming mass");
            Assert.That(target.IsAtRest, Is.True);
            _world.PrepareForPhysics(observers, .02f); Steps(1);
            Assert.That(bridge.Body.linearVelocity.x, Is.GreaterThan(1), "The second solve moves the bridge with real momentum");
            Assert.That(target.Body.linearVelocity.x, Is.GreaterThan(1), "The second solve transfers real momentum into the target");
            Assert.That(target.Body.position.x, Is.GreaterThan(previousX + .001f));
            Assert.That(target.IsAtRest, Is.False); Assert.That(target.Body.isKinematic, Is.False);
            Assert.That(_world.InspectActivation().pendingMotionWakes, Is.Zero);
        }

        [Test] public void PendingCcdWakeInsideActivationRangeClearsManagementWithoutASecondNativeWake()
        {
            var fixture = SecondaryImpactSetup(true, true);
            foreach (var block in _world.Bodies.Values) block.Collider.hasModifiableContacts = false;
            _world.PrepareForPhysics(Far, .02f); Steps(1);
            Assert.That(fixture.C.Parked && fixture.C.Body.IsSleeping(), Is.True,
                "Require the real pending-CCD impulse while the previous management tag still exists");
            Assert.That(fixture.C.Body.linearVelocity.z, Is.GreaterThan(1));
            var velocity = fixture.C.Body.linearVelocity; var angular = fixture.C.Body.angularVelocity;
            var remote = fixture.D.Body.position; var position = fixture.C.Body.position;
            var before = _world.InspectActivation();
            var observers = new[] { fixture.C.Body.position };
            Assert.That(Vector3.Distance(observers[0], fixture.D.Body.position), Is.GreaterThan(2));

            _world.PrepareForPhysics(observers, .02f);
            var after = _world.InspectActivation();
            Assert.That(fixture.C.Parked || fixture.C.Body.IsSleeping(), Is.False);
            Assert.That(after.pendingMotionWakes, Is.EqualTo(before.pendingMotionWakes + 1));
            Assert.That(after.rangeWakes, Is.EqualTo(before.rangeWakes),
                "The preceding pending-momentum WakeUp must be visible to the same-iteration range check");
            Assert.That(after.nativeWakeClears, Is.EqualTo(before.nativeWakeClears),
                "Range cleanup precedes the distant stale-tag branch, even after pending momentum wakes the body");
            Assert.That(fixture.C.Body.linearVelocity, Is.EqualTo(velocity));
            Assert.That(fixture.C.Body.angularVelocity, Is.EqualTo(angular));
            Assert.That(fixture.D.Parked && fixture.D.IsAtRest, Is.True);

            Steps(1);
            Assert.That(fixture.C.Body.position.z, Is.GreaterThan(position.z + .01f));
            Assert.That(fixture.D.Body.position, Is.EqualTo(remote));
        }

        [TestCase(0f, 0f, true)]
        [TestCase(float.Epsilon, 0f, false)]
        [TestCase(0f, float.Epsilon, false)]
        [TestCase(.00000001f, 0f, false)]
        [TestCase(0f, -.00000001f, false)]
        [TestCase(float.NaN, 0f, false)]
        [TestCase(0f, float.PositiveInfinity, false)]
        public void RestMotionRequiresExactFiniteZeroLinearAndAngularComponents(float linear, float angular, bool atRest)
        {
            var predicate = typeof(BlockDynamicBody).GetMethod("HasZeroMotion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(predicate, Is.Not.Null);
            bool actual = (bool)predicate.Invoke(null, new object[] { new Vector3(linear, 0, 0), new Vector3(0, angular, 0) });
            Assert.That(actual, Is.EqualTo(atRest), "Sub-epsilon angular or linear momentum and non-finite input must never be classified as exactly at rest");
        }

        [Test] public void NativeForceWakeIsCapturedBeforeStaleManagementTagClearsWithoutWakingRemotePile()
        {
            Floor(); var body = Spawn(new Vector3(0, 1, 0)); var remote = Spawn(new Vector3(3, 1, 0));
            Steps(350); Qualify(); Assert.That(body.Parked && remote.Parked, Is.True);
            body.Body.AddForce(Vector3.right * 2, ForceMode.Impulse);
            body.Body.AddTorque(Vector3.up * .01f, ForceMode.Impulse);
            Steps(1);
            Assert.That(body.Parked, Is.True, "This samples between native solve and the next range pass");
            Assert.That(body.Body.IsSleeping(), Is.False);
            Assert.That(body.Body.linearVelocity.x, Is.GreaterThan(.01f));
            var states = new List<BlockDynamicState>(); _world.CaptureStates(states);
            var state = states.Find(value => value.Id == body.Id);
            Assert.That(state.Sleeping, Is.False); Assert.That(state.Velocity, Is.EqualTo(body.Body.linearVelocity));
            Assert.That(state.AngularVelocity, Is.EqualTo(body.Body.angularVelocity));
            var before = _world.InspectActivation();
            Assert.That(before.active, Is.EqualTo(1)); Assert.That(before.parked, Is.EqualTo(1)); Assert.That(before.sleeping, Is.Zero);
            var velocity = body.Body.linearVelocity; var angular = body.Body.angularVelocity;
            _world.PrepareForPhysics(Far, .02f);
            Assert.That(body.Parked, Is.False); Assert.That(remote.Parked && remote.Body.IsSleeping(), Is.True);
            Assert.That(body.Body.linearVelocity, Is.EqualTo(velocity)); Assert.That(body.Body.angularVelocity, Is.EqualTo(angular));
            Assert.That(_world.InspectActivation().nativeWakeClears, Is.EqualTo(1));
        }

        [TestCase(false), TestCase(true)]
        public void SupportRemovalWakesUntaggedSleepingTowerWithoutObserversAndRetainsDisconnectedSleep(bool removeLooseSupport)
        {
            var platform = Support(new Vector3(0, .9f, 0), new Vector3(1, .2f, 1));
            Support(new Vector3(3, .9f, 0), new Vector3(1, .2f, 1));
            var lower = Spawn(new Vector3(0, 2, 0)); var upper = Spawn(new Vector3(0, 2.6f, 0));
            var remote = Spawn(new Vector3(3, 2, 0)); Steps(450);
            _world.PrepareForPhysics(Array.Empty<Vector3>(), .02f);
            Assert.That(lower.Body.IsSleeping() && upper.Body.IsSleeping() && remote.Body.IsSleeping(), Is.True);
            Assert.That(lower.Parked || upper.Parked || remote.Parked, Is.False);
            float previousHeight = upper.Body.position.y;
            if (removeLooseSupport) Assert.That(_world.DestroyIds(new[] { lower.Id }, 2), Is.EqualTo(1));
            else
            {
                _world.NotifySupportGeometryChanging(new[] { platform.bounds });
                Assert.That(lower.Body.IsSleeping(), Is.False, "Wake the local sleeping root before disabling its static support");
                platform.enabled = false;
            }
            Assert.That(upper.Body.IsSleeping(), Is.False, "Support events include untagged actual sleeping contact closure");
            Assert.That(remote.Body.IsSleeping(), Is.True, "Disconnected native sleepers are excluded from the event closure");
            Assert.That(_world.InspectActivation().supportGraphBuilds, Is.EqualTo(1));
            Steps(30); Assert.That(upper.Body.position.y, Is.LessThan(previousHeight - .15f));
        }

        [Test] public void BatchedLooseSupportRemovalDeduplicatesIdsAndWakesSelectedContactClosuresOnce()
        {
            Floor(); var left = Spawn(new Vector3(-1, 1, 0)); var leftTop = Spawn(new Vector3(-1, 1.6f, 0));
            var right = Spawn(new Vector3(1, 1, 0)); var rightTop = Spawn(new Vector3(1, 1.6f, 0));
            var remote = Spawn(new Vector3(3, 1, 0)); var friendly = Spawn(new Vector3(4, 1, 0), 2);
            Steps(450); Qualify(); uint revision = _world.LifecycleRevision;
            long removed = _world.RemovedCount;
            Assert.That(_world.DestroyIds(new[] { left.Id, right.Id, left.Id, friendly.Id, ulong.MaxValue }, 2), Is.EqualTo(2));
            Assert.That(_world.RemovedCount, Is.EqualTo(removed + 2)); Assert.That(_world.LifecycleRevision, Is.EqualTo(revision + 1));
            Assert.That(_world.Count, Is.EqualTo(4)); Assert.That(_world.Contains(friendly), Is.True);
            Assert.That(leftTop.Body.IsSleeping() || rightTop.Body.IsSleeping(), Is.False);
            Assert.That(remote.Body.IsSleeping() && friendly.Body.IsSleeping(), Is.True);
            Assert.That(_world.InspectActivation().supportGraphBuilds, Is.EqualTo(1), "The whole selected deletion batch resolves support before either collider disappears");
        }

        [Test] public void RadiusRemovalKeepsNearestEnemyBudgetAndFriendlyRemovalWhileBatchingSupport()
        {
            Floor(); var nearest = Spawn(new Vector3(0, 1, 0)); var second = Spawn(new Vector3(1, 1, 0));
            var excluded = Spawn(new Vector3(2, 1, 0)); var friendly = Spawn(new Vector3(3, 1, 0), 2);
            Steps(350); Qualify(); uint revision = _world.LifecycleRevision;
            var result = _world.DestroyInRadius(nearest.Body.position, 5, 5, 2, false, 2);
            Assert.That(result.RemovedEnemy, Is.EqualTo(2)); Assert.That(result.RemovedFriendly, Is.EqualTo(1));
            Assert.That(_world.Contains(nearest) || _world.Contains(second) || _world.Contains(friendly), Is.False);
            Assert.That(_world.Contains(excluded), Is.True); Assert.That(excluded.Body.IsSleeping(), Is.True);
            Assert.That(_world.Count, Is.EqualTo(1)); Assert.That(_world.RemovedCount, Is.EqualTo(3));
            Assert.That(_world.LifecycleRevision, Is.EqualTo(revision + 1));
            Assert.That(_world.InspectActivation().supportGraphBuilds, Is.EqualTo(1));
        }

        [Test] public void PlayerContactWakesOnlyTheContactedNativeSleeper()
        {
            Floor(); var contacted = Spawn(new Vector3(0, 1, 0)); var remote = Spawn(new Vector3(3, 1, 0));
            Steps(350); Qualify(); _world.NotifyPlayerContact(contacted);
            Assert.That(contacted.Parked || contacted.Body.IsSleeping(), Is.False);
            Assert.That(remote.Parked && remote.Body.IsSleeping(), Is.True);
            Assert.That(contacted.Body.isKinematic, Is.False); Assert.That(contacted.Body.mass, Is.EqualTo(BlockDynamicWorld.Mass));
            Assert.That(_world.InspectActivation().playerContactWakes, Is.EqualTo(1));
        }

        [Test] public void PlayerContactAuthorityChangesFreezeAndPoolReuseClearParkingState()
        {
            Floor(); var body = Spawn(new Vector3(0, 1, 0)); Steps(350); Qualify();
            _world.NotifyPlayerContact(body);
            Assert.That(body.Parked, Is.False); Assert.That(body.Body.isKinematic, Is.False);
            Steps(350); Qualify(); Assert.That(body.Parked, Is.True);
            _world.SetAuthority(false);
            Assert.That(body.Parked, Is.False); Assert.That(body.Body.isKinematic, Is.True);
            _world.SetAuthority(true);
            Assert.That(body.Parked, Is.False); Assert.That(body.Body.isKinematic, Is.False);
            Steps(350); Qualify(); Assert.That(body.Parked, Is.True);
            _world.Freeze(); _world.PrepareForPhysics(new[] { Vector3.zero }, .02f);
            Assert.That(body.Parked, Is.False); Assert.That(body.Body.isKinematic, Is.True);
            Assert.That(body.Body.useGravity, Is.False); ulong oldId = body.Id;
            _world.Reset(_world.Round + 1);
            var reused = Spawn(new Vector3(0, 2, 0));
            Assert.That(reused, Is.SameAs(body)); Assert.That(reused.Id, Is.GreaterThan(oldId));
            Assert.That(reused.Parked, Is.False); Assert.That(reused.Body.isKinematic, Is.False);
            Assert.That(_world.InspectActivation().parks, Is.Zero);
        }

        [Test] public void ReplicaSkipsOnlyExactDuplicatePoseAndAlwaysAppliesChangedSleepingTerminalPose()
        {
            _world.SetAuthority(false);
            var state = new BlockDynamicState { Id = 70, Round = _world.Round, Owner = 2,
                Position = new Vector3(0, 2, 0), Rotation = Quaternion.identity, Sleeping = true };
            Assert.That(_world.ApplySnapshot(_world.Round, 1, 1, new[] { state }, false), Is.True);
            Assert.That(_world.ApplyMotion(_world.Round, 1, 2, new[] { state }), Is.True);
            var metrics = _world.InspectActivation();
            Assert.That(metrics.replicaPoseWrites, Is.EqualTo(1)); Assert.That(metrics.skippedReplicaPoseWrites, Is.EqualTo(1));
            state.Position.x = .000001f;
            state.Rotation = Quaternion.Euler(2, 3, 4);
            Assert.That(_world.ApplyMotion(_world.Round, 1, 3, new[] { state }), Is.True);
            Assert.That(_world.Bodies[70].Body.position.x, Is.EqualTo(state.Position.x), "Sleeping changed final pose must be applied, including sub-epsilon movement");
            Assert.That(_world.InspectActivation().replicaPoseWrites, Is.EqualTo(2));
            var rotation = state.Rotation;
            state.Rotation = new Quaternion(rotation.x * 1.0001f, rotation.y * 1.0001f, rotation.z * 1.0001f, rotation.w * 1.0001f);
            Assert.That(_world.ApplySnapshot(_world.Round, 2, 4, new[] { state }, true), Is.True);
            var captured = new List<BlockDynamicState>(); _world.CaptureStates(captured);
            Assert.That(captured[0].Rotation.Equals(state.Rotation), Is.True, "Canonical frozen wire quaternion survives native normalization");
        }
    }
}
#endif
