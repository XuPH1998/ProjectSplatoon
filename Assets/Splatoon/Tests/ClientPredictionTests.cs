#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class GameplayLatencyTests
    {
        [Test] public void SamplesRefreshWithoutReliableTrafficAndExpire()
        {
            var latency = new GameplayLatency();
            Assert.That(latency.TryRead(0, out _), Is.False);
            Assert.That(latency.TryBegin(10, out uint first), Is.True);
            Assert.That(latency.Complete(first, 10.02), Is.True);
            Assert.That(latency.TryRead(10.02, out var ms), Is.True);
            Assert.That(ms, Is.EqualTo(20).Within(.0001));
            Assert.That(latency.TryBegin(10.49, out _), Is.False);
            Assert.That(latency.TryBegin(10.5, out uint next), Is.True);
            Assert.That(latency.Complete(next, 10.62), Is.True);
            Assert.That(latency.Milliseconds, Is.EqualTo(120).Within(.0001));
            Assert.That(latency.TryRead(13.62, out _), Is.True);
            Assert.That(latency.TryRead(13.621, out _), Is.False);
            Assert.That(latency.Samples, Is.EqualTo(2));
        }
        [Test] public void ReorderedDuplicateStaleAndPreviousSessionRepliesCannotRefreshHud()
        {
            var latency = new GameplayLatency();
            latency.TryBegin(0, out uint a); latency.TryBegin(.5, out uint b);
            Assert.That(latency.Complete(b, .6), Is.True);
            Assert.That(latency.Complete(a, .7), Is.False);
            Assert.That(latency.Complete(b, .8), Is.False);
            latency.TryBegin(1, out uint c);
            Assert.That(latency.Complete(c, 4.01), Is.False);
            Assert.That(latency.Complete(900, 4.02), Is.False);
            latency.TryBegin(5, out uint old); latency.Reset(); latency.TryBegin(5.1, out uint current);
            Assert.That(latency.Complete(old, 5.15), Is.False);
            Assert.That(latency.TryRead(5.15, out _), Is.False);
            Assert.That(latency.Complete(current, 5.2), Is.True);
        }
        [Test] public void LossAndLongFramesKeepProbeStorageAndSendRateBounded()
        {
            var latency = new GameplayLatency(); latency.TryBegin(0, out uint old);
            for (int i = 1; i < 40; i++) Assert.That(latency.TryBegin(i * .5, out _), Is.True);
            Assert.That(latency.Complete(old, 20), Is.False);
            Assert.That(latency.TryBegin(100, out _), Is.True);
            Assert.That(latency.TryBegin(100, out _), Is.False);
            Assert.That(latency.TryBegin(double.NaN, out _), Is.False);
        }
        [Test] public void InputAcksIncludeQueueTimeAndDoNotCountReplayOrOldLives()
        {
            var timing = new InputAcknowledgementTiming();
            timing.Track(1, 5, 1); timing.Track(2, 5, 1.02); timing.Track(3, 5, 1.04);
            timing.Acknowledge(2, 5, 1.1);
            Assert.That(timing.Samples, Is.EqualTo(2));
            Assert.That(timing.TotalMilliseconds, Is.EqualTo(180).Within(.0001));
            timing.Acknowledge(2, 5, 2); Assert.That(timing.Samples, Is.EqualTo(2));
            timing.Acknowledge(3, 6, 2); Assert.That(timing.Samples, Is.EqualTo(2));
            timing.Track(4, 6, 3); timing.DiscardPending(); timing.Acknowledge(4, 6, 4);
            Assert.That(timing.Samples, Is.EqualTo(2));
            timing.Reset(); Assert.That(timing.Samples, Is.Zero);
        }
    }

    // Runs the actual owner reconciliation and motor with real Unity physics and paper assets.
    // Network reception is supplied explicitly; independent-process RPC coverage is separate.
    public sealed class ClientPredictionTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        PrototypePlayer _player;
        PlayerMotorSimulation _motor;
        PaintSurface _floor;
        PlayerSnapshot _authority;
        List<PlayerInputFrame> History => (List<PlayerInputFrame>)typeof(PrototypePlayer).GetField("_history", Private).GetValue(_player);
        PlayerSnapshot Predicted => (PlayerSnapshot)typeof(PrototypePlayer).GetField("_predicted", Private).GetValue(_player);
        object Call(string name, params object[] args) => typeof(PrototypePlayer).GetMethod(name, Private).Invoke(_player, args);
        void Set(string name, object value) => typeof(PrototypePlayer).GetField(name, Private).SetValue(_player, value);
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            var ground = new GameObject("prediction floor");
            var box = ground.AddComponent<BoxCollider>(); box.size = new Vector3(50, 1, 50); box.center = Vector3.down * .5f;
            _floor = ground.AddComponent<PaintSurface>(); _floor.Scores = true; _floor.WalkableSize = new Vector2(50, 50); _floor.InitializeOwnership(.25f);
            _player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab")).GetComponent<PrototypePlayer>();
            var controller = _player.GetComponent<CharacterController>();
            _motor = new PlayerMotorSimulation(controller); Set("_motor", _motor); Set("_controller", controller);
            _authority = new PlayerSnapshot { HeroId = 1, Revision = 5, Team = 1, Health = 100, Ink = 100,
                Position = Vector3.up * .04f, Grounded = true, Swimming = true, SwimWasHeld = true,
                SwimSource = SwimSurface.Neutral, Movement = MovementMode.GroundInk, RequiredPaintSequence = 100, SimulatedAt = 1 };
            _motor.Restore(_authority, false);
            // Start from a real resting contact, not an invented Grounded flag above the floor.
            for (int i = 0; i < 6; i++) _motor.Step(ref _authority, new PlayerInputFrame { Swim = true }, 1f / 60, 1, false);
            Assert.That(_authority.Grounded, Is.True, "authority fixture is resting on the floor");
            Set("_predicted", _authority);
        }
        [TearDown] public void Cleanup()
        {
            PaperBodyTests.ReleaseTestBodies(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        void Paint(byte team) { foreach (var region in _floor.GameplayRegions) for (int i = 0; i < region.Grid.Cells.Length; i++) region.Grid.Set(i, team); }
        PlayerInputFrame Input(uint sequence, uint jump = 0) => new() { Sequence = sequence, Revision = 5, Swim = true, Move = Vector2.up, JumpSequence = jump };
        void AddHistory(int count) { for (uint i = 1; i <= count; i++) History.Add(Input(i)); }
        void Reconcile(PlayerSnapshot state, bool sync = true, uint applied = 0) => Call("ReconcilePrediction", state, false, sync, applied, MatchPhase.Practice);
        void Refresh(PlayerSnapshot state, bool sync, uint applied) => Call("RefreshPredictionPaint", state, sync, applied, MatchPhase.Practice);

        [TestCase((byte)0)] [TestCase((byte)1)]
        public void RemotePaintLagDoesNotStopGroundReplayOrNewInput(byte ink)
        {
            Paint(ink); AddHistory(24); Reconcile(_authority);
            Assert.That(_player.LastReplaySteps, Is.EqualTo(24));
            Assert.That(_player.PaintReplayPending, Is.True);
            Assert.That(Predicted.Swimming, Is.True);
            Assert.That(Predicted.Position.z, Is.GreaterThan(.5f));
            float before = Predicted.Position.z;
            Call("PredictInput", Input(25), true, MatchPhase.Practice);
            Assert.That(Predicted.Position.z, Is.GreaterThan(before));
            Assert.That(_player.PredictionSteps, Is.EqualTo(1));
        }
        [Test] public void PaintCatchupReplaysLatestSnapshotOnceAndRespectsAcknowledgements()
        {
            AddHistory(20); Reconcile(_authority);
            var latest = _authority; latest.AcknowledgedInput = 8; latest.Position.z = .2f; latest.RequiredPaintSequence = 120;
            Refresh(latest, true, 100); Assert.That(_player.PaintReplayCount, Is.Zero);
            Refresh(latest, true, 120);
            Assert.That(_player.PaintReplayCount, Is.EqualTo(1)); Assert.That(_player.LastReplaySteps, Is.EqualTo(12));
            Assert.That(History[0].Sequence, Is.EqualTo(9)); Assert.That(_player.PaintReplayPending, Is.False);
            var final = Predicted; Refresh(latest, true, 130);
            Assert.That(Predicted.Position, Is.EqualTo(final.Position)); Assert.That(_player.PaintReplayCount, Is.EqualTo(1));
        }
        [Test] public void InitialSyncAndInputTimeoutStillPauseAndRecover()
        {
            AddHistory(10); Reconcile(_authority, false);
            Assert.That(_player.LastReplaySteps, Is.Zero);
            Call("PredictInput", Input(11), false, MatchPhase.Practice);
            Assert.That(Predicted.Position, Is.EqualTo(_authority.Position));
            Refresh(_authority, true, 0); Assert.That(_player.LastReplaySteps, Is.EqualTo(10), "initial map is ready even if live paint is still ahead");
            var timeout = _authority; timeout.InputTimedOut = true; Reconcile(timeout);
            Assert.That(History, Is.Empty); History.Add(Input(12));
            Call("PredictInput", Input(12), true, MatchPhase.Practice); Assert.That(Predicted.Position, Is.EqualTo(timeout.Position));
            var recovered = _authority; recovered.AcknowledgedInput = 11; Reconcile(recovered);
            Assert.That(_player.LastReplaySteps, Is.EqualTo(1)); Assert.That(Predicted.Position.z, Is.GreaterThan(0));
        }
        [Test] public void FreshEnemyInkCorrectsPredictedSwimmingWithoutClientAuthority()
        {
            Paint(1); AddHistory(18); Reconcile(_authority); Assert.That(Predicted.Swimming, Is.True);
            Paint(2);
            Assert.That(PrototypeArena.TryGetGround(_authority.Position, out byte owner), Is.True);
            Assert.That(owner, Is.EqualTo(2), "new ink reached the queried ground");
            Refresh(_authority, true, 100);
            Assert.That(_player.PaintReplayCount, Is.EqualTo(1));
            Assert.That(Predicted.Swimming, Is.False, JsonUtility.ToJson(Predicted)); Assert.That(Predicted.HasInkRecovery, Is.False);
            Assert.That(Predicted.Movement, Is.EqualTo(MovementMode.Human));
            Assert.That(_authority.Team, Is.EqualTo(1)); Assert.That(_authority.Health, Is.EqualTo(100));
        }
        [Test] public void DeathAndNewLifeApplyImmediatelyDespiteMissingPaint()
        {
            AddHistory(18); Reconcile(_authority);
            var dead = _authority; dead.Health = 0; dead.Movement = MovementMode.Dead; Reconcile(dead, false);
            Assert.That(Predicted.Health, Is.Zero); Assert.That(_player.GetComponent<CharacterController>().enabled, Is.False);
            var respawn = _authority; respawn.Revision++; respawn.Team = 2; respawn.Position = new Vector3(2, .04f, 2);
            Reconcile(respawn, false); Assert.That(History, Is.Empty);
            Assert.That(Predicted.Team, Is.EqualTo(2)); Assert.That(Predicted.Position, Is.EqualTo(respawn.Position));
            Assert.That(_player.GetComponent<CharacterController>().enabled, Is.True);
        }
        [Test] public void ReplayNeverBakesIntermediatePaperButAuthorityRestoreStillDoes()
        {
            _motor.Restore(_authority); var capture = _player.SwimBody.Capture;
            Assert.That(capture, Is.Not.Null); int version = capture.PoseVersion;
            AddHistory(60); Reconcile(_authority);
            Assert.That(_player.LastReplaySteps, Is.EqualTo(60)); Assert.That(capture.PoseVersion, Is.EqualTo(version));
            _player.SwimBody.ApplyCollision(Predicted);
            Assert.That(capture.PoseVersion, Is.EqualTo(version + 1));
            Assert.That(_player.SwimBody.HitVolume.transform.position, Is.EqualTo(Predicted.PaperCenter));
            _motor.Restore(_authority);
            Assert.That(capture.PoseVersion, Is.EqualTo(version + 2), "authority default still publishes the requested pose");
        }
        [Test] public void JumpReplayKeepsMotorAndPaperPoseDeterministic()
        {
            for (uint i = 1; i <= 30; i++) History.Add(Input(i, 1));
            Reconcile(_authority); var expected = Predicted;
            Assert.That(expected.Grounded, Is.False); Assert.That(expected.PaperPose, Is.EqualTo(PaperPose.Air));
            Reconcile(_authority);
            Assert.That(Vector3.Distance(Predicted.Position, expected.Position), Is.LessThan(.0001f));
            Assert.That(Vector3.Distance(Predicted.PaperCenter, expected.PaperCenter), Is.LessThan(.0001f));
        }
    }
}
#endif
