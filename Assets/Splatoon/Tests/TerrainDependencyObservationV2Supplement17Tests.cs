#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using R = Splatoon.Tests.TerrainDependencyFixtureReflection;

namespace Splatoon.Tests
{
    // Supplements the frozen V2 fixture; no new production seam or replacement pipeline.
    public sealed class TerrainDependencyObservationV2Supplement17Tests
    {
        const float Dt = 1f / 60;

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
            foreach (var row in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                SubWeaponConfigService.Current.SetById(row.Id, AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath));
            foreach (var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
                SpecialWeaponConfigService.Current.Set(row.Id, AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath));
        }

        [UnityTearDown] public IEnumerator Teardown()
        {
            PaperBodyTests.ReleaseTestBodies();
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear(); LubanConfigService.Current.Reset();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static BlockWorld Floor(uint revision, uint round = 4)
        {
            var world = new BlockWorld(new Vector2(32, 64), round);
            for (int z = -12; z <= 12; z++) for (int x = -12; x <= 12; x++)
                world.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
            world.Seal(revision, world.Chunks.Keys.ToArray()); return world;
        }

        static BlockWorld Single(uint revision, Vector3Int cell, byte owner, uint round = 4)
        {
            var world = new BlockWorld(new Vector2(32, 64), round);
            world.Set(cell, BlockCell.Create(owner, true));
            world.Seal(revision, world.Chunks.Keys.ToArray()); return world;
        }

        static BlockWorld MultipartNext(uint round)
        {
            var world = new BlockWorld(new Vector2(32, 64), round);
            for (int z = 0; z < 16; z++) for (int x = 64; x < 80; x++)
                world.Set(new Vector3Int(x, 1, z), BlockCell.Create(2, true));
            world.Seal(2, world.Chunks.Keys.ToArray()); return world;
        }

        static string Phase(object row) => R.Read<string>(row, "Phase");
        static object Identity(object row) => R.Read(row, "identity");
        static object Publication(object report, uint transfer, string phase)
            => R.Rows(report, "publications").Cast<object>().Single(row =>
                R.Read<uint>(row, "transferId") == transfer && R.Read<string>(row, "phase") == phase);
        static object Lifecycle(object report, uint transfer)
            => R.Rows(report, "transferLifecycles").Cast<object>().Single(row => R.Read<uint>(row, "id") == transfer);

        static void AssertPublishedToken(object accepted, object published)
        {
            Assert.That(R.Read<bool>(accepted, "AcceptedAvailable"), Is.True);
            Assert.That(R.Read<bool>(published, "PublishedAvailable"), Is.True);
            Assert.That(R.Read<int>(published, "TerrainObjectId"), Is.EqualTo(R.Read<int>(accepted, "TerrainObjectId")));
            Assert.That(R.Read<long>(published, "InstallId"), Is.EqualTo(R.Read<long>(accepted, "InstallId")));
            Assert.That(R.Read<long>(published, "PendingId"), Is.GreaterThan(0));
            Assert.That(R.Read<long>(published, "TerminalStamp"), Is.GreaterThan(0));
            var expected = R.Read(accepted, "Transfer"); var actual = R.Read(published, "Transfer");
            foreach (string field in new[] { "MatchObjectId", "Id", "Round", "BaseRevision", "Revision", "Hash", "Length", "FragmentCount", "Full" })
                Assert.That(R.Read(actual, field), Is.EqualTo(R.Read(expected, field)), "Full scalar token field: " + field);
        }

        static (long Id, long Created) CaptureActualPending(BlockTerrain terrain, object accepted)
        {
            var pending = R.Read(terrain, "_pending"); Assert.That(pending, Is.Not.Null);
            Assert.That(R.Read<long>(pending, "TraceInstallId"), Is.EqualTo(R.Read<long>(accepted, "InstallId")));
            var expected = R.Read(accepted, "Transfer"); var actual = R.Read(pending, "TraceContext");
            foreach (string field in new[] { "MatchObjectId", "Id", "Round", "BaseRevision", "Revision", "Hash", "Length", "FragmentCount", "Full", "Available" })
                Assert.That(R.Read(actual, field), Is.EqualTo(R.Read(expected, field)), "Actual Pending context: " + field);
            var next = R.Read<BlockWorld>(pending, "Next");
            Assert.That(next.Round, Is.EqualTo(R.Read<uint>(expected, "Round")));
            Assert.That(next.Revision, Is.EqualTo(R.Read<uint>(expected, "Revision")));
            long id = R.Read<long>(pending, "WallId"), created = R.Read<long>(pending, "CreatedWallStamp");
            Assert.That(id, Is.GreaterThan(0)); Assert.That(created, Is.GreaterThan(0)); return (id, created);
        }

        static void AssertSamePublishedToken(object expected, object actual)
        {
            foreach (string field in new[] { "TerrainObjectId", "InstallId", "PendingId", "TerminalStamp", "AcceptedAvailable", "PublishedAvailable" })
                Assert.That(R.Read(actual, field), Is.EqualTo(R.Read(expected, field)), "Exact published token: " + field);
            var expectedTransfer = R.Read(expected, "Transfer"); var actualTransfer = R.Read(actual, "Transfer");
            foreach (string field in new[] { "MatchObjectId", "Id", "Round", "BaseRevision", "Revision", "Hash", "Length", "FragmentCount", "Full", "Available" })
                Assert.That(R.Read(actualTransfer, field), Is.EqualTo(R.Read(expectedTransfer, field)), "Exact published manifest: " + field);
        }

        static void AssertActualInstallationChain(object accepted, object published, List<object> observations,
            (long Id, long Created) pending, object report)
        {
            AssertPublishedToken(accepted, published);
            long install = R.Read<long>(accepted, "InstallId");
            var staged = observations.Single(row => Phase(row) == "Staged" && R.Read<long>(row, "InstallId") == install);
            var terminal = observations.Single(row => Phase(row) == "Published" && R.Read<long>(row, "InstallId") == install);
            Assert.That(R.Read<long>(staged, "PendingId"), Is.EqualTo(pending.Id));
            Assert.That(R.Read<long>(staged, "PendingCreatedStamp"), Is.EqualTo(pending.Created));
            Assert.That(R.Read<long>(terminal, "PendingId"), Is.EqualTo(pending.Id));
            Assert.That(R.Read<long>(terminal, "PendingCreatedStamp"), Is.EqualTo(pending.Created));
            Assert.That(R.Read<long>(published, "PendingId"), Is.EqualTo(pending.Id));
            Assert.That(R.Read<long>(published, "TerminalStamp"), Is.EqualTo(R.Read<long>(terminal, "Stamp")));
            Assert.That(R.Read<bool>(terminal, "TimingAvailable"), Is.True);
            AssertSamePublishedToken(R.Read(terminal, "Token"), published);
            var transfer = R.Read(accepted, "Transfer");
            var publication = Publication(report, R.Read<uint>(transfer, "Id"), "Published");
            Assert.That(R.Read<long>(publication, "installId"), Is.EqualTo(install));
            Assert.That(R.Read<long>(publication, "pendingId"), Is.EqualTo(pending.Id));
            Assert.That(R.Read<long>(publication, "pendingCreatedStamp"), Is.EqualTo(pending.Created));
            Assert.That(R.Read<long>(publication, "stamp"), Is.EqualTo(R.Read<long>(terminal, "Stamp")));
            Assert.That(R.Read<int>(publication, "terrainObjectId"), Is.EqualTo(R.Read<int>(published, "TerrainObjectId")));
            Assert.That(R.Read<int>(Identity(publication), "matchObjectId"), Is.EqualTo(R.Read<int>(transfer, "MatchObjectId")));
            Assert.That(R.Read<bool>(publication, "transferScopeAvailable"), Is.True);
        }

        static void AssertDependencyChunk(object check, string setName, Vector3Int chunk, uint required, uint applied)
        {
            var set = R.Read(check, setName); Assert.That(R.Read<bool>(set, "available"), Is.True);
            var row = ((Array)R.Read(set, "rows")).Cast<object>().Single(value => R.Read<Vector3Int>(value, "chunk") == chunk);
            Assert.That(R.Read<uint>(row, "requiredRevision"), Is.EqualTo(required));
            Assert.That(R.Read<bool>(row, "appliedAvailable"), Is.True);
            Assert.That(R.Read<uint>(row, "appliedRevision"), Is.EqualTo(applied));
        }

        static void AssertActualSourceAssociation(TerrainDependencyObservationV2FixtureRig rig, ActualPlayer player,
            PrototypeMatch match, int generation)
        {
            var observed = player.Events.Single();
            int playerId = player.Player.GetInstanceID();
            var source = R.Rows(rig.MainReport, "reconcileEvents").Cast<object>().Single(row => R.Read<int>(row, "playerObjectId") == playerId);
            Assert.That(R.Read<int>(source, "connectionGeneration"), Is.EqualTo(generation));
            var association = R.Rows(rig.TraceReport, "reconcileAssociations").Cast<object>().Single(row =>
                R.Read<int>(Identity(row), "playerObjectId") == playerId && R.Read<long>(row, "reconcileInvocation") == observed.ReconcileInvocation);
            Assert.That(R.Read<int>(Identity(association), "matchObjectId"), Is.EqualTo(match.GetInstanceID()));
            Assert.That(R.Read<bool>(Identity(association), "connectionGenerationKnown"), Is.True);
            Assert.That(R.Read<int>(Identity(association), "connectionGeneration"), Is.EqualTo(generation));
            Assert.That(R.Read<ulong>(Identity(association), "networkObjectId"), Is.EqualTo(player.Player.NetworkObjectId));
            Assert.That(R.Read<bool>(association, "sourceEventAvailable"), Is.True);
            Assert.That(R.Read<long>(association, "sourceEventId"), Is.EqualTo(R.Read<long>(source, "eventId")));
            Assert.That(R.Read<bool>(association, "dependencyEventAvailable"), Is.True);
            Assert.That(R.Read<long>(association, "dependencyInvocation"), Is.EqualTo(observed.DependencyCheckInvocation));
            var dependency = R.Rows(rig.TraceReport, "dependencyChecks").Cast<object>().Single(row =>
                R.Read<long>(row, "eventId") == R.Read<long>(association, "dependencyEventId"));
            Assert.That(R.Read<long>(dependency, "invocation"), Is.EqualTo(observed.DependencyCheckInvocation));
            Assert.That(R.Read<string>(dependency, "source"), Is.EqualTo("Reconcile"));
            Assert.That(R.Read<int>(Identity(dependency), "playerObjectId"), Is.EqualTo(playerId));
            Assert.That(R.Read<int>(Identity(dependency), "matchObjectId"), Is.EqualTo(match.GetInstanceID()));
        }

        static void AssertRealContact(BlockTerrain terrain, Vector3Int cell, byte owner)
        {
            Assert.That(terrain.World.TryGet(cell, out var live), Is.True); Assert.That(live.Owner, Is.EqualTo(owner));
            Assert.That(Physics.Raycast(BlockWorld.Center(cell) + Vector3.up * .5f, Vector3.down,
                out var hit, .6f, 1 << BlockTerrain.TerrainLayer), Is.True);
            Assert.That(hit.collider, Is.TypeOf<MeshCollider>());
            Assert.That(hit.collider.transform.IsChildOf(terrain.transform), Is.True);
            Assert.That(terrain.TryContact(hit, out var contact), Is.True); Assert.That(contact.Owner, Is.EqualTo(owner));
        }

        static IEnumerator Drain(BlockTerrain terrain, Action tick, uint revision)
        {
            for (int attempt = 0; attempt < 1000 && (terrain.PendingTransactions != 0 || terrain.Revision != revision); attempt++)
            { tick(); yield return null; }
            Assert.That(terrain.PendingTransactions, Is.Zero, "The actual generation/upload/bake/publication must finish");
            Assert.That(terrain.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }

        static void AssertNoDiagnosticLoss(object report)
        {
            foreach (string name in new[] { "consumerErrors", "providerErrors", "sourceReconcileErrors", "openInstalls", "openTransfers",
                "unknownLifecycleRows", "snapshotErrors", "unconsumedSourceAppends", "unavailableAssociations", "unavailableIdentityRows" })
                Assert.That(R.Read<int>(report, name), Is.Zero, name);
            Assert.That(R.Read<long>(report, "droppedRows"), Is.Zero);
            Assert.That(R.Read<bool>(report, "readSetTruncated"), Is.False);
            Assert.That(R.Rows(report, "endOpen").Count, Is.Zero);
            Assert.That(R.Read<bool>(report, "captureComplete"), Is.True);
        }

        [Timeout(600000)]
        [UnityTest] public IEnumerator CompleteActualReceiveAndPublicationAssociatesTheSameAckTerrainResume()
            => CompleteReceiveAndResume(false);

        [Timeout(600000)]
        [UnityTest] public IEnumerator ThrowingDiagnosticObserversKeepActualPublicationAckAndSameAckResume()
            => CompleteReceiveAndResume(true);

        IEnumerator CompleteReceiveAndResume(bool throwingObservers)
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            IDisposable transferFault = null, installFault = null;
            IDisposable transferTail = null, installTail = null;
            int transferTailCount = 0, installTailCount = 0;
            int transferFaultBaseline = (int)R.ReadStatic(typeof(PrototypeMatch), "TerrainTransferProviderErrors");
            int installFaultBaseline = (int)R.ReadStatic(typeof(BlockTerrain), "RemoteInstallProviderErrors");
            try
            {
                if (throwingObservers)
                {
                    transferFault = new TerrainDependencyFixtureSubscription(typeof(PrototypeMatch), "TerrainTransferObserved",
                        (sender, _) => { if (ReferenceEquals(sender, rig.Match)) throw new InvalidOperationException("Fixture transfer observer fault"); });
                    installFault = new TerrainDependencyFixtureSubscription(typeof(BlockTerrain), "RemoteInstallObserved",
                        (sender, _) => { if (ReferenceEquals(sender, rig.Terrain)) throw new InvalidOperationException("Fixture install observer fault"); });
                    // These consumers are deliberately after the throwing consumer.
                    // One catch around multicast Invoke would drop them and must fail this test.
                    transferTail = new TerrainDependencyFixtureSubscription(typeof(PrototypeMatch), "TerrainTransferObserved",
                        (sender, _) => { if (ReferenceEquals(sender, rig.Match)) transferTailCount++; });
                    installTail = new TerrainDependencyFixtureSubscription(typeof(BlockTerrain), "RemoteInstallObserved",
                        (sender, _) => { if (ReferenceEquals(sender, rig.Terrain)) installTailCount++; });
                }
                // Every install here starts at the real receive body, not a direct scoped install.
                rig.ReceiveAll(Floor(1).EncodeSnapshot(), 201, 1, true);
                Assert.That(rig.Incoming, Is.Not.Null); Assert.That(rig.Incoming.Complete, Is.True);
                Assert.That(rig.Terrain.Revision, Is.Zero);
                rig.Tick(); Assert.That(R.Read(rig.Terrain, "_pending"), Is.Not.Null);
                yield return Drain(rig.Terrain, rig.Tick, 1);
                rig.PollOriginalAck(); Assert.That(rig.Incoming, Is.Null); Assert.That(rig.Match.InitialSyncComplete, Is.True);

                using var player = new ActualPlayer(rig, 9101);
                var authority = player.State; authority.Position = new Vector3(.05f, .14f, .05f);
                authority.AcknowledgedInput = 1; authority.Grounded = true;
                player.Motor.Restore(authority, false);
                for (int tick = 0; tick < 4; tick++)
                    player.Motor.Step(ref authority, new PlayerInputFrame { Swim = true }, Dt, authority.SimulatedAt + (tick + 1) * Dt, false);
                Assert.That(authority.SupportTerrainRevision, Is.EqualTo(1), "Support comes from the real published collider/contact");
                var support = authority.SupportCell; var chunk = BlockWorld.ChunkAt(support);
                AssertRealContact(rig.Terrain, support, 1);
                var next = rig.Terrain.World.Clone(); next.Seal(2, new[] { chunk });
                // Represents a received host snapshot requiring the next real terrain revision.
                // The contact/owner and subsequent encoded terrain publication are real.
                authority.SupportTerrainRevision = 2;
                player.Start(authority);
                for (uint sequence = 2; sequence <= 4; sequence++) player.History.Add(new PlayerInputFrame
                    { Sequence = sequence, Revision = authority.Revision, HeroRevision = authority.HeroRevision, Swim = true, Move = Vector2.up });
                player.Refresh(authority);
                Assert.That(player.Events.Count, Is.EqualTo(1)); var waiting = player.Events[0];
                Assert.That(waiting.Origin, Is.EqualTo(ReconcileOrigin.TerrainWait));
                Assert.That(waiting.TerrainPendingAfter, Is.True); Assert.That(waiting.ReplaySteps, Is.Zero);
                Assert.That(waiting.Authority.AppliedSupportRevision, Is.EqualTo(1));
                Assert.That(waiting.Authority.RequiredSupportRevision, Is.EqualTo(2));
                var mainBefore = R.Rows(rig.MainReport, "reconcileEvents"); Assert.That(mainBefore.Count, Is.EqualTo(1));
                string frozenWaiting = JsonUtility.ToJson(mainBefore[0]);
                var waitingAssociation = R.Rows(rig.TraceReport, "reconcileAssociations").Cast<object>().Single(row =>
                    R.Read<int>(Identity(row), "playerObjectId") == player.Player.GetInstanceID() && R.Read<long>(row, "reconcileInvocation") == waiting.ReconcileInvocation);
                Assert.That(R.Read<bool>(waitingAssociation, "sourceEventAvailable"), Is.True);
                Assert.That(R.Read<long>(waitingAssociation, "sourceEventId"), Is.EqualTo(R.Read<long>(mainBefore[0], "eventId")));
                Assert.That(R.Read<bool>(waitingAssociation, "dependencyEventAvailable"), Is.True);
                var waitingDependency = R.Rows(rig.TraceReport, "dependencyChecks").Cast<object>().Single(row =>
                    R.Read<long>(row, "eventId") == R.Read<long>(waitingAssociation, "dependencyEventId"));
                Assert.That(R.Read<long>(waitingDependency, "invocation"), Is.EqualTo(waiting.DependencyCheckInvocation));
                Assert.That(R.Read<string>(waitingDependency, "source"), Is.EqualTo("Reconcile"));
                Assert.That(R.Read<bool>(waitingDependency, "ready"), Is.False);
                AssertDependencyChunk(waitingDependency, "after", chunk, 2, 1);

                rig.ReceiveAll(next.EncodeSnapshot(), 202, 2, true);
                var accepted = R.Read(rig.Match, "_terrainIncomingAccepted");
                Assert.That(R.Read<bool>(accepted, "AcceptedAvailable"), Is.True);
                Assert.That(rig.Terrain.Revision, Is.EqualTo(1), "No received/accepted data can precede collider publication");
                rig.Tick(); Assert.That(R.Read(rig.Terrain, "_pending"), Is.Not.Null);
                var actualPending = CaptureActualPending(rig.Terrain, accepted);
                Assert.That(rig.InstallObservations.Count(row => Phase(row) == "Staged" && R.Read<long>(row, "InstallId") == R.Read<long>(accepted, "InstallId")), Is.EqualTo(1));
                yield return Drain(rig.Terrain, rig.Tick, 2);
                Assert.That(rig.Terrain.ChunkRevision(chunk), Is.EqualTo(2)); AssertRealContact(rig.Terrain, support, 1);
                var callback = rig.TransferObservations.Single(row => Phase(row) == "PublishedCommitCallback" && R.Read<uint>(row, "Id") == 202);
                Assert.That(R.Read<string>(callback, "AssociationAvailability"), Is.EqualTo("Exact"));
                AssertActualInstallationChain(accepted, R.Read(callback, "AssociatedPublication"), rig.InstallObservations, actualPending, rig.TraceReport);
                rig.PollOriginalAck();
                var ack = rig.TransferObservations.Single(row => Phase(row) == "AppliedAckCallReturned" && R.Read<uint>(row, "Id") == 202);
                AssertPublishedToken(accepted, R.Read(ack, "AssociatedPublication"));
                Assert.That(R.Read<string>(ack, "AssociationAvailability"), Is.EqualTo("Exact"));
                AssertSamePublishedToken(R.Read(callback, "AssociatedPublication"), R.Read(ack, "AssociatedPublication"));
                Assert.That(rig.Incoming, Is.Null); Assert.That(rig.Match.InitialSyncComplete, Is.True);
                player.Refresh(authority);
                Assert.That(player.Events.Count, Is.EqualTo(2)); var resumed = player.Events[1];
                Assert.That(resumed.Origin, Is.EqualTo(ReconcileOrigin.TerrainResume));
                Assert.That(resumed.Authority.AcknowledgedInput, Is.EqualTo(waiting.Authority.AcknowledgedInput));
                Assert.That(resumed.TerrainPendingBefore, Is.True); Assert.That(resumed.TerrainPendingAfter, Is.False);
                Assert.That(resumed.ReplaySteps, Is.EqualTo(3));
                Assert.That(resumed.Authority.AppliedSupportRevision, Is.EqualTo(2));
                Assert.That(resumed.WallStamp, Is.GreaterThanOrEqualTo(R.Read<long>(ack, "Stamp")));

                var main = R.Rows(rig.MainReport, "reconcileEvents"); Assert.That(main.Count, Is.EqualTo(2));
                Assert.That(JsonUtility.ToJson(main[0]), Is.EqualTo(frozenWaiting), "Publication cannot rewrite the earlier raw event");
                Assert.That(R.Read<string>(main[1], "origin"), Is.EqualTo("TerrainResume"));
                Assert.That(R.Read<bool>(main[1], "sameAcknowledgement"), Is.True);
                Assert.That(R.Read<double>(main[1], "metres"), Is.EqualTo(resumed.RawDistance).Within(1e-7));
                var association = R.Rows(rig.TraceReport, "reconcileAssociations").Cast<object>().Single(row =>
                    R.Read<long>(row, "reconcileInvocation") == resumed.ReconcileInvocation);
                Assert.That(R.Read<bool>(association, "sourceEventAvailable"), Is.True);
                Assert.That(R.Read<long>(association, "sourceEventId"), Is.EqualTo(R.Read<long>(main[1], "eventId")));
                Assert.That(R.Read<bool>(association, "dependencyEventAvailable"), Is.True);
                Assert.That(R.Read<long>(association, "dependencyInvocation"), Is.EqualTo(resumed.DependencyCheckInvocation));
                var dependency = R.Rows(rig.TraceReport, "dependencyChecks").Cast<object>().Single(row =>
                    R.Read<long>(row, "eventId") == R.Read<long>(association, "dependencyEventId"));
                Assert.That(R.Read<string>(dependency, "source"), Is.EqualTo("Reconcile"));
                Assert.That(R.Read<long>(dependency, "invocation"), Is.EqualTo(resumed.DependencyCheckInvocation));
                Assert.That(R.Read<bool>(dependency, "ready"), Is.True);
                var after = R.Read(dependency, "after");
                Assert.That(R.Read<int>(after, "terrainObjectId"), Is.EqualTo(rig.Terrain.GetInstanceID()));
                Assert.That(R.Read<uint>(after, "terrainRound"), Is.EqualTo(rig.Terrain.Round));
                Assert.That(R.Read<uint>(after, "worldRevision"), Is.EqualTo(2));
                var release = R.Rows(rig.TraceReport, "dependencyChecks").Cast<object>().Single(row =>
                    R.Read<string>(row, "source") == "RefreshPrecheck" && R.Read<bool>(row, "ready") && R.Read<int>(row, "satisfiedCount") > 0);
                Assert.That(((Vector3Int[])R.Read(release, "satisfiedChunks")).Contains(chunk), Is.True);
                Assert.That(R.Read<int>(Identity(release), "playerObjectId"), Is.EqualTo(player.Player.GetInstanceID()));
                Assert.That(R.Read<int>(Identity(release), "matchObjectId"), Is.EqualTo(rig.Match.GetInstanceID()));
                AssertDependencyChunk(release, "before", chunk, 2, 2);
                Assert.That(R.Read<int>(R.Read(release, "after"), "totalCount"), Is.Zero);
                Assert.That(R.Read<long>(release, "endStamp"), Is.LessThanOrEqualTo(resumed.WallStamp));
                var published = Publication(rig.TraceReport, 202, "Published");
                Assert.That(R.Read<int>(published, "terrainObjectId"), Is.EqualTo(R.Read<int>(after, "terrainObjectId")));
                Assert.That(R.Read<uint>(published, "round"), Is.EqualTo(R.Read<uint>(after, "terrainRound")));
                Assert.That(R.Read<uint>(published, "revision"), Is.EqualTo(R.Read<uint>(after, "worldRevision")));
                Assert.That(R.Read<int>(Identity(published), "traceConnectionOrdinal"), Is.EqualTo(R.Read<int>(Identity(association), "traceConnectionOrdinal")));
                Assert.That(R.Read<bool>(Identity(published), "connectionGenerationKnown"), Is.True);
                Assert.That(R.Read<int>(Identity(published), "connectionGeneration"), Is.EqualTo(R.Read<int>(main[1], "connectionGeneration")));
                Assert.That(R.Read<int>(rig.MainReport, "errors"), Is.Zero);
                Assert.That(R.Read<int>(rig.MainReport, "reconcileConsumerErrors"), Is.Zero);
                Assert.That(R.Read<bool>(rig.MainReport, "reconcileEventsComplete"), Is.True);
                player.Refresh(authority); Assert.That(player.Events.Count, Is.EqualTo(2), "No invented third same-ACK recovery");
                var report = rig.FinishSidecar();
                if (throwingObservers)
                {
                    Assert.That(R.Read<int>(report, "providerErrors"), Is.GreaterThan(0));
                    Assert.That((int)R.ReadStatic(typeof(PrototypeMatch), "TerrainTransferProviderErrors"), Is.GreaterThan(transferFaultBaseline));
                    Assert.That((int)R.ReadStatic(typeof(BlockTerrain), "RemoteInstallProviderErrors"), Is.GreaterThan(installFaultBaseline));
                    Assert.That(transferTailCount, Is.EqualTo(rig.TransferObservations.Count), "Later transfer consumers survive every observer exception");
                    Assert.That(installTailCount, Is.EqualTo(rig.InstallObservations.Count), "Later install consumers survive every observer exception");
                    Assert.That(R.Read<bool>(report, "captureComplete"), Is.False);
                    Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero); Assert.That(R.Read<int>(report, "openTransfers"), Is.Zero);
                    Assert.That(R.Read<int>(report, "consumerErrors"), Is.Zero);
                    Assert.That(R.Rows(report, "endOpen").Count, Is.Zero);
                }
                else AssertNoDiagnosticLoss(report);
            }
            finally { installTail?.Dispose(); transferTail?.Dispose(); installFault?.Dispose(); transferFault?.Dispose(); }
        }

        [Timeout(600000)]
        [UnityTest] public IEnumerator OldPendingTerminatesOnItsOwnGenerationThenTheNextGenerationSucceeds()
            => TwoGenerations(false);

        [Timeout(600000)]
        [UnityTest] public IEnumerator AfterBothGenerationsCloseARealNewPartialAssemblyRemainsEndOpen()
            => TwoGenerations(true);

        IEnumerator TwoGenerations(bool leavePartial)
        {
            using var rig = new TerrainDependencyObservationV2FixtureRig();
            using var oldPlayer = new ActualPlayer(rig, 9201);
            oldPlayer.Reconcile(oldPlayer.State);
            Assert.That(R.Read<int>(rig.MainReport, "connectionGenerations"), Is.EqualTo(1));
            var oldCell = new Vector3Int(5, 1, 5);
            rig.ReceiveAll(Single(1, oldCell, 1).EncodeSnapshot(), 301, 1, true);
            var oldAccepted = R.Read(rig.Match, "_terrainIncomingAccepted");
            rig.Tick(); Assert.That(R.Read(rig.Terrain, "_pending"), Is.Not.Null);
            var oldPending = CaptureActualPending(rig.Terrain, oldAccepted);
            using var next = new ActualGeneration(rig, 9002);
            Assert.That(PrototypeMatch.Current, Is.SameAs(next.Match)); Assert.That(BlockTerrain.Current, Is.SameAs(next.Terrain));
            using var newPlayer = new ActualPlayer(rig, 9202);
            newPlayer.Reconcile(newPlayer.State);
            Assert.That(R.Read<int>(rig.MainReport, "connectionGenerations"), Is.EqualTo(2));

            // The old asynchronous work actually terminates while Current is the new match.
            yield return Drain(rig.Terrain, rig.Tick, 1);
            rig.PollOriginalAck(); Assert.That(rig.Incoming, Is.Null);
            AssertRealContact(rig.Terrain, oldCell, 1);
            Assert.That(PrototypeMatch.Current, Is.SameAs(next.Match));
            var oldPublished = Publication(rig.TraceReport, 301, "Published");
            var oldIdentity = Identity(oldPublished);
            Assert.That(R.Read<int>(oldIdentity, "matchObjectId"), Is.EqualTo(rig.Match.GetInstanceID()));
            Assert.That(R.Read<int>(oldIdentity, "connectionGeneration"), Is.EqualTo(1));
            Assert.That(R.Read<bool>(oldIdentity, "connectionGenerationKnown"), Is.True);
            var oldCallback = rig.TransferObservations.Single(row => Phase(row) == "PublishedCommitCallback" && R.Read<uint>(row, "Id") == 301);
            AssertActualInstallationChain(oldAccepted, R.Read(oldCallback, "AssociatedPublication"), rig.InstallObservations, oldPending, rig.TraceReport);
            var oldAck = rig.TransferObservations.Single(row => Phase(row) == "AppliedAckCallReturned" && R.Read<uint>(row, "Id") == 301);
            Assert.That(R.Read<string>(oldAck, "AssociationAvailability"), Is.EqualTo("Exact"));
            AssertSamePublishedToken(R.Read(oldCallback, "AssociatedPublication"), R.Read(oldAck, "AssociatedPublication"));
            AssertActualSourceAssociation(rig, oldPlayer, rig.Match, 1);
            Assert.That(R.Read<string>(oldCallback, "AssociationAvailability"), Is.EqualTo("Exact"));
            var oldLifecycle = Lifecycle(rig.TraceReport, 301);
            Assert.That(R.Read<bool>(oldLifecycle, "open"), Is.False); Assert.That(R.Read<bool>(oldLifecycle, "ackExact"), Is.True);

            var nextCell = new Vector3Int(65, 1, 5);
            next.ReceiveAll(Single(1, nextCell, 2).EncodeSnapshot(), 401, 1, true);
            var nextAccepted = R.Read(next.Match, "_terrainIncomingAccepted");
            next.Tick(); Assert.That(R.Read(next.Terrain, "_pending"), Is.Not.Null);
            var nextPending = CaptureActualPending(next.Terrain, nextAccepted);
            yield return Drain(next.Terrain, next.Tick, 1);
            next.PollOriginalAck(); Assert.That(next.Incoming, Is.Null);
            AssertRealContact(next.Terrain, nextCell, 2);
            var nextPublished = Publication(rig.TraceReport, 401, "Published"); var nextIdentity = Identity(nextPublished);
            Assert.That(R.Read<int>(nextIdentity, "matchObjectId"), Is.EqualTo(next.Match.GetInstanceID()));
            Assert.That(R.Read<int>(nextIdentity, "connectionGeneration"), Is.EqualTo(2));
            Assert.That(R.Read<bool>(nextIdentity, "connectionGenerationKnown"), Is.True);
            Assert.That(R.Read<int>(nextIdentity, "traceConnectionOrdinal"), Is.Not.EqualTo(R.Read<int>(oldIdentity, "traceConnectionOrdinal")));
            Assert.That(R.Read<long>(nextPublished, "stamp"), Is.GreaterThanOrEqualTo(R.Read<long>(oldPublished, "stamp")));
            var nextCallback = next.TransferObservations.Single(row => Phase(row) == "PublishedCommitCallback" && R.Read<uint>(row, "Id") == 401);
            AssertActualInstallationChain(nextAccepted, R.Read(nextCallback, "AssociatedPublication"), next.InstallObservations, nextPending, rig.TraceReport);
            Assert.That(R.Read<string>(nextCallback, "AssociationAvailability"), Is.EqualTo("Exact"));
            var nextAck = next.TransferObservations.Single(row => Phase(row) == "AppliedAckCallReturned" && R.Read<uint>(row, "Id") == 401);
            Assert.That(R.Read<string>(nextAck, "AssociationAvailability"), Is.EqualTo("Exact"));
            AssertSamePublishedToken(R.Read(nextCallback, "AssociatedPublication"), R.Read(nextAck, "AssociatedPublication"));
            AssertActualSourceAssociation(rig, newPlayer, next.Match, 2);
            var nextLifecycle = Lifecycle(rig.TraceReport, 401);
            Assert.That(R.Read<bool>(nextLifecycle, "open"), Is.False); Assert.That(R.Read<bool>(nextLifecycle, "ackExact"), Is.True);

            if (leavePartial)
            {
                var bytes = MultipartNext(next.Terrain.Round).EncodeDelta(1);
                Assert.That(bytes.Length, Is.GreaterThan(BlockTransferFragment.PayloadBytes));
                var first = TerrainDependencyObservationV2FixtureRig.Part(bytes, next.Terrain.Round, 402, 1, 2, false, 0);
                next.Receive(first);
                Assert.That(next.Incoming, Is.Not.Null); Assert.That(next.Incoming.Complete, Is.False);
                Assert.That(next.Incoming.ReceivedCount, Is.EqualTo(1));
                Assert.That(next.Terrain.PendingTransactions, Is.Zero);
                Assert.That(next.TransferObservations.Any(row => R.Read<uint>(row, "Id") == 402 && Phase(row) == "VerifyReturned"), Is.False);
                var report = rig.FinishSidecar();
                Assert.That(R.Read<bool>(report, "captureComplete"), Is.False);
                Assert.That(R.Read<int>(report, "openInstalls"), Is.Zero); Assert.That(R.Read<int>(report, "openTransfers"), Is.EqualTo(1));
                Assert.That(R.Read<int>(report, "unknownLifecycleRows"), Is.Zero);
                foreach (string name in new[] { "consumerErrors", "providerErrors", "sourceReconcileErrors", "snapshotErrors",
                    "unconsumedSourceAppends", "unavailableAssociations", "unavailableIdentityRows" })
                    Assert.That(R.Read<int>(report, name), Is.Zero, "Partial receive is the only incomplete boundary: " + name);
                Assert.That(R.Read<long>(report, "droppedRows"), Is.Zero);
                Assert.That(R.Read<bool>(report, "readSetTruncated"), Is.False);
                Assert.That(R.Read<bool>(report, "sourceProbeComplete"), Is.True);
                Assert.That(R.Read<bool>(report, "sourceReconcileComplete"), Is.True);
                var open = R.Rows(report, "endOpen").Cast<object>().Single();
                Assert.That(R.Read<string>(open, "reason"), Is.EqualTo("PartialAssemblyAtFinish"));
                Assert.That(R.Read<bool>(open, "snapshotAvailable"), Is.True);
                Assert.That(R.Read<bool>(open, "incomingPresent"), Is.True);
                Assert.That(R.Read<int>(Identity(open), "matchObjectId"), Is.EqualTo(next.Match.GetInstanceID()));
                Assert.That(R.Read<int>(Identity(open), "connectionGeneration"), Is.EqualTo(2));
                Assert.That(R.Read<int>(Identity(open), "traceConnectionOrdinal"), Is.EqualTo(R.Read<int>(nextIdentity, "traceConnectionOrdinal")));
                Assert.That(R.Read<uint>(open, "id"), Is.EqualTo(first.Id)); Assert.That(R.Read<uint>(open, "round"), Is.EqualTo(first.Round));
                Assert.That(R.Read<uint>(open, "baseRevision"), Is.EqualTo(first.BaseRevision)); Assert.That(R.Read<uint>(open, "revision"), Is.EqualTo(first.Revision));
                Assert.That(R.Read<uint>(open, "hash"), Is.EqualTo(first.Hash)); Assert.That(R.Read<int>(open, "length"), Is.EqualTo(first.Length));
                Assert.That(R.Read<int>(open, "fragmentCount"), Is.EqualTo(first.Count)); Assert.That(R.Read<bool>(open, "full"), Is.False);
                Assert.That(R.Read<int>(open, "receivedCount"), Is.EqualTo(1)); Assert.That(next.Incoming.ReceivedCount, Is.EqualTo(1));
                Assert.That(R.Read<bool>(Lifecycle(report, 301), "open"), Is.False);
                Assert.That(R.Read<bool>(Lifecycle(report, 401), "open"), Is.False);
            }
            else AssertNoDiagnosticLoss(rig.FinishSidecar());
            Assert.That(R.Read<int>(rig.MainReport, "errors"), Is.Zero);
            Assert.That(R.Read<int>(rig.MainReport, "reconcileConsumerErrors"), Is.Zero);
        }

        sealed class ActualPlayer : IDisposable
        {
            readonly GameObject _object;
            readonly IDisposable _subscription;
            internal readonly PrototypePlayer Player;
            internal readonly PlayerMotorSimulation Motor;
            internal readonly List<PrototypePlayer.ReconcileTelemetryEvent> Events = new();
            internal List<PlayerInputFrame> History => R.Read<List<PlayerInputFrame>>(Player, "_history");
            internal PlayerSnapshot State => new PlayerSnapshot { HeroId = 1, Revision = 7, Team = 1, Health = 100, Ink = 100,
                Position = new Vector3(10, 1, 10), Movement = MovementMode.Human, AcknowledgedInput = 30, SimulatedAt = 2,
                SubWeaponId = SubWeaponConfigService.Current.DefaultId(1), SpecialWeaponId = 1 };
            internal ActualPlayer(TerrainDependencyObservationV2FixtureRig rig, ulong id)
            {
                _object = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
                Player = _object.GetComponent<PrototypePlayer>(); Player.enabled = false;
                rig.BindFixtureNetworkBehaviour(Player, _object.GetComponent<NetworkObject>(), id);
                var controller = Player.GetComponent<CharacterController>(); Motor = new PlayerMotorSimulation(controller);
                R.Write(Player, "_motor", Motor); R.Write(Player, "_controller", controller); Start(State);
                _subscription = new TerrainDependencyFixtureSubscription(typeof(PrototypePlayer), "ReconcileObserved",
                    (sender, value) => { if (ReferenceEquals(sender, Player)) Events.Add((PrototypePlayer.ReconcileTelemetryEvent)value); });
            }
            internal void Start(PlayerSnapshot state) { Motor.Restore(state, false); R.Write(Player, "_predicted", state); }
            internal void Refresh(PlayerSnapshot state) => R.Call(Player, "RefreshPredictionPaint", state, true, (uint)0, MatchPhase.Practice);
            internal void Reconcile(PlayerSnapshot state) => R.Call(Player, "ReconcilePrediction", state, false, true, (uint)0, MatchPhase.Practice);
            public void Dispose()
            {
                _subscription.Dispose();
                if (_object == null) return;
                foreach (var behaviour in _object.GetComponents<NetworkBehaviour>()) R.Write(behaviour, "IsSpawned", false);
                R.Write(_object.GetComponent<NetworkObject>(), "IsSpawned", false); Object.DestroyImmediate(_object);
            }
        }

        // Second real BlockTerrain/PrototypeMatch endpoint sharing the fixture's existing NGO manager.
        // It invokes the original generated receive body, not an alternative decoder/queue/publisher.
        sealed class ActualGeneration : IDisposable
        {
            readonly GameObject _terrainObject, _matchObject;
            readonly PrototypeMatch _priorMatch;
            readonly BlockTerrain _priorTerrain;
            readonly Action<BlockTerrainCommit> _published;
            readonly IDisposable _capture, _installCapture;
            double _now = 10;
            internal readonly BlockTerrain Terrain;
            internal readonly PrototypeMatch Match;
            internal readonly List<object> TransferObservations = new();
            internal readonly List<object> InstallObservations = new();
            internal BlockTransferAssembly Incoming => R.Read<BlockTransferAssembly>(Match, "_blockIncoming");
            internal ActualGeneration(TerrainDependencyObservationV2FixtureRig rig, ulong id)
            {
                _priorMatch = PrototypeMatch.Current; _priorTerrain = BlockTerrain.Current;
                _terrainObject = new GameObject("V2 real next-generation terrain");
                Terrain = _terrainObject.AddComponent<BlockTerrain>(); Terrain.Initialize(null); Terrain.Clear(rig.Terrain.Round);
                _matchObject = new GameObject("V2 inactive next-generation receive endpoint"); _matchObject.SetActive(false);
                var networkObject = _matchObject.AddComponent<NetworkObject>(); Match = _matchObject.AddComponent<PrototypeMatch>();
                rig.BindFixtureNetworkBehaviour(Match, networkObject, id);
                R.Write(Match, "Blocks", Terrain); R.Write(Match, "_paintRound", Terrain.Round); R.Write(Match, "InitialSyncComplete", true);
                R.Write(Match, "_blockObservedTerrain", Terrain); R.WriteStatic(typeof(PrototypeMatch), "Current", Match);
                _published = commit => R.Call(Match, "OnBlockPublished", commit); Terrain.PublishedCommit += _published;
                _capture = new TerrainDependencyFixtureSubscription(typeof(PrototypeMatch), "TerrainTransferObserved",
                    (sender, value) => { if (ReferenceEquals(sender, Match)) TransferObservations.Add(value); });
                _installCapture = new TerrainDependencyFixtureSubscription(typeof(BlockTerrain), "RemoteInstallObserved",
                    (sender, value) => { if (ReferenceEquals(sender, Terrain)) InstallObservations.Add(value); });
            }
            internal void Receive(BlockTransferFragment fragment)
            {
                var stage = R.Member(typeof(NetworkBehaviour), "__rpc_exec_stage") as FieldInfo;
                Assert.That(stage, Is.Not.Null); var prior = stage.GetValue(Match);
                try
                {
                    stage.SetValue(Match, Enum.ToObject(stage.FieldType, 1));
                    R.Call(Match, "BlockFragmentClientRpc", fragment, default(ClientRpcParams));
                }
                finally { stage.SetValue(Match, prior); }
            }
            internal void ReceiveAll(byte[] bytes, uint id, uint revision, bool full)
            {
                int count = (bytes.Length + BlockTransferFragment.PayloadBytes - 1) / BlockTransferFragment.PayloadBytes;
                for (int i = 0; i < count; i++) Receive(TerrainDependencyObservationV2FixtureRig.Part(bytes, Terrain.Round, id, 0, revision, full, i));
            }
            internal void Tick() { _now += Dt; Terrain.Tick(false, _now, Dt); }
            internal void PollOriginalAck() => R.Call(Match, "UpdateBlockSync");
            public void Dispose()
            {
                _installCapture.Dispose(); _capture.Dispose(); if (Terrain != null) Terrain.PublishedCommit -= _published;
                R.WriteStatic(typeof(PrototypeMatch), "Current", _priorMatch);
                R.WriteStatic(typeof(BlockTerrain), "Current", _priorTerrain);
                if (_matchObject != null)
                {
                    foreach (var behaviour in _matchObject.GetComponents<NetworkBehaviour>()) R.Write(behaviour, "IsSpawned", false);
                    R.Write(_matchObject.GetComponent<NetworkObject>(), "IsSpawned", false); Object.DestroyImmediate(_matchObject);
                }
                if (_terrainObject != null) Object.DestroyImmediate(_terrainObject);
            }
        }
    }
}
#endif
