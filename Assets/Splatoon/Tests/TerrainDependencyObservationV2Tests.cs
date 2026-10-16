#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
using Object = UnityEngine.Object;
using R = Splatoon.Tests.TerrainDependencyFixtureReflection;

namespace Splatoon.Tests
{
    public sealed class TerrainDependencyObservationV2Tests
    {
        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            // Finish the real bootstrap before the historical fixture releases weapon handles.
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((PrototypeApp.Current == null || !PrototypeApp.Current.Ready) &&
                   Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (PrototypeApp.Current != null && !string.IsNullOrEmpty(PrototypeApp.Current.Error)) break;
                yield return null;
            }
            Assert.That(PrototypeApp.Current, Is.Not.Null, "Real application bootstrap must exist before the terrain dependency fixture.");
            Assert.That(PrototypeApp.Current.Ready, Is.True, PrototypeApp.Current.Error);
            Assert.That(LubanConfigService.Current.IsReady, Is.True);
            Assert.That(LubanConfigService.Current.ContentSignature, Has.Length.EqualTo(32));
            // Dispose the completed bootstrap before installing fixture-owned snapshots.
            // Its OnDestroy resets Luban/weapon state and destroys its own NGO manager.
            var bootstrapSignature = (byte[])LubanConfigService.Current.ContentSignature.Clone();
            var bootstrap = PrototypeApp.Current;
            var bootstrapManager = bootstrap.Manager;
            Assert.That(bootstrapManager, Is.Not.Null, "Completed bootstrap must own its network manager.");
            Assert.That(Unity.Netcode.NetworkManager.Singleton, Is.SameAs(bootstrapManager));
            Assert.That(bootstrap.InRoom, Is.False, "Fixture isolation must not dispose a live room.");
            Assert.That(bootstrapManager.IsListening, Is.False, "Fixture isolation requires an idle bootstrap manager.");
            Object.Destroy(bootstrap.gameObject);
            deadline = Time.realtimeSinceStartupAsDouble + 45;
            while ((bootstrap != null || bootstrapManager != null || Unity.Netcode.NetworkManager.Singleton != null) &&
                   Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(bootstrap == null, Is.True, "Bootstrap disposal must finish before fixture configuration.");
            Assert.That(bootstrapManager == null, Is.True, "Bootstrap-owned manager disposal must finish before the rig.");
            Assert.That(PrototypeApp.Current, Is.Null);
            Assert.That(Unity.Netcode.NetworkManager.Singleton, Is.Null, "The rig must create its own NGO manager.");
            // Restore signed configuration through the real service after its production reset.
            var configReady = LubanConfigService.Current.InitializeAsync(default).GetAwaiter();
            while (!configReady.IsCompleted) yield return null;
            configReady.GetResult();
            Assert.That(LubanConfigService.Current.IsReady, Is.True);
            Assert.That(LubanConfigService.Current.ContentSignature, Is.EqualTo(bootstrapSignature));
            HeroMigrationTests.Load();
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            PaperBodyTests.ReleaseTestBodies();
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear(); LubanConfigService.Current.Reset();
            yield return new ExitPlayMode(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static BlockWorld World(uint revision, params (Vector3Int cell, byte owner)[] cells)
        {
            var world = new BlockWorld(new Vector2(32, 64), 4);
            foreach (var cell in cells) world.Set(cell.cell, BlockCell.Create(cell.owner, true));
            world.Seal(revision, world.Chunks.Keys.ToArray()); return world;
        }
        static BlockWorld MultipartWorld()
        {
            var cells = new List<(Vector3Int, byte)>();
            for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) cells.Add((new Vector3Int(x, 1, z), 1));
            return World(1, cells.ToArray());
        }
        static long Install(object token) => R.Read<long>(token, "InstallId");
        static long Pending(object token) => R.Read<long>(token, "PendingId");
        static uint Transfer(object token) => R.Read<uint>(R.Read(token, "Transfer"), "Id");
        static string Phase(object observation) => R.Read<string>(observation, "Phase");
        static object SinglePhase(List<object> observations, string phase, uint transfer)
        {
            var matches = observations.Where(value => Phase(value) == phase &&
                R.Read<uint>(value, "Id") == transfer).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1), "Expected one real " + phase + " for transfer " + transfer);
            return matches[0];
        }
        static void ExactToken(object expected, object actual)
        {
            Assert.That(R.Read<bool>(actual, "AcceptedAvailable"), Is.True);
            Assert.That(R.Read<bool>(actual, "PublishedAvailable"), Is.True);
            Assert.That(R.Read<int>(actual, "TerrainObjectId"), Is.EqualTo(R.Read<int>(expected, "TerrainObjectId")));
            Assert.That(Install(actual), Is.EqualTo(Install(expected)));
            Assert.That(Pending(actual), Is.GreaterThan(0)); Assert.That(Transfer(actual), Is.EqualTo(Transfer(expected)));
            Assert.That(R.Read<long>(actual, "TerminalStamp"), Is.GreaterThan(0));
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SameRevisionQueuedFullDoesNotBorrowTheOldPendingCallbackOrAppliedAck()
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            var cell = new Vector3Int(5, 1, 5); var extra = new Vector3Int(6, 1, 5);
            var one = World(10, (cell, (byte)1));
            var two = World(10, (cell, (byte)2), (extra, (byte)1));
            var acceptedOne = rig.ApplyScoped(one.EncodeSnapshot(), 41, 10);
            Assert.That(R.Read<bool>(acceptedOne, "AcceptedAvailable"), Is.True);
            rig.Tick();
            Assert.That(R.Read(rig.Terrain, "_pending"), Is.Not.Null, "I1 must really be pending before I2 enters");
            Assert.That(rig.Terrain.Revision, Is.Zero);
            rig.ReceiveAll(two.EncodeSnapshot(), 42, 10, true);
            Assert.That(rig.Incoming, Is.Not.Null, "The original ClientRpc body must actually create assembly I2");
            Assert.That(rig.Incoming.Manifest.Id, Is.EqualTo(42)); Assert.That(rig.Incoming.Complete, Is.True);
            Assert.That(R.Read<bool>(rig.Match, "_blockInstalling"), Is.True);
            Assert.That(R.Read(rig.Terrain, "_remoteQueued"), Is.Not.Null, "I2 must be accepted behind I1, not silently rejected");
            var acceptedTwo = R.Read(rig.Match, "_terrainIncomingAccepted");
            Assert.That(Install(acceptedTwo), Is.GreaterThan(0)); Assert.That(Install(acceptedTwo), Is.Not.EqualTo(Install(acceptedOne)));
            Assert.That(Transfer(acceptedTwo), Is.EqualTo(42));
            int firstPublicationTick = -1;
            for (int tick = 0; tick < 1000 && !rig.TransferObservations.Any(row => Phase(row) == "PublishedCommitCallback"); tick++)
            { rig.Tick(); firstPublicationTick = tick; yield return null; }
            Assert.That(firstPublicationTick, Is.GreaterThanOrEqualTo(0));
            var oldCallback = rig.TransferObservations.First(row => Phase(row) == "PublishedCommitCallback");
            Assert.That(R.Read<bool>(oldCallback, "OriginalManifestBranchMatched"), Is.True,
                "The original round/revision branch still runs, even though I1 and I2 differ");
            Assert.That(R.Read<string>(oldCallback, "AssociationAvailability"), Is.EqualTo("Mismatch"));
            Assert.That(Install(R.Read(oldCallback, "ActualPublication")), Is.EqualTo(Install(acceptedOne)));
            Assert.That(Install(R.Read(oldCallback, "ExpectedInstall")), Is.EqualTo(Install(acceptedTwo)));
            Assert.That(R.Read<bool>(R.Read(oldCallback, "AssociatedPublication"), "PublishedAvailable"), Is.False,
                "No I2 terminal can be fabricated from I1's same-revision callback");
            Assert.That(R.Read<bool>(rig.Match, "_blockInstallPublished"), Is.True,
                "The original gameplay flag is intentionally unchanged");
            Assert.That(rig.Terrain.PendingTransactions, Is.GreaterThan(0));
            rig.PollOriginalAck();
            Assert.That(R.Read<uint>(rig.Match, "_blockCompletedId"), Is.Zero,
                "The original pending queue must still prevent an early applied ACK");
            Assert.That(rig.TransferObservations.Any(row => Phase(row) == "AppliedAckCallReturned"), Is.False);

            for (int tick = 0; tick < 1000 && rig.Terrain.PendingTransactions != 0; tick++) { rig.Tick(); yield return null; }
            Assert.That(rig.Terrain.PendingTransactions, Is.Zero);
            var callbacks = rig.TransferObservations.Where(row => Phase(row) == "PublishedCommitCallback").ToArray();
            Assert.That(callbacks.Length, Is.EqualTo(2));
            var exact = callbacks[1]; Assert.That(R.Read<string>(exact, "AssociationAvailability"), Is.EqualTo("Exact"));
            ExactToken(acceptedTwo, R.Read(exact, "ActualPublication"));
            ExactToken(acceptedTwo, R.Read(exact, "AssociatedPublication"));
            var stageTwo = rig.InstallObservations.Single(row => Phase(row) == "Staged" && R.Read<long>(row, "InstallId") == Install(acceptedTwo));
            var publishedTwo = rig.InstallObservations.Single(row => Phase(row) == "Published" && R.Read<long>(row, "InstallId") == Install(acceptedTwo));
            Assert.That(R.Read<long>(stageTwo, "PendingId"), Is.EqualTo(Pending(R.Read(exact, "AssociatedPublication"))));
            Assert.That(R.Read<long>(stageTwo, "Stamp"), Is.LessThanOrEqualTo(R.Read<long>(publishedTwo, "Stamp")));
            Assert.That(R.Read<long>(publishedTwo, "Stamp"), Is.LessThanOrEqualTo(R.Read<long>(exact, "Stamp")));
            Assert.That(rig.Terrain.World.TryGet(cell, out var live), Is.True); Assert.That(live.Owner, Is.EqualTo(2));
            Assert.That(rig.Terrain.PinkCount, Is.EqualTo(1)); Assert.That(rig.Terrain.BlueCount, Is.EqualTo(1));
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                out var hit, .6f, 1 << BlockTerrain.TerrainLayer), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
            Assert.That(rig.Terrain.TryContact(hit, out var contact), Is.True); Assert.That(contact.Owner, Is.EqualTo(2));
            rig.PollOriginalAck();
            Assert.That(R.Read<uint>(rig.Match, "_blockCompletedId"), Is.EqualTo(42)); Assert.That(rig.Incoming, Is.Null);
            Assert.That(rig.Match.InitialSyncComplete, Is.True);
            var ack = SinglePhase(rig.TransferObservations, "AppliedAckCallReturned", 42);
            Assert.That(R.Read<string>(ack, "AssociationAvailability"), Is.EqualTo("Exact"));
            ExactToken(acceptedTwo, R.Read(ack, "AssociatedPublication"));
            Assert.That(R.Read<long>(ack, "Stamp"), Is.GreaterThanOrEqualTo(R.Read<long>(publishedTwo, "Stamp")));
            rig.Receive(TerrainDependencyObservationV2FixtureRig.Part(two.EncodeSnapshot(), 4, 42, 0, 10, true, 0));
            var duplicateAck = SinglePhase(rig.TransferObservations, "CompletedDuplicateAckCallReturned", 42);
            ExactToken(acceptedTwo, R.Read(duplicateAck, "AssociatedPublication"));
            var report = rig.FinishSidecar();
            Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero); Assert.That(R.Read<int>(report, "openTransfers"), Is.Zero);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ARealPartialDeltaAssemblyAtFinishCannotReportCompleteCapture()
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            var world = MultipartWorld();
            var bytes = world.EncodeDelta(0); Assert.That(bytes.Length, Is.GreaterThan(BlockTransferFragment.PayloadBytes));
            var first = TerrainDependencyObservationV2FixtureRig.Part(bytes, 4, 51, 0, 1, false, 0);
            Assert.That(rig.Match.InitialSyncComplete, Is.True);
            rig.Receive(first);
            Assert.That(rig.Incoming, Is.Not.Null, "RPC receive body must actually run");
            Assert.That(rig.Incoming.ReceivedCount, Is.EqualTo(1)); Assert.That(rig.Incoming.Complete, Is.False);
            Assert.That(rig.Terrain.PendingTransactions, Is.Zero, "No accepted install exists yet");
            Assert.That(rig.TransferObservations.Count(row => Phase(row) == "AssemblyCreated"), Is.EqualTo(1));
            Assert.That(rig.TransferObservations.Count(row => Phase(row) == "VerifyReturned"), Is.Zero);
            var report = rig.FinishSidecar();
            Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero);
            Assert.That(R.Read<int>(report, "openTransfers"), Is.GreaterThan(0));
            Assert.That(R.Rows(report, "endOpen").Count, Is.GreaterThan(0));
            Assert.That(R.Read<bool>(report, "captureComplete"), Is.False);
            var lifecycle = R.Rows(report, "transferLifecycles").Cast<object>().Single(row => R.Read<uint>(row, "id") == 51);
            Assert.That(R.Read<uint>(lifecycle, "round"), Is.EqualTo(first.Round));
            Assert.That(R.Read<uint>(lifecycle, "baseRevision"), Is.EqualTo(first.BaseRevision));
            Assert.That(R.Read<uint>(lifecycle, "revision"), Is.EqualTo(first.Revision));
            Assert.That(R.Read<uint>(lifecycle, "hash"), Is.EqualTo(first.Hash));
            Assert.That(R.Read<int>(lifecycle, "length"), Is.EqualTo(first.Length));
            Assert.That(R.Read<int>(lifecycle, "fragmentCount"), Is.EqualTo(first.Count));
            Assert.That(R.Read<bool>(lifecycle, "full"), Is.EqualTo(first.Full));
            var endOpen = R.Rows(report, "endOpen").Cast<object>().Single(row =>
                R.Read<string>(row, "key") == R.Read<string>(lifecycle, "key"));
            Assert.That(R.Read<bool>(endOpen, "incomingPresent"), Is.True);
            Assert.That(R.Read<int>(endOpen, "receivedCount"), Is.EqualTo(1));
            Assert.That(rig.Incoming.ReceivedCount, Is.EqualTo(1), "Finish does not Add/Verify/decode the payload");
            yield return null;
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ChecksumRejectedAndCodecRejectedTransfersCloseWithoutInventingAnInstall()
        {
            foreach (bool badChecksum in new[] { true, false })
            {
                using var rig = new TerrainDependencyObservationV2FixtureRig();
                byte[] bytes = badChecksum ? World(1, (new Vector3Int(5, 1, 5), (byte)1)).EncodeSnapshot() : new byte[1801];
                rig.ReceiveAll(bytes, badChecksum ? 61u : 62u, 1, true, corrupt: badChecksum);
                Assert.That(rig.Incoming, Is.Null); Assert.That(rig.Match.InitialSyncComplete, Is.False);
                Assert.That(rig.Terrain.PendingTransactions, Is.Zero);
                Assert.That(rig.InstallObservations.Any(row => Phase(row) == "Queued"), Is.False);
                var phase = badChecksum ? "VerifyReturned" : "ApplyReturned";
                var failed = SinglePhase(rig.TransferObservations, phase, badChecksum ? 61u : 62u);
                Assert.That(R.Read<bool>(failed, "ResultKnown"), Is.True); Assert.That(R.Read<bool>(failed, "Result"), Is.False);
                var report = rig.FinishSidecar();
                Assert.That(R.Read<int>(report, "openTransfers"), Is.Zero); Assert.That(R.Rows(report, "endOpen").Count, Is.Zero);
                Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero);
                yield return null;
            }
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SupersededAndResetPartialAssembliesHaveExplicitClosingDispositions()
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            var bytes = MultipartWorld().EncodeSnapshot();
            Assert.That(bytes.Length, Is.GreaterThan(BlockTransferFragment.PayloadBytes));
            rig.Receive(TerrainDependencyObservationV2FixtureRig.Part(bytes, 4, 71, 0, 1, true, 0));
            rig.Receive(TerrainDependencyObservationV2FixtureRig.Part(bytes, 4, 72, 0, 1, true, 0));
            Assert.That(rig.Incoming.Manifest.Id, Is.EqualTo(72));
            Assert.That(rig.TransferObservations.Any(row => Phase(row) == "Abandoned" && R.Read<uint>(row, "Id") == 71), Is.True);
            rig.ResetOriginalSync(); Assert.That(rig.Incoming, Is.Null);
            Assert.That(rig.TransferObservations.Any(row => Phase(row) == "Abandoned" && R.Read<uint>(row, "Id") == 72), Is.True);
            var report = rig.FinishSidecar();
            Assert.That(R.Read<int>(report, "openTransfers"), Is.Zero); Assert.That(R.Rows(report, "endOpen").Count, Is.Zero);
            Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero); yield return null;
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator FailedMainConsumerOnSameFrameAndSameAckCannotBorrowThePreviousAppendToken()
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            var player = go.GetComponent<PrototypePlayer>(); player.enabled = false;
            var controller = player.GetComponent<CharacterController>(); var motor = new PlayerMotorSimulation(controller);
            R.Write(player, "_motor", motor); R.Write(player, "_controller", controller);
            var initial = new PlayerSnapshot { HeroId = 1, Revision = 7, Team = 1, Health = 100, Ink = 100,
                Position = new Vector3(10, 1, 10), Movement = MovementMode.Human, AcknowledgedInput = 30, SimulatedAt = 2 };
            motor.Restore(initial, false); R.Write(player, "_predicted", initial);
            var observed = new List<object>();
            using var capture = new TerrainDependencyFixtureSubscription(typeof(PrototypePlayer), "ReconcileObserved",
                (sender, value) => { if (ReferenceEquals(sender, player)) observed.Add(value); });
            var motion = R.Read(rig.Probe, "_reconcileMotion");
            try
            {
                int frame = Time.frameCount;
                var first = initial; first.AcknowledgedInput = 31; first.Position += Vector3.right * .2f;
                R.Call(player, "ReconcilePrediction", first, false, true, (uint)0, MatchPhase.Practice);
                Assert.That(R.Rows(rig.MainReport, "reconcileEvents").Count, Is.EqualTo(1));
                var firstRow = R.Rows(rig.MainReport, "reconcileEvents")[0]; long firstId = R.Read<long>(firstRow, "eventId");
                int errors = R.Read<int>(rig.MainReport, "errors"); int consumerErrors = R.Read<int>(rig.MainReport, "reconcileConsumerErrors");
                R.Write(rig.Probe, "_reconcileMotion", null); // Actual original consumer throws before append and runs its original catch.
                var second = first; second.Position += Vector3.forward * .3f;
                R.Call(player, "ReconcilePrediction", second, false, true, (uint)0, MatchPhase.Practice);
                R.Write(rig.Probe, "_reconcileMotion", motion);
                Assert.That(Time.frameCount, Is.EqualTo(frame)); Assert.That(observed.Count, Is.EqualTo(2));
                Assert.That(R.Read<uint>(R.Read(observed[0], "Authority"), "AcknowledgedInput"), Is.EqualTo(31));
                Assert.That(R.Read<uint>(R.Read(observed[1], "Authority"), "AcknowledgedInput"), Is.EqualTo(31));
                Assert.That(R.Read<long>(observed[1], "ReconcileInvocation"), Is.Not.EqualTo(R.Read<long>(observed[0], "ReconcileInvocation")));
                Assert.That(R.Rows(rig.MainReport, "reconcileEvents").Count, Is.EqualTo(1));
                Assert.That(R.Read<long>(R.Rows(rig.MainReport, "reconcileEvents")[0], "eventId"), Is.EqualTo(firstId));
                Assert.That(R.Read<int>(rig.MainReport, "errors"), Is.EqualTo(errors + 1));
                Assert.That(R.Read<int>(rig.MainReport, "reconcileConsumerErrors"), Is.EqualTo(consumerErrors + 1));
                Assert.That(R.Read<bool>(rig.MainReport, "reconcileEventsComplete"), Is.False);
                var rows = R.Rows(rig.TraceReport, "reconcileAssociations"); Assert.That(rows.Count, Is.EqualTo(2));
                Assert.That(R.Read<bool>(rows[0], "sourceEventAvailable"), Is.True);
                Assert.That(R.Read<long>(rows[0], "sourceEventId"), Is.EqualTo(firstId));
                Assert.That(R.Read<bool>(rows[1], "sourceEventAvailable"), Is.False,
                    "Same-frame, same-ACK old last row cannot fabricate a new successful main append");
                Assert.That(R.Read<long>(rows[1], "sourceEventId"), Is.Not.EqualTo(firstId));
                Assert.That(player.LastCorrectionDistance, Is.EqualTo(.3f).Within(.00001));
                var report = rig.FinishSidecar(false); Assert.That(R.Read<bool>(report, "captureComplete"), Is.False);
                Assert.That(R.Read<int>(report, "sourceReconcileErrors"), Is.EqualTo(consumerErrors + 1));
            }
            finally { R.Write(rig.Probe, "_reconcileMotion", motion); Object.DestroyImmediate(go); }
            yield return null;
        }
    }
}
#endif
