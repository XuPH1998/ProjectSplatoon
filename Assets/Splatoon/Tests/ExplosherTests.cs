#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class ExplosherTests
    {
        readonly List<GameObject> objects = new();
        WeaponRuntimeConfig W => GameplayConfig.GetWeapon(3);
        [SetUp] public void Setup()
        {
            HeroMigrationTests.Load();
            WeaponConfigService.Current.SetForEditor(3, AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(3).WeaponConfigPath).Snapshot());
        }
        [TearDown] public void Cleanup()
        { foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go); LubanConfigService.Current.Reset(); }
        static PlayerSnapshot Alive() => new() { HeroId = 3, Health = 100, Ink = 100, Grounded = true, Team = 1, Revision = 1 };
        static bool Tick(ref PlayerSnapshot s, WeaponRuntimeConfig w, int tick, bool held = true, uint press = 1, bool cancel = false, bool emerged = false)
            => WeaponSimulation.Step(ref s, new PlayerInputFrame { Fire = held, FireSequence = press, Sequence = (uint)tick + 1, CancelFire = cancel }, w, tick / 60.0, emerged, true);

        [Test] public void LiveProfileMatchesPinnedReferenceAndHeavyHero()
        {
            WeaponConfigValidation.Validate(W);
            var p = SimpleJSON.JSONNode.Parse(File.ReadAllText("Tools/ValidationData/Explosher/WeaponSlosherWashtub.1130.json"))["GameParameters"];
            Assert.That(W.Damage, Is.EqualTo(p["UnitGroupParam"]["Unit"][0]["DamageParam"]["ValueMax"].AsFloat / 10));
            Assert.That(W.Ammo.ExplosionDamage, Is.EqualTo(35)); Assert.That(W.PelletCount, Is.EqualTo(1));
            Assert.That(W.ShotInk, Is.EqualTo(p["WeaponParam"]["InkConsume"].AsFloat * 100).Within(.00001));
            Assert.That(WeaponSimulation.FireInterval(W), Is.EqualTo(55.0 / 60).Within(.000001));
            Assert.That(W.Ammo.ExplosionRadius, Is.EqualTo(2.87 * 18 / 24.037).Within(.00001));
            Assert.That(GameplayConfig.GetHero(3).MoveSpeed, Is.EqualTo(3.9539044).Within(.00001));
            Assert.That(GameplayConfig.GetHero(3).SwimSpeed, Is.EqualTo(7.76403045).Within(.00001));
            Assert.That(GameplayConfig.GetHero(3).WeaponTypeName, Is.EqualTo("爆炸泼桶"));
            Assert.That(W.Ammo.HasExplosionVisual, Is.True);
        }
        [TestCase(false,16)] [TestCase(true,22)]
        public void StartupCadenceTankAndIndependentLocks(bool emerged, int first)
        {
            var s = Alive(); var fired = new List<int>();
            for (int tick=0;tick<550;tick++)
            {
                if (!Tick(ref s,W,tick,emerged:emerged && tick==0)) continue;
                fired.Add(tick);
                Assert.That(s.AttackRecoveryUntil, Is.EqualTo((tick+35)/60.0).Within(1e-7));
                Assert.That(s.AttackMoveUntil, Is.EqualTo((tick+55)/60.0).Within(1e-7));
                Assert.That(s.InkRecoverAt, Is.EqualTo((tick+70)/60.0).Within(1e-7));
            }
            Assert.That(fired, Is.EqualTo(Enumerable.Range(0,8).Select(i=>first+i*55).ToArray()));
            Assert.That(s.Ink, Is.EqualTo(6.4).Within(.0001));
        }
        [Test] public void QuickTapCommitsOneShotAndCancelRequiresRelease()
        {
            var s=Alive(); int count=0;
            for(int t=0;t<120;t++) if(Tick(ref s,W,t,held:t==0)) count++;
            Assert.That(count,Is.EqualTo(1));
            s=Alive(); Tick(ref s,W,0); Tick(ref s,W,1,cancel:true);
            for(int t=2;t<100;t++) Assert.That(Tick(ref s,W,t),Is.False);
            Assert.That(s.Ink,Is.EqualTo(100)); Tick(ref s,W,100,held:false);
            for(int t=101;t<=117;t++) Tick(ref s,W,t,press:2);
            Assert.That(s.ShotSequence,Is.EqualTo(1));
        }
        [Test] public void LaunchInheritsLocalMovementAndAirSpeedWithoutRandomSpread()
        {
            var still=ExplosherSimulation.Launch(Vector3.forward,W,true);
            Assert.That(still.z,Is.EqualTo(63.374797).Within(.0001)); Assert.That(still.y,Is.EqualTo(still.z*.1f).Within(.00001));
            var moving=ExplosherSimulation.Launch(Vector3.forward,W,true,new Vector3(10,4,2));
            Assert.That(Vector3.Distance(moving-still,new Vector3(3,0,2)),Is.LessThan(.00001));
            var air = ExplosherSimulation.Launch(Vector3.forward,W,false,new Vector3(10,4,2));
            Assert.That(Vector3.Distance(air-ExplosherSimulation.Launch(Vector3.forward,W,false),new Vector3(3,2,2)),Is.LessThan(.00001));
            Assert.That(ExplosherSimulation.Launch(Vector3.forward,W,false).z,Is.EqualTo(62.4424845).Within(.0001));
            Assert.That(ExplosherSimulation.Radius(W,0,true),Is.EqualTo(.0435*18/24.037).Within(.00001));
            Assert.That(ExplosherSimulation.Radius(W,5.0/60,true),Is.EqualTo(W.ReferencePlayerRadius));
        }
        InkShot Shot() => new() { Id=1,Round=1,HeroId=3,Team=1,Shooter=999,Origin=new Vector3(500,1.4f,500),
            Velocity=ExplosherSimulation.Launch(Vector3.forward,W,true),Configuration=W,Born=10,ActionId=1,Seed=17,ShotSequence=1 };
        GameObject Box(string name,Vector3 center,Vector3 size,int surfaceId=0)
        {
            var go=new GameObject(name);objects.Add(go);go.transform.position=center;go.AddComponent<BoxCollider>().size=size;
            if(surfaceId!=0)go.AddComponent<PaintSurface>().SurfaceId=surfaceId;Physics.SyncTransforms();return go;
        }
        [Test] public void LifetimeExpiryDoesNotAirburstAndLateShotsKeepTheirSnapshot()
        {
            var shot=Shot();shot.Origin+=Vector3.up*1000;
            var service=new InkProjectileService();service.SpawnForMeasurement(shot);service.Simulate(15);
            Assert.That(service.ActiveCount,Is.Zero);Assert.That(service.Explosions,Is.Empty);Assert.That(service.Impacts.Count,Is.EqualTo(1));
            Assert.That(service.Impacts[0].ContinuesProjectile,Is.False);
            service.Simulate(20);Assert.That(service.Impacts.Count,Is.EqualTo(1));
        }
        [TestCase(0)] [TestCase(30)] [TestCase(-30)]
        public void PhysicsLandingAndPaintAgreeAcrossOuterRates(int pitch)
        {
            Box("Explosher floor",new Vector3(500,-.1f,520),new Vector3(100,.2f,100),94001);
            var shot=Shot();shot.Velocity=ExplosherSimulation.Launch(Quaternion.Euler(pitch,0,0)*Vector3.forward,W,true);
            List<PaintStamp> Measure(int hz,out Vector3 impact)
            {
                var stamps=new List<PaintStamp>();var service=new InkProjectileService{PaintObserved=stamps.Add};
                service.SpawnForMeasurement(shot);
                for(int i=1;i<=hz*6;i++)service.Simulate(shot.Born+(double)i/hz);
                Assert.That(service.Explosions.Count,Is.EqualTo(1));Assert.That(service.ActiveCount,Is.Zero);
                impact=service.Explosions[0].Position;return stamps;
            }
            var baseline=Measure(60,out var point);
            Assert.That(baseline.Any(s=>s.Radius>W.Ammo.ExplosionRadius),Is.True,"Paint must exceed damage radius");
            Assert.That(baseline.Any(s=>s.ClipEnabled),Is.True);
            foreach(int hz in new[]{30,144})
            {
                var actual=Measure(hz,out var end);Assert.That(end,Is.EqualTo(point));Assert.That(actual.Count,Is.EqualTo(baseline.Count));
                for(int i=0;i<actual.Count;i++) { Assert.That(actual[i].Position,Is.EqualTo(baseline[i].Position));Assert.That(actual[i].ShapeSeed,Is.EqualTo(baseline[i].ShapeSeed));Assert.That(actual[i].Radius,Is.EqualTo(baseline[i].Radius)); }
            }
            var aim=new TpsAimSolution{Muzzle=shot.Origin,InitialDirection=Quaternion.Euler(pitch,0,0)*Vector3.forward};
            Assert.That(Vector3.Distance(ExplosherSimulation.PredictLanding(new TpsAimSolver(),aim,W,true,default,0,999),point),Is.LessThan(.001));
        }
        [Test] public void BlastPaintInterpolatesDistanceSeparatelyFromDamageRadius()
        {
            var shot=Shot();
            Assert.That(ExplosherSimulation.PaintRadius(shot,shot.Origin),Is.EqualTo(W.Ammo.ExplosionPaintRadiusMax));
            Assert.That(ExplosherSimulation.PaintRadius(shot,shot.Origin+Vector3.forward*50),Is.EqualTo(W.Ammo.ExplosionPaintRadiusMin));
            Assert.That(InkExplosionRules.Damage(W.Ammo,true,W.Ammo.ExplosionRadius),Is.EqualTo(35));
            Assert.That(InkExplosionRules.Damage(W.Ammo,true,W.Ammo.ExplosionRadius+.01f),Is.Zero);
        }
        [Test] public void NetworkRoundTripPreservesIndependentDeadlinesAndNonterminalHits()
        {
            var s=Alive();s.AttackRecoveryUntil=.583333;s.AttackMoveUntil=.916667;
            using var writer=new FastBufferWriter(4096,Allocator.Temp);writer.WriteNetworkSerializable(s);
            using var reader=new FastBufferReader(writer,Allocator.Temp);reader.ReadNetworkSerializable(out PlayerSnapshot copy);
            Assert.That(copy.AttackMoveUntil,Is.EqualTo(s.AttackMoveUntil));Assert.That(copy.AttackRecoveryUntil,Is.EqualTo(s.AttackRecoveryUntil));
            var impact=new InkImpact{ContinuesProjectile=true,Id=8,Damage=55,Victim=2};
            using var ew=new FastBufferWriter(512,Allocator.Temp);ew.WriteNetworkSerializable(impact);
            using var er=new FastBufferReader(ew,Allocator.Temp);er.ReadNetworkSerializable(out InkImpact restored);
            Assert.That(restored.ContinuesProjectile,Is.True);Assert.That(restored.Damage,Is.EqualTo(55));
        }
        [Test] public void FlightSurvivesPiercingFeedbackAndTerminatesOnce()
        {
            var parent=new GameObject("Explosher visual test");objects.Add(parent);
            using var flight=new InkFlightPresentation(parent.transform,W.Ammo);
            var shot=Shot();Assert.That(flight.Spawn(shot,10),Is.True);
            flight.Complete(new InkImpact{Id=1,Round=1,Damage=55,ContinuesProjectile=true});Assert.That(flight.ActiveShots,Is.EqualTo(1));
            flight.Complete(new InkImpact{Id=1,Round=1});Assert.That(flight.ActiveShots,Is.Zero);
            Assert.That(flight.Spawn(shot,10),Is.False);
        }
        [TestCase("flat")] [TestCase("up30")] [TestCase("down30")] [TestCase("high-drop")]
        [TestCase("wall-near")] [TestCase("wall-middle")] [TestCase("wall-far")] [TestCase("slope")] [TestCase("occluded")]
        public void MeasuredOwnershipAndTrajectoriesMatchAcrossDriverRates(string scenario)
        {
            const string path="Reports/Explosher/Measurements";Directory.CreateDirectory(path);
            var reference=WeaponReferenceMeasurements.Capture(3,0,scenario,60,1,path,fullActions:true);
            Assert.That(reference.emittedProjectiles,Is.EqualTo(1));Assert.That(reference.impacts,Is.EqualTo(1));
            Assert.That(reference.inkSpent,Is.EqualTo(11.7).Within(.0001));
            foreach(int rate in new[]{30,144})
            {
                var actual=WeaponReferenceMeasurements.Capture(3,0,scenario,rate,1,path,fullActions:true);
                Assert.That(actual.gridHash,Is.EqualTo(reference.gridHash),scenario+" ownership");
                Assert.That(actual.ownedArea,Is.EqualTo(reference.ownedArea));Assert.That(actual.paintStamps,Is.EqualTo(reference.paintStamps));
            }
        }
        [Test] public void RecoveryInkAndMovementLocksExpireIndependently()
        {
            var s=Alive();for(int t=0;t<=16;t++)Tick(ref s,W,t);
            WeaponSimulation.Cancel(ref s,new PlayerInputFrame{FireSequence=1});
            Assert.That(WeaponSimulation.RecoveryLocked(s,50/60.0),Is.True);
            Assert.That(WeaponSimulation.RecoveryLocked(s,51/60.0),Is.False);
            Assert.That(s.AttackMoveUntil,Is.EqualTo(71/60.0).Within(.000001));
            float ink=s.Ink;var hero=GameplayConfig.GetHero(3);
            ResourceSimulation.Step(ref s,hero,false,false,1f/60,85/60.0);Assert.That(s.Ink,Is.EqualTo(ink));
            ResourceSimulation.Step(ref s,hero,false,false,1f/60,86/60.0);Assert.That(s.Ink,Is.GreaterThan(ink));
            s=Alive();s.Ink=11.699f;for(int t=0;t<90;t++)Tick(ref s,W,t);
            Assert.That(s.ShotSequence,Is.Zero);Assert.That(s.Ink,Is.EqualTo(11.699f));
        }
        [Test] public void EveryExplosherParameterParticipatesInSnapshotAndSignature()
        {
            var source=AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(3).WeaponConfigPath);
            foreach(var field in typeof(WeaponConfigAsset).GetFields().Where(f=>f.Name.StartsWith("explosher")))
            {
                var clone=UnityEngine.Object.Instantiate(source);
                try
                {
                    if(field.FieldType==typeof(float))field.SetValue(clone,(float)field.GetValue(clone)+.001f);
                    else field.SetValue(clone,(double)field.GetValue(clone)+.001);
                    Assert.That(W.SameValues(clone.Snapshot()),Is.False,field.Name);
                }
                finally{UnityEngine.Object.DestroyImmediate(clone);}
            }
        }
        [Test] public void AllFloorPaintIncludingTrailAndImpactIsClippedByThinSideWall()
        {
            InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
            Box("Clip floor",new Vector3(500,-.1f,520),new Vector3(100,.2f,100),94001);
            Box("Thin side wall",new Vector3(501.5f,3,520),new Vector3(.1f,6,100));
            var stamps=new List<PaintStamp>();var service=new InkProjectileService{PaintObserved=stamps.Add};
            service.SpawnForMeasurement(Shot());service.Simulate(20);
            Assert.That(stamps.Count,Is.GreaterThan(3));Assert.That(stamps.All(s=>s.ClipEnabled),Is.True);
            foreach(var stamp in stamps.Where(s=>s.Normal.y>.9f))
                for(float x=501.6f;x<504;x+=.125f)for(float z=499;z<522;z+=.125f)
                    Assert.That(InkShapeAtlas.Coverage(new Vector3(x,0,z),stamp),Is.LessThan(.5f),"No floor ownership through thin wall");
        }
        [Test] public void PhysicsMotorHonorsDiveAndMovementDeadlinesSeparately()
        {
            Box("Motor floor",new Vector3(500,-.1f,500),new Vector3(100,.2f,100));
            var go=new GameObject("Explosher motor");objects.Add(go);go.layer=8;
            var cc=go.AddComponent<CharacterController>();cc.height=1.8f;cc.center=Vector3.up*.9f;cc.radius=.35f;
            var motor=new PlayerMotorSimulation(cc);var s=Alive();s.Position=new Vector3(500,.05f,500);
            s.AttackRecoveryUntil=35/60.0;s.AttackMoveUntil=55/60.0;motor.Restore(s);
            motor.Step(ref s,new PlayerInputFrame{Swim=true},1f/60,34/60.0,false,W.ShootMoveSpeed);
            Assert.That(s.Swimming,Is.False);
            motor.Step(ref s,new PlayerInputFrame{Swim=true},1f/60,35/60.0,false,W.ShootMoveSpeed);
            Assert.That(s.Swimming,Is.True,"Dive unlocks at 35 frames independently of move restriction");
            s.Swimming=false;s.Movement=MovementMode.Human;s.PlanarVelocity=Vector3.forward*W.ShootMoveSpeed;motor.Restore(s);
            motor.Step(ref s,new PlayerInputFrame{Move=Vector2.up},1f/60,54/60.0,false,W.ShootMoveSpeed);
            Assert.That(s.PlanarVelocity.magnitude,Is.EqualTo(W.ShootMoveSpeed).Within(.0001));
            motor.Step(ref s,new PlayerInputFrame{Move=Vector2.up},1f/60,55/60.0,false,W.ShootMoveSpeed);
            Assert.That(s.PlanarVelocity.magnitude,Is.GreaterThan(W.ShootMoveSpeed));
        }
        [Test] public void HeroChangeCancelsStartupAndRetainsFrozenFlight()
        {
            var old=W;var s=Alive();Tick(ref s,W,0);Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Starting));
            var service=new InkProjectileService();var shot=Shot();service.SpawnForMeasurement(shot);
            HeroSelectionRules.Apply(ref s,1,false,new PlayerInputFrame{Fire=true,FireSequence=1});
            Assert.That(s.WeaponPhase,Is.EqualTo(WeaponPhase.Idle));Assert.That(s.Ink,Is.EqualTo(100));Assert.That(s.AttackNeedsRelease,Is.True);
            Assert.That(service.LiveShots().Single().Configuration,Is.SameAs(old));
            Assert.That(service.LiveShots().Single().HeroId,Is.EqualTo(3));
        }
        [Test] public void AuthoritativeRestoreDoesNotReplayFeedbackOrResurrectTerminalFlight()
        {
            var go=new GameObject("Explosher restore");objects.Add(go);var view=go.AddComponent<InkPresentation>();
            var shot=Shot();view.ClearFlights(10.1);view.Spawn(shot);view.UpdateFlights(10.2,null);Assert.That(view.ActiveShots,Is.Zero);
            view.RestoreExplosher(shot,10.15);view.RestoreExplosher(shot,10.15);view.UpdateFlights(10.2,null);
            Assert.That(view.ActiveShots,Is.EqualTo(1));Assert.That(view.ExplosherRestoredCount,Is.EqualTo(1));
            view.Impact(new InkImpact{Id=shot.Id,Round=shot.Round});view.RestoreExplosher(shot,10.2);view.UpdateFlights(10.3,null);
            Assert.That(view.ActiveShots,Is.Zero);
            view.ClearFlights(11);shot.Id++;view.RestoreExplosher(shot,10.9);Assert.That(view.ExplosherRestoredCount,Is.EqualTo(1));
        }
    }
}
#endif
