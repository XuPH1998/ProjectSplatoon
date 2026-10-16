#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // An actual CharacterController.Move and published MeshCollider fixture.
    // The semantic regression intentionally fails on the unguarded implementation.
    public sealed class MotorDestinationGroundTests
    {
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        CharacterController controller;
        MotorGroundContactRecorder contactRecorder;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        double now;
        readonly List<PlayerMotorSimulation.MotorBranchObservation> events = new();

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            terrain = new GameObject("Destination ground trace world").AddComponent<BlockTerrain>(); terrain.Initialize(null);
            var root = new GameObject("Destination ground real motor") { layer = 8 };
            controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f; controller.center = Vector3.up * .9f; controller.radius = .35f;
            controller.skinWidth = .03f; controller.stepOffset = .3f;
            contactRecorder = root.AddComponent<MotorGroundContactRecorder>(); motor = new PlayerMotorSimulation(controller);
            PlayerMotorSimulation.MotorBranchObserved += Observe;
        }
        [TearDown] public void Cleanup()
        {
            PlayerMotorSimulation.MotorBranchObserved -= Observe; PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Observe(PlayerMotorSimulation source, PlayerMotorSimulation.MotorBranchObservation observation)
        { if (source == motor) events.Add(observation); }
        void ResetAir(Vector2 xz, uint sequence)
        {
            events.Clear(); now = 10;
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1,
                LifeState = PlayerLifeState.Alive, Health = 100, Ink = 43, SpecialPoints = 7,
                Position = new Vector3(xz.x, 1.3f, xz.y), Movement = MovementMode.Air,
                Swimming = true, Grounded = false, PaperPose = PaperPose.Air, AirHumanOffset = .87f,
                PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 0),
                SwimSource = SwimSurface.Neutral, AirSwimSource = SwimSurface.Neutral, SwimWasHeld = true,
                VerticalSpeed = -1.5f, Velocity = Vector3.down * 1.5f, GroundNormal = Vector3.up, SimulatedAt = now };
            state.PaperCenter = state.Position + Vector3.up * .03f;
            input = new PlayerInputFrame { Swim = true, Move = Vector2.zero, Look = Vector2.zero,
                Sequence = sequence, Revision = 1, HeroRevision = 1 };
            motor.Restore(state); Physics.SyncTransforms();
        }
        void Tick()
        {
            now += Dt; input.Sequence++; input.Tick++; state.SimulatedAt = now; state.SimulationTick++;
            contactRecorder.Begin(input.Sequence); motor.Step(ref state, input, Dt, now, false);
            state.AcknowledgedInput = input.Sequence;
        }
        IEnumerator Publish(BlockWorld world)
        {
            uint revision = terrain.Revision + 1;
            world.Seal(revision, world.Chunks.Keys.Concat(terrain.World.Chunks.Keys).Distinct().ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); tick++)
            { terrain.Tick(false, now, Dt); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
        static BlockWorld OneVoxel() { var world = new BlockWorld(new Vector2(32, 64)); world.Set(new Vector3Int(1, 10, 1), BlockCell.Create(1, true)); return world; }

        [Serializable] sealed class TraceRow
        {
            public string branch;
            public ulong invocation;
            public uint sequence, simulationTick, life, round, terrainRevision;
            public Vector3 beforePosition, afterPosition, groundPoint, groundNormal, controllerPosition, requestedMove;
            public Vector3Int groundCell, afterSupportCell;
            public uint groundRevision, afterSupportRevision;
            public float beforeOffset, afterOffset, rebaseHeight, beforeVerticalSpeed, afterVerticalSpeed;
            public bool queryEvaluated, querySucceeded, controllerGrounded, beforeSwimming, afterSwimming;
            public bool beforeCompact, afterCompact, beforeGrounded, afterGrounded, standingEvaluated, standingFits;
            public int collisions;
        }
        [Serializable] sealed class ContactRow
        {
            public uint sequence;
            public Vector3 point, normal, moveDirection;
            public float moveLength;
            public int colliderId, layer;
            public bool blockCollider;
        }
        [Serializable] sealed class Evidence
        {
            public string scope = "actual EditMode PhysX diagnostic; no Player gate is replaced";
            public string label;
            public bool missedDestinationFound, realBlockTopContactFound, originalClearAndReopenObserved;
            public List<TraceRow> transitions = new();
            public List<ContactRow> contacts = new();
        }
        void Save(string label, bool missed, bool realTop, bool reopened, IReadOnlyList<MotorGroundContactRecorder.ContactObservation> contacts, string scope = null)
        {
            var report = new Evidence { label = label, missedDestinationFound = missed, realBlockTopContactFound = realTop, originalClearAndReopenObserved = reopened };
            if (scope != null) report.scope = scope;
            foreach (var o in events) report.transitions.Add(new TraceRow { branch = o.Branch.ToString(), invocation = o.Invocation,
                sequence = o.Input.Sequence, simulationTick = o.SimulationTick, life = o.Life, round = o.TerrainRound, terrainRevision = o.TerrainRevision,
                beforePosition = o.Before.Position, afterPosition = o.After.Position, beforeOffset = o.Before.AirHumanOffset, afterOffset = o.After.AirHumanOffset,
                beforeVerticalSpeed = o.Before.VerticalSpeed, afterVerticalSpeed = o.After.VerticalSpeed,
                groundPoint = o.Contact.Point, groundNormal = o.Contact.Normal, groundCell = o.Contact.Cell, groundRevision = o.Contact.Revision,
                afterSupportCell = o.After.SupportCell, afterSupportRevision = o.After.SupportTerrainRevision, controllerPosition = o.ControllerPosition,
                requestedMove = o.RequestedMove, rebaseHeight = o.RebaseHeight, collisions = (int)o.Collisions,
                queryEvaluated = o.ProbeEvaluated, querySucceeded = o.ProbeSucceeded, controllerGrounded = o.ControllerGrounded,
                beforeSwimming = o.Before.Swimming, afterSwimming = o.After.Swimming, beforeCompact = o.Before.CompactBody, afterCompact = o.After.CompactBody,
                beforeGrounded = o.Before.Grounded, afterGrounded = o.After.Grounded, standingEvaluated = o.StandingEvaluated, standingFits = o.StandingFits });
            foreach (var o in contacts) report.contacts.Add(new ContactRow { sequence = o.InputSequence, point = o.Point, normal = o.Normal,
                moveDirection = o.MoveDirection, moveLength = o.MoveLength, colliderId = o.ColliderId, layer = o.Layer, blockCollider = o.BlockCollider });
            string folder = Path.GetFullPath("Reports/BlockTerrain/PredictionAudit/PhysXGround"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true)); TestContext.Progress.WriteLine("Motor ground evidence: " + path);
        }

        [UnityTest] public IEnumerator RemovedSupportPreservesAirOriginInResolveGroundMethodContract()
        {
            // This is a method contract. A real Move first obtains native Below
            // and isGrounded. Publication then removes support BEFORE reflection
            // invokes ResolveGround. It does not reproduce a failed query inside
            // the same Move or replace the retained original PhysX reproduction.
            var cell = new Vector3Int(1, 10, 1); yield return Publish(OneVoxel());
            ResetAir(new Vector2(.15f, .15f), 9100);
            var actualContacts = new List<MotorGroundContactRecorder.ContactObservation>();
            CollisionFlags lastMove = CollisionFlags.None;
            for (int tick = 0; tick < 40; tick++)
            {
                contactRecorder.Begin(input.Sequence);
                lastMove = controller.Move(Vector3.down * .025f);
                actualContacts.AddRange(contactRecorder.Contacts);
                if ((lastMove & CollisionFlags.Below) != 0 && controller.isGrounded) break;
            }
            Assert.That((lastMove & CollisionFlags.Below) != 0, Is.True);
            Assert.That(controller.isGrounded, Is.True, "The method precondition must follow a real native Move");
            Assert.That(actualContacts.Any(c => c.BlockCollider && c.Normal.y > .9f && Mathf.Abs(c.Point.y - 1.1f) < .03f), Is.True);
            state.Position = controller.transform.position;
            state.Grounded = controller.isGrounded;
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out var oldGround), Is.True);
            Assert.That(oldGround.Cell, Is.EqualTo(cell));
            yield return Publish(new BlockWorld(new Vector2(32, 64)));
            Assert.That(terrain.World.TryGet(cell, out _), Is.False);
            Assert.That(PlayerMotorSimulation.TryGetGround(state.Position, out _), Is.False,
                "The guard must receive a fresh failure after collision publication, not a mocked probe");
            var before = state;
            var method = typeof(PlayerMotorSimulation).GetMethod("ResolveGround",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { state, input, false };
            method.Invoke(motor, arguments); state = (PlayerSnapshot)arguments[0];
            var expected = before; expected.Grounded = false;
            Assert.That(state, Is.EqualTo(expected),
                "A missing published ground can only clear Grounded; it cannot reset the saved air origin or form");
            Assert.That(state.AirHumanOffset, Is.EqualTo(.87f));
            Save("method-contract-removed-support-not-same-move", false, true, false, actualContacts,
                "ResolveGround method contract only: real native Move/Below/isGrounded, then support removal publication, then reflection; not a same-Move failed-probe reproduction or a Player gate");
        }

        [UnityTest] public IEnumerator DiagonalProductionSupportSucceedsWithoutReopeningAirPaper()
        {
            yield return Publish(OneVoxel()); ResetAir(new Vector2(.08f, .08f), 1000);
            for (int tick = 0; tick < 90 && !state.Grounded; tick++) Tick();
            var landings = events.Where(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundEvaluation && o.ProbeSucceeded).ToArray();
            var landingContacts = contactRecorder.Contacts.ToArray();
            for (int tick = 0; tick < 12; tick++) Tick();
            Save("diagonal-supported-footprint", false, landingContacts.Any(o => o.BlockCollider && o.Normal.y > .9f), false, landingContacts);
            Assert.That(landings.Length, Is.GreaterThan(0), "The production query must discover the real diagonal support");
            var feet = landings[0].Before.Position;
            foreach (var offset in new[] { Vector3.zero, Vector3.right * (controller.radius * .8f),
                         Vector3.left * (controller.radius * .8f), Vector3.forward * (controller.radius * .8f), Vector3.back * (controller.radius * .8f) })
            {
                bool oldRay = Physics.Raycast(feet + offset + Vector3.up * .08f, Vector3.down, out var hit, .16f,
                    PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore) && hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);
                Assert.That(oldRay, Is.False, "Retain the saved original five-ray miss as an independent diagnostic");
            }
            Assert.That(landingContacts.Any(o => o.BlockCollider && o.Normal.y > .9f && Mathf.Abs(o.Point.y - 1.1f) < .03f), Is.True);
            Assert.That(state.Grounded, Is.True); Assert.That(state.Swimming, Is.True); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(state.SupportCell, Is.EqualTo(new Vector3Int(1, 10, 1))); Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision));
            Assert.That(events.Any(o => o.Invocation > landings[0].Invocation && o.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry && o.RebaseHeight > .8f), Is.False);
        }

        [UnityTest] public IEnumerator CentredVoxelLandingHasARealContactAndClearsTheOffsetOnce()
        {
            yield return Publish(OneVoxel()); ResetAir(new Vector2(.15f, .15f), 2000);
            for (int tick = 0; tick < 90 && !state.Grounded; tick++) Tick();
            var landings = events.Where(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundEvaluation && o.ProbeSucceeded).ToArray();
            Save("centred-valid-support", false, landings.Length > 0, false, contactRecorder.Contacts);
            Assert.That(landings.Length, Is.GreaterThan(0)); Assert.That(state.Grounded, Is.True);
            Assert.That(state.Swimming, Is.True); Assert.That(state.AirHumanOffset, Is.Zero);
            Assert.That(state.SupportCell, Is.EqualTo(new Vector3Int(1, 10, 1))); Assert.That(state.SupportTerrainRevision, Is.EqualTo(terrain.Revision));
            Assert.That(landings[0].Contact.Normal.y, Is.GreaterThan(.99f));
            events.Clear(); for (int tick = 0; tick < 6; tick++) Tick();
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.AirEntry && o.RebaseHeight > .8f), Is.False);
        }

        [UnityTest] public IEnumerator RemovedVoxelProvidesNeitherGroundedControllerNorSupportDependency()
        {
            yield return Publish(OneVoxel()); yield return Publish(new BlockWorld(new Vector2(32, 64)));
            ResetAir(new Vector2(.08f, .08f), 3000); for (int tick = 0; tick < 20; tick++) Tick();
            Save("removed-support", false, false, false, contactRecorder.Contacts);
            Assert.That(events.Any(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.GroundEvaluation), Is.False);
            Assert.That(state.Grounded, Is.False); Assert.That(state.SupportTerrainRevision, Is.Zero); Assert.That(state.AirHumanOffset, Is.EqualTo(.87f));
        }

        [Test] public void TraceCopiesAreImmutableAndObserverFailureCannotChangeMotorResults()
        {
            ResetAir(new Vector2(4, 4), 4000); int beforeErrors = PlayerMotorSimulation.MotorBranchObserverErrors;
            Action<PlayerMotorSimulation, PlayerMotorSimulation.MotorBranchObservation> throwing = (source, o) =>
            { if (source == motor && o.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete) throw new InvalidOperationException("motor observer isolation"); };
            PlayerMotorSimulation.MotorBranchObserved += throwing;
            try
            {
                Tick(); var complete = events.Last(o => o.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete); var first = state;
                Assert.That(PlayerMotorSimulation.MotorBranchObserverErrors, Is.EqualTo(beforeErrors + 1));
                state.Position = Vector3.one * 99; input.Swim = false;
                Assert.That(complete.After.Position, Is.EqualTo(first.Position)); Assert.That(complete.Input.Swim, Is.True);
                Assert.That(complete.Life, Is.EqualTo(first.Revision)); Assert.That(complete.SimulationTick, Is.EqualTo(first.SimulationTick));
                Assert.That(complete.IdentityAvailable, Is.False, "This pure motor has no invented network identity");
            }
            finally { PlayerMotorSimulation.MotorBranchObserved -= throwing; }
        }
    }
}
#endif
