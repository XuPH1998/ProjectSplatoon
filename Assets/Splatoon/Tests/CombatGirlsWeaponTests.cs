#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using UnityEditor;

namespace Splatoon.Tests
{
    public sealed class CombatGirlsWeaponTests
    {
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            // Retain the retired semi-auto alternation contract in a test-only fixture.
            // Authored automatic dualies are covered by DualPistolGirlTests.
            WeaponConfigService.Current.SetForEditor(2, WeaponAssetTests.Changed(2, a =>
            {
                a.motionMode = ProjectileMotionMode.Ballistic; a.fireMode = WeaponFireMode.SemiAutomatic;
                a.fireRate = 10; a.shotInk = .7f; a.startSeconds = 2.0 / 60;
                a.inkRecoverLockSeconds = .25; a.semiBufferSeconds = .1; a.collisionRadius = .025f;
            }));
            // Exercise semi-auto buffering independently of PistolGirl's authored
            // main weapon, which is now automatic. Never edit the source asset.
            WeaponConfigService.Current.SetForEditor(4, WeaponAssetTests.Changed(4, a =>
            {
                a.fireMode = WeaponFireMode.SemiAutomatic;
                a.fireRate = 5; a.shotInk = 1;
                a.startSeconds = 2.0 / 60; a.semiBufferSeconds = .1;
            }));
        }
        [TearDown] public void Cleanup() => LubanConfigService.Current.Reset();
        static PlayerSnapshot Player(int id) => new() { HeroId=id,Health=100,Ink=100,Grounded=true,Team=1,Revision=1 };
        static bool Step(ref PlayerSnapshot s,int tick,uint press,bool held=false,bool cancel=false,bool clearance=true)
            => WeaponSimulation.Step(ref s,new PlayerInputFrame { Sequence=(uint)tick+1,FireSequence=press,Fire=held,CancelFire=cancel },GameplayConfig.GetWeapon(s.HeroId),tick/60.0,false,clearance);

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void HeldTriggerRepeatsAtCooldownAndReleasedQuickTapEmitsOnce(int id)
        {
            foreach(bool held in new[]{true,false})
            {
                var s=Player(id);var shots=new List<int>();
                for(int tick=0;tick<240;tick++)if(Step(ref s,tick,1,held))shots.Add(tick);
                var w = GameplayConfig.GetWeapon(id);
                int intervalTicks = (int)Math.Ceiling(60.0 / w.FireRate - 1e-6);
                var expected = held ? Enumerable.Range(0, 1 + (239 - WeaponTimeFixture.ReferenceFrames(w.StartSeconds)) / intervalTicks).Select(n => WeaponTimeFixture.ReferenceFrames(w.StartSeconds) + n * intervalTicks).ToArray() : new[] { WeaponTimeFixture.ReferenceFrames(w.StartSeconds) };
                Assert.That(shots,Is.EqualTo(expected));Assert.That(s.Ink,Is.EqualTo(100-shots.Count*w.ShotInk).Within(.0002));
            }
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void SixFrameBufferAcceptsOneClickAndNeverBuildsAQueue(int id)
        {
            var w=GameplayConfig.GetWeapon(id);var s=Player(id);uint press=1;var shots=new List<int>();
            int intervalTicks=(int)Math.Ceiling(60.0/w.FireRate-1e-6);
            int next=2+intervalTicks;
            for(int tick=0;tick<next+intervalTicks+10;tick++)
            {
                if(tick==next-7||tick==next-6||tick==next-4)press++;
                if(Step(ref s,tick,press))shots.Add(tick);
            }
            Assert.That(shots,Is.EqualTo(new[]{2,next}));
        }
        [Test] public void FailedClicksCannotResurfaceLaterOrChangeAlternatingHand()
        {
            var s=Player(2);s.Ink=0;
            for(int i=0;i<5;i++)Step(ref s,i,1,true);
            Assert.That(s.NextMuzzle,Is.Zero);s.Ink=100;
            for(int i=5;i<25;i++)Assert.That(Step(ref s,i,1,false),Is.False);
            Assert.That(s.ShotSequence,Is.Zero);
            Step(ref s,25,2);Step(ref s,26,2);Assert.That(Step(ref s,27,2),Is.True);
            Assert.That(s.LastShotMuzzle,Is.Zero);Assert.That(s.NextMuzzle,Is.EqualTo(1));
            for(int i=28;i<50;i++)Step(ref s,i,3,false,false,false);
            for(int i=50;i<70;i++)Assert.That(Step(ref s,i,3),Is.False);
            Assert.That(s.NextMuzzle,Is.EqualTo(1));
        }
        [Test] public void AlternationAndShotIdentitySurviveReplayAndResetOnlyWithHeroLifecycle()
        {
            var s=Player(2);var hands=new List<byte>();uint press=0;PlayerSnapshot checkpoint=default;
            for(int tick=0;tick<35;tick++)
            {
                if(tick%10==0)press++;
                if(Step(ref s,tick,press)){hands.Add(s.LastShotMuzzle);if(hands.Count==2)checkpoint=s;}
            }
            Assert.That(hands,Is.EqualTo(new byte[]{0,1,0,1}));
            var replay=checkpoint;for(int tick=13;tick<35;tick++)Step(ref replay,tick,(uint)(tick/10+1));
            Assert.That(replay.ShotActionId,Is.EqualTo(s.ShotActionId));Assert.That(replay.NextMuzzle,Is.EqualTo(s.NextMuzzle));
            WeaponSimulation.Cancel(ref s,default);Assert.That(s.NextMuzzle,Is.EqualTo(replay.NextMuzzle));
            HeroSelectionRules.Apply(ref s,1,false,default);HeroSelectionRules.Apply(ref s,2,false,default);
            Assert.That(s.NextMuzzle,Is.Zero);Assert.That(s.LeftShotAction,Is.Zero);
        }
        [Test] public void CancelledBufferedClickDoesNotFireAfterControlReturns()
        {
            var s=Player(4);for(int tick=0;tick<3;tick++)Step(ref s,tick,1);
            Step(ref s,12,2);Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Starting));
            Step(ref s,13,2,false,true);for(int tick=14;tick<90;tick++)Assert.That(Step(ref s,tick,2),Is.False);
            Assert.That(s.ShotSequence,Is.EqualTo(1));
        }
        [Test] public void SemiHoldRequestsShootingAndKeepsConfiguredRecoveryRule()
        {
            var s=Player(2);var w=GameplayConfig.GetWeapon(2);for(int tick=0;tick<40;tick++)Step(ref s,tick,1,true);
            var input=new PlayerInputFrame{Fire=true,FireSequence=1};
            Assert.That(WeaponSimulation.WantsFire(s,input,w,1),Is.True);
            float before=s.Ink;ResourceSimulation.Step(ref s,GameplayConfig.GetHero(s.HeroId),false,input.Fire&&!WeaponSimulation.IsSemi(w),1f/60,1);
            Assert.That(s.Ink,Is.GreaterThan(before));
        }
        [Test] public void ShotgunSpendsOnceForEightDistinctBoundedDeterministicPellets()
        {
            var s=Player(3);for(int tick=0;tick<3;tick++)Step(ref s,tick,1);
            var w=GameplayConfig.GetWeapon(3);
            Assert.That(s.Ink,Is.EqualTo(96));Assert.That(s.ShotSequence,Is.EqualTo(1));
            Assert.That(w.PelletCount,Is.EqualTo(8));Assert.That(w.PelletCount*w.Damage,Is.EqualTo(80));
            var velocities=Enumerable.Range(0,8).Select(i=>InkBallistics.PelletVelocity(Vector3.forward,w,w.SpreadDegrees,i,77)).ToArray();
            Assert.That(velocities.Distinct().Count(),Is.EqualTo(8));
            for(int i=0;i<8;i++)
            { Assert.That(velocities[i],Is.EqualTo(InkBallistics.PelletVelocity(Vector3.forward,w,w.SpreadDegrees,i,77)));Assert.That(Vector3.Angle(Vector3.forward,velocities[i]),Is.LessThanOrEqualTo(7.001)); }
            Assert.That(WeaponSimulation.Damage(w,1)*8,Is.EqualTo(32));
        }
        [Test] public void NewProtocolAndHeroSchemaRejectInvalidGunAssemblies()
        {
            Assert.That(PlayerSnapshot.ProtocolVersion,Is.GreaterThanOrEqualTo(27));GameplayConfig.Validate();
            HeroMigrationTests.Load(rows=>rows[1]["pelletCount"]=8);
            Assert.Throws<InvalidOperationException>(()=>GameplayConfig.Validate());
        }
        [Test] public void SnapshotRoundTripRestoresBufferedClickAndBothHandTimelines()
        {
            var s=Player(2);for(int tick=0;tick<=12;tick++)Step(ref s,tick,(uint)(tick<10?1:2));
            Step(ref s,16,3);Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Starting));
            using var writer=new FastBufferWriter(2048,Allocator.Temp);writer.WriteNetworkSerializable(s);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot copy);
            Assert.That(copy.NextMuzzle,Is.EqualTo(s.NextMuzzle));Assert.That(copy.WeaponReadyAt,Is.EqualTo(s.WeaponReadyAt));
            Assert.That(copy.RightShotAction,Is.EqualTo(s.RightShotAction));Assert.That(copy.LeftShotAction,Is.EqualTo(s.LeftShotAction));
            Assert.That(copy.RightShotAt,Is.EqualTo(s.RightShotAt));Assert.That(copy.LeftShotAt,Is.EqualTo(s.LeftShotAt));
            for(int tick=17;tick<35;tick++)Assert.That(Step(ref copy,tick,3),Is.EqualTo(Step(ref s,tick,3)));
            Assert.That(copy.ShotActionId,Is.EqualTo(s.ShotActionId));Assert.That(copy.NextMuzzle,Is.EqualTo(s.NextMuzzle));
        }
        [Test] public void AlternatingLogicalMuzzlesHaveIndependentWorldObstruction()
        {
            var root=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            root.SetActive(false);var player=root.GetComponent<PrototypePlayer>();
            player.CharacterView=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Characters/DualPistolGirl/Prefabs/DualPistolGirlVisual.prefab").GetComponent<InkCharacterView>();
            var s=Player(2);s.Position=Vector3.one*1000;s.CurrentSpread=s.LastShotSpread=.000001f;s.LastShotVerticalSpread=.000001f;
            Vector3 pivot=s.Position+player.Presentation.CameraPivot;
            Vector3 left=s.Position+player.MuzzleOffset(0,1),right=s.Position+player.MuzzleOffset(0,0);
            var wall=new GameObject("Left muzzle obstacle");wall.transform.position=Vector3.Lerp(pivot,left,.8f);wall.AddComponent<BoxCollider>().size=Vector3.one*.07f;
            try
            {
                Physics.SyncTransforms();var service=new InkProjectileService();
                s.LastShotMuzzle=0;service.Spawn(player,s,0,1);Assert.That(service.ActiveCount,Is.EqualTo(1));Assert.That(service.Spawned[0].Origin,Is.EqualTo(right));
                service.Clear();s.LastShotMuzzle=1;service.Spawn(player,s,0,1);
                Assert.That(service.ActiveCount,Is.Zero);Assert.That(service.Impacts.Count,Is.EqualTo(1));Assert.That(service.Spawned[0].MuzzleIndex,Is.EqualTo(1));
                Assert.That(service.Spawned[0].Origin,Is.EqualTo(pivot));
            }
            finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(wall);Physics.SyncTransforms();}
        }
    }
}
#endif
