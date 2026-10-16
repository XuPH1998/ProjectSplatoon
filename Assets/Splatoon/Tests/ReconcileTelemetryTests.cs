#if UNITY_EDITOR
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
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class ReconcileTelemetryTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const float Dt = 1f / 60;
        readonly List<PrototypePlayer.ReconcileTelemetryEvent> _events = new();
        PrototypePlayer _player;
        PlayerMotorSimulation _motor;
        BlockTerrain _terrain;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            _player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab")).GetComponent<PrototypePlayer>();
            var controller = _player.GetComponent<CharacterController>();
            _motor = new PlayerMotorSimulation(controller);
            Set("_motor", _motor); Set("_controller", controller);
            _events.Clear();
            PrototypePlayer.ReconcileObserved += Observe;
        }

        [TearDown] public void Cleanup()
        {
            PrototypePlayer.ReconcileObserved -= Observe;
            PaperBodyTests.ReleaseTestBodies();
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear();
            LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Observe(PrototypePlayer player, PrototypePlayer.ReconcileTelemetryEvent observation)
        { if (player == _player) _events.Add(observation); }
        void Set(string name, object value) => typeof(PrototypePlayer).GetField(name, Private).SetValue(_player, value);
        void Call(string name, params object[] args) => typeof(PrototypePlayer).GetMethod(name, Private).Invoke(_player, args);
        List<PlayerInputFrame> History => (List<PlayerInputFrame>)typeof(PrototypePlayer).GetField("_history", Private).GetValue(_player);
        static PlayerSnapshot State() => new()
        {
            HeroId = 1, Revision = 7, Team = 1, Health = 100, Ink = 100,
            Position = new Vector3(10, 1, 10), Movement = MovementMode.Human,
            AcknowledgedInput = 30, SimulatedAt = 2
        };
        void StartFrom(PlayerSnapshot state) { _motor.Restore(state, false); Set("_predicted", state); }
        void Reconcile(PlayerSnapshot authority) => Call("ReconcilePrediction", authority, false, true, (uint)0, MatchPhase.Practice);
        void Refresh(PlayerSnapshot authority) => Call("RefreshPredictionPaint", authority, true, (uint)0, MatchPhase.Practice);
        void LoadReplayConfigs()
        {
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear();
            foreach (var row in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                SubWeaponConfigService.Current.SetById(row.Id, AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath));
            foreach (var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
                SpecialWeaponConfigService.Current.Set(row.Id, AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath));
        }

        [TestCase(false)] [TestCase(true)]
        public void SameFrameReconciliationsPreserveBothEventsAndTheirOriginalValues(bool advanceSecondAck)
        {
            var initial = State(); StartFrom(initial);
            var first = initial; first.AcknowledgedInput = 31; first.Position += Vector3.right * .2f;
            first.SimulatedAt += Dt;
            var second = first; second.AcknowledgedInput = advanceSecondAck ? 32u : 31u;
            second.Position += Vector3.forward * .3f; second.SimulatedAt += Dt;
            int frame = Time.frameCount;

            Reconcile(first); Reconcile(second);

            Assert.That(Time.frameCount, Is.EqualTo(frame));
            Assert.That(_events.Count, Is.EqualTo(2), "each completed reconciliation must be delivered synchronously, including the same ACK");
            var firstEvent = _events[0]; var secondEvent = _events[1];
            Assert.That(firstEvent.Before.Position, Is.EqualTo(initial.Position));
            Assert.That(firstEvent.Authority.AcknowledgedInput, Is.EqualTo(31));
            Assert.That(firstEvent.Replayed.Position, Is.EqualTo(first.Position));
            Assert.That(firstEvent.RawDistance, Is.EqualTo(.2f).Within(.00001));
            Assert.That(secondEvent.Before.Position, Is.EqualTo(first.Position));
            Assert.That(secondEvent.Authority.AcknowledgedInput, Is.EqualTo(second.AcknowledgedInput));
            Assert.That(secondEvent.RawDistance, Is.EqualTo(.3f).Within(.00001));
            Assert.That(firstEvent.Origin, Is.EqualTo(ReconcileOrigin.Snapshot));
            Assert.That(secondEvent.Origin, Is.EqualTo(ReconcileOrigin.Snapshot));

            second.Position = Vector3.one * 999; second.AcknowledgedInput = 900;
            Set("_predicted", second);
            Assert.That(_events[0].Replayed.Position, Is.EqualTo(first.Position));
            Assert.That(_events[0].Replayed.AcknowledgedInput, Is.EqualTo(31));
            Assert.That(_events[1].Replayed.Position, Is.EqualTo(first.Position + Vector3.forward * .3f));
            Assert.That(_events[1].Authority.AcknowledgedInput, Is.EqualTo(advanceSecondAck ? 32u : 31u));
        }

        [Test] public void CoordinateRebaseRetainsRawCorrectionAndSeparateHumanDistance()
        {
            var before = State(); before.Position = new Vector3(5, 1.75f, 5);
            before.AirHumanOffset = .75f; before.CameraRebaseOffset = -.75f;
            before.Movement = MovementMode.Air; before.PaperPose = PaperPose.Air;
            before.CompactBody = true; StartFrom(before);
            var authority = before; authority.AcknowledgedInput++;
            authority.Position.y = 1.1f; authority.AirHumanOffset = .1f;
            authority.CameraRebaseOffset = -.1f; authority.PaperPose = PaperPose.None;
            authority.CompactBody = false; authority.Movement = MovementMode.Human;

            Reconcile(authority);

            Assert.That(_events.Count, Is.EqualTo(1));
            var observation = _events[0];
            Assert.That(observation.Lifecycle, Is.False);
            Assert.That(observation.RawDistance, Is.EqualTo(.65f).Within(.00001));
            Assert.That(observation.RawDistance, Is.EqualTo(_player.LastCorrectionDistance));
            Assert.That(observation.UnadjustedRawDistance, Is.EqualTo(.65f).Within(.00001));
            Assert.That(observation.HumanDistance, Is.Zero.Within(.00001));
            Assert.That(observation.UnadjustedHumanDistance, Is.Zero.Within(.00001));
            Assert.That(observation.Before.HumanPosition, Is.EqualTo(new Vector3(5, 1, 5)));
            Assert.That(Vector3.Distance(observation.Before.HumanPosition, observation.Replayed.HumanPosition), Is.LessThan(.00001));
            Assert.That(observation.Before.AirHumanOffset, Is.EqualTo(.75f));
            Assert.That(observation.Replayed.AirHumanOffset, Is.EqualTo(.1f));
            Assert.That(observation.Before.CameraRebaseOffset, Is.EqualTo(-.75f));
            Assert.That(observation.Replayed.CameraRebaseOffset, Is.EqualTo(-.1f));
        }

        [UnityTest] public IEnumerator SupportPublicationEmitsWaitAndResumeForTheSameAcknowledgement()
        {
            SubWeaponConfigService.Current.Clear(); SpecialWeaponConfigService.Current.Clear();
            foreach (var row in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                SubWeaponConfigService.Current.SetById(row.Id, AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath));
            foreach (var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
                SpecialWeaponConfigService.Current.Set(row.Id, AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath));
            _terrain = new GameObject("Reconcile telemetry terrain").AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            var world = new BlockWorld(new Vector2(32, 64));
            for (int z = -12; z <= 12; z++) for (int x = -12; x <= 12; x++)
                world.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
            yield return Publish(world);

            var authority = State(); authority.Position = new Vector3(.05f, .14f, .05f);
            authority.AcknowledgedInput = 1; authority.Grounded = true;
            authority.SubWeaponId = SubWeaponConfigService.Current.DefaultId(1); authority.SpecialWeaponId = 1;
            _motor.Restore(authority, false);
            for (int tick = 0; tick < 4; tick++)
                _motor.Step(ref authority, new PlayerInputFrame { Swim = true }, Dt, authority.SimulatedAt + (tick + 1) * Dt, false);
            Assert.That(authority.SupportTerrainRevision, Is.GreaterThan(0), "the dependency must come from contact with published blocks");
            authority.SupportTerrainRevision = _terrain.World.Revision + 1;
            StartFrom(authority);
            for (uint sequence = 2; sequence <= 4; sequence++) History.Add(new PlayerInputFrame
                { Sequence = sequence, Revision = authority.Revision, HeroRevision = authority.HeroRevision, Swim = true, Move = Vector2.up });

            Refresh(authority);

            Assert.That(_events.Count, Is.EqualTo(1));
            var waiting = _events[0];
            Assert.That(waiting.Origin, Is.EqualTo(ReconcileOrigin.TerrainWait));
            Assert.That(waiting.TerrainPendingBefore, Is.False); Assert.That(waiting.TerrainPendingAfter, Is.True);
            Assert.That(waiting.ReplaySteps, Is.Zero);
            Assert.That(waiting.Authority.AppliedSupportRevision, Is.LessThan(waiting.Authority.RequiredSupportRevision));
            uint oldApplied = waiting.Authority.AppliedSupportRevision;
            var supportChunk = BlockWorld.ChunkAt(authority.SupportCell);

            yield return Publish(_terrain.World.Clone(), supportChunk);
            Refresh(authority);

            Assert.That(_events.Count, Is.EqualTo(2), "same-ACK terrain recovery is a separate reconciliation event");
            var resumed = _events[1];
            Assert.That(resumed.Origin, Is.EqualTo(ReconcileOrigin.TerrainResume));
            Assert.That(resumed.TerrainPendingBefore, Is.True); Assert.That(resumed.TerrainPendingAfter, Is.False);
            Assert.That(resumed.Authority.AcknowledgedInput, Is.EqualTo(waiting.Authority.AcknowledgedInput));
            Assert.That(resumed.Authority.Life, Is.EqualTo(waiting.Authority.Life));
            Assert.That(resumed.ReplaySteps, Is.EqualTo(3));
            Assert.That(resumed.Authority.AppliedSupportRevision, Is.GreaterThanOrEqualTo(resumed.Authority.RequiredSupportRevision));
            Assert.That(resumed.Replayed.SimulatedAt, Is.EqualTo(authority.SimulatedAt + 3.0 / GameplayConfig.Global.SimulationRate).Within(1e-7));
            Assert.That(_events[0].Authority.AppliedSupportRevision, Is.EqualTo(oldApplied), "publication must not rewrite an earlier event's terrain evidence");
            Assert.That(_events[0].TerrainPendingAfter, Is.True);

            Refresh(authority);
            Assert.That(_events.Count, Is.EqualTo(2), "ready terrain without a pending replay must not emit an invented recovery");
        }

        [UnityTest] public IEnumerator LosingSupportDoesNotReplayUntilThePreviouslyRequiredChunkPublishes()
        {
            LoadReplayConfigs();
            _terrain = new GameObject("Lost-support prediction terrain").AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            var world = new BlockWorld(new Vector2(32, 64));
            for (int z = -12; z <= 12; z++) for (int x = -12; x <= 12; x++)
                world.Set(new Vector3Int(x, 0, z), BlockCell.Create(1, true));
            yield return Publish(world);
            var authority = State(); authority.Position = new Vector3(.05f, .14f, .05f); authority.Grounded = true;
            authority.SubWeaponId = SubWeaponConfigService.Current.DefaultId(1); authority.SpecialWeaponId = 1;
            _motor.Restore(authority, false);
            for (int tick = 0; tick < 6; tick++)
                _motor.Step(ref authority, new PlayerInputFrame { Swim = true }, Dt, authority.SimulatedAt + Dt, false);
            Assert.That(authority.SupportTerrainRevision, Is.GreaterThan(0), "use a real published block contact");
            var supportChunk = BlockWorld.ChunkAt(authority.SupportCell);
            authority.SupportTerrainRevision = _terrain.Revision + 1;
            StartFrom(authority);
            for (uint sequence = 31; sequence <= 34; sequence++) History.Add(new PlayerInputFrame
                { Sequence = sequence, Revision = authority.Revision, Swim = true, Move = Vector2.up });
            Reconcile(authority);
            Assert.That(_player.TerrainReplayPending, Is.True);

            var detached = authority; detached.AcknowledgedInput = 31; detached.SimulatedAt += Dt;
            detached.SupportTerrainRevision = 0; detached.SupportCell = default;
            detached.Grounded = detached.Swimming = detached.CompactBody = false;
            detached.Movement = MovementMode.Air; detached.PaperPose = PaperPose.None;
            Reconcile(detached);
            Assert.That(_player.LastReplaySteps, Is.Zero, "support zero must not release an unpublished replay dependency");
            Assert.That(_player.TerrainReplayPending, Is.True);
            Assert.That(History.Count, Is.EqualTo(3), "acknowledged input is still removed while replay waits");
            Assert.That(History[0].Sequence, Is.EqualTo(32));
            int waitingEvents = _events.Count;
            Refresh(detached); Assert.That(_events.Count, Is.EqualTo(waitingEvents));

            yield return Publish(_terrain.World.Clone(), supportChunk);
            Refresh(detached);
            Assert.That(_player.TerrainReplayPending, Is.False);
            Assert.That(_player.LastReplaySteps, Is.EqualTo(3));
            Assert.That(_events.Count, Is.EqualTo(waitingEvents + 1));
            var resumed = _events[_events.Count - 1];
            Assert.That(resumed.Origin, Is.EqualTo(ReconcileOrigin.TerrainResume));
            Assert.That(resumed.Authority.AcknowledgedInput, Is.EqualTo(31));
            Assert.That(resumed.Authority.RequiredSupportRevision, Is.Zero);
            Assert.That(resumed.TerrainPendingBefore, Is.True); Assert.That(resumed.TerrainPendingAfter, Is.False);
            Assert.That(resumed.RawDistance, Is.EqualTo(_player.LastCorrectionDistance));
            Refresh(detached); Assert.That(_events.Count, Is.EqualTo(waitingEvents + 1));
        }

        [UnityTest] public IEnumerator MissingReplacementGeometryDefersFormAndResumesFromTheLatestAcknowledgement()
        {
            LoadReplayConfigs();
            _terrain = new GameObject("Deferred wall replacement terrain").AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var world = new BlockWorld(new Vector2(32, 64));
            for (int x = -8; x <= 8; x++) for (int y = 0; y < 50; y++)
                world.Set(new Vector3Int(x, y, 10), BlockCell.Create(1, true));
            yield return Publish(world);
            var wall = State(); wall.Position = new Vector3(.05f, 2, .6f); wall.Swimming = true; wall.Movement = MovementMode.Air;
            wall.SubWeaponId = SubWeaponConfigService.Current.DefaultId(1); wall.SpecialWeaponId = 1;
            _motor.Restore(wall, false);
            _motor.Step(ref wall, new PlayerInputFrame { Swim = true, Move = Vector2.up }, Dt, wall.SimulatedAt + Dt, false);
            Assert.That(wall.Movement, Is.EqualTo(MovementMode.WallInk)); Assert.That(wall.SupportTerrainRevision, Is.GreaterThan(0));
            var chunk = BlockWorld.ChunkAt(wall.SupportCell);
            var predicted = wall; predicted.Position += Vector3.up * .87f; predicted.AirHumanOffset = .87f;
            predicted.Movement = MovementMode.Air; predicted.PaperPose = PaperPose.Air;
            predicted.SupportCell = default; predicted.SupportTerrainRevision = 0;
            PaperPoseSimulation.Resolve(ref predicted, predicted, null, predicted.SimulatedAt);
            StartFrom(predicted);
            for (uint sequence = 31; sequence <= 34; sequence++) History.Add(new PlayerInputFrame
                { Sequence = sequence, Revision = wall.Revision, HeroRevision = wall.HeroRevision });
            wall.SupportTerrainRevision = _terrain.Revision + 1; wall.AcknowledgedInput = 31;
            wall.Ink = 77; wall.Health = 83; wall.ConsumedFire = 9; wall.SubConsumedPress = 6;
            Reconcile(wall);
            Assert.That(_events.Count, Is.EqualTo(1)); Assert.That(_events[0].TerrainPendingAfter, Is.True);
            Assert.That(_events[0].Replayed.Position, Is.EqualTo(predicted.Position));
            Assert.That(_events[0].Replayed.AirHumanOffset, Is.EqualTo(predicted.AirHumanOffset));
            Assert.That(_player.transform.position, Is.EqualTo(predicted.Position), "Do not install an incompatible movement capsule before collision publishes");
            Assert.That(History.Count, Is.EqualTo(3)); Assert.That(_events[0].RawDistance, Is.Zero);
            var duringWait = (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(_player);
            Assert.That(duringWait.AcknowledgedInput, Is.EqualTo(31)); Assert.That(duringWait.ConsumedFire, Is.EqualTo(9));
            Assert.That(duringWait.SubConsumedPress, Is.EqualTo(6)); Assert.That(duringWait.Ink, Is.EqualTo(77)); Assert.That(duringWait.Health, Is.EqualTo(83));
            var latest = predicted; latest.AcknowledgedInput = 32; latest.Position += Vector3.right * .1f;
            Reconcile(latest);
            Assert.That(_events.Count, Is.EqualTo(2)); Assert.That(_events[1].TerrainPendingAfter, Is.True);
            Assert.That(_events[1].Replayed.Position, Is.EqualTo(predicted.Position));
            Assert.That(_events[1].Authority.AcknowledgedInput, Is.EqualTo(32)); Assert.That(History.Count, Is.EqualTo(2));
            var expected = latest;
            foreach (var input in History) _motor.Step(ref expected, input, Dt, expected.SimulatedAt + Dt, false);
            StartFrom(predicted);
            yield return Publish(_terrain.World.Clone(), chunk);
            Refresh(latest);
            Assert.That(_events.Count, Is.EqualTo(3)); var resumed = _events[2];
            Assert.That(resumed.Origin, Is.EqualTo(ReconcileOrigin.TerrainResume)); Assert.That(resumed.TerrainPendingAfter, Is.False);
            Assert.That(resumed.Authority.AcknowledgedInput, Is.EqualTo(32)); Assert.That(resumed.ReplaySteps, Is.EqualTo(2));
            Assert.That(Vector3.Distance(resumed.Replayed.Position, expected.Position), Is.LessThan(.00001f));
            Assert.That(resumed.RawDistance, Is.EqualTo(Vector3.Distance(predicted.Position, resumed.Replayed.Position)).Within(.00001f),
                "The final actual correction is still measured in the original raw motor coordinates");
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void RefreshInstallsANewRoundMovementWithoutReplayingOldRoundInputs(bool alreadyWaiting, bool nextGeometryPending)
        {
            _terrain = new GameObject("Refresh round-boundary terrain").AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var before = State(); before.Movement = MovementMode.Air; before.PaperPose = PaperPose.Air;
            before.AirHumanOffset = .87f; before.CompactBody = true; StartFrom(before);
            var first = before;
            if (alreadyWaiting) { first.SupportCell = new Vector3Int(20, 0, 20); first.SupportTerrainRevision = 100; }
            Reconcile(first);
            Assert.That(_player.TerrainReplayPending, Is.EqualTo(alreadyWaiting));
            Assert.That(_player.transform.position, Is.EqualTo(before.Position));
            History.Add(new PlayerInputFrame { Sequence = 33, Revision = before.Revision, HeroRevision = before.HeroRevision, Move = Vector2.up });
            int previousEvents = _events.Count;
            _terrain.Clear(_terrain.Round + 1);
            var next = before; next.Position += Vector3.right * 2; next.AcknowledgedInput++;
            next.Movement = MovementMode.Human; next.PaperPose = PaperPose.None; next.AirHumanOffset = 0; next.CompactBody = false;
            if (nextGeometryPending) { next.SupportCell = new Vector3Int(40, 0, 40); next.SupportTerrainRevision = 200; }
            next.Ink = 67; next.ConsumedFire = 9;

            Refresh(next);

            Assert.That(_events.Count, Is.EqualTo(previousEvents + 1), "Refresh must not consume the round boundary before movement installs");
            var observation = _events.Last();
            Assert.That(observation.TerrainPendingAfter, Is.EqualTo(nextGeometryPending));
            Assert.That(History, Is.Empty, "Unacknowledged inputs from the old round cannot move the new round's avatar");
            Assert.That(observation.ReplaySteps, Is.Zero);
            Assert.That(observation.Replayed.Position, Is.EqualTo(next.Position));
            Assert.That(observation.Replayed.Movement, Is.EqualTo(next.Movement));
            Assert.That(observation.Replayed.AirHumanOffset, Is.Zero);
            Assert.That(observation.Replayed.AcknowledgedInput, Is.EqualTo(next.AcknowledgedInput));
            var installed = (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(_player);
            Assert.That(installed.Ink, Is.EqualTo(67)); Assert.That(installed.ConsumedFire, Is.EqualTo(9));
            Assert.That(_player.transform.position, Is.EqualTo(next.Position));
            Assert.That(observation.RawDistance, Is.EqualTo(2).Within(.00001f), "The actual boundary correction remains in raw telemetry");
            Refresh(next);
            Assert.That(_events.Count, Is.EqualTo(previousEvents + 1), "The boundary is consumed only once; waiting refreshes do not invent corrections");
        }

        [Test] public void PredictionObservingANewRoundWaitsForItsAuthoritativeMovementFrame()
        {
            _terrain = new GameObject("Prediction first observes round terrain").AddComponent<BlockTerrain>(); _terrain.Initialize(null);
            var before = State(); StartFrom(before); Reconcile(before);
            _terrain.Clear(_terrain.Round + 1);
            bool shot = (bool)typeof(PrototypePlayer).GetMethod("PredictInput", Private).Invoke(_player,
                new object[] { new PlayerInputFrame { Sequence = 31, Revision = before.Revision, HeroRevision = before.HeroRevision,
                    Move = Vector2.up }, true, MatchPhase.Practice });
            Assert.That(shot, Is.False); Assert.That(_player.TerrainReplayPending, Is.False, "An observed boundary is not missing collision geometry");
            var current = (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(_player);
            Assert.That(current.Position, Is.EqualTo(before.Position)); Assert.That(current.SimulatedAt, Is.EqualTo(before.SimulatedAt));
            var next = before; next.Position += Vector3.forward * 2; next.AcknowledgedInput++;
            next.SupportCell = new Vector3Int(40, 0, 40); next.SupportTerrainRevision = 200;

            Refresh(next);

            Assert.That(_events.Count, Is.EqualTo(2)); Assert.That(_events.Last().TerrainPendingAfter, Is.True);
            Assert.That(_events.Last().Replayed.Position, Is.EqualTo(next.Position));
            Assert.That(_player.transform.position, Is.EqualTo(next.Position));
            Assert.That(_events.Last().RawDistance, Is.EqualTo(2).Within(.00001f));
        }

        [TestCase("life")] [TestCase("round")] [TestCase("death")] [TestCase("timeout")]
        public void OldReplayDependenciesDoNotSurviveSimulationBoundaries(string boundary)
        {
            _terrain = new GameObject("Prediction dependency boundary terrain").AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            var authority = State(); authority.SupportCell = new Vector3Int(20, 0, 20); authority.SupportTerrainRevision = 100;
            StartFrom(authority); Reconcile(authority);
            Assert.That(_player.TerrainReplayPending, Is.True);
            authority.SupportTerrainRevision = 0; authority.SupportCell = default;
            if (boundary == "life") authority.Revision++;
            if (boundary == "round") _terrain.Clear(_terrain.Round + 1);
            if (boundary == "death") { authority.Health = 0; authority.Movement = MovementMode.Dead; }
            if (boundary == "timeout") authority.InputTimedOut = true;
            Reconcile(authority);
            Assert.That(_player.TerrainReplayPending, Is.False, "old collision dependencies cannot block a new simulation boundary");
            Assert.That(_events[_events.Count - 1].Replayed.Position, Is.EqualTo(authority.Position));
        }

        IEnumerator Publish(BlockWorld world, params Vector3Int[] changedChunks)
        {
            uint revision = _terrain.World.Revision + 1;
            var chunks = changedChunks.Length > 0 ? changedChunks : world.Chunks.Keys.Concat(_terrain.World.Chunks.Keys).Distinct().ToArray();
            world.Seal(revision, chunks);
            Assert.That(_terrain.ApplyRemoteSnapshot(world.EncodeSnapshot()), Is.True);
            for (int tick = 0; tick < 1000 && (_terrain.PendingTransactions != 0 || _terrain.World.Revision != revision); tick++)
            { _terrain.Tick(false, tick * (double)Dt, Dt); yield return null; }
            Assert.That(_terrain.PendingTransactions, Is.Zero, "support geometry must publish before resuming prediction");
            Assert.That(_terrain.World.Revision, Is.EqualTo(revision)); Physics.SyncTransforms();
        }
    }
}
#endif
