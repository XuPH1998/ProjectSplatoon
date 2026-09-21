#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
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
    public sealed class SubWeaponTests
    {
        SubWeaponConfigAsset Asset(SubWeaponType type)
        { var a = ScriptableObject.CreateInstance<SubWeaponConfigAsset>(); SubWeaponDefaults.Apply(a, type); return a; }
        [Test]
        public void AllThirteenDefaultsValidateAndSnapshotOnlyActiveFields()
        {
            foreach (SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
            {
                var a = Asset(type);
                try
                {
                    var before = a.Snapshot(); before.Validate();
                    if (type != SubWeaponType.ToxicMist) { a.mist.duration = double.NaN; a.mist.moveRate = -10; }
                    if (type != SubWeaponType.FizzyBomb) a.fizzy.thirdCharge = -10;
                    var after = a.Snapshot(); after.Validate(); Assert.That(after.ContentHash, Is.EqualTo(before.ContentHash), type.ToString());
                    a.common.inkCost += 1; Assert.That(a.Snapshot().ContentHash, Is.Not.EqualTo(before.ContentHash));
                    Assert.That(before.Common.inkCost, Is.EqualTo(after.Common.inkCost));
                }
                finally { UnityEngine.Object.DestroyImmediate(a); }
            }
        }
        [Test]
        public void InspectorVisibilityUsesSelectedTypeAtBothLevels()
        {
            var editor = Type.GetType("Splatoon.Editor.SubWeaponConfigAssetEditor, Splatoon.Editor"); Assert.That(editor, Is.Not.Null);
            var visible = editor.GetMethod("IsVisible");
            foreach (SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
            {
                Assert.That(visible.Invoke(null, new object[] { "common.inkLock", type }), Is.True);
                Assert.That(visible.Invoke(null, new object[] { "fizzy.thirdCharge", type }), Is.EqualTo(type == SubWeaponType.FizzyBomb));
                Assert.That(visible.Invoke(null, new object[] { "blast.directDamage", type }), Is.EqualTo(type == SubWeaponType.BurstBomb || type == SubWeaponType.Torpedo));
                Assert.That(visible.Invoke(null, new object[] { "flight.speed", type }), Is.EqualTo(type != SubWeaponType.InkMine && type != SubWeaponType.AngleShooter && type != SubWeaponType.CurlingBomb));
                Assert.That(visible.Invoke(null, new object[] { "tracking.triggerRadius", type }), Is.EqualTo(type==SubWeaponType.Autobomb));
            }
        }
        [Test]
        public void HiddenFieldSurvivesUndoAndTypeChanges()
        {
            var a = Asset(SubWeaponType.FizzyBomb);
            try
            {
                a.fizzy.thirdCharge = 2.75;
                var so = new SerializedObject(a); so.FindProperty("type").intValue = (int)SubWeaponType.SplatBomb; so.ApplyModifiedProperties();
                Assert.That(a.fizzy.thirdCharge, Is.EqualTo(2.75));
                Undo.PerformUndo(); Assert.That(a.type, Is.EqualTo(SubWeaponType.FizzyBomb)); Assert.That(a.fizzy.thirdCharge, Is.EqualTo(2.75));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }
        [Test]
        public void ChargeThresholdsAndRecoveryLocksAreIndependent()
        {
            var f = Asset(SubWeaponType.FizzyBomb); var c = Asset(SubWeaponType.CurlingBomb);
            try
            {
                var fizzy = f.Snapshot(); Assert.That(fizzy.Charge(39d / 60), Is.Zero); Assert.That(fizzy.Charge(40d / 60), Is.EqualTo(.5f)); Assert.That(fizzy.Charge(80d / 60), Is.EqualTo(1));
                Assert.That(fizzy.InkLock(1), Is.EqualTo(85d / 60));
                var curling = c.Snapshot(); Assert.That(curling.Charge(.5), Is.EqualTo(.5f)); Assert.That(curling.Charge(4), Is.EqualTo(1));
                Assert.That(curling.InkLock(0), Is.EqualTo(70d / 60)); Assert.That(curling.InkLock(1), Is.EqualTo(.5));
            }
            finally { UnityEngine.Object.DestroyImmediate(f); UnityEngine.Object.DestroyImmediate(c); }
        }
        static PlayerSnapshot Player(float ink = 100) => new() { Health = 100, Ink = ink, HeroId = 1, HeroRevision = 1, Revision = 1, Grounded = true };
        static PlayerInputFrame Input(uint press, uint release, bool held) => new() { HeroRevision = 1, Revision = 1, SubPressSequence = press, SubReleaseSequence = release, SubHeld = held };
        [Test]
        public void InkLockDoesNotPreventSecondBurstAndDuplicateReleaseCannotSpendAgain()
        {
            var a = Asset(SubWeaponType.BurstBomb);
            try
            {
                a.common.startup = 0; var c = a.Snapshot(); var s = Player();
                Assert.That(SubWeaponSimulation.Step(ref s, Input(1,1,false), c, 1f/60, 1, true, SubWeaponFailure.None, out _), Is.True);
                Assert.That(s.Ink, Is.EqualTo(55)); Assert.That(s.InkRecoverAt, Is.EqualTo(2));
                Assert.That(SubWeaponSimulation.Step(ref s, Input(1,1,false), c, 1f/60, 1.4, true, SubWeaponFailure.None, out _), Is.False);
                Assert.That(SubWeaponSimulation.Step(ref s, Input(2,2,false), c, 1f/60, 1.4, true, SubWeaponFailure.None, out _), Is.True);
                Assert.That(s.Ink, Is.EqualTo(10)); Assert.That(s.SubAction, Is.EqualTo(2));
                Assert.That(SubWeaponSimulation.Step(ref s, Input(3,3,false), c, 1f/60, 1.8, true, SubWeaponFailure.None, out _), Is.False); Assert.That(s.Ink, Is.EqualTo(10));
            }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }
        [TestCase(SubWeaponFailure.InvalidGround)] [TestCase(SubWeaponFailure.ActiveLimit)]
        public void FailedPlacementNeverSpendsOrQueuesAutomaticUse(SubWeaponFailure failure)
        {
            var a = Asset(SubWeaponType.InkMine);
            try
            {
                a.common.startup=0; var c=a.Snapshot(); var s=Player(); var i=Input(1,1,false);
                Assert.That(SubWeaponSimulation.Step(ref s,i,c,.016f,1,true,failure,out _),Is.False); Assert.That(s.Ink,Is.EqualTo(100));
                Assert.That(SubWeaponSimulation.Step(ref s,i,c,.016f,2,true,SubWeaponFailure.None,out _),Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }
        [Test]
        public void CancelAndSwimNeverReleaseChargedBombAndCannotSkipRecovery()
        {
            var a=Asset(SubWeaponType.FizzyBomb);
            try
            {
                var c=a.Snapshot(); var s=Player(); var i=Input(1,0,true);
                SubWeaponSimulation.Step(ref s,i,c,1,1,true,SubWeaponFailure.None,out _); Assert.That(s.SubCharge,Is.EqualTo(.5f));
                i.CancelSub=true; i.SubReleaseSequence=1; i.SubHeld=false;
                Assert.That(SubWeaponSimulation.Step(ref s,i,c,.016f,2,true,SubWeaponFailure.None,out _),Is.False); Assert.That(s.Ink,Is.EqualTo(100));
                s.SubRecoveryUntil=8; SubWeaponSimulation.Cancel(ref s,i); Assert.That(SubWeaponSimulation.BlocksMain(s,7),Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(a); }
        }
        [Test]
        public void InputAndStateRoundTripIncludesEdgesChargeAndStatuses()
        {
            var input=Input(8,7,true); input.CancelSub=true;
            using var writer=new FastBufferWriter(4096,Allocator.Temp); writer.WriteNetworkSerializable(input);
            var s=Player(); s.SubAction=12;s.SubPhase=SubWeaponPhase.Holding;s.SubChargeSeconds=1.25;s.SubRecoveryUntil=5;s.MarkedUntilBlue=15;s.MistMoveRate=.6f;
            writer.WriteNetworkSerializable(s);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PlayerInputFrame i);reader.ReadNetworkSerializable(out PlayerSnapshot other);
            Assert.That(i.SubPressSequence,Is.EqualTo(8));Assert.That(i.SubReleaseSequence,Is.EqualTo(7));Assert.That(i.CancelSub&&i.SubHeld,Is.True);
            Assert.That(other.SubChargeSeconds,Is.EqualTo(1.25));Assert.That(other.SubRecoveryUntil,Is.EqualTo(5));Assert.That(other.MarkedUntilBlue,Is.EqualTo(15));Assert.That(other.MistMoveRate,Is.EqualTo(.6f));
        }
        [Test]
        public void AllAssetsAndHeroBindingsResolve()
        {
            var types=(SubWeaponType[])Enum.GetValues(typeof(SubWeaponType)); Assert.That(types.Length,Is.EqualTo(13));
            foreach(var t in types)
            {
                var a=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(t));Assert.That(a,Is.Not.Null,t.ToString());a.Snapshot().Validate();
                Assert.That(a.common.entityPrefab,Is.Not.Null);Assert.That(a.common.icon,Is.Not.Null);Assert.That(a.common.useAudio,Is.Not.Null);Assert.That(a.common.effectAudio,Is.Not.Null);
                Assert.That(a.common.effectMaterial,Is.Not.Null);Assert.That(a.common.effectMaterial.shader,Is.Not.Null);
            }
            var json=SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/tbhero.json"));
            foreach(var row in json.Children) Assert.That(AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row["subWeaponConfigPath"]),Is.Not.Null);
        }
        [TestCase("startup")] [TestCase("death")] [TestCase("hero")] [TestCase("swim")]
        public void CancellationDuringStartupCannotSpendInk(string reason)
        {
            var a=Asset(SubWeaponType.FizzyBomb);
            try
            {
                var c=a.Snapshot();var s=Player();var input=Input(1,0,true);
                SubWeaponSimulation.Step(ref s,input,c,1.5f,1,true,SubWeaponFailure.None,out _);
                input=Input(1,1,false);Assert.That(SubWeaponSimulation.Step(ref s,input,c,.016f,2,true,SubWeaponFailure.None,out _),Is.False);
                Assert.That(s.SubPhase,Is.EqualTo(SubWeaponPhase.Starting));
                if(reason=="startup")input.CancelSub=true;else if(reason=="death")s.Health=0;else if(reason=="hero")input.HeroRevision++;else input.Swim=true;
                Assert.That(SubWeaponSimulation.Step(ref s,input,c,.2f,2.2,true,SubWeaponFailure.None,out _),Is.False);
                Assert.That(s.Ink,Is.EqualTo(100));Assert.That(s.SubAction,Is.Zero);Assert.That(s.SubPhase,Is.EqualTo(SubWeaponPhase.Idle));
            }
            finally {UnityEngine.Object.DestroyImmediate(a);}
        }
        [Test]
        public void ERefundsReservedMainInkExactlyOnceAndChecksStandingSpace()
        {
            var a=Asset(SubWeaponType.SplatBomb);
            try
            {
                a.common.startup=0;var c=a.Snapshot();var s=Player(60);s.SplatlingReservedInk=40;s.WeaponPhase=WeaponPhase.Charging;
                var input=Input(1,0,true);SubWeaponSimulation.Step(ref s,input,c,.016f,1,false,SubWeaponFailure.None,out _);
                Assert.That(s.SubFailure,Is.EqualTo(SubWeaponFailure.NoStandingRoom));Assert.That(s.Ink,Is.EqualTo(60));
                input=Input(1,1,false);SubWeaponSimulation.Step(ref s,input,c,.016f,1.1,true,SubWeaponFailure.None,out _);
                input=Input(2,2,false);Assert.That(SubWeaponSimulation.Step(ref s,input,c,.016f,1.2,true,SubWeaponFailure.None,out _),Is.True);
                Assert.That(s.Ink,Is.EqualTo(30));Assert.That(s.SplatlingReservedInk,Is.Zero);
                SubWeaponSimulation.Step(ref s,input,c,.016f,2,true,SubWeaponFailure.None,out _);Assert.That(s.Ink,Is.EqualTo(30));
            }
            finally {UnityEngine.Object.DestroyImmediate(a);}
        }
        [Test]
        public void ExistingEntitiesKeepSnapshotAfterReload()
        {
            var a=Asset(SubWeaponType.SuctionBomb);var service=new SubWeaponConfigService();
            try
            {
                service.Set(1,a);uint revision=service.Revision(1);var old=service.ForEntity(1,revision);
                a.suction.fuse=3;service.Set(1,a);
                Assert.That(service.Revision(1),Is.GreaterThan(revision));Assert.That(service.ForEntity(1,revision),Is.SameAs(old));Assert.That(old.Suction.fuse,Is.EqualTo(2));
                Assert.That(service.ForEntity(1,service.Revision(1)).Suction.fuse,Is.EqualTo(3));
            }
            finally {service.Clear();UnityEngine.Object.DestroyImmediate(a);}
        }
        [Test]
        public void DeployableMultipliersMatchPinnedTable()
        {
            HeroMigrationTests.Load();
            try
            {
            Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(3),SubWeaponType.Sprinkler),Is.EqualTo(5));
            Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(3),SubWeaponType.Sprinkler,true),Is.EqualTo(4));
            Assert.That(SubWeaponObjectDamage.SubMultiplier(SubWeaponType.FizzyBomb,SubWeaponType.SplashWall),Is.EqualTo(3.6f));
            Assert.That(SubWeaponObjectDamage.SubMultiplier(SubWeaponType.Torpedo,SubWeaponType.SplashWall,true),Is.EqualTo(1));
            Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(7),SubWeaponType.SplashWall),Is.EqualTo(2));
            Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(5),SubWeaponType.Sprinkler),Is.EqualTo(2.4f));
            Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(8),SubWeaponType.SplashWall),Is.EqualTo(1.1f));
            for(int hero=1;hero<=9;hero++)Assert.That(SubWeaponObjectDamage.Multiplier(GameplayConfig.GetWeapon(hero),SubWeaponType.Torpedo),Is.EqualTo(1));
            }
            finally { LubanConfigService.Current.Reset(); }
        }
    }
}
#endif
