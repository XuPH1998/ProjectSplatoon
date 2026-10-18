#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
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
    /// <summary>Original TrainingGround wall traversal with a published, single attached coat.</summary>
    public sealed class BlockSurfaceWallPlayTests
    {
        const float Dt = 1f / 60;
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        readonly List<string> _trace = new();
        BlockTerrain Terrain => _match.Blocks;
        PlayerMotorSimulation Motor => (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(_host);

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local?.SwimBody != null && PrototypeMatch.Current?.Blocks?.Dynamic != null,
                "Real host, authored paper profile and surface world");
            _host = PrototypePlayer.Local; _match = PrototypeMatch.Current;
            _match.enabled = false; foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match); _fixture.Tick();
            Terrain.Clear(_match.State.Value.Round); BlockAttackService.Clear(); _fixture.Tick();
            Reset(new Vector3(0, .04f, -20));
            Assert.That(Terrain.SurfaceOnly, Is.True);
            Assert.That(PrototypeArena.Current.gameObject.scene.name, Is.EqualTo("TrainingGround"));
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_trace.Count > 0)
            {
                const string output = "Reports/BlockPhysics20261017/SurfaceWall";
                Directory.CreateDirectory(output);
                File.WriteAllLines(output + "/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".jsonl", _trace);
            }
            _fixture?.Dispose();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Restore(PlayerSnapshot state)
        {
            state.SimulatedAt = Math.Max(state.SimulatedAt, Math.Max(Terrain.SimulationTime, _host.NetworkManager.ServerTime.Time));
            Motor.Restore(state); _host.Snapshot.Value = state; Physics.SyncTransforms();
        }

        void Reset(Vector3 position)
        {
            var s = _host.Snapshot.Value;
            s.Position = position; s.Team = 1; s.Yaw = s.BodyYaw = s.Pitch = 0;
            s.PlanarVelocity = s.Velocity = Vector3.zero; s.VerticalSpeed = 0;
            s.Health = 100; s.Ink = 40; s.LifeState = PlayerLifeState.Alive; s.ProtectedUntil = 0;
            s.Swimming = s.CompactBody = s.SwimWasHeld = s.FriendlyInkContact = false;
            s.SwimSource = s.AirSwimSource = SwimSurface.None; s.PaperPose = PaperPose.None;
            s.PaperCenter = Vector3.zero; s.PaperRotation = Quaternion.identity; s.Grounded = true;
            s.AirHumanOffset = s.CameraRebaseOffset = 0; s.ConsumedJump = 0; s.Movement = MovementMode.Human;
            s.WeaponPhase = WeaponPhase.Idle; s.SubPhase = SubWeaponPhase.Idle;
            s.AttackNeedsRelease = s.SubNeedsRelease = s.Firing = false;
            s.AttackMoveUntil = s.AttackRecoveryUntil = s.SubRecoveryUntil = s.InkRecoverAt = 0;
            s.MistUntil = s.MistExposure = 0; s.BurstRemaining = 0; s.NextShotAt = s.BurstReadyAt = 0;
            Restore(s);
        }

        void Tick(bool swim, Vector2 move = default, uint jump = 0, string stage = "wall")
        {
            var s = _host.Snapshot.Value;
            _fixture.Now = Math.Max(_fixture.Now, Math.Max(s.SimulatedAt, Terrain.SimulationTime)); _fixture.Tick();
            ((SortedDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(_host)).Clear();
            typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(_host, new PlayerInputFrame
            {
                Revision = s.Revision, HeroRevision = s.HeroRevision, Sequence = s.AcknowledgedInput + 1,
                Swim = swim, JumpSequence = jump, Move = move, Look = new Vector2(270, 0),
                FireSequence = s.ConsumedFire, ReleaseSequence = s.ConsumedRelease
            });
            typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(_host, _host.NetworkManager.ServerTime.Time);
            _host.Simulate(Dt, Math.Max(s.SimulatedAt, Terrain.SimulationTime) + Dt, _match.State.Value.Phase);
            Physics.SyncTransforms();
            var after = _host.Snapshot.Value;
            _trace.Add(stage + " " + JsonUtility.ToJson(after));
            Assert.That(after.InputTimedOut, Is.False, stage);
        }

        static RaycastHit MapHit(Vector3 origin, Vector3 direction, float distance, string label)
        {
            Assert.That(Physics.Raycast(origin, direction, out var hit, distance, PlayerMotorSimulation.WorldMask,
                QueryTriggerInteraction.Ignore), Is.True, label);
            Assert.That(BlockTerrain.Current.IsAuthoredMapCollider(hit.collider), Is.True, label + " must use original map collision");
            return hit;
        }

        IEnumerator Coat(RaycastHit hit, float radius)
        {
            bool complete = false; int added = 0;
            Terrain.QueueBuild(hit.point, hit.normal, 1, 2048, radius, true, result => { complete = true; added = result.Added; });
            yield return _fixture.Publish(() => complete);
            Assert.That(added, Is.GreaterThan(0), "Original map face accepts its first coat");
            Assert.That(Terrain.TrySurfaceContactFromMap(hit, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(1)); Assert.That(contact.Stable, Is.True);
            Assert.That(Vector3.Distance(contact.Point, hit.point), Is.LessThan(.0001f));
            Assert.That(Terrain.World.TryGet(contact.Cell, out var cell) && cell.SurfaceAttached && cell.Anchored, Is.True);
        }

        void AssertWallPlane(PlayerSnapshot s, float mapX)
        {
            Assert.That(s.Movement, Is.EqualTo(MovementMode.WallInk));
            Assert.That(s.PaperPose, Is.EqualTo(PaperPose.Wall));
            Assert.That(s.SwimSource, Is.EqualTo(SwimSurface.Friendly));
            Assert.That(s.FriendlyInkContact && s.HasInkRecovery, Is.True);
            Assert.That(s.WallPoint.x, Is.EqualTo(mapX).Within(.002f), "Wall anchor remains on the authored collision plane");
            Assert.That(s.PaperCenter.x, Is.EqualTo(mapX + _host.SwimBody.Profile.SurfaceOffset).Within(.003f),
                "The invisible-to-character coat cannot move the paper out by one cube");
            Assert.That(Terrain.PublishedTraversalPoseFits(_host, s), Is.True, "Full paper silhouette fits the original map scene");
            Assert.That(PlayerMotorSimulation.Query(s.Position + Vector3.up * .35f + s.WallNormal * .08f,
                -s.WallNormal, .8f, out var current), Is.True);
            Assert.That(current.IsBlock && current.Stable && current.Climbable && current.Owner == 1, Is.True);
            Assert.That(current.Point.x, Is.EqualTo(mapX).Within(.002f));
            Assert.That(Terrain.World.TryGet(current.Cell, out var coat) && coat.SurfaceAttached, Is.True);
        }

        [UnityTest, Timeout(600000)] public IEnumerator OriginalMapCoatPreservesFriendlyWallEntryRecoveryClimbJumpAndMantle()
        {
            var wall = MapHit(new Vector3(15, 1.5f, 0), Vector3.left, 4, "PlatformBody_1 wall");
            Assert.That(wall.collider.name, Is.EqualTo("PlatformBody_1"));
            Assert.That(wall.point.x, Is.EqualTo(13).Within(.002f));
            var floor = MapHit(new Vector3(14.5f, 1, 0), Vector3.down, 2, "Wall approach ground");
            var top = MapHit(new Vector3(11.5f, 4, 0), Vector3.down, 2, "Authored platform top");
            Assert.That(top.point.y, Is.EqualTo(3).Within(.002f));
            yield return Coat(wall, 2.1f);
            yield return Coat(floor, 2.1f);
            yield return Coat(top, 2.1f);
            Assert.That(Terrain.Dynamic.Count, Is.Zero); Assert.That(Terrain.World.Falling.Count, Is.Zero);
            byte[] originalCoat = Terrain.World.EncodeSnapshot();

            Reset(new Vector3(15.5f, floor.point.y + .04f, 0));
            var controller = _host.GetComponent<CharacterController>();
            for (int i = 0; i < 12; i++)
            {
                Tick(false, stage: "settle");
                if (_host.Snapshot.Value.Grounded && controller.isGrounded) break;
            }
            Assert.That(_host.Snapshot.Value.Grounded && controller.isGrounded, Is.True, "Entry starts from actual human landing");
            for (int i = 0; i < 160 && _host.Snapshot.Value.Movement != MovementMode.WallInk; i++)
                Tick(true, Vector2.up, stage: "entry");
            var entered = _host.Snapshot.Value; AssertWallPlane(entered, wall.point.x);

            entered.Ink = 30; entered.InkRecoverAt = 0; Restore(entered);
            float beforeInk = _host.Snapshot.Value.Ink;
            const int recoverTicks = 10;
            for (int i = 0; i < recoverTicks; i++) { Tick(true, stage: "recover"); AssertWallPlane(_host.Snapshot.Value, wall.point.x); }
            var hero = GameplayConfig.GetHero(_host.Snapshot.Value.HeroId);
            Assert.That(_host.Snapshot.Value.Ink - beforeInk, Is.EqualTo(hero.SwimRecoverInk * recoverTicks * Dt).Within(.002f));

            float y = _host.Snapshot.Value.Position.y;
            const int climbTicks = 15;
            for (int i = 0; i < climbTicks; i++) Tick(true, Vector2.up, stage: "climb");
            var climbed = _host.Snapshot.Value; AssertWallPlane(climbed, wall.point.x);
            Assert.That(climbed.Position.y - y, Is.EqualTo(hero.WallSwimSpeed * climbTicks * Dt).Within(.02f));

            Tick(true, Vector2.up, 1, "jump");
            var jumped = _host.Snapshot.Value;
            Assert.That(jumped.Movement, Is.EqualTo(MovementMode.Air));
            Assert.That(jumped.VerticalSpeed, Is.GreaterThan(0));
            Assert.That(Vector3.Dot(jumped.PlanarVelocity, climbed.WallNormal), Is.GreaterThan(0));
            Assert.That(jumped.HasInkRecovery, Is.False);

            Restore(climbed); bool mantle = false;
            for (int i = 0; i < 180; i++)
            {
                Tick(true, Vector2.up, stage: "mantle");
                var state = _host.Snapshot.Value;
                mantle |= state.Movement == MovementMode.Mantle;
                if (mantle && state.Grounded && state.Position.y > top.point.y - .1f) break;
            }
            var landed = _host.Snapshot.Value;
            Assert.That(mantle, Is.True, "Production motor must traverse its mantle branch");
            Assert.That(landed.Grounded, Is.True); Assert.That(landed.Position.y, Is.EqualTo(top.point.y).Within(.08f));
            Assert.That(landed.PaperPose, Is.EqualTo(PaperPose.Ground));
            Assert.That(landed.SwimSource, Is.EqualTo(SwimSurface.Friendly)); Assert.That(landed.HasInkRecovery, Is.True);
            Assert.That(landed.PaperCenter.y, Is.EqualTo(top.point.y + _host.SwimBody.Profile.SurfaceOffset).Within(.005f));
            CollectionAssert.AreEqual(originalCoat, Terrain.World.EncodeSnapshot(), "Traversal never grows, moves or consumes the coating");
        }

        [UnityTest, Timeout(600000)] public IEnumerator FriendlyFreeBlockCannotGrantWallAttachmentOrInkRecovery()
        {
            Vector3 center = new Vector3(2, 1.1f, -20);
            Assert.That(Terrain.Dynamic.TrySpawn(1, center, Quaternion.identity, out var loose), Is.True);
            loose.Body.Sleep();
            Assert.That(PlayerMotorSimulation.Query(center + Vector3.right * .5f, Vector3.left, 1, out var side), Is.True);
            Assert.That(side.IsBlock && side.Owner == 1, Is.True); Assert.That(side.Stable, Is.False); Assert.That(side.Climbable, Is.False);
            Assert.That(PlayerMotorSimulation.CanClimb(side, 1), Is.False, "Even friendly loose geometry grants no wall capability");
            Reset(center + Vector3.up * (BlockWorld.CellSize * .5f + .02f));
            Assert.That(PlayerMotorSimulation.TryGetGround(_host.Snapshot.Value.Position, out var support), Is.True);
            Assert.That(support.IsBlock && !support.Stable && support.Owner == 1, Is.True, "The real loose collider is the initial support");
            Tick(true, stage: "free-support");
            var state = _host.Snapshot.Value;
            Assert.That(state.Movement, Is.Not.EqualTo(MovementMode.WallInk));
            Assert.That(state.FriendlyInkContact, Is.False); Assert.That(state.HasInkRecovery, Is.False);
            Assert.That(state.SwimSource, Is.Not.EqualTo(SwimSurface.Friendly));
            yield break;
        }
    }
}
#endif
