using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Splatoon.Combat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class PaperBodyBuilder
    {
        public const string Root = "Assets/GameResource/Characters/Shared/Paper";
        public const string Output = "Reports/PaperBody";
        public static readonly string[] Heroes = { "RifleGirl", "DualPistolGirl", "ShotgunGirl", "PistolGirl", "RocketLauncherGirl", "MachineGunGirl", "BubbleGirl" };
        public static void ConfigureHero(string hero, Vector2? size = null) => Configure(hero, Shader.Find("Splatoon/PaperBody"), size);
        static PaperBodyBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed) return;
            const string request = "Temp/PaperBody/install";
            if (!File.Exists(request)) return;
            File.Delete(request); Directory.CreateDirectory(Output);
            try { Install(); File.WriteAllText(Output + "/assets-result.txt", "PASS live capture " + DateTime.UtcNow.ToString("O")); }
            catch (Exception ex) { File.WriteAllText(Output + "/assets-result.txt", ex.ToString()); Debug.LogException(ex); }
        }
        [MenuItem("喷墨对战/内容/配置实时纸片潜墨")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Cannot configure during Play Mode");
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Output); AssetDatabase.Refresh();
            var shader=Shader.Find("Splatoon/PaperBody");
            if (shader == null) throw new InvalidOperationException("Paper shader not imported");
            foreach (string hero in Heroes) Configure(hero,shader);
            var shared=PrefabUtility.LoadPrefabContents(SwimBodyBuilder.PrefabPath);
            try
            {
                var body=shared.GetComponent<SwimBody>();
                body.Bind(AssetDatabase.LoadAssetAtPath<PaperBodyProfile>(Root+"/RifleGirl/Paper.asset"));
                body.HitVolume.sharedMesh=null;
                body.HitVolume.transform.localPosition=body.BodyRenderer.transform.localPosition=Vector3.zero;
                body.HitVolume.transform.localRotation=body.BodyRenderer.transform.localRotation=Quaternion.identity;
                body.BodyRenderer.shadowCastingMode=ShadowCastingMode.Off;
                body.HitVolume.enabled=body.BodyRenderer.enabled=false;
                PrefabUtility.SaveAsPrefabAsset(shared,SwimBodyBuilder.PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(shared); }
            AssetDatabase.SaveAssets();
            // Only obsolete files produced by the superseded paper implementation.
            foreach (string hero in Heroes)
                foreach (string file in new[]{"Paper.png","PaperPose.anim","PaperHit.asset"})
                    AssetDatabase.DeleteAsset(Root+"/"+hero+"/"+file);
        }
        static void Configure(string hero,Shader shader, Vector2? size = null)
        {
            string folder=Root+"/"+hero; Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            string source=$"Assets/GameResource/Characters/{hero}/Prefabs/{hero}Visual.prefab";
            var instance=PrefabUtility.LoadPrefabContents(source);
            GameObject capture;
            float referenceSpeed;
            try
            {
                var view=instance.GetComponent<InkCharacterView>(); var animator=view.Animator;
                foreach (var ps in instance.GetComponentsInChildren<ParticleSystem>(true)) Object.DestroyImmediate(ps.gameObject);
                if (view.TeamMarker != null) Object.DestroyImmediate(view.TeamMarker.gameObject);
                if (view.Weapon != null) Object.DestroyImmediate(view.Weapon.gameObject);
                if (view.LeftWeapon != null) Object.DestroyImmediate(view.LeftWeapon.gameObject);
                var weaponPrefab=AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/GameResource/Weapons/"+hero+"/Prefabs"})
                    .Select(guid=>AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)))
                    .Single(prefab=>prefab.GetComponent<HeroWeaponBindings>() != null);
                var weapon=(GameObject)PrefabUtility.InstantiatePrefab(weaponPrefab,view.WeaponSocket);
                weapon.transform.localPosition=Vector3.zero; weapon.transform.localRotation=Quaternion.identity;
                var bindings=weapon.GetComponent<HeroWeaponBindings>();
                if (bindings.LeftPart != null) bindings.LeftPart.SetParent(view.LeftWeaponSocket,false);
                var nozzle=bindings.Nozzle; var grip=bindings.SupportLeftHand ? bindings.LeftGrip : null;
                var aim=view.AimReference; float spineWeight=view.Profile.SpineAimWeight;
                referenceSpeed=view.Profile.AnimationReferenceSpeed;
                foreach (var c in instance.GetComponentsInChildren<MonoBehaviour>(true)) if (c is not MachineGunFaceShadow) Object.DestroyImmediate(c);
                foreach (var c in instance.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                foreach (var c in instance.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(c);
                foreach (var t in instance.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=PaperCapture.Layer;
                // Keep the authored bind transforms. Sampling and saving a pose
                // would change default bone values when the runtime Animator binds.
                animator.applyRootMotion=false; animator.fireEvents=false; animator.enabled=false;
                var rig=instance.AddComponent<PaperCaptureRig>();
                rig.Animator=animator; rig.Nozzle=nozzle; rig.LeftGrip=grip; rig.AimReference=aim; rig.SpineAimWeight=spineWeight;
                var readable=new Dictionary<Mesh,Mesh>();
                foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh=filter.sharedMesh;
                    if (mesh == null || mesh.isReadable || !filter.GetComponent<MeshRenderer>().enabled) continue;
                    if (!readable.TryGetValue(mesh,out var copy))
                    {
                        // Static weapon FBXs discard their CPU data in builds.
                        // Keep a capture-local copy instead of changing source import settings.
                        copy=Save(ReadableMesh(mesh),folder+"/PaperWeaponMesh-"+readable.Count+".asset");
                        readable.Add(mesh,copy);
                    }
                    filter.sharedMesh=copy;
                }
                capture=PrefabUtility.SaveAsPrefabAsset(instance,folder+"/PaperCapture.prefab");
            }
            finally { PrefabUtility.UnloadPrefabContents(instance); }
            var profile=AssetDatabase.LoadAssetAtPath<PaperBodyProfile>(folder+"/Paper.asset");
            if (profile == null) { profile=ScriptableObject.CreateInstance<PaperBodyProfile>(); AssetDatabase.CreateAsset(profile,folder+"/Paper.asset"); }
            profile.CapturePrefab=capture; profile.CaptureCenter=Vector3.up; profile.CaptureHeight=3;
            profile.AnimationReferenceSpeed=referenceSpeed;
            profile.TextureWidth=512; profile.TextureHeight=1024; profile.Size=size ?? new Vector2(.9f,1.8f);
            profile.Thickness=.04f; profile.SurfaceOffset=.03f;
            // Measure the actual runtime sampler, including its quantization and
            // retargeting, so the camera envelope covers every supported direction.
            using (var sampler=new PaperCapture(profile))
            {
                Bounds envelope=default; bool first=true;
                foreach (var move in new[]{Vector2.zero,Vector2.up,Vector2.down,Vector2.left,Vector2.right,
                    new Vector2(.75f,.75f),new Vector2(.75f,-.75f),new Vector2(-.75f,.75f),new Vector2(-.75f,-.75f)})
                for (int frame=0;frame<=90;frame++)
                {
                    sampler.Sample(new Splatoon.Prototype.PlayerSnapshot { PaperMove=move,PaperAnimationTime=frame/30f });
                    if (first) { envelope=sampler.PosedBounds; first=false; } else envelope.Encapsulate(sampler.PosedBounds);
                }
                profile.CaptureCenter=envelope.center;
                profile.CaptureHeight=Mathf.Max(envelope.size.y,envelope.size.x*2)*1.06f;
            }
            profile.DisplayMesh=Save(Quad(profile.Size),folder+"/PaperDisplay.asset");
            profile.Material=Save(new Material(shader) { name=hero+"LivePaper" },folder+"/Paper.mat");
            using (var hash=SHA256.Create()) profile.ContentHash=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                AssetDatabase.GetAssetDependencyHash(folder+"/PaperCapture.prefab").ToString()))).Replace("-","").ToLowerInvariant();
            EditorUtility.SetDirty(profile);
            var presentation=AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>($"Assets/GameResource/Characters/{hero}/{hero}Presentation.asset");
            presentation.Paper=profile; profile.CameraOffset=presentation.CameraPivot; EditorUtility.SetDirty(profile); EditorUtility.SetDirty(presentation);
        }
        static T Save<T>(T asset,string path) where T:Object
        { var old=AssetDatabase.LoadAssetAtPath<T>(path); if(old==null) { AssetDatabase.CreateAsset(asset,path); return asset; } EditorUtility.CopySerialized(asset,old); Object.DestroyImmediate(asset); EditorUtility.SetDirty(old); return old; }
        static Mesh ReadableMesh(Mesh source)
        {
            var mesh=new Mesh { name=source.name+" paper capture", indexFormat=source.indexFormat,
                vertices=source.vertices, normals=source.normals, tangents=source.tangents, colors32=source.colors32 };
            var uv=new List<Vector4>();
            for (int channel=0;channel<8;channel++) { source.GetUVs(channel,uv); if (uv.Count>0) mesh.SetUVs(channel,uv); }
            mesh.subMeshCount=source.subMeshCount;
            for (int sub=0;sub<source.subMeshCount;sub++) mesh.SetIndices(source.GetIndices(sub),source.GetTopology(sub),sub);
            mesh.bounds=source.bounds; return mesh;
        }
        static Mesh Quad(Vector2 size)
        {
            var mesh=new Mesh { name="Paper display" };
            mesh.vertices=new[]{new Vector3(-size.x/2,-size.y/2,0),new Vector3(size.x/2,-size.y/2,0),new Vector3(size.x/2,size.y/2,0),new Vector3(-size.x/2,size.y/2,0)};
            mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up}; mesh.triangles=new[]{0,1,2,0,2,3}; mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
    }
}
