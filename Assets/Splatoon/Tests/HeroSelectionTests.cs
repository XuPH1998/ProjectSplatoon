#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class HeroSelectionTests
    {
        [SetUp] public void Setup() { HeroMigrationTests.LoadHistoricalWeapons(); LegacyChargeFixture.Install(); }
        [TearDown] public void Cleanup() { LubanConfigService.Current.Reset(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        static PlayerSnapshot Alive(int id = 1) => new() { HeroId = id, Health = 100, Ink = 100, Team = 1, Grounded = true, Revision = 1 };
        static PlayerInputFrame Input(int tick, bool held = true, uint press = 1, bool cancel = false) => new() { Sequence = (uint)tick + 1, Fire = held, FireSequence = press, CancelFire = cancel };
        static bool Step(ref PlayerSnapshot s, int tick, bool held = true, uint press = 1, bool cancel = false) => WeaponSimulation.Step(ref s, Input(tick, held, press, cancel), GameplayConfig.GetWeapon(s.HeroId), tick / 60.0, false, true);

        [TestCase(1,6,108)] [TestCase(2,4,200)] [TestCase(3,9,66)]
        public void AutomaticWeaponsHaveTheirOwnCadenceAndInkBoundary(int id, int interval, int count)
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            var s = Alive(id); var shots = new List<int>();
            for (int i = 0; i < 2000; i++) if (Step(ref s,i)) shots.Add(i);
            Assert.That(shots.Count, Is.EqualTo(count)); Assert.That(s.Ink, Is.GreaterThanOrEqualTo(0));
            Assert.That(shots[0], Is.EqualTo(2));
            for (int i = 1; i < shots.Count; i++) Assert.That(shots[i]-shots[i-1], Is.EqualTo(interval));
        }
        [Test] public void HeldBurstUsesFourFrameIntervalsAndSixteenFrameRecovery()
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            var s=Alive(4); var shots=new List<int>();
            for(int i=0;i<60;i++) if(Step(ref s,i))shots.Add(i);
            Assert.That(shots.Take(6),Is.EqualTo(new[]{2,6,10,26,30,34}));
            Assert.That(WeaponDisplay.SustainedRate(GameplayConfig.GetWeapon(4)),Is.EqualTo(7.5f));
        }
        [Test] public void ContinuousFireDoesNotRestartUpperBodyLoopEveryShot()
        {
            var s=Alive();double started=-1;
            for(int i=0;i<90;i++)if(Step(ref s,i))
            {if(started<0)started=s.FireStartedAt;else Assert.That(s.FireStartedAt,Is.EqualTo(started));}
        }
        [Test] public void BurstReleaseFinishesGroupButCancelOrEmptyStopsIt()
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            var s=Alive(4);int shots=0;for(int i=0;i<45;i++)if(Step(ref s,i,i==0))shots++;
            Assert.That(shots,Is.EqualTo(3));
            s=Alive(4);shots=0;for(int i=0;i<20;i++)if(Step(ref s,i,i<4,1,i>=4))shots++;
            Assert.That(shots,Is.EqualTo(1));
            s=Alive(4);s.Ink=2.2f;shots=0;for(int i=0;i<60;i++)if(Step(ref s,i))shots++;
            Assert.That(shots,Is.EqualTo(2));Assert.That(s.Ink,Is.Zero.Within(.0001));
        }
        [TestCase(2,0,40,2)] [TestCase(32,.5f,60,5)] [TestCase(62,1,160,8)]
        public void ChargeReleasesOnceWithResolvedDamageInkAndRange(int release, float q, float damage, float ink)
        {
            var s=Alive(5);int shots=0;
            for(int i=0;i<=release;i++)if(Step(ref s,i,i<release))shots++;
            Assert.That(shots,Is.EqualTo(1));Assert.That(s.LastShotCharge,Is.EqualTo(q).Within(.0001));
            var w=GameplayConfig.GetWeapon(5);
            Assert.That(WeaponSimulation.Damage(w,1,s.LastShotCharge),Is.EqualTo(damage).Within(.001));
            Assert.That(s.Ink,Is.EqualTo(100-ink).Within(.001));
            Assert.That(WeaponSimulation.Range(w,q),Is.EqualTo(8+10*q));
            var swimInput=Input(release+1,false);swimInput.Swim=true;
            Assert.That(WeaponSimulation.WantsFire(s,swimInput),Is.False,"a released charge must immediately allow swimming, including during cooldown");
            Assert.That(s.BurstRemaining,Is.Zero);
            Assert.That(s.NextShotAt,Is.EqualTo(release/60.0+1.0/w.FireRate).Within(.000001));
            Assert.That(s.InkRecoverAt,Is.EqualTo((release+WeaponTimeFixture.ReferenceFrames(w.InkRecoverLockSeconds))/60.0).Within(.000001));
            for(int i=release+1;i<release+150;i++)
            {
                Assert.That(Step(ref s,i,false),Is.False);
                Assert.That(WeaponSimulation.WantsFire(s,swimInput),Is.False);
            }
        }
        [Test] public void ChargeWaitsAtFullAndRejectsCooldownClicks()
        {
            var s=Alive(5);
            for(int i=0;i<120;i++)Assert.That(Step(ref s,i),Is.False);
            Assert.That((s.ChargeElapsedSeconds * 60),Is.EqualTo(60));Assert.That(Step(ref s,120,false),Is.True);
            for(int i=121;i<150;i++)Assert.That(Step(ref s,i,true,2),Is.False);
            for(int i=150;i<170;i++)Assert.That(Step(ref s,i,true,2),Is.False,"cooldown press must not queue");
            Assert.That(Step(ref s,170,false,2),Is.False);Assert.That(Step(ref s,171,true,3),Is.False);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Starting));
        }
        [TestCase(5,30)] [TestCase(2,0)]
        public void ChargeCannotExceedAffordablePower(float ink,int expectedTicks)
        {
            var s=Alive(5);s.Ink=ink;
            for(int i=0;i<100;i++)Step(ref s,i);
            Assert.That((s.ChargeElapsedSeconds * 60),Is.EqualTo(expectedTicks));Assert.That(Step(ref s,100,false),Is.True);
            Assert.That(s.Ink,Is.Zero.Within(.0001));
        }
        [Test] public void UiCancellationDoesNotDischargeOrSpendAndRequiresRelease()
        {
            var s=Alive(5);for(int i=0;i<63;i++)Step(ref s,i);
            Assert.That(Step(ref s,63,false,1,true),Is.False);Assert.That(s.Ink,Is.EqualTo(100));
            Assert.That((s.ChargeElapsedSeconds * 60),Is.Zero);Assert.That(Step(ref s,64,true,2),Is.False);
            Assert.That(Step(ref s,65,false,2),Is.False);Assert.That(Step(ref s,66,true,3),Is.False);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Starting));
        }
        [TestCase(1,36,18)] [TestCase(2,24,12)] [TestCase(3,52,26)] [TestCase(4,34,17)]
        public void EachRegularGunUsesItsConfiguredDamageFalloff(int id,float near,float far)
        {
            var w=GameplayConfig.GetWeapon(id);
            Assert.That(WeaponSimulation.Damage(w,WeaponTimeFixture.ReferenceFrames(w.DamageReduceStartSeconds)/60.0),Is.EqualTo(near));
            Assert.That(WeaponSimulation.Damage(w,(WeaponTimeFixture.ReferenceFrames(w.DamageReduceStartSeconds)+WeaponTimeFixture.ReferenceFrames(w.DamageReduceEndSeconds))/120.0),Is.EqualTo((near+far)/2).Within(.001));
            Assert.That(WeaponSimulation.Damage(w,WeaponTimeFixture.ReferenceFrames(w.DamageReduceEndSeconds)/60.0),Is.EqualTo(far));
        }
        [Test] public void HeldChargeDoesNotRecoverInkAndCannotStartBelowMinimum()
        {
            var s=Alive(5);s.Ink=5;
            for(int i=0;i<100;i++)
            {
                Step(ref s,i);
                ResourceSimulation.Step(ref s,GameplayConfig.DefaultHero,false,true,1f/60,i/60.0);
            }
            Assert.That(s.Ink,Is.EqualTo(5));Assert.That((s.ChargeElapsedSeconds * 60),Is.EqualTo(30));
            s=Alive(5);s.Ink=1.99f;for(int i=0;i<100;i++)Assert.That(Step(ref s,i),Is.False);
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Idle));Assert.That((s.ChargeElapsedSeconds * 60),Is.Zero);
        }
        [Test] public void CancellingShotFeedbackCannotReviveOldFireLoop()
        {
            var s=Alive();Step(ref s,0);Step(ref s,1);Assert.That(Step(ref s,2),Is.True);
            Assert.That(Step(ref s,3,false,1,true),Is.False);
            Step(ref s,4,false);Assert.That(s.Firing,Is.False);Assert.That(s.FireVisualUntil,Is.Zero);
        }
        [TestCase(4)] [TestCase(5)]
        public void SwitchingMidAttackRequiresReleaseAndNewStartup(int previous)
        {
            var s=Alive(previous);for(int i=0;i<8;i++)Step(ref s,i);
            float ink=s.Ink;uint shots=s.ShotSequence;
            HeroSelectionRules.Apply(ref s,2,false,Input(8));
            for(int i=8;i<60;i++)Assert.That(Step(ref s,i),Is.False);
            Assert.That(s.ShotSequence,Is.EqualTo(shots));Assert.That(s.Ink,Is.EqualTo(ink));
            Step(ref s,60,false);Assert.That(Step(ref s,61,true,2),Is.False);
            Assert.That(Step(ref s,62,true,2),Is.False);Assert.That(Step(ref s,63,true,2),Is.True);
        }
        [TestCase(MatchPhase.Practice,HeroSelectionOrigin.Warmup,false,true)]
        [TestCase(MatchPhase.Playing,HeroSelectionOrigin.Warmup,true,false)]
        [TestCase(MatchPhase.Playing,HeroSelectionOrigin.Debug,true,false)]
        [TestCase(MatchPhase.Playing,HeroSelectionOrigin.Debug,false,false)]
        [TestCase(MatchPhase.Finished,HeroSelectionOrigin.Debug,true,false)]
        public void ServerChecksPhaseAndBuildNotJustButtonVisibility(MatchPhase phase,HeroSelectionOrigin origin,bool dev,bool allowed)
        {Assert.That(HeroSelectionRules.Validate(Alive(),2,origin,3,3,1,phase,dev)==null,Is.EqualTo(allowed));}
        [Test] public void SwitchPreservesLifeMovementProtectionAndLocksWithPhaseSpecificInk()
        {
            var s=Alive(4);s.Position=new Vector3(1,2,3);s.Ink=20;s.Health=70;s.ProtectedUntil=12;s.NextShotAt=5;s.BurstReadyAt=6;s.InkRecoverAt=9;s.BurstRemaining=2;s.Movement=MovementMode.WallInk;
            Assert.That(HeroSelectionRules.Apply(ref s,5,false,Input(100)),Is.True);
            Assert.That(s.Ink,Is.EqualTo(20));Assert.That(s.Health,Is.EqualTo(70));Assert.That(s.Position,Is.EqualTo(new Vector3(1,2,3)));
            Assert.That(s.Revision,Is.EqualTo(1));Assert.That(s.HeroRevision,Is.EqualTo(1));Assert.That(s.ProtectedUntil,Is.EqualTo(12));
            Assert.That(s.Movement,Is.EqualTo(MovementMode.WallInk));Assert.That(s.NextShotAt,Is.EqualTo(6));Assert.That(s.InkRecoverAt,Is.EqualTo(9));Assert.That(s.BurstRemaining,Is.Zero);
            Assert.That(HeroSelectionRules.Apply(ref s,5,true,Input(101)),Is.False);Assert.That(s.Ink,Is.EqualTo(20));
            HeroSelectionRules.Apply(ref s,2,true,Input(102));Assert.That(s.Ink,Is.EqualTo(100));Assert.That(s.InkRecoverAt,Is.EqualTo(9));
        }
        [Test] public void StaleRoundLifeDeadAndInvalidWeaponAreRejected()
        {
            var s=Alive();
            Assert.That(HeroSelectionRules.Validate(s,999,0,0,0,1,0,true),Is.Not.Null);
            Assert.That(HeroSelectionRules.Validate(s,2,0,0,1,1,0,true),Is.Not.Null);
            Assert.That(HeroSelectionRules.Validate(s,2,0,0,0,2,0,true),Is.Not.Null);
            s.Health=0;Assert.That(HeroSelectionRules.Validate(s,2,0,0,0,1,0,true),Is.Not.Null);
        }
        [TestCase(1,0,13.6f)] [TestCase(5,0,11.2f)] [TestCase(5,1,20.4f)]
        public void HistoricalMuzzleAndProjectileRetainPinnedRangeAndLaunchWeapon(int id,float charge,float target)
        {
            HeroMigrationTests.LoadHistoricalWeapons();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(100,.5f,100);
            var go=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            CharacterPresentationProfile historical = null;
            try
            {
                // This historical range fixture pins its old camera as well as the old weapon.
                // Current camera/weapon contact parity is covered by WeaponImpactPredictionTests.
                var view = go.GetComponent<PrototypePlayer>().CharacterView;
                historical = UnityEngine.Object.Instantiate(view.Profile);
                historical.CameraPivot = new Vector3(0, 1.315198f, 0);
                historical.CameraOffset = new Vector3(.65f, .15f, -3.8f);
                view.Profile = historical;
                var s=Alive(id);s.Position=Vector3.up*.04f;s.CurrentSpread=s.LastShotSpread=.000001f;s.LastShotVerticalSpread=.000001f;s.LastShotCharge=charge;go.transform.position=s.Position;Physics.SyncTransforms();
                var service=new InkProjectileService();service.Spawn(go.GetComponent<PrototypePlayer>(),s,0,0);
                HeroSelectionRules.Apply(ref s,id==1?2:1,false,default);
                Assert.That(service.Spawned[0].HeroId,Is.EqualTo(id));Assert.That(service.Spawned[0].Charge,Is.EqualTo(charge));
                service.Simulate(1.3);Assert.That(service.Impacts.Count,Is.EqualTo(1));Assert.That(service.Impacts[0].Position.z,Is.InRange(target*.9f,target*1.1f));
                Debug.Log($"[WEAPON-RANGE] id={id} charge={charge} floor={service.Impacts[0].Position.z:F3} target={target}");
            }
            finally{if(historical!=null)UnityEngine.Object.DestroyImmediate(historical);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(floor);}
        }
    }
}
#endif
