#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkLookUpgrade
    {
        const string Art="Assets/GameResource/Environment/TrainingGround";
        [MenuItem("喷墨对战/内容/升级粉蓝墨水材质")]
        public static void Run()
        {
            LoadConfig();EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();
            foreach(var surface in arena.GetComponentsInChildren<PaintSurface>())
            {
                var mesh=surface.GetComponent<MeshFilter>().sharedMesh;
                // An authored tag makes re-running this migration idempotent.
                if(!mesh.name.EndsWith("_InkV4",StringComparison.Ordinal))
                {
                    if(surface.Scores)
                    {
                        surface.Resolution=Mathf.Max(32,Mathf.CeilToInt(surface.WalkableSize.x*32/32)*32);
                        surface.ResolutionHeight=Mathf.Max(32,Mathf.CeilToInt(surface.WalkableSize.y*32/32)*32);
                        mesh.RecalculateTangents();
                    }
                    else
                    {
                        InkSurfaceAtlas.Rebuild(mesh,out int width,out int height);surface.Resolution=width;surface.ResolutionHeight=height;
                    }
                    mesh.name+="_InkV4";EditorUtility.SetDirty(mesh);
                    var collider=surface.GetComponent<MeshCollider>();if(collider!=null){collider.sharedMesh=null;collider.sharedMesh=mesh;}
                }
                surface.DisplayShader=Shader.Find("Splatoon/InkDisplay");EditorUtility.SetDirty(surface);
                var material=surface.GetComponent<Renderer>().sharedMaterial;
                material.shader=Shader.Find("Splatoon/InkSurface");
                material.SetFloat("_InkWorldScale",GameplayConfig.Global.PaintWorldUvScale);material.SetFloat("_InkShapeNoiseScale",GameplayConfig.Global.PaintShapeNoiseScale);material.SetFloat("_InkThreshold",GameplayConfig.Global.PaintThreshold);
                material.SetVector("Vector2_e97cb9b7b5564bc9857e7669e2d0b82f",new Vector4(20,20,0,0));
                if(surface.Scores)
                {
                    var reference=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Materials/PaintableWall.mat");
                    material.SetTexture("Texture2D_41271c3c5f484ca2a435c65087a81705",reference.GetTexture("Texture2D_41271c3c5f484ca2a435c65087a81705"));
                }
                material.SetColor("Color_863351f5ceea4c998ef51baab6dd758b",surface.Scores?new Color(.85f,.87f,.86f):new Color(1.45f,1.45f,1.45f));
                EditorUtility.SetDirty(material);
            }
            foreach(var t in arena.GetComponentsInChildren<Transform>())if(t.name.Contains("Orange"))t.name=t.name.Replace("Orange","Pink");
            foreach(string root in new[]{Art+"/Materials","Assets/GameResource/Gameplay/Prototype/Materials"})
            {
                string orange=root+"/Orange.mat",pink=root+"/Pink.mat";
                if(AssetDatabase.LoadAssetAtPath<Material>(orange)!=null){var error=AssetDatabase.MoveAsset(orange,pink);if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(error);}
                SetTeamMaterial(pink,PrototypeArena.Pink);SetTeamMaterial(root+"/Blue.mat",PrototypeArena.Blue);
            }
            var noise=AssetImporter.GetAtPath("Assets/GameResource/Environment/Ink/Textures/noise_texture_0002.png") as TextureImporter;
            if(noise!=null&&!noise.mipmapEnabled){noise.mipmapEnabled=true;noise.filterMode=FilterMode.Trilinear;noise.anisoLevel=4;noise.SaveAndReimport();}
            ConfigureLight(arena);
            InkEdgeUpgrade.Apply(arena);
            TrainingGroundBuilder.Bake(arena);EditorSceneManager.MarkSceneDirty(arena.gameObject.scene);EditorSceneManager.SaveScene(arena.gameObject.scene);AssetDatabase.SaveAssets();
            Debug.Log("[INK-LOOK] Upgrade complete.");
        }
        public static void UpgradeAndValidate() { Run(); InkLookValidation.Run(); }
        static void SetTeamMaterial(string path,Color color)
        {var mat=AssetDatabase.LoadAssetAtPath<Material>(path);if(mat==null)return;mat.name=Path.GetFileNameWithoutExtension(path);mat.SetColor("_BaseColor",color);EditorUtility.SetDirty(mat);}
        public static void LoadConfig()
        {
            var tables=new cfg.Tables(name=>SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/"+name+".json")));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,tables);GameplayConfig.Validate();
        }
        public static void ConfigureLight(PrototypeArena arena)
        {
            var sun=arena.GetComponentsInChildren<Light>().First(l=>l.type==LightType.Directional);sun.intensity=2;sun.color=new Color(1,.95686275f,.8392157f);sun.shadows=LightShadows.Soft;
            RenderSettings.sun=sun;RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.ambientIntensity=1;RenderSettings.reflectionIntensity=1;RenderSettings.fog=false;
            RenderSettings.ambientSkyColor=new Color(.55f,.62f,.64f);RenderSettings.ambientEquatorColor=new Color(.38f,.4f,.42f);RenderSettings.ambientGroundColor=new Color(.18f,.2f,.22f);
            RenderSettings.skybox=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Sky/Sky_8.mat");
            var volume=arena.GetComponentInChildren<Volume>();if(volume==null){var go=new GameObject("Ink lighting volume");go.transform.SetParent(arena.transform);volume=go.AddComponent<Volume>();}
            const string path=Art+"/InkVolume.asset";var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if(profile==null){profile=ScriptableObject.CreateInstance<VolumeProfile>();AssetDatabase.CreateAsset(profile,path);}
            if(!profile.TryGet<Bloom>(out var bloom)){bloom=profile.Add<Bloom>();AssetDatabase.AddObjectToAsset(bloom,profile);}
            bloom.threshold.Override(1);bloom.intensity.Override(.08f);bloom.scatter.Override(.45f);
            if(!profile.TryGet<Tonemapping>(out var tone)){tone=profile.Add<Tonemapping>();AssetDatabase.AddObjectToAsset(tone,profile);}tone.mode.Override(TonemappingMode.Neutral);
            volume.isGlobal=true;volume.priority=10;volume.sharedProfile=profile;EditorUtility.SetDirty(profile);EditorUtility.SetDirty(volume);
            foreach(var camera in arena.GetComponentsInChildren<Camera>()){camera.clearFlags=CameraClearFlags.Skybox;camera.allowHDR=true;camera.GetUniversalAdditionalCameraData().renderPostProcessing=true;EditorUtility.SetDirty(camera);}
            var probe=arena.GetComponentInChildren<ReflectionProbe>();if(probe==null){var go=new GameObject("Ink environment reflection");go.transform.SetParent(arena.transform);probe=go.AddComponent<ReflectionProbe>();}
            probe.resolution=128;probe.size=new Vector3(80,30,100);probe.center=new Vector3(0,5,0);probe.cullingMask=0;probe.clearFlags=ReflectionProbeClearFlags.Skybox;
            const string reflectionPath=Art+"/InkSkyReflection.exr";
            var reflection=AssetDatabase.LoadAssetAtPath<Cubemap>(reflectionPath);
            if(reflection==null)
            {
                probe.mode=ReflectionProbeMode.Baked;
                if(!Lightmapping.BakeReflectionProbe(probe,reflectionPath))throw new InvalidOperationException("天空反射烘焙失败");
                AssetDatabase.ImportAsset(reflectionPath,ImportAssetOptions.ForceSynchronousImport);reflection=AssetDatabase.LoadAssetAtPath<Cubemap>(reflectionPath);
            }
            if(reflection==null)throw new InvalidOperationException("天空反射 Cubemap 缺失");
            // PC quality disables realtime reflection probes. Persist the convolved sky so
            // player builds and additive scene loads have the same reflections as the Editor.
            probe.mode=ReflectionProbeMode.Custom;probe.customBakedTexture=reflection;EditorUtility.SetDirty(probe);
            RenderSettings.defaultReflectionMode=DefaultReflectionMode.Custom;RenderSettings.customReflectionTexture=reflection;
        }
    }
}
#endif
