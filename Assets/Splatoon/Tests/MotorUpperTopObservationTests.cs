#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using D = Splatoon.Combat.PlayerMotorSimulation;

namespace Splatoon.Tests
{
    // Real motor Step and published colliders; reports do not replace Player/LAN gates.
    public sealed class MotorUpperTopObservationTests
    {
        const string PrefabPath = "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab";
        const int MaxSteps = 180, RawCap = 4096, ContactCap = 8192;
        const float Dt = 1f / 60;
        BlockTerrain terrain;
        CharacterController controller;
        MotorGroundContactRecorder recorder;
        D motor;
        PlayerSnapshot state;
        PlayerInputFrame input;
        MotorDetailCapture capture;
        D.MoveCapsuleDetails copiedProductionCapsule, restoredInitialCapsule;
        readonly List<D.MotorBranchObservation> raw = new(RawCap);
        readonly List<NativeContact> contacts = new(ContactCap);
        readonly List<uint> mantleScopeSequences = new(MaxSteps);
        bool observationOverflow, detailsSubscribed, stepTouchesMantle;
        int legacyProviderErrorsBefore, copiedNativeContacts;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            terrain = new GameObject("Real published upper top observation").AddComponent<BlockTerrain>(); terrain.Initialize(null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath); Assert.That(prefab, Is.Not.Null);
            var production = prefab.GetComponent<CharacterController>(); Assert.That(production, Is.Not.Null);
            var root = new GameObject("Production capsule physics fixture") { layer = prefab.layer };
            controller = root.AddComponent<CharacterController>();
            controller.height = production.height; controller.center = production.center; controller.radius = production.radius;
            controller.slopeLimit = production.slopeLimit; controller.stepOffset = production.stepOffset;
            controller.skinWidth = production.skinWidth; controller.minMoveDistance = production.minMoveDistance;
            copiedProductionCapsule = CapsuleSettings(controller);
            recorder = root.AddComponent<MotorGroundContactRecorder>(); motor = new D(controller);
            legacyProviderErrorsBefore = D.MotorBranchObserverErrors;
            D.MotorBranchObserved += ObserveBase;
        }
        [TearDown] public void Cleanup()
        {
            StopDetails(); D.MotorBranchObserved -= ObserveBase;
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void BeginDetails(MotorDetailCapture target = null)
        {
            StopDetails(); capture = target ?? new MotorDetailCapture();
            D.MotorDetailObserved += ObserveDetail; D.MotorDetailInvocationFinished += ObserveFinished; detailsSubscribed = true;
        }
        void StopDetails()
        {
            if (detailsSubscribed) { D.MotorDetailObserved -= ObserveDetail; D.MotorDetailInvocationFinished -= ObserveFinished; detailsSubscribed = false; }
            capture?.Close();
        }
        void ObserveBase(D source, D.MotorBranchObservation observation)
        {
            if (source != motor) return;
            // Do not Assert in an observer: legacy code catches observer errors.
            if (raw.Count >= RawCap) { observationOverflow = true; return; }
            raw.Add(observation); capture?.OnBaseEvent(source, observation, raw.Count);
            if (observation.Before.Movement == MovementMode.Mantle || observation.After.Movement == MovementMode.Mantle) stepTouchesMantle = true;
            if (observation.Branch == D.MotorTraceBranch.DestinationMove || observation.Branch == D.MotorTraceBranch.WallMove ||
                observation.Branch == D.MotorTraceBranch.WallAttachEvaluation)
                CopyNativeContacts(stepTouchesMantle ? 0 : raw.Count, observation.Invocation,
                    stepTouchesMantle ? "mantle-scope" : observation.Branch.ToString());
        }
        void ObserveDetail(D source, D.MotorDetailObservation observation)
        { if (source == motor) capture.OnDetail(source, observation); }
        void ObserveFinished(D source, D.MotorDetailObservation observation)
        { if (source == motor) capture.OnInvocationFinished(source, observation); }
        void SeedAir(float y, float z, float offset, uint sequence = 1000)
        {
            raw.Clear(); contacts.Clear(); mantleScopeSequences.Clear(); observationOverflow = false;
            state = new PlayerSnapshot { HeroId = 1, HeroRevision = 1, Revision = 1, Team = 1, Health = 100, Ink = 43,
                LifeState = PlayerLifeState.Alive, Movement = MovementMode.Air, Position = new Vector3(.05f, y, z),
                Swimming = true, Grounded = false, PaperPose = PaperPose.Air, AirHumanOffset = offset,
                SwimSource = SwimSurface.Friendly, AirSwimSource = SwimSurface.Friendly, SwimWasHeld = true,
                VerticalSpeed = -.1f, Velocity = Vector3.down * .1f, GroundNormal = Vector3.up,
                PaperRotation = PaperPoseSimulation.GroundRotation(Vector3.up, 0), SimulationTick = 1, SimulatedAt = 10 };
            state.PaperCenter = state.Position + Vector3.up * .03f;
            input = new PlayerInputFrame { Sequence = sequence, Tick = sequence, Revision = 1, HeroRevision = 1,
                Swim = true, Fire = false, Move = Vector2.up, Look = Vector2.zero };
            motor.Restore(state); restoredInitialCapsule = CapsuleSettings(controller); Physics.SyncTransforms();
        }
        static D.MoveCapsuleDetails CapsuleSettings(CharacterController c) => new()
        { MoveExecuted = false, Height = c.height, Center = c.center, Radius = c.radius, StepOffset = c.stepOffset,
            SkinWidth = c.skinWidth, SlopeLimit = c.slopeLimit, MinMoveDistance = c.minMoveDistance };
        void Step()
        {
            input.Sequence++; input.Tick++; state.SimulationTick++; state.SimulatedAt += Dt;
            int nativeStart = contacts.Count, rawStart = raw.Count;
            stepTouchesMantle = state.Movement == MovementMode.Mantle;
            copiedNativeContacts = 0; recorder.Begin(input.Sequence); motor.Step(ref state, input, Dt, state.SimulatedAt, false);
            stepTouchesMantle |= state.Movement == MovementMode.Mantle || raw.Skip(rawStart).Any(o =>
                o.Before.Movement == MovementMode.Mantle || o.After.Movement == MovementMode.Mantle);
            // Mantle completion can perform an untraced Move and then continue
            // into DestinationMove in one invocation. Conservatively unanchor
            // every native callback in such a step, including earlier callbacks.
            if (stepTouchesMantle)
            {
                for (int i = nativeStart; i < contacts.Count; i++)
                { contacts[i].sourceMotorEventId = 0; contacts[i].sourceBranch = "mantle-scope"; }
                if (mantleScopeSequences.Count < MaxSteps) mantleScopeSequences.Add(input.Sequence); else observationOverflow = true;
            }
            CopyNativeContacts(0, 0, stepTouchesMantle ? "mantle-scope" : "untraced-move");
        }
        void CopyNativeContacts(long sourceId, ulong invocation, string branch)
        {
            for (; copiedNativeContacts < recorder.Contacts.Count; copiedNativeContacts++)
            {
                if (contacts.Count >= ContactCap) { observationOverflow = true; copiedNativeContacts = recorder.Contacts.Count; break; }
                var c = recorder.Contacts[copiedNativeContacts];
                contacts.Add(new NativeContact { sourceMotorEventId = sourceId, invocation = invocation, sourceBranch = branch,
                    sequence = c.InputSequence, point = c.Point, normal = c.Normal,
                    moveDirection = c.MoveDirection, moveLength = c.MoveLength, colliderId = c.ColliderId,
                    layer = c.Layer, bounds = c.Bounds, block = c.BlockCollider });
            }
        }
        static BlockWorld World(int wallTop, int topY, int frontZ)
        {
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -8; x <= 8; x++) for (int y = 0; y < wallTop; y++) world.Set(new Vector3Int(x, y, 10), BlockCell.Create(1, true));
            for (int x = -8; x <= 8; x++) for (int z = frontZ; z <= 18; z++) world.Set(new Vector3Int(x, topY, z), BlockCell.Create(1, true));
            for (int x = -8; x <= 8; x += 8) for (int z = 15; z <= 18; z++) for (int y = 0; y < topY; y++)
                world.Set(new Vector3Int(x, y, z), BlockCell.Create(1, true));
            return world;
        }
        IEnumerator Publish(BlockWorld world)
        {
            uint revision = terrain.Revision + 1;
            world.Seal(revision, world.Chunks.Keys.Concat(terrain.World.Chunks.Keys).Distinct().ToArray());
            Assert.That(terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int i = 0; i < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); i++)
            { terrain.Tick(false, 10, Dt); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero); Assert.That(terrain.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
        static bool UpperLanding(D.MotorBranchObservation o, int topY) => o.Branch == D.MotorTraceBranch.GroundResolved &&
            o.ProbeSucceeded && o.Contact.IsBlock && o.Contact.Stable && o.Contact.Owner == 1 && o.Contact.Cell.y == topY;
        IEnumerable<(D.MotorBranchObservation o, long id)> Rows() => raw.Select((o, i) => (o, id: i + 1L));
        ChainEvidence FindUpperLanding(MotorDetailCapture.Snapshot sidecar, int topY, long minimumMoveId)
        {
            var rows = Rows().ToArray();
            foreach (var landing in rows.Where(x => UpperLanding(x.o, topY)))
            {
                var evaluation = rows.LastOrDefault(x => x.id < landing.id && x.o.Invocation == landing.o.Invocation &&
                    x.o.Branch == D.MotorTraceBranch.GroundEvaluation);
                var move = rows.LastOrDefault(x => x.id < evaluation.id && x.o.Invocation == landing.o.Invocation &&
                    x.o.Branch == D.MotorTraceBranch.DestinationMove);
                if (move.id < minimumMoveId || move.id == 0 || evaluation.id == 0 ||
                    evaluation.o.Before.Movement != MovementMode.Air || !evaluation.o.ProbeSucceeded ||
                    evaluation.o.Contact.Cell != landing.o.Contact.Cell || evaluation.o.Contact.Revision != terrain.Revision ||
                    (move.o.Collisions & CollisionFlags.Below) == 0 || !move.o.ControllerGrounded) continue;
                var group = sidecar.invocations.SingleOrDefault(g => g.invocation == landing.o.Invocation);
                if (group == null || !group.rows.Any(r => r.kind == D.MotorDetailKind.Move.ToString() &&
                    r.sourceMotorEventId == move.id && r.capsule.MoveExecuted)) continue;
                var query = group.rows.FirstOrDefault(r => r.kind == D.MotorDetailKind.GroundQuery.ToString() && r.sourceMotorEventId == evaluation.id);
                if (!query.ground.Evaluated || query.ground.Winner == D.GroundWinner.None) continue;
                int nativeIndex = contacts.FindIndex(c => c.sourceMotorEventId == move.id && c.invocation == move.o.Invocation &&
                    c.sequence == move.o.Input.Sequence && c.block && c.normal.y > .9f &&
                    Mathf.Abs(c.point.y - (topY + 1) * .1f) < .03f);
                if (nativeIndex < 0) continue;
                return new ChainEvidence { landingInvocation = landing.o.Invocation, landingMove = move.id,
                    groundEvaluation = evaluation.id, groundResolved = landing.id, nativeContactIndex = nativeIndex,
                    groundWinner = query.ground.Winner.ToString(), groundEvaluatedPaths = query.ground.EvaluatedPaths.ToString() };
            }
            return null;
        }
        ChainEvidence FindNaturalChain(MotorDetailCapture.Snapshot sidecar, int topY)
        {
            var rows = Rows().ToArray();
            foreach (var group in sidecar.invocations) foreach (var detach in group.rows.Where(r => r.kind == D.MotorDetailKind.WallDetach.ToString()))
            {
                var anchor = rows.FirstOrDefault(x => x.id == detach.anchorMotorEventId);
                if (anchor.id == 0 || anchor.o.Invocation != group.invocation || anchor.o.Before.Movement != MovementMode.WallInk) continue;
                var wallMove = rows.LastOrDefault(x => x.id < anchor.id && x.o.Branch == D.MotorTraceBranch.WallMove);
                var attach = rows.LastOrDefault(x => x.id < wallMove.id && x.o.Branch == D.MotorTraceBranch.WallAttachResolved && x.o.ProbeSucceeded);
                var air = rows.FirstOrDefault(x => x.id > anchor.id && x.o.Invocation == group.invocation &&
                    x.o.Branch == D.MotorTraceBranch.AirEntry && x.o.Before.PaperPose == PaperPose.Wall);
                var airMove = rows.FirstOrDefault(x => x.id > air.id && x.o.Invocation == group.invocation && x.o.Branch == D.MotorTraceBranch.DestinationMove);
                if (attach.id == 0 || wallMove.id == 0 || air.id == 0 || airMove.id == 0) continue;
                var chain = FindUpperLanding(sidecar, topY, airMove.id); if (chain == null) continue;
                if (rows.Any(x => x.id > air.id && x.id < chain.groundResolved &&
                    (x.o.Branch == D.MotorTraceBranch.WallMove || x.o.Branch == D.MotorTraceBranch.AirExit ||
                     x.o.Branch == D.MotorTraceBranch.WallAttachResolved && x.o.ProbeSucceeded))) continue;
                chain.wallAttach = attach.id; chain.wallMove = wallMove.id; chain.detachInvocation = group.invocation;
                chain.detachAnchor = anchor.id; chain.airEntry = air.id; chain.firstAirMove = airMove.id;
                chain.wallDetachReason = detach.wall.Reason.ToString(); return chain;
            }
            return null;
        }

        [UnityTest] public IEnumerator NaturalWallDetachAirThenPublishedUpperTopIsAnObservedChain()
        {
            int reached = 0, mantleScopeMisses = 0;
            foreach (int wallTop in new[] { 16, 17, 18 }) foreach (int front in new[] { 6, 7, 8 })
            {
                yield return Publish(World(wallTop, 22, front)); SeedAir(1.18f, .60f, .87f); BeginDetails();
                var initial = state; bool mantle = false;
                for (int i = 0; i < MaxSteps; i++)
                { Step(); if (stepTouchesMantle) { mantle = true; break; } if (raw.Any(o => UpperLanding(o, 22))) break; }
                var sidecar = capture.Finish(); StopDetails();
                var chain = mantle ? null : FindNaturalChain(sidecar, 22);
                if (mantle) mantleScopeMisses++; if (chain != null) reached++;
                AssertIntegrity(sidecar); Save("natural-wall-upper-top-" + wallTop + "-" + front, initial, sidecar, chain, mantle);
            }
            Assert.That(reached, Is.GreaterThan(0), "Fixture did not reach its claimed natural wall/air/upper-top chain; this is fixture setup failure, not evidence for a runtime fix. Mantle scope misses=" + mantleScopeMisses);
        }
        [UnityTest] public IEnumerator RestoredAuthorityAirUsesNativeMoveAndFreshPublishedUpperTopWithoutNewAirEntry()
        {
            int reached = 0;
            foreach (int front in new[] { 6, 7, 8 }) foreach (float z in new[] { .55f, .65f, .75f })
            {
                yield return Publish(World(0, 25, front)); SeedAir(2.42f, z, .8700001f); BeginDetails(); var initial = state; bool mantle = false;
                for (int i = 0; i < MaxSteps && !raw.Any(o => UpperLanding(o, 25)); i++)
                { Step(); if (stepTouchesMantle) { mantle = true; break; } }
                var sidecar = capture.Finish(); StopDetails();
                var chain = mantle || raw.Any(o => o.Branch == D.MotorTraceBranch.AirEntry) ? null : FindUpperLanding(sidecar, 25, 1);
                if (chain != null) reached++;
                AssertIntegrity(sidecar); Save("restored-air-upper-top-" + front + "-" + z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), initial, sidecar, chain, mantle);
            }
            Assert.That(reached, Is.GreaterThan(0), "Fixture did not reach its claimed restored-Air upper-top chain; it is not an exact network-event 722 replay.");
        }
        [Test] public void DetailsTogglePreservesAllLegacyBranchStateAndMovementFields()
        {
            SeedAir(4, 0, .87f); capture = null; for (int i = 0; i < 12; i++) Step();
            var before = raw.Select(SemanticRow).ToArray(); var endpoint = StateJson(state); int legacyErrors = D.MotorBranchObserverErrors;
            SeedAir(4, 0, .87f); BeginDetails(); for (int i = 0; i < 12; i++) Step(); var sidecar = capture.Finish(); StopDetails();
            CollectionAssert.AreEqual(before, raw.Select(SemanticRow).ToArray()); Assert.That(StateJson(state), Is.EqualTo(endpoint));
            Assert.That(D.MotorBranchObserverErrors, Is.EqualTo(legacyErrors)); AssertIntegrity(sidecar);
        }
        [Test] public void WholeInvocationCapLeavesEveryLegacySourceRowAndStepCompleteIntact()
        {
            SeedAir(4, 0, .87f); BeginDetails(new MotorDetailCapture(2, 4, 1, 8)); for (int i = 0; i < 8; i++) Step();
            var sidecar = capture.Finish(); StopDetails();
            Assert.That(raw.Count(o => o.Branch == D.MotorTraceBranch.StepComplete), Is.EqualTo(8));
            Assert.That(sidecar.retainedInvocations, Is.EqualTo(2)); Assert.That(sidecar.retainedRows, Is.EqualTo(4));
            Assert.That(sidecar.droppedInvocations, Is.EqualTo(6)); Assert.That(sidecar.complete, Is.False);
            Assert.That(sidecar.invocations.All(g => g.rows.Length == 2), Is.True); Assert.That(sidecar.consumerErrors, Is.Zero);
        }
        [Test] public void DetailObserverFailureIsSeparateAndDropsTheAffectedInvocation()
        {
            SeedAir(4, 0, .87f); int legacyErrors = D.MotorBranchObserverErrors;
            Action<D, D.MotorDetailObservation> poison = (_, _) => throw new InvalidOperationException("detail-observer-sentinel");
            // Register after the valid consumer to cover a later observer error too.
            BeginDetails(); D.MotorDetailObserved += poison;
            try
            {
                Step(); var sidecar = capture.Finish();
                Assert.That(sidecar.providerErrors, Is.GreaterThan(0)); Assert.That(sidecar.retainedInvocations, Is.Zero);
                Assert.That(sidecar.droppedInvocations, Is.EqualTo(1)); Assert.That(sidecar.incompleteInvocations, Is.EqualTo(1));
                Assert.That(D.MotorBranchObserverErrors, Is.EqualTo(legacyErrors));
                Assert.That(raw.Count(o => o.Branch == D.MotorTraceBranch.StepComplete), Is.EqualTo(1));
            }
            finally { D.MotorDetailObserved -= poison; StopDetails(); }
        }
        [Test] public void LaterFinalizationObserverFailureDropsTheWholeInvocation()
        {
            SeedAir(4, 0, .87f); int legacyErrors = D.MotorBranchObserverErrors;
            Action<D, D.MotorDetailObservation> poison = (_, _) => throw new InvalidOperationException("finalizer-observer-sentinel");
            BeginDetails(); D.MotorDetailInvocationFinished += poison;
            try
            {
                Step(); var sidecar = capture.Finish();
                Assert.That(sidecar.providerErrors, Is.GreaterThan(0)); Assert.That(sidecar.retainedInvocations, Is.Zero);
                Assert.That(sidecar.droppedInvocations, Is.EqualTo(1)); Assert.That(sidecar.incompleteInvocations, Is.EqualTo(1));
                Assert.That(sidecar.complete, Is.False); Assert.That(D.MotorBranchObserverErrors, Is.EqualTo(legacyErrors));
                Assert.That(raw.Count(o => o.Branch == D.MotorTraceBranch.StepComplete), Is.EqualTo(1));
            }
            finally { D.MotorDetailInvocationFinished -= poison; StopDetails(); }
        }
        void AssertIntegrity(MotorDetailCapture.Snapshot sidecar)
        {
            Assert.That(observationOverflow, Is.False); Assert.That(D.MotorBranchObserverErrors, Is.EqualTo(legacyProviderErrorsBefore));
            Assert.That(sidecar.complete, Is.True); Assert.That(sidecar.consumerErrors, Is.Zero); Assert.That(sidecar.providerErrors, Is.Zero);
            var byId = raw.Select((o, i) => (o, id: i + 1L)).ToDictionary(x => x.id, x => x.o);
            foreach (var group in sidecar.invocations)
            {
                Assert.That(raw.Count(o => o.Invocation == group.invocation && o.Branch == D.MotorTraceBranch.StepComplete), Is.EqualTo(1));
                foreach (var row in group.rows)
                {
                    if (row.kind == D.MotorDetailKind.WallDetach.ToString())
                    {
                        Assert.That(row.sourceMotorEventId, Is.Zero); Assert.That(byId.TryGetValue(row.anchorMotorEventId, out var anchor), Is.True);
                        Assert.That(anchor.Invocation, Is.EqualTo(group.invocation));
                        Assert.That(row.wall.BeforeMovement, Is.EqualTo(MovementMode.WallInk)); Assert.That(row.wall.AfterMovement, Is.EqualTo(MovementMode.Air)); continue;
                    }
                    Assert.That(byId.TryGetValue(row.sourceMotorEventId, out var original), Is.True);
                    Assert.That(original.Invocation, Is.EqualTo(group.invocation)); Assert.That(original.Branch.ToString(), Is.EqualTo(row.sourceBranch));
                    if (row.kind == D.MotorDetailKind.GroundQuery.ToString())
                    {
                        Assert.That(row.ground.Evaluated, Is.True); Assert.That(row.ground.Feet, Is.EqualTo(original.Before.Position));
                        Assert.That(row.ground.Winner != D.GroundWinner.None, Is.EqualTo(original.ProbeSucceeded));
                    }
                    if (row.kind == D.MotorDetailKind.Move.ToString() && row.capsule.MoveExecuted)
                    {
                        Assert.That(row.capsule.Height, Is.GreaterThan(0)); Assert.That(row.capsule.Radius, Is.GreaterThan(0));
                        Assert.That(row.capsule.StepOffset, Is.InRange(0, row.capsule.Height));
                        if (original.Branch != D.MotorTraceBranch.WallAttachEvaluation)
                            Assert.That(original.ControllerPosition, Is.EqualTo(original.After.Position));
                    }
                }
            }
            foreach (var contact in contacts.Where(c => c.sourceMotorEventId > 0))
            {
                Assert.That(byId.TryGetValue(contact.sourceMotorEventId, out var source), Is.True);
                Assert.That(contact.invocation, Is.EqualTo(source.Invocation)); Assert.That(contact.sequence, Is.EqualTo(source.Input.Sequence));
                Assert.That(contact.sourceBranch, Is.EqualTo(source.Branch.ToString()));
                Assert.That(sidecar.invocations.Single(g => g.invocation == source.Invocation).rows.Any(r =>
                    r.sourceMotorEventId == contact.sourceMotorEventId && r.kind == D.MotorDetailKind.Move.ToString() && r.capsule.MoveExecuted), Is.True);
            }
            foreach (var o in raw)
            {
                float rootY = o.After.Position.y - o.Before.Position.y;
                float humanY = D.HumanPosition(o.After).y - D.HumanPosition(o.Before).y;
                Assert.That(rootY - humanY, Is.EqualTo(o.After.AirHumanOffset - o.Before.AirHumanOffset).Within(.0001f));
                if (o.Branch == D.MotorTraceBranch.GroundResolved && !o.ProbeSucceeded)
                    Assert.That(o.After.AirHumanOffset, Is.EqualTo(o.Before.AirHumanOffset));
            }
        }
        // Invoke the production DTO mapper without widening its internal constructor.
        static BlockNetworkProbe.MotorStateSample CaptureState(PlayerSnapshot s) =>
            (BlockNetworkProbe.MotorStateSample)Activator.CreateInstance(typeof(BlockNetworkProbe.MotorStateSample),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { s }, null);
        static string StateJson(PlayerSnapshot s) => JsonUtility.ToJson(CaptureState(s));
        static BlockNetworkProbe.MotorBranchEventSample FullRow(D.MotorBranchObservation o, long id)
        {
            var c = o.Contact;
            return new BlockNetworkProbe.MotorBranchEventSample { eventId = id, invocation = o.Invocation, owner = o.Owner,
                networkObjectId = o.NetworkObjectId, playerObjectId = o.PlayerObjectId, connectionGeneration = 0,
                terrainRound = o.TerrainRound, terrainRevision = o.TerrainRevision, simulationTick = o.SimulationTick, life = o.Life,
                stage = "fixture", phase = "PhysX", branch = o.Branch.ToString(), before = CaptureState(o.Before),
                after = CaptureState(o.After), input = o.Input, identityAvailable = o.IdentityAvailable,
                probeEvaluated = o.ProbeEvaluated, probeSucceeded = o.ProbeSucceeded, controllerGrounded = o.ControllerGrounded,
                standingEvaluated = o.StandingEvaluated, standingFits = o.StandingFits, wantsFire = o.WantsFire, shootingMovement = o.ShootingMovement,
                collisions = (int)o.Collisions, requestedMove = o.RequestedMove, controllerPosition = o.ControllerPosition,
                contactPoint = c.Point, contactNormal = c.Normal, contactCell = c.Cell, contactOwner = c.Owner, contactRevision = c.Revision,
                contactIsBlock = c.IsBlock, contactStable = c.Stable, contactClimbable = c.Climbable, contactSurfaceId = c.SurfaceId,
                contactRegionId = c.RegionId, rebaseHeight = o.RebaseHeight, simulatedAt = o.SimulatedAt, realtime = o.Realtime, terrainTime = o.TerrainTime };
        }
        static string SemanticRow(D.MotorBranchObservation o)
        {
            var full = FullRow(o, 0); full.invocation = 0; full.realtime = 0;
            return JsonUtility.ToJson(full);
        }
        [Serializable] sealed class NativeContact
        {
            public long sourceMotorEventId; public ulong invocation; public string sourceBranch;
            public uint sequence; public Vector3 point, normal, moveDirection; public float moveLength; public int colliderId, layer; public Bounds bounds; public bool block;
        }
        [Serializable] sealed class ChainEvidence
        {
            public long wallAttach, wallMove, detachAnchor, airEntry, firstAirMove, landingMove, groundEvaluation, groundResolved;
            public ulong detachInvocation, landingInvocation;
            public int nativeContactIndex;
            public string wallDetachReason, groundWinner, groundEvaluatedPaths;
        }
        [Serializable] sealed class Evidence
        {
            public string label, scope = "synthetic published geometry, production prefab capsule settings/config and real motor; not exact 488/722 map or Player/LAN acceptance";
            public string prefab = PrefabPath, publishedSnapshotSha256;
            public bool reachedChain, mantleScopeMiss;
            public ChainEvidence observedChain;
            public int unanchoredNativeContacts;
            public uint[] mantleScopeSequences;
            public int maxSteps = MaxSteps, rawCap = RawCap, contactCap = ContactCap;
            public BlockNetworkProbe.MotorStateSample initial, final;
            public D.MoveCapsuleDetails prefabCapsuleSettings, restoredInitialCapsuleSettings;
            public BlockNetworkProbe.MotorBranchEventSample[] fullMotorRows;
            public NativeContact[] nativeContacts;
            public MotorDetailCapture.Snapshot sidecar;
        }
        void Save(string label, PlayerSnapshot initial, MotorDetailCapture.Snapshot sidecar, ChainEvidence chain, bool mantle)
        {
            string digest; using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(terrain.World.EncodeSnapshot())).Replace("-", "").ToLowerInvariant();
            var evidence = new Evidence { label = label, initial = CaptureState(initial), final = CaptureState(state),
                reachedChain = chain != null, observedChain = chain, unanchoredNativeContacts = contacts.Count(c => c.sourceMotorEventId == 0),
                mantleScopeSequences = this.mantleScopeSequences.ToArray(),
                mantleScopeMiss = mantle, publishedSnapshotSha256 = digest,
                prefabCapsuleSettings = copiedProductionCapsule, restoredInitialCapsuleSettings = restoredInitialCapsule,
                fullMotorRows = raw.Select((o, i) => FullRow(o, i + 1)).ToArray(), nativeContacts = contacts.ToArray(), sidecar = sidecar };
            string folder = Path.GetFullPath("Reports/BlockTerrain/PredictionAudit/PendingUpperTopMotorObservation/FixtureRuns"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + label + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(evidence, true)); TestContext.Progress.WriteLine(path);
        }
    }
}
#endif
