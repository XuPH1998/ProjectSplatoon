#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Netcode;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class PrototypeBuilder
    {
        private const string Root = "Assets/GameResource/Gameplay/Prototype";
        [MenuItem("喷墨对战/原型/搭建灰盒场景")]
        public static void SetupGraybox()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Prefabs");
            AssetDatabase.Refresh();
            var stone = Material("Stone", "Universal Render Pipeline/Lit", new Color(.25f,.30f,.33f));
            var pale = Material("Pale", "Universal Render Pipeline/Lit", new Color(.68f,.72f,.72f));
            var dark = Material("Weapon", "Universal Render Pipeline/Lit", new Color(.045f,.065f,.085f));
            var orange = Material("Orange", "Universal Render Pipeline/Lit", PrototypeArena.Orange);
            var blue = Material("Blue", "Universal Render Pipeline/Lit", PrototypeArena.Blue);
            var paint = Material("Paint", "Splatoon/PaintVertex", Color.white);
            var tracer = Material("Tracer", "Universal Render Pipeline/Particles/Unlit", Color.white);
            CreatePlayer(pale, dark, tracer);
            var match = new GameObject("PrototypeMatch",typeof(NetworkObject),typeof(PrototypeMatch));
            PrefabUtility.SaveAsPrefabAsset(match,Root+"/Prefabs/PrototypeMatch.prefab"); UnityEngine.Object.DestroyImmediate(match);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root = new GameObject("灰盒场地"); var arena = root.AddComponent<PrototypeArena>(); arena.PaintMaterial = paint;
            var blocked = new List<BoxCollider>();
            Cube("地面",new Vector3(0,-.3f,0),new Vector3(32,.6f,32),stone,root.transform);
            blocked.Add(Cube("西侧边界",new Vector3(-16.4f,1.5f,0),new Vector3(.8f,3,33),stone,root.transform));
            blocked.Add(Cube("东侧边界",new Vector3(16.4f,1.5f,0),new Vector3(.8f,3,33),stone,root.transform));
            blocked.Add(Cube("南侧边界",new Vector3(0,1.5f,-16.4f),new Vector3(32,3,.8f),orange,root.transform));
            blocked.Add(Cube("北侧边界",new Vector3(0,1.5f,16.4f),new Vector3(32,3,.8f),blue,root.transform));
            // 对称掩体保留中央射击通道和两侧绕行路线。
            foreach (int sign in new[]{-1,1})
            {
                blocked.Add(Cube("中央掩体 "+sign,new Vector3(sign*4,1.25f,0),new Vector3(3,2.5f,5),pale,root.transform));
                blocked.Add(Cube("侧路掩体 "+sign,new Vector3(sign*11,1,0),new Vector3(2,2,7),stone,root.transform));
                foreach (int side in new[]{-1,1})
                    blocked.Add(Cube("矮掩体 "+sign+"_"+side,new Vector3(side*7,.6f,sign*8),new Vector3(3,1.2f,2),pale,root.transform));
                var mat=sign==-1?orange:blue;
                Cube("出生标记 "+sign,new Vector3(0,.006f,sign*14),new Vector3(10,.01f,.2f),mat,root.transform);
            }
            arena.Unpaintable = blocked.ToArray();
            var cameraGO = new GameObject("主相机",typeof(Camera),typeof(AudioListener)); cameraGO.tag="MainCamera";
            var camera = cameraGO.GetComponent<Camera>(); camera.fieldOfView=65;camera.nearClipPlane=.05f;camera.farClipPlane=150;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.15f,.21f,.27f);
            cameraGO.AddComponent<UniversalAdditionalCameraData>();cameraGO.transform.SetPositionAndRotation(new Vector3(0,14,-18),Quaternion.Euler(37,0,0));
            var sunGO=new GameObject("阳光",typeof(Light));var sun=sunGO.GetComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.6f;sun.shadows=LightShadows.Soft;
            sunGO.transform.rotation=Quaternion.Euler(52,-32,0);sunGO.AddComponent<UniversalAdditionalLightData>();
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.65f,.7f,.77f);RenderSettings.sun=sun;
            EditorSceneManager.SaveScene(scene,Root+"/PrototypeArena.unity");
            ConfigureAddressables();
            PlayerSettings.productName="喷墨对战";
            PlayerSettings.runInBackground=true;PlayerSettings.defaultScreenWidth=1280;PlayerSettings.defaultScreenHeight=720;
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.resizableWindow=true;
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/Scenes/Main/Boot.unity",true)};
            AssetDatabase.SaveAssets(); EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            Debug.Log("[PrototypeBuilder] 灰盒场景和本地 Addressables 已配置。");
        }
        private static Material Material(string name,string shader,Color color)
        {
            string path=Root+"/Materials/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            var found=Shader.Find(shader);if(found==null)throw new InvalidOperationException("缺少着色器："+shader);
            if(material==null){material=new Material(found);AssetDatabase.CreateAsset(material,path);}else material.shader=found;
            if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
            if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",.18f);
            EditorUtility.SetDirty(material);return material;
        }
        private static BoxCollider Cube(string name,Vector3 position,Vector3 scale,Material material,Transform parent)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent);
            go.transform.position=position;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=material;
            return go.GetComponent<BoxCollider>();
        }
        private static void CreatePlayer(Material bodyMat,Material gunMat,Material tracer)
        {
            var root=new GameObject("PrototypePlayer");root.layer=8;
            root.AddComponent<NetworkObject>();var cc=root.AddComponent<CharacterController>();cc.height=1.8f;cc.radius=.35f;cc.center=Vector3.up*.9f;cc.stepOffset=.3f;cc.skinWidth=.03f;
            var player=root.AddComponent<PrototypePlayer>();
            var visual=new GameObject("角色外观");visual.transform.SetParent(root.transform,false);player.Visual=visual.transform;
            var body=GameObject.CreatePrimitive(PrimitiveType.Capsule);body.name="胶囊角色";body.transform.SetParent(visual.transform,false);
            body.transform.localPosition=Vector3.up*.9f;body.transform.localScale=new Vector3(.7f,.9f,.7f);
            UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());body.GetComponent<Renderer>().sharedMaterial=bodyMat;player.Body=body.GetComponent<Renderer>();
            var gun=Cube("墨水枪",new Vector3(.4f,1.25f,.4f),new Vector3(.22f,.24f,.8f),gunMat,visual.transform);
            UnityEngine.Object.DestroyImmediate(gun);player.TracerMaterial=tracer;
            PrefabUtility.SaveAsPrefabAsset(root,Root+"/Prefabs/PrototypePlayer.prefab");UnityEngine.Object.DestroyImmediate(root);
        }
        public static void ConfigureAddressables()
        {
            var settings=AddressableAssetSettingsDefaultObject.GetSettings(true);
            var group=settings.FindGroup("Splatoon Local")??settings.CreateGroup("Splatoon Local",false,false,true,null,typeof(BundledAssetGroupSchema),typeof(ContentUpdateGroupSchema));
            var schema=group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings,AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings,AddressableAssetSettings.kLocalLoadPath);
            schema.BundleMode=BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            // 逐项注册 JSON，避免将文件夹地址误当作可加载的 TextAsset 列表。
            var rootEntry=settings.FindAssetEntry(AssetDatabase.AssetPathToGUID("Assets/GameResource"));if(rootEntry!=null)settings.RemoveAssetEntry(rootEntry.guid);
            foreach(string path in Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban","*.json"))
            {
                var entry=settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path),group);entry.address=Path.GetFileNameWithoutExtension(path);entry.SetLabel("Luban",true,true);
            }
            AddAddress(settings,group,Root+"/PrototypeArena.unity",PrototypeApp.ArenaAddress);
            AddAddress(settings,group,Root+"/Prefabs/PrototypePlayer.prefab",PrototypeApp.PlayerAddress);
            AddAddress(settings,group,Root+"/Prefabs/PrototypeMatch.prefab",PrototypeApp.MatchAddress);
            settings.BuildAddressablesWithPlayerBuild=AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            EditorUtility.SetDirty(settings);AssetDatabase.SaveAssets();
        }
        private static void AddAddress(AddressableAssetSettings settings,AddressableAssetGroup group,string path,string address)
        {settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path),group).address=address;}
        [MenuItem("喷墨对战/原型/构建 Windows 版本")]
        public static void BuildWindows()
        {
            SetupGraybox();
            AddressableAssetSettings.BuildPlayerContent(out var content);
            if(!string.IsNullOrEmpty(content.Error))throw new BuildFailedException(content.Error);
            Directory.CreateDirectory("Builds/Windows");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{"Assets/Scenes/Main/Boot.unity"},locationPathName="Builds/Windows/InkLan.exe",target=BuildTarget.StandaloneWindows64,
                options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new BuildFailedException("Windows 构建失败："+report.summary.result);
            Debug.Log("[PrototypeBuilder] Windows 构建成功：Builds/Windows/InkLan.exe");
        }
    }
}
#endif
