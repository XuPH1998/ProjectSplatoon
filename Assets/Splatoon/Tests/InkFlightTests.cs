#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class InkFlightTests
    {
        GameObject _root;
        Camera _camera;
        InkFlightPresentation _flight;
        WeaponRuntimeConfig _weapon;
        InkFlightProfile _profile;
        readonly ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[2048];
        [SetUp] public void Setup()
        {
            _root = new GameObject("Flight acceptance"); _camera = _root.AddComponent<Camera>();
            _camera.transform.position = new Vector3(3, 2, -4);
            _profile = AssetDatabase.LoadAssetAtPath<InkFlightProfile>("Assets/GameResource/Effects/Ink/InkFlightProfile.asset");
            Assert.That(_profile, Is.Not.Null, "Run InkFlightBuilder once to bind authored assets");
            _flight = new InkFlightPresentation(_root.transform, _profile.FlightPrefab, _profile);
            _weapon = new WeaponRuntimeConfig(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset"));
        }
        [TearDown] public void TearDown() { _flight?.Dispose(); Object.DestroyImmediate(_root); }
        InkShot Shot(uint id = 1, ulong shooter = 1, byte muzzle = 0, byte pellet = 0) => new()
        { Id = id, Round = 1, Seed = 123 + id, Shooter = shooter, HeroId = 1, Team = 1, Born = 10,
            Origin = Vector3.up * 2, Velocity = Vector3.forward * 32, Configuration = _weapon,
            ActionId = id, MuzzleIndex = muzzle, PelletIndex = pellet, Lifecycle = 1, HeroRevision = 1 };
        [Test] public void StableAgeAndVelocityFollowTheFullAuthorityTrajectory()
        {
            var s = Shot(); s.FirstSegmentLength = 2; s.PostCorrectionVelocity = new Vector3(.3f,0,1).normalized * 32; s.GravityStartAge = .04f;
            Assert.That(_flight.Spawn(s, 10.2), Is.True);
            foreach (double age in new[] { .2, .4, .8 })
            {
                _flight.Update(10 + age, _camera); Assert.That(_flight.CopyParticles(1, _particles), Is.GreaterThan(0));
                Assert.That(Vector3.Distance(_particles[0].position, InkBallistics.Position(s, _weapon, age)), Is.LessThan(.0001));
                Assert.That(Vector3.Distance(_particles[0].velocity, InkBallistics.Velocity(s, _weapon, age)), Is.LessThan(.0001));
                Assert.That(_particles[0].startLifetime - _particles[0].remainingLifetime, Is.EqualTo(age).Within(.0001));
            }
        }
        [Test] public void ReferenceSizeCurvesProduceAgeDependentAnisotropicInk()
        {
            _flight.Spawn(Shot(),10); _flight.Update(10.08,_camera);
            var stream = _flight.Streams.First(p=>p.particleCount>0); stream.GetParticles(_particles);
            Vector3 early = _particles[0].GetCurrentSize3D(stream);
            _flight.Update(10.93,_camera); stream.GetParticles(_particles);
            Vector3 late = _particles[0].GetCurrentSize3D(stream);
            Assert.That(early.z, Is.GreaterThan(early.x * 1.1f), "Reference stretches along velocity");
            Assert.That((late-early).sqrMagnitude, Is.GreaterThan(.00001f), "Age must reach source curves");
            Assert.That(stream.GetComponent<ParticleSystemRenderer>().alignment, Is.EqualTo(ParticleSystemRenderSpace.Velocity));
        }
        [Test] public void DifferentShootersHandsLivesAndPelletsNeverShareRibbons()
        {
            _flight.Spawn(Shot(1),10); _flight.Spawn(Shot(2,2),10); _flight.Spawn(Shot(3,1,1),10); _flight.Spawn(Shot(4,1,0,1),10);
            var s = Shot(5); s.Lifecycle++; _flight.Spawn(s,10);
            _flight.Update(10.2,_camera);
            Assert.That(_flight.ActiveGroups,Is.EqualTo(5));
            Assert.That(_root.GetComponentsInChildren<MeshRenderer>().All(r=>r.gameObject.layer==InkFlightProfile.Layer),Is.True);
        }
        [Test] public void HitBeforeShotDuplicateLateAndOldRoundCannotResurrectFlight()
        {
            var s = Shot(); _flight.Complete(new InkImpact{Id=s.Id,Round=1});
            Assert.That(_flight.Spawn(s,10),Is.False);
            s.Id=2; Assert.That(_flight.Spawn(s,10.1),Is.True); Assert.That(_flight.Spawn(s,10.1),Is.False);
            _flight.Complete(new InkImpact{Id=2,Round=1}); _flight.Update(10.2,_camera); Assert.That(_flight.ParticleCount,Is.Zero);
            s.Id=3; s.Round=2; _flight.Spawn(s,10.2); s.Id=4;s.Round=1; Assert.That(_flight.Spawn(s,10.2),Is.False);
            s.Id=5;s.Round=2; Assert.That(_flight.Spawn(s,12),Is.False);
        }
        [Test] public void ClearDropsRibbonsAndRejectsEventsFromBeforeTheClear()
        {
            _flight.Spawn(Shot(),10); _flight.Update(10.3,_camera); _flight.Clear(10.4);
            Assert.That(_flight.ActiveShots+_flight.ActiveGroups+_flight.ParticleCount,Is.Zero);
            Assert.That(_flight.Spawn(Shot(9),10.5),Is.False);
            var next=Shot(10);next.Born=10.5;Assert.That(_flight.Spawn(next,10.5),Is.True);
        }
        [Test] public void ContinuousVisualDensityDoesNotAddAuthorityShots()
        {
            for(uint i=0;i<15;i++){var s=Shot(i+1);s.Born+=i/15.0;_flight.Spawn(s,s.Born);}
            Assert.That(_flight.ActiveShots,Is.EqualTo(15));
            _flight.Update(10.999,_camera);
            Assert.That(_flight.ParticleCount,Is.InRange(60,68));
            Assert.That(_flight.ActiveGroups,Is.EqualTo(1));
        }
        [Test] public void RepeatedImpactsShrinkRibbonIndicesAndReuseTheSameVertexCapacity()
        {
            for(uint cycle=0;cycle<5;cycle++)
            {
                for(uint i=1;i<=12;i++){var s=Shot(cycle*12+i);s.Born=10+i*.01;_flight.Spawn(s,10.2);}
                _flight.Update(10.3,_camera);
                var mesh=_root.GetComponentsInChildren<MeshFilter>().Single().sharedMesh;
                Assert.That(mesh.vertexCount,Is.EqualTo(InkFlightPresentation.ParticlesPerGroup*2));
                for(uint i=1;i<=12;i++)
                {
                    _flight.Complete(new InkImpact{Id=cycle*12+i,Round=1});_flight.Update(10.3,_camera);
                    Assert.That(mesh.triangles.All(index=>index<mesh.vertexCount),Is.True);
                }
                Assert.That(_flight.ActiveGroups+_flight.ParticleCount,Is.Zero);
                Assert.That(mesh.GetIndexCount(0),Is.Zero);
            }
        }
        [Test] public void WarmFlightUpdateHasNoManagedAllocationAndPoolIsBounded()
        {
            for(uint i=0;i<8;i++)_flight.Spawn(Shot(i+1,i+1),10);
            for(int i=0;i<8;i++)_flight.Update(10.1+i*.001,_camera);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<60;i++)_flight.Update(10.2+i*.001,_camera);
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
            for(uint i=8;i<InkFlightPresentation.GroupLimit+12;i++)_flight.Spawn(Shot(i+1,i+1),10.2);
            Assert.That(_flight.ActiveGroups,Is.EqualTo(InkFlightPresentation.GroupLimit));
            Assert.That(_flight.DroppedSamples,Is.GreaterThan(0));
        }
        [Test] public void EightShotgunsCanKeepThreeLiveReleasesWithoutDroppingPelletLanes()
        {
            var shotgun=new WeaponRuntimeConfig(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/ShotgunGirl/ShotgunGirlWeaponConfig.asset"));
            uint id=1;
            for(uint shooter=1;shooter<=8;shooter++)for(uint burst=0;burst<3;burst++)for(byte pellet=0;pellet<8;pellet++)
            {
                var s=Shot(id++,shooter,0,pellet);s.Configuration=shotgun;s.ActionId=burst+1;s.Born+=burst*.4;
                Assert.That(_flight.Spawn(s,10.85),Is.True);
            }
            _flight.Update(10.85,_camera);
            Assert.That(_flight.ActiveGroups,Is.EqualTo(192));Assert.That(_flight.ParticleCount,Is.EqualTo(384));
            Assert.That(_flight.DroppedSamples,Is.Zero);
        }
        [Test] public void MuzzleUsesReferenceBurstAndContinuousGateWithoutCollisionCallbacks()
        {
            var ps=Object.Instantiate(_profile.MuzzlePrefab,_root.transform);
            var muzzle=ps.gameObject.AddComponent<InkMuzzleEmitter>();muzzle.Initialize(_profile);
            muzzle.Shot(41,1,true,10,10);Assert.That(muzzle.BurstCount,Is.EqualTo(1));
            var drops = new ParticleSystem.Particle[160]; int count = ps.GetParticles(drops);
            Assert.That(count, Is.EqualTo(40));
            for (int i = 0; i < count; i++)
            {
                Assert.That(drops[i].startSize, Is.InRange(.079f, .101f), "Reference muzzle start size");
                Assert.That(drops[i].startSize3D.y, Is.EqualTo(drops[i].startSize3D.x));
                Assert.That(drops[i].startSize3D.z, Is.EqualTo(drops[i].startSize3D.x));
                Assert.That(drops[i].startLifetime, Is.InRange(.049f, .201f), "Reference muzzle lifetime");
            }
            muzzle.Shot(42,1,true,10.066,10.066);muzzle.Present(true,true,10.12);Assert.That(muzzle.BurstCount,Is.EqualTo(1));
            muzzle.Present(true,true,10.14);Assert.That(muzzle.BurstCount,Is.EqualTo(2));
            muzzle.Present(false,true,10.15);muzzle.Present(true,true,10.3);Assert.That(muzzle.BurstCount,Is.EqualTo(2));
            Assert.That(ps.collision.sendCollisionMessages || ps.subEmitters.enabled || ps.emission.enabled,Is.False);
            muzzle.Shot(50,2,false,11,10);Assert.That(muzzle.BurstCount,Is.EqualTo(2));
            muzzle.Shot(51,2,false,11,11);Assert.That(muzzle.BurstCount,Is.EqualTo(3));
            muzzle.Stop();Assert.That(ps.particleCount,Is.Zero);
        }
    }
}
#endif
