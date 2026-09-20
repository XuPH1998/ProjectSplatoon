#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class PaperTraversalTests
    {
        const float Dt = 1f / 60;
        PrototypePlayer player;
        PlayerMotorSimulation motor;
        PlayerSnapshot state;
        PaintSurface floor, wall;
        double now;

        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            HeroMigrationTests.Load();
            var ground = new GameObject("ground");
            var box = ground.AddComponent<BoxCollider>(); box.size = new Vector3(30,1,30); box.center = Vector3.down*.5f;
            floor = ground.AddComponent<PaintSurface>(); floor.Scores = true; floor.WalkableSize = new Vector2(30,30); floor.InitializeOwnership(.125f);
            var support = new GameObject("wall");
            box = support.AddComponent<BoxCollider>(); box.size = new Vector3(8,4,2); box.center = new Vector3(0,2,5);
            wall = support.AddComponent<PaintSurface>(); wall.SurfaceId = 901;
            wall.WallRegions = new[] { new PaintRegion { Id=1,Origin=new Vector3(0,2,4),Rotation=Quaternion.FromToRotation(Vector3.up,Vector3.back),Size=new Vector2(8,4),Climbable=true } };
            wall.InitializeOwnership(.125f); PaintWall(1);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            player = Object.Instantiate(prefab).GetComponent<PrototypePlayer>();
            motor = new PlayerMotorSimulation(player.GetComponent<CharacterController>());
            Reset(1);
        }
        [TearDown] public void Cleanup()
        {
            PaperBodyTests.ReleaseTestBodies();
            LubanConfigService.Current.Reset(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        }
        void Reset(int hero)
        {
            player.SwimBody.Bind(PaperBodyTests.Profile(GameplayConfig.GetHero(hero).CharacterPrefabAddress.Split('/').Last()));
            state = new PlayerSnapshot { HeroId=hero,Team=1,Health=100,Ink=100,Grounded=true,Position=new Vector3(0,.04f,1.5f) };
            now=0; motor.Restore(state);
        }
        void PaintFloor(byte owner) { for(int i=0;i<floor.Ownership.Cells.Length;i++) floor.Ownership.Set(i,owner); }
        void PaintWall(byte owner) { foreach(var r in wall.WallRegions) for(int i=0;i<r.Grid.Cells.Length;i++) r.Grid.Set(i,owner); }
        void Tick(Vector2 move, float yaw=0, bool hit=true, uint jump=0, bool swim=true)
        {
            now+=Dt;
            motor.Step(ref state,new PlayerInputFrame { Swim=swim,Move=move,Look=new Vector2(yaw,0),JumpSequence=jump },Dt,now,false);
            player.SwimBody.ApplyCollision(state); player.SwimBody.HitVolume.enabled &= hit; Physics.SyncTransforms();
        }
        void Enter(Vector2 move, bool hit=true)
        {
            for(int i=0;i<160 && state.Movement!=MovementMode.WallInk;i++) Tick(move,hit:hit);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk),"continuous entry from 2.5 metres");
            Assert.That(state.Position.z,Is.EqualTo(4-player.GetComponent<CharacterController>().radius-.015f).Within(.045f));
            Assert.That(Vector3.Distance(state.Position,player.transform.position),Is.LessThan(.0001f));
            Assert.That(state.PaperPose,Is.EqualTo(PaperPose.Wall));
        }
        [Test] public void AllHeroesApproachFromFriendlyAndNeutralGroundStraightAndDiagonally()
        {
            for(int hero=1;hero<=5;hero++) foreach(byte owner in new byte[]{0,1}) foreach(float x in new[]{0f,.5f})
            {
                Reset(hero); PaintFloor(owner); Enter(new Vector2(x,1).normalized);
                var start=state.Position;
                for(int i=0;i<15;i++) Tick(Vector2.up);
                Assert.That(state.Position.y-start.y,Is.EqualTo(1).Within(.015f),"wall speed hero "+hero);
                Assert.That(state.Swimming,Is.True);
            }
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void DescendingWallResolvesDestinationGround(byte owner)
        {
            Enter(Vector2.up); for(int i=0;i<20;i++) Tick(Vector2.up);
            PaintFloor(owner);
            for(int i=0;i<80 && !state.Grounded;i++) Tick(Vector2.down);
            Assert.That(state.Grounded,Is.True); Assert.That(state.Position.y,Is.InRange(-.05f,.1f));
            Assert.That(state.Swimming,Is.EqualTo(owner!=2));
            Assert.That(state.SwimSource,Is.EqualTo(owner==2 ? SwimSurface.None : owner==1 ? SwimSurface.Friendly : SwimSurface.Neutral));
            Assert.That(state.Movement,Is.EqualTo(owner==2 ? MovementMode.Human : MovementMode.GroundInk));
        }
        [Test] public void WallAxesCameraRotationAndReplayKeepContinuousMotion()
        {
            Enter(Vector2.up); for(int i=0;i<20;i++) Tick(Vector2.up);
            foreach(var move in new[]{Vector2.up,Vector2.down,Vector2.left,Vector2.right,new Vector2(.6f,.8f),new Vector2(-.6f,-.8f)})
            {
                var before=state; double time=now;
                for(int i=0;i<8;i++) Tick(move, 45+i*35);
                var expected=state;
                var delta=(Vector3.up*move.y+Vector3.right*move.x)*GameplayConfig.GetHero(1).WallSwimSpeed*8*Dt;
                // PhysX may settle the skin along the wall normal when changing
                // axes. Measure wall speed in its plane, and bound that gap separately.
                Assert.That(Vector3.Distance(Vector3.ProjectOnPlane(state.Position-before.Position,state.WallNormal),delta),Is.LessThan(.005f));
                Assert.That(4-state.Position.z,Is.InRange(.35f,.385f));
                Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk));
                Assert.That(Quaternion.Angle(state.PaperRotation,before.PaperRotation),Is.LessThan(.001f));
                state=before; now=time; motor.Restore(state);
                for(int i=0;i<8;i++) Tick(move,45+i*35);
                Assert.That(Vector3.Distance(state.Position,expected.Position),Is.LessThan(.001f));
                Assert.That(state.PaperAnimationTime,Is.EqualTo(expected.PaperAnimationTime).Within(.0001f));
                Assert.That(Vector3.Distance(state.PaperCenter,expected.PaperCenter),Is.LessThan(.001f));
            }
        }
        [Test] public void OwnHitProxyDoesNotChangeApproachWallAndJumpTrajectory()
        {
            PlayerSnapshot[] Run(bool hit)
            {
                Reset(1); var path=new PlayerSnapshot[100];
                for(int i=0;i<path.Length;i++) { Tick(i>=80 ? Vector2.down : Vector2.up,hit:hit,jump:i>=80 ? 1u : 0u); path[i]=state; }
                return path;
            }
            var withHit=Run(true); var withoutHit=Run(false);
            for(int i=0;i<withHit.Length;i++)
            {
                Assert.That(Vector3.Distance(withHit[i].Position,withoutHit[i].Position),Is.LessThan(.0001f),"tick "+i);
                Assert.That(withHit[i].Movement,Is.EqualTo(withoutHit[i].Movement));
            }
            Assert.That(withHit.Any(s=>s.Movement==MovementMode.WallInk),Is.True);
            Assert.That(withHit.Last().Movement,Is.EqualTo(MovementMode.Air));
        }
        [Test] public void PaperMayOverhangWallAndCapsuleSlidesAlongObstacle()
        {
            Enter(Vector2.up); for(int i=0;i<18;i++) Tick(Vector2.up);
            for(int i=0;i<56;i++) Tick(Vector2.right);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.PaperCenter.x+player.SwimBody.Profile.Size.x/2,Is.GreaterThan(4));
            Reset(1); Enter(Vector2.up); for(int i=0;i<18;i++) Tick(Vector2.up);
            var obstacle=new GameObject("actual obstacle"); var box=obstacle.AddComponent<BoxCollider>();
            box.center=new Vector3(1,2,3.5f); box.size=new Vector3(.3f,4,1); Physics.SyncTransforms();
            float y=state.Position.y;
            for(int i=0;i<25;i++) Tick(new Vector2(.8f,.6f));
            Assert.That(state.Position.x,Is.LessThan(.6f));
            Assert.That(state.Position.y-y,Is.GreaterThan(.9f),"slide retains vertical movement");
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk));
        }
        [TestCase(2)]
        public void InvalidWallBlocksCapsuleWithoutEnteringPaperWall(byte owner)
        {
            PaintWall(owner); for(int i=0;i<120;i++) Tick(Vector2.up);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.GroundInk));
            Assert.That(state.Position.z,Is.InRange(3.5f,3.72f));
        }
        [Test] public void PaperCrossesCoplanarSeamButEnemyNeighbourDetachesImmediately()
        {
            Enter(Vector2.up); for(int i=0;i<18;i++) Tick(Vector2.up);
            var original=wall.WallRegions[0];
            PaintRegion Half(int id,float x) => new() { Id=id,Origin=original.Origin+Vector3.right*x,
                Rotation=original.Rotation,Size=new Vector2(3.96f,4),Climbable=true };
            wall.WallRegions=new[]{Half(2,-2.02f),Half(3,2.02f)}; wall.InitializeOwnership(.125f); PaintWall(1);
            state.Position.x=-.07f; var before=state; double time=now; motor.Restore(state);
            Tick(Vector2.right);
            Assert.That(state.HasInkRecovery,Is.False,"coplanar seam has no ink contact even while traversal grace remains");
            Tick(Vector2.right);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk));
            Assert.That(state.Position.x,Is.GreaterThan(.04f));
            foreach(var r in wall.WallRegions) if(r.Id==3) for(int i=0;i<r.Grid.Cells.Length;i++) r.Grid.Set(i,2);
            state=before; now=time; motor.Restore(state); Tick(Vector2.right);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.Air));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(8)]
        public void NeutralWallHasReducedSpeedAndPreservesSourceThroughJumpAndMantle(int hero)
        {
            Reset(hero); PaintWall(0); Enter(Vector2.up);
            var start = state.Position;
            for (int i=0;i<15;i++) Tick(Vector2.up);
            Assert.That(state.Position.y-start.y,Is.EqualTo(.75f).Within(.015f));
            Assert.That(state.SwimSource,Is.EqualTo(SwimSurface.Neutral));
            Assert.That(state.HasInkRecovery,Is.False);
            Assert.That(InkCharacterView.ShouldEmitSwimEffect(state),Is.False);
            var before=state; double time=now;
            Tick(Vector2.down,jump:1);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.Air));
            Assert.That(state.AirSwimSource,Is.EqualTo(SwimSurface.Neutral));
            Tick(Vector2.zero,jump:1,swim:false); Tick(Vector2.zero,jump:1);
            Assert.That(state.SwimSource,Is.EqualTo(SwimSurface.Neutral));
            state=before; now=time; motor.Restore(state);
            bool mantle=false;
            for(int i=0;i<140;i++)
            {
                Tick(Vector2.up);
                if(state.Movement==MovementMode.Mantle)
                { mantle=true; Assert.That(state.SwimSource,Is.EqualTo(SwimSurface.Neutral)); Assert.That(state.HasInkRecovery,Is.False); }
                if(mantle && state.Movement!=MovementMode.Mantle) break;
            }
            Assert.That(mantle,Is.True);
        }
        [Test] public void RepaintWhileStationaryOrJumpingUsesActualContactAndReplayAgrees()
        {
            Enter(Vector2.up); for(int i=0;i<18;i++) Tick(Vector2.up);
            PaintWall(0); Tick(Vector2.zero);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk)); Assert.That(state.HasInkRecovery,Is.False);
            var before=state; double time=now;
            Tick(Vector2.right); var expected=state;
            state=before; now=time; motor.Restore(state); Tick(Vector2.right);
            Assert.That(Vector3.Distance(state.Position,expected.Position),Is.LessThan(.0001f));
            Assert.That(Vector3.ProjectOnPlane(state.Velocity,state.WallNormal).magnitude,Is.EqualTo(3).Within(.01f));
            PaintWall(1); Tick(Vector2.right);
            Assert.That(state.HasInkRecovery,Is.True); Assert.That(Vector3.ProjectOnPlane(state.Velocity,state.WallNormal).magnitude,Is.EqualTo(4).Within(.01f));
            PaintWall(0); Tick(Vector2.zero,jump:1);
            Assert.That(state.AirSwimSource,Is.EqualTo(SwimSurface.Neutral));
            state=before; now=time; motor.Restore(state); PaintWall(2); Tick(Vector2.zero);
            Assert.That(state.Movement,Is.Not.EqualTo(MovementMode.WallInk));
        }
        [Test] public void NeutralCoplanarSeamKeepsReducedSpeed()
        {
            PaintWall(0); Enter(Vector2.up); for(int i=0;i<20;i++) Tick(Vector2.up);
            var original=wall.WallRegions[0];
            PaintRegion Half(int id,float x) => new() { Id=id,Origin=original.Origin+Vector3.right*x,
                Rotation=original.Rotation,Size=new Vector2(3.96f,4),Climbable=true };
            wall.WallRegions=new[]{Half(2,-2.02f),Half(3,2.02f)}; wall.InitializeOwnership(.125f); PaintWall(0);
            state.Position.x=-.07f; motor.Restore(state);
            for(int i=0;i<4;i++) Tick(Vector2.right);
            Assert.That(state.Movement,Is.EqualTo(MovementMode.WallInk)); Assert.That(state.Position.x,Is.GreaterThan(.04f));
            Assert.That(state.SwimSource,Is.EqualTo(SwimSurface.Neutral));
        }
        [Test] public void ContentSignatureRejectsDifferentHitProxyContacts()
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            var baseline=GameplayContentSignature.Compute(new byte[]{1},"paper-test",player);
            bool ignored=Physics.GetIgnoreLayerCollision(SwimBody.HitProxyLayer,0);
            try
            {
                Physics.IgnoreLayerCollision(SwimBody.HitProxyLayer,0,!ignored);
                CollectionAssert.AreNotEqual(baseline,GameplayContentSignature.Compute(new byte[]{1},"paper-test",player));
            }
            finally { Physics.IgnoreLayerCollision(SwimBody.HitProxyLayer,0,ignored); }
            Assert.That(PlayerSnapshot.ProtocolVersion,Is.EqualTo(32)); // Current secondary-weapon wire contract.
        }
        [Test] public void WallJumpCanSwitchTwiceAndAirEntryRejectsEnemyWall()
        {
            for(int hero=1;hero<=5;hero++)
            {
                Reset(hero); PaintWall(1); Enter(Vector2.up); for(int i=0;i<15;i++) Tick(Vector2.up);
                Tick(Vector2.down,jump:1);
                Assert.That(state.AirSwimSource,Is.EqualTo(SwimSurface.Friendly));
                Assert.That(Vector3.Dot(state.PaperRotation*Vector3.forward,Vector3.up),Is.GreaterThan(.999f));
                foreach(float yaw in new[]{40f,150f})
                {
                    Tick(Vector2.zero,jump:1,swim:false); Tick(Vector2.zero,yaw,jump:1);
                    Assert.That(state.Swimming,Is.True); Assert.That(state.SwimSource,Is.EqualTo(SwimSurface.Friendly));
                    Assert.That(Quaternion.Angle(state.PaperRotation,PaperPoseSimulation.GroundRotation(Vector3.up,yaw)),Is.LessThan(.001f));
                }
                foreach(byte owner in new byte[]{0,2,1})
                {
                    PaintWall(owner); state.Position=new Vector3(0,1.4f,3.5f); state.Movement=MovementMode.Air;
                    state.Swimming=state.Grounded=false; state.VerticalSpeed=0; state.PlanarVelocity=Vector3.zero; motor.Restore(state);
                    Tick(Vector2.up,jump:1);
                    Assert.That(state.Movement==MovementMode.WallInk,Is.EqualTo(owner!=2),"air wall owner "+owner);
                }
            }
        }
    }
}
#endif
