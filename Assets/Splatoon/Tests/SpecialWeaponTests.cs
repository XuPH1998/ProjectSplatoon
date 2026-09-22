#if UNITY_EDITOR
using System;
using System.IO;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
namespace Splatoon.Tests
{
    public sealed class SpecialWeaponTests
    {
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            foreach(var r in LubanConfigService.Current.Tables.TbSubWeapon.DataList)SubWeaponConfigService.Current.SetById(r.Id,AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(r.ConfigPath));
            foreach(var r in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)SpecialWeaponConfigService.Current.Set(r.Id,AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(r.ConfigPath));
        }
        [TearDown]public void Cleanup(){SpecialWeaponConfigService.Current.Clear();SubWeaponConfigService.Current.Clear();LubanConfigService.Current.Reset();}
        static PlayerSnapshot Player(int special=1)=>new(){HeroId=1,SubWeaponId=1,SpecialWeaponId=special,Health=100,Ink=15,Team=1,SpecialPoints=200,HeroRevision=2,Grounded=true};
        static PlayerInputFrame Input()=>new(){HeroRevision=2,SpecialSequence=1};
        static bool Step(ref PlayerSnapshot s,PlayerInputFrame i,double now,bool stand=true)=>SpecialWeaponSimulation.Step(ref s,i,SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId),200,100,stand,now);
        [TestCase(9u)][TestCase(11u)]
        public void OldLoadoutCannotRewriteCurrentSpecialEdge(uint staleEdge)
        {
            var s=Player();s.SpecialConsumed=10;
            var i=Input();i.HeroRevision=s.HeroRevision-1;i.SpecialSequence=staleEdge;
            Step(ref s,i,0);
            Assert.That(s.SpecialConsumed,Is.EqualTo(10),"An old loadout must not rewind or advance the new loadout's Q baseline");
            i.HeroRevision=s.HeroRevision;i.SpecialSequence=10;
            Step(ref s,i,1);Assert.That(s.SpecialAction,Is.Zero,"Current baseline is not a new Q press");
            i.SpecialSequence=11;Step(ref s,i,2);Assert.That(s.SpecialAction,Is.EqualTo(1),"A fresh scoped edge still activates");
        }
        [TestCase(1,5)][TestCase(8,16)][TestCase(13,5)]
        public void SonarThrowUsesSelectedSubStartupAndLocksFromCommit(int sub,int frames)
        {
            var s=Player(3);s.SubWeaponId=sub;var i=Input();Step(ref s,i,0);
            Step(ref s,i,900);Assert.That(s.SpecialPhase,Is.EqualTo(SpecialPhase.Active),"Unthrown sonar has no use timeout");
            i.Fire=true;i.FireSequence++;Step(ref s,i,901);
            i.Fire=false;i.ReleaseSequence++;const double released=902;
            Assert.That(Step(ref s,i,released),Is.False);
            for(int f=1;f<=frames;f++)
            {
                bool committed=Step(ref s,i,released+f/60d);
                Assert.That(committed,Is.EqualTo(f==frames),$"Sub {sub}, frame {f}");
                if(!committed)Assert.That(s.SpecialChargeLockedUntil,Is.Zero);
            }
            Assert.That(s.SpecialAttack,Is.EqualTo(1));
            Assert.That(s.SpecialChargeLockedUntil,Is.EqualTo(released+frames/60d+6.5).Within(.00001));
            Assert.That(Step(ref s,i,released+(frames+1)/60d),Is.False,"Replay cannot throw twice");
        }
        [TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void EverySpecialLoadsWithBundledPresentation(int id)
        {var c=SpecialWeaponConfigService.Current.Get(id);c.Validate();Assert.That(c.HeldPrefab,Is.Not.Null);Assert.That(c.Material,Is.Not.Null);Assert.That(c.Icon,Is.Not.Null);Assert.That(c.UseAudio,Is.Not.Null);}
        [Test]public void AllNineHeroesCanSelectAllThirteenSubsAndFiveSpecials()
        {foreach(var h in LubanConfigService.Current.Tables.TbHero.DataList)for(int sub=1;sub<=13;sub++)for(int special=1;special<=5;special++){var l=new PlayerLoadout(h.Id,sub,special);Assert.That(PlayerLoadout.Validate(l),Is.Null);Assert.That(l.RequiredPoints,Is.EqualTo(200));}}
        [Test]public void SameHeroDifferentSubsAreIsolated()
        {var a=Player();var b=a;b.SubWeaponId=13;Assert.That(PlayerLoadout.SubWeapon(a).Type,Is.EqualTo(SubWeaponType.SplatBomb));Assert.That(PlayerLoadout.SubWeapon(b).Type,Is.EqualTo(SubWeaponType.SplashWall));Assert.That(PlayerLoadout.SubWeapon(a).Type,Is.EqualTo(SubWeaponType.SplatBomb));}
        [Test]public void HistoricalSubConfigSurvivesOtherSelection()
        {var service=SubWeaponConfigService.Current;uint revision=service.RevisionById(2);var first=service.ForEntityId(2,revision);Assert.That(service.GetById(13).Type,Is.EqualTo(SubWeaponType.SplashWall));Assert.That(service.ForEntityId(2,revision),Is.SameAs(first));}
        [Test]public void InsufficientQIsConsumedAndCannotAutoActivateAfterCharge()
        {var s=Player();s.SpecialPoints=199;var i=Input();Step(ref s,i,0);Assert.That(s.SpecialPhase,Is.EqualTo(SpecialPhase.Charging));SpecialWeaponSimulation.AddPoints(ref s,1,1,200);Step(ref s,i,1);Assert.That(s.SpecialAction,Is.Zero);i.SpecialSequence++;Step(ref s,i,2);Assert.That(s.SpecialAction,Is.EqualTo(1));Assert.That(s.Ink,Is.EqualTo(100));}
        [Test]public void RepeatedQAndReplayCannotConsumeAnotherSpecial()
        {var s=Player();var i=Input();Step(ref s,i,0);Step(ref s,i,0);i.SpecialSequence++;Step(ref s,i,.01);Assert.That(s.SpecialAction,Is.EqualTo(1));Assert.That(s.SpecialPoints,Is.Zero);}
        [Test]public void OldEquipmentAndMenuCannotStartSpecial()
        {var s=Player();var i=Input();i.HeroRevision=1;Step(ref s,i,0);Assert.That(s.SpecialAction,Is.Zero);i.HeroRevision=2;i.SpecialSequence++;i.CancelFire=true;Step(ref s,i,1);Assert.That(s.SpecialAction,Is.Zero);i.CancelFire=false;Step(ref s,i,2);Assert.That(s.SpecialAction,Is.Zero);}
        [Test]public void NoSpaceDoesNotConsumeCharge()
        {var s=Player();Step(ref s,Input(),0,false);Assert.That(s.SpecialPoints,Is.EqualTo(200));Assert.That(s.SpecialFailure,Is.EqualTo(SpecialFailure.NoSpace));}
        [Test]public void ActivationCancelsReservedMainInkBeforeRefill()
        {var s=Player();s.SplatlingReservedInk=40;s.SplatlingRemaining=5;s.SubPhase=SubWeaponPhase.Holding;Step(ref s,Input(),0);Assert.That(s.Ink,Is.EqualTo(100));Assert.That(s.SplatlingReservedInk,Is.Zero);Assert.That(s.SubPhase,Is.EqualTo(SubWeaponPhase.Idle));}
        [Test]public void FractionalCreditClampsAndLockIsIndependentOfEntityLifetime()
        {var s=Player();s.SpecialPoints=199.75;SpecialWeaponSimulation.AddPoints(ref s,.5,0,200);Assert.That(s.SpecialPoints,Is.EqualTo(200));s.SpecialPoints=10;s.SpecialChargeLockedUntil=3;SpecialWeaponSimulation.AddPoints(ref s,20,2,200);Assert.That(s.SpecialPoints,Is.EqualTo(10));SpecialWeaponSimulation.AddPoints(ref s,20,3,200);Assert.That(s.SpecialPoints,Is.EqualTo(30));}
        [Test]public void BubbleKeepsUnusedChargeAndFinalDeathHalvesIt()
        {var s=Player();s.SpecialPoints=151;s.LifeState=PlayerLifeState.Bubble;s.Health=0;SpecialWeaponSimulation.Interrupt(ref s);SpecialWeaponSimulation.AddPoints(ref s,100,10,200);Assert.That(s.SpecialPoints,Is.EqualTo(151));s.LifeState=PlayerLifeState.Alive;s.Health=100;Assert.That(s.SpecialPoints,Is.EqualTo(151));SpecialWeaponSimulation.Die(ref s);Assert.That(s.SpecialPoints,Is.EqualTo(75.5));}
        [Test]public void DownedActiveSpecialNeverRefunds()
        {var s=Player();Step(ref s,Input(),0);s.LifeState=PlayerLifeState.Bubble;s.Health=0;SpecialWeaponSimulation.Interrupt(ref s);Assert.That(s.SpecialPoints,Is.Zero);Assert.That(s.SpecialPhase,Is.EqualTo(SpecialPhase.Charging));SpecialWeaponSimulation.Die(ref s);Assert.That(s.SpecialPoints,Is.Zero);}
        [Test]public void AreaConversionUsesSquareOfProjectScale()
        {double side=10*SpecialWeaponDefaults.Scale;Assert.That(PaintCredit.Points(side*side),Is.EqualTo(100d/3).Within(.0001));}
        [TestCase(2)][TestCase(3)][TestCase(4)]public void MenuCancelsUncommittedThrow(int type)
        {var s=Player(type);var i=Input();Step(ref s,i,0);i.Fire=true;i.FireSequence=1;Step(ref s,i,1);Assert.That(s.SpecialAiming,Is.True);i.CancelFire=true;i.Fire=false;i.ReleaseSequence=1;Assert.That(Step(ref s,i,1.1),Is.False);i.CancelFire=false;Step(ref s,i,1.2);Assert.That(s.SpecialRemaining,Is.EqualTo(type==2?3:1));Assert.That(s.SpecialShotAt,Is.Zero);}
        [TestCase(2)][TestCase(3)][TestCase(4)]public void HeldMouseAtQRequiresNewClick(int type)
        {var s=Player(type);var i=Input();i.Fire=true;i.FireSequence=1;Step(ref s,i,0);Step(ref s,i,1);i.Fire=false;i.ReleaseSequence=1;Step(ref s,i,1.1);Assert.That(s.SpecialAiming,Is.False);Assert.That(s.SpecialShotAt,Is.Zero);}
        [Test]public void TrizookaFiresThreeTimesAndEnforcesDelay()
        {var s=Player();var i=Input();Step(ref s,i,0);i.Fire=true;i.FireSequence=1;var frames=new System.Collections.Generic.List<int>();for(int f=1;f<400;f++)if(Step(ref s,i,f/60d))frames.Add(f);Assert.That(frames.Count,Is.EqualTo(3));Assert.That(frames[1]-frames[0],Is.EqualTo(55));Assert.That(frames[2]-frames[1],Is.EqualTo(55));Assert.That(s.SpecialRemaining,Is.Zero);}
        [Test]public void TrizookaUsesMeasuredEquipDelayFiringSpeedAndRecovery()
        {
            var s=Player();var input=Input();var config=SpecialWeaponConfigService.Current.Get(1);Step(ref s,input,0);
            input.Fire=true;input.FireSequence=1;
            for(int frame=1;frame<=22;frame++){Assert.That(Step(ref s,input,frame/60d),Is.False);Assert.That(s.SpecialShotAt,Is.Zero);}
            Step(ref s,input,23d/60);
            Assert.That(SpecialWeaponSimulation.MovementSpeed(s,config,23d/60),Is.EqualTo(.04f*60*SpecialWeaponDefaults.Scale).Within(.0001));
            for(int frame=24;frame<38;frame++)Assert.That(Step(ref s,input,frame/60d),Is.False);
            Assert.That(Step(ref s,input,38d/60),Is.True,"First projectile: 18 equip + 5 delay + 15 shot frames");
            input.Fire=false;
            for(int frame=39;frame<=78;frame++)Step(ref s,input,frame/60d);
            Assert.That(SpecialWeaponSimulation.MovementSpeed(s,config,77d/60),Is.EqualTo(.04f*60*SpecialWeaponDefaults.Scale).Within(.0001));
            Assert.That(SpecialWeaponSimulation.MovementSpeed(s,config,78d/60),Is.EqualTo(.07f*60*SpecialWeaponDefaults.Scale).Within(.0001));
            input.Fire=true;input.FireSequence++;
            for(int frame=79;frame<=180;frame++)Step(ref s,input,frame/60d);
            Assert.That(s.SpecialRemaining,Is.Zero);
            Assert.That(s.SpecialUntil-s.SpecialAttackAt,Is.EqualTo(40d/60).Within(.000001),"Last shot retains its 40-frame recovery");
        }
        [Test]public void TrizookaTravelsSixteenStraightFramesThenClampsBrakeSpeed()
        {
            var p=SpecialWeaponConfigService.Current.Get(1).P;
            Vector3 pos=Vector3.zero,velocity=Vector3.forward*p.projectileSpeed;
            for(int frame=0;frame<16;frame++)SpecialWeaponService.AdvanceTrizooka(ref pos,ref velocity,frame,p);
            Assert.That(pos.y,Is.Zero);Assert.That(pos.z,Is.EqualTo(16*1.125f*SpecialWeaponDefaults.Scale).Within(.0001));
            SpecialWeaponService.AdvanceTrizooka(ref pos,ref velocity,16,p);
            Assert.That(velocity.z,Is.EqualTo(.91f*60*SpecialWeaponDefaults.Scale).Within(.0001));
            Assert.That(velocity.y,Is.EqualTo(-.09f*60*SpecialWeaponDefaults.Scale).Within(.0001));
        }
        [Test]public void EveryMappedOverrideMatchesRawSource()
        {
            var rows=JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SpecialWeapons1130/reference-map.json")).AsArray;
            foreach(JSONNode row in rows)
            {
                var source=JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SpecialWeapons1130/"+row["file"].Value))["GameParameters"];
                foreach(string part in row["path"].Value.Split('/'))source=source.IsArray?source[int.Parse(part)]:source[part];
                Assert.That(source.IsNumber,Is.True,row["path"].Value+" must exist, never default missing fields to zero");
                Assert.That(source.AsDouble,Is.EqualTo(row["raw"].AsDouble).Within(.000001));
                object boxed=SpecialWeaponConfigService.Current.Get(row["id"].AsInt).P;
                double actual=Convert.ToDouble(typeof(SpecialParameters).GetField(row["field"].Value).GetValue(boxed));
                Assert.That(actual,Is.EqualTo(row["expected"].AsDouble).Within(.0001),row["field"].Value);
            }
        }
        [Test]public void TrizookaPredictionSamplingHasSameFlightAtThirtySixtyAnd144Fps()
        {
            var c=SpecialWeaponConfigService.Current.Get(1);
            Vector3[] expected=null;
            foreach(int fps in new[]{30,60,144})
            {
                var e=new SpecialWeaponService.Entity(new SpecialEntityState{Position=Vector3.up*100,Velocity=Vector3.forward*c.P.projectileSpeed,Direction=Vector3.forward},c);
                var positions=new Vector3[3];
                for(int frame=1;frame<=fps*2;frame++)for(int i=0;i<3;i++)SpecialWeaponService.SampleTrizookaLobe(e,i,frame/(double)fps,out positions[i]);
                if(expected==null)expected=positions;
                else for(int i=0;i<3;i++)Assert.That(Vector3.Distance(positions[i],expected[i]),Is.LessThan(.000001f),$"lobe {i}, {fps} FPS");
            }
        }
        [TestCase(3)][TestCase(4)]public void DeployablesCanBeHeldWithoutAnInventedTenSecondTimeout(int id)
        {var s=Player(id);var i=Input();Step(ref s,i,0);Step(ref s,i,30);Assert.That(SpecialWeaponSimulation.Active(s),Is.True);Assert.That(s.SpecialRemaining,Is.EqualTo(1));Assert.That(s.SpecialChargeLockedUntil,Is.Zero);}
        [Test]public void QWhileDownedDoesNotReplayOnRescue()
        {var s=Player();s.Health=0;s.LifeState=PlayerLifeState.Bubble;var i=Input();Step(ref s,i,0);s.Health=100;s.LifeState=PlayerLifeState.Alive;Step(ref s,i,1);Assert.That(s.SpecialAction,Is.Zero);}
        [Test]public void AllDamageRatesToSonarMatchPinnedTable()
        {
            var cells=JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SpecialWeapons1130/DamageRates.json"))["CellList"];
            string[] rows={"UltraShot","TripleTornado","ShockSonar_Wave","InkStorm","Skewer"};
            for(int i=0;i<rows.Length;i++)Assert.That(SpecialObjectDamage.ToSonar((SpecialWeaponType)(i+1)),Is.EqualTo(cells[rows[i]+"___ShockSonar"]["DamageRate"].AsFloat));
        }
        [Test]public void FriendlyRainUsesSwimRecoveryWithoutShorteningDamageDelay()
        {
            var s=Player(4);s.Health=50;s.LastDamageAt=10;s.RainRecoveryUntil=20;var hero=GameplayConfig.GetHero(s.HeroId);
            ResourceSimulation.Step(ref s,hero,false,false,1f/60,10+hero.HealthRecoverDelay-.1);Assert.That(s.Health,Is.EqualTo(50));
            ResourceSimulation.Step(ref s,hero,false,false,1f/60,10+hero.HealthRecoverDelay);Assert.That(s.Health,Is.EqualTo(50+hero.SwimHealthRecoverRate/60).Within(.001));
        }
        [Test]public void EveryHeroUsesMeasuredFrameRecoveryRates()
        {
            foreach(var hero in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                Assert.That(hero.HealthRecoverDelay,Is.EqualTo(1));
                Assert.That(hero.HealthRecoverRate/60,Is.EqualTo(.21f).Within(.000001));
                Assert.That(hero.SwimHealthRecoverRate/60,Is.EqualTo(1.75f).Within(.000001));
            }
        }
        [Test]public void ReefInvincibilityHasVulnerableStartupAndBoundedTail()
        {var s=Player(5);var c=SpecialWeaponConfigService.Current.Get(5);Step(ref s,Input(),10);Assert.That(SpecialWeaponSimulation.Invincible(s,c,10),Is.False);Assert.That(SpecialWeaponSimulation.Invincible(s,c,10+c.P.rideInvincibleStart),Is.True);s.SpecialBurstAt=12;Assert.That(SpecialWeaponSimulation.Invincible(s,c,12+c.P.rideInvincibleAfter),Is.False);}
        [Test]public void AirborneReefWaitingStateRoundtripsAndDowningConsumesIt()
        {
            var s=Player(5);s.Grounded=false;Step(ref s,Input(),10);
            Assert.That(s.SpecialRideStage,Is.EqualTo(-1));Assert.That(s.SpecialPoints,Is.EqualTo(200));
            Assert.That(SpecialWeaponSimulation.CanCharge(s,100),Is.False);
            Assert.That(SpecialWeaponSimulation.Invincible(s,SpecialWeaponConfigService.Current.Get(5),100),Is.False);
            using var writer=new FastBufferWriter(1024,Allocator.Temp);writer.WriteNetworkSerializable(s);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot received);
            Assert.That(received.SpecialRideStage,Is.EqualTo(-1));Assert.That(received.SpecialPoints,Is.EqualTo(200));
            received.Health=0;received.LifeState=PlayerLifeState.Bubble;SpecialWeaponSimulation.Interrupt(ref received);
            Assert.That(received.SpecialPoints,Is.Zero);Assert.That(received.SpecialPhase,Is.EqualTo(SpecialPhase.Charging));
        }
        [Test]public void NetworkRoundtripCarriesLoadoutChargeEdgesAndRideState()
        {
            var s=Player(5);s.SpecialPoints=123.456;s.SpecialAction=45;s.SpecialConsumed=88;s.SpecialRideStage=2;s.SpecialDirection=Vector3.left;s.SpecialBurstAt=13.7;
            using var w=new FastBufferWriter(4096,Allocator.Temp);w.WriteNetworkSerializable(s);using var r=new FastBufferReader(w,Allocator.Temp);r.ReadNetworkSerializable(out PlayerSnapshot read);
            Assert.That(read.SubWeaponId,Is.EqualTo(s.SubWeaponId));Assert.That(read.SpecialWeaponId,Is.EqualTo(5));Assert.That(read.SpecialPoints,Is.EqualTo(123.456));Assert.That(read.SpecialConsumed,Is.EqualTo(88));Assert.That(read.SpecialDirection,Is.EqualTo(Vector3.left));Assert.That(read.SpecialBurstAt,Is.EqualTo(13.7));
        }
        [Test]public void ReferenceOverridesMatchPinnedRawData()
        {
            JSONNode Load(string name)=>JSONNode.Parse(File.ReadAllText("Tools/ValidationData/SpecialWeapons1130/WeaponSp"+name+".json"))["GameParameters"];
            var z=Load("UltraShot");var a=SpecialWeaponConfigService.Current.Get(1).P;Assert.That(a.directDamage,Is.EqualTo(z["DamageParam"]["DirectHitDamage"].AsFloat/10));Assert.That(a.innerDamage,Is.EqualTo(z["BlastParam"]["DistanceDamage"][0]["Damage"].AsFloat/10));Assert.That(a.duration,Is.EqualTo(z["spl__WeaponSpUltraShotParam"]["SpecialDurationFrame"]["Low"].AsFloat/60));
            var sonar=Load("ShockSonar")["spl__BulletSpShockSonarParam"];var b=SpecialWeaponConfigService.Current.Get(3).P;Assert.That(b.health,Is.EqualTo(sonar["GeneratorParam"]["MaxHP"].AsFloat/10));Assert.That(b.innerDamage,Is.EqualTo(sonar["WaveParam"]["Damage"].AsFloat/10));
            Assert.That(SpecialWeaponConfigService.Current.Get(4).P.effectDuration,Is.EqualTo(Load("InkStorm")["CloudParam"]["RainyFrame"]["Low"].AsFloat/60));
            Assert.That(SpecialWeaponConfigService.Current.Get(5).P.rideInvincibleStart,Is.EqualTo(Load("Skewer")["WeaponParam"]["NoDamageStartFrame_PreMove"].AsFloat/60));
        }
    }
}
#endif
