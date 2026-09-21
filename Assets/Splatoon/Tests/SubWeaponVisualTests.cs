#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SubWeaponVisualTests
    {
        [Test]
        public void AllThirteenPoolsRestorePoseMaterialsAndParticlesAcrossTwentyUses()
        {
            var parent = new GameObject("Pool test"); var placement = new GameObject("Placement");
            using var pool = new SubWeaponVisualPool(parent.transform);
            try
            {
                foreach (SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));
                    Assert.That(asset.visuals.trailMaterial, Is.Not.Null, type.ToString());
                    for (int use = 0; use < 20; use++)
                    {
                        placement.transform.SetPositionAndRotation(new Vector3(12+use, 3, -7-use), Quaternion.Euler(use*7, use*17, 0));
                        var model = pool.Rent(asset.common.entityPrefab, placement.transform);
                        Assert.That(Vector3.Distance(model.transform.position,placement.transform.position),Is.LessThan(.00001),type+" use "+use);
                        Assert.That(Quaternion.Angle(model.transform.rotation,placement.transform.rotation),Is.LessThan(.01));
                        Assert.That(model.transform.localScale,Is.EqualTo(Vector3.one));
                        var renderer=model.GetComponentInChildren<Renderer>();var original=renderer.sharedMaterial;
                        Assert.That(original,Is.Not.EqualTo(asset.visuals.previewMaterial),"Ghost material must not leak into live models");
                        renderer.sharedMaterial=asset.visuals.previewMaterial;
                        model.transform.SetPositionAndRotation(Vector3.one*100,Quaternion.Euler(34,67,123));model.transform.localScale=Vector3.one*.13f;
                        pool.Return(model);
                    }
                }
                var prefab=new GameObject("Effect fixture");var ps=prefab.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var trail=prefab.AddComponent<TrailRenderer>();
                try
                {
                    var go=pool.Rent(prefab,placement.transform);go.GetComponent<ParticleSystem>().Emit(10);go.GetComponent<TrailRenderer>().AddPosition(Vector3.one*5);pool.Return(go);
                    go=pool.Rent(prefab,placement.transform);Assert.That(go.GetComponent<ParticleSystem>().particleCount,Is.Zero);Assert.That(go.GetComponent<TrailRenderer>().positionCount,Is.Zero);pool.Return(go);
                }
                finally{Object.DestroyImmediate(prefab);}
            }
            finally{Object.DestroyImmediate(placement);Object.DestroyImmediate(parent);}
        }

        [Test]
        public void LifecycleSurvivesShortFlightsDuplicatesAndOlderSnapshots()
        {
            HeroMigrationTests.Load();
            var go=new GameObject("Lifecycle test");var p=go.AddComponent<SubWeaponPresentation>();
            try
            {
                var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(SubWeaponType.BurstBomb));
                SubWeaponConfigService.Current.Set(1,asset);
                var state=new SubEntityState{Id=77,Version=1,Owner=42,Hero=1,ConfigRevision=SubWeaponConfigService.Current.Revision(1),Type=asset.type,Team=1,Normal=Vector3.up,
                    Position=new Vector3(5000,5000,5000),Velocity=Vector3.right*60,Born=1,SampledAt=1,Expires=9};
                var spawn=new SubLifecycleEvent{Sequence=1,Kind=SubLifecycleKind.Spawn,State=state};p.Lifecycle(spawn);p.Lifecycle(spawn);
                var end=state;end.Position+=Vector3.right*2;end.SampledAt=1;end.Version=2;
                p.Lifecycle(new SubLifecycleEvent{Sequence=2,Kind=SubLifecycleKind.Remove,State=end});
                p.PresentAt(1.3,1f/30);
                Assert.That(p.EntityIds.Count,Is.Zero);Assert.That(p.ObservedFlights,Does.Contain(SubWeaponType.BurstBomb));
                var lines=go.GetComponentsInChildren<LineRenderer>();Assert.That(lines.Length,Is.EqualTo(1));
                Assert.That(lines[0].positionCount,Is.EqualTo(2));Assert.That(Vector3.Distance(lines[0].GetPosition(0),lines[0].GetPosition(1)),Is.EqualTo(2).Within(.001));
                p.Apply(new[]{state},1);Assert.That(p.EntityIds.Count,Is.Zero,"Old complete state must not resurrect removed entity");
                state.Id=78;state.Version=3;state.Born=state.SampledAt=2;
                p.Lifecycle(new SubLifecycleEvent{Sequence=3,Kind=SubLifecycleKind.Spawn,State=state});
                p.Apply(Array.Empty<SubEntityState>(),2);p.PresentAt(2.15,1f/60);
                Assert.That(p.EntityIds,Does.Contain(78u),"Older absence cannot remove a newer spawn");
                Assert.That(p.MaximumAnchorError,Is.LessThan(.01));
            }
            finally{Object.DestroyImmediate(go);SubWeaponConfigService.Current.Clear();LubanConfigService.Current.Reset();}
        }

        [Test]
        public void SpecializedVisualsAreFilteredAndExcludedFromGameplayHash()
        {
            var editor=Type.GetType("Splatoon.Editor.SubWeaponConfigAssetEditor, Splatoon.Editor");var visible=editor.GetMethod("IsVisible");
            foreach(SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
            {
                var a=ScriptableObject.CreateInstance<SubWeaponConfigAsset>();SubWeaponDefaults.Apply(a,type);
                try
                {
                    Assert.That(visible.Invoke(null,new object[]{"visuals.previewAlpha",type}),Is.True);
                    Assert.That(visible.Invoke(null,new object[]{"typeVisuals.wallFlow",type}),Is.EqualTo(type==SubWeaponType.SplashWall));
                    Assert.That(visible.Invoke(null,new object[]{"typeVisuals.dropletPrefab",type}),Is.EqualTo(type==SubWeaponType.Sprinkler||type==SubWeaponType.Torpedo));
                    var before=a.Snapshot();a.visuals.trailLifetime=.8f;var after=a.Snapshot();
                    Assert.That(after.ContentHash,Is.EqualTo(before.ContentHash));Assert.That(SubWeaponConfigService.Same(before,after),Is.False);
                }
                finally{Object.DestroyImmediate(a);}
            }
        }

        [Test]
        public void FlightCollisionSupportsNonConvexMeshesAndEmbeddedOrigins()
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.transform.position=Vector3.one*8000;
            Object.DestroyImmediate(go.GetComponent<BoxCollider>());var collider=go.AddComponent<MeshCollider>();collider.sharedMesh=go.GetComponent<MeshFilter>().sharedMesh;
            try
            {
                Physics.SyncTransforms();
                Assert.That(SubWeaponService.FlightCollision(new TpsAimSolver(),go.transform.position+Vector3.forward*.45f,Vector3.forward,.15f,999,1,out var hit),Is.True);
                Assert.That(hit.Collider,Is.SameAs(collider));Assert.That(hit.Point.magnitude,Is.GreaterThan(1000));
                Assert.That(hit.Normal.sqrMagnitude,Is.GreaterThan(.9));
            }
            finally{Object.DestroyImmediate(go);}
        }

        [TestCase(SubWeaponType.CurlingBomb)]
        [TestCase(SubWeaponType.SplatBomb)]
        [TestCase(SubWeaponType.FizzyBomb)]
        [TestCase(SubWeaponType.Torpedo)]
        public void GroundMotionContinuesBetweenTenHertzSnapshots(SubWeaponType type)
        {
            HeroMigrationTests.Load();
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.position=Vector3.one*8000;floor.transform.localScale=new Vector3(50,1,50);
            var root=new GameObject("Grounded display extrapolation");var presentation=root.AddComponent<SubWeaponPresentation>();
            try
            {
                var asset=AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(type));SubWeaponConfigService.Current.Set(1,asset);var config=asset.Snapshot();
                var state=new SubEntityState{Id=1,Version=1,Owner=42,Hero=1,ConfigRevision=SubWeaponConfigService.Current.Revision(1),Type=type,Team=1,Normal=Vector3.up,
                    Position=floor.transform.position+Vector3.up*(.5f+config.Flight.radius+.005f),Velocity=Vector3.right*10,Phase=SubEntityPhase.Grounded,Born=1,SampledAt=1,Expires=9};
                Physics.SyncTransforms();var entity=new SubWeaponService.Entity(state,config);var aim=new TpsAimSolver();
                for(int i=1;i<=6;i++)SubWeaponMotion.Advance(entity,aim,1+i/60d,1f/60,out _);
                presentation.Lifecycle(new SubLifecycleEvent{Sequence=1,Kind=SubLifecycleKind.Spawn,State=state});
                presentation.PresentAt(1.1+SubWeaponPresentation.RemoteDelay,1f/60);
                Assert.That(presentation.TryVisual(1,out var visual),Is.True);
                Assert.That(Vector3.Distance(visual.Model.transform.position,entity.State.Position),Is.LessThan(.01),"All six motion steps must remain visible across a snapshot interval");
            }
            finally{Object.DestroyImmediate(root);Object.DestroyImmediate(floor);SubWeaponConfigService.Current.Clear();LubanConfigService.Current.Reset();}
        }
    }
}
#endif
