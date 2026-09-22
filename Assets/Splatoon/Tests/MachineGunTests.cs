#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class MachineGunTests
    {
        WeaponRuntimeConfig W => GameplayConfig.GetWeapon(6);
        cfg.HeroConfig Hero => GameplayConfig.GetHero(6);
        static PlayerSnapshot Player(float ink = 100, bool ground = true) => new() { HeroId=6, Health=100, Ink=ink, Grounded=ground, Team=1, Revision=1 };
        [SetUp] public void Setup() => HeroMigrationTests.Load();
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        bool Step(ref PlayerSnapshot s, int tick, bool held, uint press=1, bool cancel=false, bool clearance=true, bool emerge=false)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame {Sequence=(uint)tick+1, FireSequence=press, Fire=held, CancelFire=cancel}, W, tick/60.0, emerge, clearance);
        List<int> Magazine(ref PlayerSnapshot s, int release, int end=750)
        {
            var shots=new List<int>();
            for(int t=0;t<=end;t++) if(Step(ref s,t,t<release)) shots.Add(t);
            return shots;
        }
        [TestCase(0,1,8)] [TestCase(7,1,8)] [TestCase(8,1,8)] [TestCase(17,10,17)]
        [TestCase(18,11,18)] [TestCase(19,12,19)] [TestCase(26,20,26)] [TestCase(27,22,27)]
        public void ChargeBoundariesAndInclusiveLastRound(int release,int rounds,int first)
        {
            var s=Player();var shots=Magazine(ref s,release);
            Assert.That(shots,Is.EqualTo(Enumerable.Range(0,rounds).Select(n=>first+n*4).ToArray()));
            Assert.That(s.Ink,Is.EqualTo(100-rounds*W.ShotInk).Within(.0003));
            Assert.That(s.SplatlingReservedInk,Is.Zero);Assert.That(s.SplatlingRemaining,Is.Zero);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Idle));
        }
        [Test] public void FullHoldKeepsItsMagazineWithoutFiringOrOrdinaryRecovery()
        {
            var s=Player();
            for(int t=0;t<=300;t++) { Assert.That(Step(ref s,t,true),Is.False); ResourceSimulation.Step(ref s,Hero,false,false,1f/60,t/60.0); }
            Assert.That((s.SplatlingChargeSeconds * 60),Is.EqualTo(27));Assert.That(s.SplatlingLoaded,Is.EqualTo(22));
            Assert.That(s.Ink,Is.EqualTo(85).Within(.0003));Assert.That(s.SplatlingReservedInk,Is.EqualTo(15).Within(.0003));
            Assert.That(Step(ref s,301,false),Is.True);Assert.That(s.LastShotCharge,Is.EqualTo(1));
        }
        [Test] public void FullDamageIdentitySurvivesTheWholeMagazine()
        {
            var s=Player();
            for(int t=0;t<450;t++) if(Step(ref s,t,t<27))
            { Assert.That(s.LastShotCharge,Is.EqualTo(1)); Assert.That(WeaponSimulation.Damage(W,0,s.LastShotCharge),Is.EqualTo(32)); }
            Assert.That(WeaponSimulation.Damage(W,0,26f/27),Is.EqualTo(32));
            Assert.That(WeaponSimulation.Damage(W,11/60.0,1),Is.EqualTo(32));
            Assert.That(WeaponSimulation.Damage(W,15/60.0,1),Is.EqualTo(24).Within(.0001));
            Assert.That(WeaponSimulation.Damage(W,19/60.0,1),Is.EqualTo(16));
            Assert.That(Mathf.CeilToInt(100/WeaponSimulation.Damage(W,0,1)),Is.EqualTo(4));
            Assert.That(Mathf.CeilToInt(100/WeaponSimulation.Damage(W,0,.5f)),Is.EqualTo(4));
        }
        [TestCase(6)] [TestCase(18)] [TestCase(27)] [TestCase(44)]
        public void CancelRefundsOnlyUnspentReservationOnce(int at)
        {
            var s=Player();int shots=0;
            for(int t=0;t<=at;t++) if(Step(ref s,t,t<27)) shots++;
            WeaponSimulation.Cancel(ref s,default,true);WeaponSimulation.Cancel(ref s,default,true);
            Assert.That(s.Ink,Is.EqualTo(100-shots*W.ShotInk).Within(.0003));
            Assert.That(s.SplatlingReservedInk,Is.Zero);Assert.That(s.SplatlingRemaining,Is.Zero);Assert.That(s.SplatlingEndedAt,Is.Zero);
            for(int t=at+1;t<at+80;t++) Assert.That(Step(ref s,t,true),Is.False);
            Step(ref s,at+80,false);Step(ref s,at+81,true,2);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Charging));
        }
        [TestCase(false,100)] [TestCase(true,0)] [TestCase(false,0)]
        public void AirOrEmptyChargeIsSixTimesSlowerWithoutStacking(bool ground,int ink)
        {
            var s=Player(ink,ground);
            for(int t=0;t<=162;t++) Assert.That(Step(ref s,t,true),Is.False);
            Assert.That((s.SplatlingChargeSeconds * 60),Is.EqualTo(27).Within(.001));Assert.That(s.SplatlingLoaded,Is.EqualTo(22));
            Assert.That(s.Ink,Is.GreaterThanOrEqualTo(0));Assert.That(s.Ink+s.SplatlingReservedInk,Is.LessThanOrEqualTo(100.001));
            Step(ref s,163,false);
            Assert.That(s.LastShotCharge,Is.EqualTo(1),"162 slow ticks must preserve full-charge damage");
        }
        [Test] public void LowInkFillingAndCancellationCannotRefundTwice()
        {
            var s=Player(.2f);float priorTotal=.2f;
            for(int t=0;t<=60;t++)
            {
                Step(ref s,t,true);
                Assert.That(s.Ink,Is.GreaterThanOrEqualTo(0));
                Assert.That(s.Ink+s.SplatlingReservedInk,Is.GreaterThanOrEqualTo(priorTotal-.0001));
                priorTotal=s.Ink+s.SplatlingReservedInk;
            }
            Assert.That((s.SplatlingChargeSeconds * 60),Is.LessThan(27));
            WeaponSimulation.Cancel(ref s,default);Assert.That(s.Ink,Is.EqualTo(priorTotal).Within(.0001));
            WeaponSimulation.Cancel(ref s,default);Assert.That(s.Ink,Is.EqualTo(priorTotal).Within(.0001));
        }
        [Test] public void HeldNextMagazineStartsAfterRecoveryButReleasedTapsDoNotQueue()
        {
            foreach(bool held in new[]{false,true})
            {
                var s=Player();
                for(int t=0;t<=111;t++) Step(ref s,t,t<27 || (held ? t>40 : t==50),t>=50?2u:1u);
                Assert.That(s.SplatlingRemaining,Is.Zero);Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Ending));
                for(int t=112;t<=115;t++) Step(ref s,t,held,2);
                Assert.That(s.WeaponPhase,Is.EqualTo(held?WeaponPhase.Charging:WeaponPhase.Idle));
                Assert.That(s.ShotSequence,Is.EqualTo(22));
            }
        }
        [Test] public void RecoveryDoesNotSlowWalkingAndFreshTapAtItsBoundaryIsAccepted()
        {
            var s=Player();for(int t=0;t<=8;t++)Step(ref s,t,false);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Ending));
            Assert.That(WeaponSimulation.WantsFire(s,new PlayerInputFrame{Fire=true,FireSequence=1},W,9/60.0),Is.False);
            for(int t=9;t<12;t++)Step(ref s,t,false);
            Step(ref s,12,false,2);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Charging));
            for(int t=13;t<20;t++)Assert.That(Step(ref s,t,false,2),Is.False);
            Assert.That(Step(ref s,20,false,2),Is.True);
        }
        [Test] public void ClearanceCancelDeathAndHeroSwitchClearAllPendingOutput()
        {
            for(int reason=0;reason<4;reason++)
            {
                var s=Player();for(int t=0;t<18;t++)Step(ref s,t,true);
                if(reason==0)Step(ref s,18,true,1,false,false);
                if(reason==1)Step(ref s,18,true,1,true);
                if(reason==2){s.Health=0;Step(ref s,18,false);}
                if(reason==3)HeroSelectionRules.Apply(ref s,1,false,default);
                Assert.That((s.SplatlingChargeSeconds * 60),Is.Zero);Assert.That(s.SplatlingReservedInk,Is.Zero);
                Assert.That(s.Ink,Is.EqualTo(100).Within(.0003));Assert.That(s.Firing,Is.False);
            }
        }
        [Test] public void SnapshotReplayKeepsMagazineAndStableUniqueRoundIdentities()
        {
            var s=Player();for(int t=0;t<=50;t++)Step(ref s,t,t<27);
            using var writer=new FastBufferWriter(2048,Allocator.Temp);writer.WriteNetworkSerializable(s);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot copy);
            Assert.That(copy.SplatlingReservedInk,Is.EqualTo(s.SplatlingReservedInk));Assert.That(copy.SplatlingReleasedAt,Is.EqualTo(s.SplatlingReleasedAt));
            var ids=new HashSet<ulong>();
            for(int t=51;t<150;t++)
            {
                bool shot=Step(ref s,t,false);Assert.That(Step(ref copy,t,false),Is.EqualTo(shot));
                Assert.That(copy.SplatlingRemaining,Is.EqualTo(s.SplatlingRemaining));Assert.That(copy.Ink,Is.EqualTo(s.Ink));
                Assert.That(copy.ShotActionId,Is.EqualTo(s.ShotActionId));
                if(shot)Assert.That(ids.Add(s.ShotActionId),Is.True);
            }
            Assert.That(copy.ShotSequence,Is.EqualTo(22));
        }
        [Test] public void SplatlingVelocityIsSeededAndRespectsIndependentSpreadAndChargeRange()
        {
            var horizontal=new List<float>();var vertical=new List<float>();
            for(uint i=1;i<=2048;i++)
            {
                uint a=i,b=i;var v=InkBallistics.SplatlingVelocity(Vector3.forward,W,1,3,2,ref a);
                Assert.That(v,Is.EqualTo(InkBallistics.SplatlingVelocity(Vector3.forward,W,1,3,2,ref b)));
                float x=Mathf.Abs(Mathf.Atan2(v.x,v.z)*Mathf.Rad2Deg),y=Mathf.Abs(Mathf.Atan2(v.y,v.z)*Mathf.Rad2Deg);
                Assert.That(x,Is.LessThanOrEqualTo(3.001));Assert.That(y,Is.LessThanOrEqualTo(2.001));
                horizontal.Add(x);vertical.Add(y);
                Assert.That(v.magnitude,Is.InRange(W.SpeedMin-.001f,W.SpeedMax+.001f));
            }
            Assert.That(horizontal.Average(),Is.LessThan(1.5));Assert.That(vertical.Average(),Is.LessThan(1));
            Assert.That(WeaponSimulation.Range(W,18f/27),Is.EqualTo(WeaponSimulation.Range(W,1)));
            Assert.That(WeaponSimulation.Range(W,8f/27),Is.LessThan(WeaponSimulation.Range(W,1)));
        }
        [Test] public void FormalAssetsHaveElevenClipsPelvisAvatarAndSdfFaces()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/MachineGunGirl/Prefabs/MachineGunGirlVisual.prefab");
            Assert.That(prefab,Is.Not.Null);var view=prefab.GetComponent<InkCharacterView>();
            Assert.That(view.Animator.avatar.isHuman&&view.Animator.avatar.isValid,Is.True);
            Assert.That(view.Animator.avatar.humanDescription.human.Single(h=>h.humanName=="Hips").boneName,Is.EqualTo("pelvis"));
            var clips=view.Animator.runtimeAnimatorController.animationClips.Distinct().ToArray();
            Assert.That(clips.Length,Is.EqualTo(11));Assert.That(clips.Single(c=>c.name=="MG_Shoot_Loop").isLooping,Is.True);
            Assert.That(clips.Single(c=>c.name=="MG_Shoot_End").isLooping,Is.False);
            Assert.That(prefab.GetComponent<MachineGunFaceShadow>().Faces.Length,Is.GreaterThan(0));
            Assert.That(view.Nozzle,Is.Not.Null);Assert.That(view.LeftGrip.name,Is.EqualTo("L_Hand_Position"));
            Assert.That(view.Profile.MuzzlePosition.y,Is.GreaterThan(.5f),"Logic muzzle must be held above the floor");
            foreach(var t in prefab.GetComponentsInChildren<Transform>(true))Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject),Is.Zero,t.name);
        }
        [Test] public void ActualTrajectoryAndPaintAreStableAcrossSimulationDriverRates()
        {
            const string output="Reports/CombatGirls/MachineGunGirl/Ballistics";Directory.CreateDirectory(output);
            WeaponReferenceMeasurements.Capture(6,1,"flat",60,1,output);
            foreach(float charge in new[]{8f/27,18f/27,1f})
            {
                var results=new List<WeaponReferenceMeasurements.Result>();
                int rounds=SplatlingSimulation.Rounds(W,charge*W.ChargeSeconds);
                foreach(int hz in new[]{30,60,144})
                {
                    var r=WeaponReferenceMeasurements.Capture(6,charge,"continuous",hz,rounds,output);results.Add(r);
                    Assert.That(r.impacts,Is.EqualTo(rounds));Assert.That(r.paintStamps,Is.GreaterThan(0));
                    Assert.That(r.ownedArea,Is.GreaterThan(0));
                }
                Assert.That(results.Select(r=>r.gridHash).Distinct().Count(),Is.EqualTo(1),"Host tick subdivisions cannot change seeded paint");
            }
        }
        [Test] public void MiniMovementAndChargeJumpUseActualMotorLimits()
        {
            var floor=new GameObject("Mini motor floor");floor.transform.position=new Vector3(2000,-.5f,2000);floor.AddComponent<BoxCollider>().size=new Vector3(100,1,100);
            var go=new GameObject("Mini motor");go.layer=8;var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.radius=.35f;cc.center=Vector3.up*.9f;
            try
            {
                foreach(var phase in new[]{WeaponPhase.Idle,WeaponPhase.Charging,WeaponPhase.Firing})
                {
                    var motor=new PlayerMotorSimulation(cc);var s=Player();s.Position=new Vector3(2000,.05f,2000);s.WeaponPhase=phase;s.SplatlingRemaining=phase==WeaponPhase.Firing?20:0;
                    motor.Restore(s);Physics.SyncTransforms();
                    bool firing=phase!=WeaponPhase.Idle;
                    for(int t=0;t<60;t++)motor.Step(ref s,new PlayerInputFrame{Move=Vector2.up},1f/60,t/60.0,firing,SplatlingSimulation.MovementSpeed(s,W));
                    float expected=phase==WeaponPhase.Idle?Hero.MoveSpeed:phase==WeaponPhase.Charging?W.SplatlingChargeMoveSpeed:W.ShootMoveSpeed;
                    Assert.That(s.Velocity.z,Is.EqualTo(expected).Within(.015),phase.ToString());
                    motor.Step(ref s,new PlayerInputFrame{JumpSequence=1},1f/60,1,firing,SplatlingSimulation.MovementSpeed(s,W));
                    float jump=phase==WeaponPhase.Charging?W.SplatlingChargeJumpSpeed:Hero.JumpSpeed;
                    Assert.That(s.VerticalSpeed,Is.EqualTo(jump-Hero.CharacterGravity/60).Within(.001));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(floor);Physics.SyncTransforms();}
        }
    }
}
#endif
