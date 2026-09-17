// Run only in an isolated COPY of ProjectSplatoon; never install into the live project.
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class WaterShotgunValidation
    {
        static readonly string SourceProject = Environment.GetEnvironmentVariable("WATER_SHOTGUN_SOURCE_PROJECT") ?? "D:/XPHUNITY/ProjectSplatoon";
        static readonly string Output = SourceProject + "/ArtSource/Weapons/WaterShotgun";
        const string WeaponPath = "Assets/GameResource/Weapons/ShotgunGirl/Prefabs/Shotgun.prefab";
        const string CharacterPath = "Assets/GameResource/Characters/ShotgunGirl/Prefabs/ShotgunGirlVisual.prefab";
        const string ModelPath = "Assets/WaterShotgunValidation/WaterShotgun.fbx";
        [Serializable] public class Marker { public string name; public Vector3 position; public Quaternion rotation; }
        [Serializable] public class ReferenceData
        {
            public string sourcePrefab = WeaponPath;
            public string unityVersion = Application.unityVersion;
            public string meshName;
            public int triangles;
            public Vector3 origin, forward, right, up, min, max;
            public Vector3[] vertices, designVertices;
            public int[] indices;
            public Marker[] markers;
        }
        static void Guard()
        {
            if (Path.GetFullPath(".").TrimEnd('\\','/').Equals(Path.GetFullPath(SourceProject).TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Validation must run in an isolated project copy.");
        }
        public static void ExportReference()
        {
            try
            {
                Guard();
                var gun = PrefabUtility.LoadPrefabContents(WeaponPath);
                try
                {
                    var binding = gun.GetComponent<HeroWeaponBindings>();
                    var filter = gun.GetComponentsInChildren<MeshFilter>().Single();
                    var d = new ReferenceData { meshName=filter.sharedMesh.name, triangles=filter.sharedMesh.triangles.Length/3,
                        origin=gun.transform.InverseTransformPoint(binding.Nozzle.position),
                        forward=gun.transform.InverseTransformDirection(binding.Nozzle.forward),
                        right=gun.transform.InverseTransformDirection(binding.Nozzle.right),
                        up=gun.transform.InverseTransformDirection(binding.Nozzle.up),
                        indices=filter.sharedMesh.triangles };
                    d.vertices=filter.sharedMesh.vertices.Select(p=>gun.transform.InverseTransformPoint(filter.transform.TransformPoint(p))).ToArray();
                    d.designVertices=d.vertices.Select(p=>new Vector3(Vector3.Dot(p-d.origin,d.forward), -Vector3.Dot(p-d.origin,d.right), Vector3.Dot(p-d.origin,d.up))).ToArray();
                    d.min=new Vector3(d.designVertices.Min(v=>v.x),d.designVertices.Min(v=>v.y),d.designVertices.Min(v=>v.z));
                    d.max=new Vector3(d.designVertices.Max(v=>v.x),d.designVertices.Max(v=>v.y),d.designVertices.Max(v=>v.z));
                    d.markers=new[]{new Marker{name="Mount",position=Vector3.zero,rotation=Quaternion.identity},
                        new Marker{name="LeftGrip",position=binding.LeftGrip.localPosition,rotation=binding.LeftGrip.localRotation},
                        new Marker{name="Muzzle",position=binding.Nozzle.localPosition,rotation=binding.Nozzle.localRotation}};
                    Directory.CreateDirectory(Output+"/Reference");
                    File.WriteAllText(Output+"/Reference/shotgun-reference.json",JsonUtility.ToJson(d,true));
                    Debug.Log("WATER_REFERENCE "+d.meshName+" triangles="+d.triangles+" min="+d.min.ToString("F6")+" max="+d.max.ToString("F6"));
                }
                finally { PrefabUtility.UnloadPrefabContents(gun); }
                var probe=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/WaterShotgunValidation/coordinate-probe.fbx");
                if(probe!=null)
                {
                    var inst=UnityEngine.Object.Instantiate(probe);
                    var rows=inst.GetComponentsInChildren<Transform>().Select(t=>t.name+" "+inst.transform.InverseTransformPoint(t.position).ToString("F6")+" q="+t.localRotation.ToString("F6")).ToArray();
                    File.WriteAllLines(Output+"/Reference/unity-coordinate-probe.txt",rows);
                    UnityEngine.Object.DestroyImmediate(inst);
                }
                EditorApplication.Exit(0);
            }
            catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
        }

        [Serializable] public class MarkerCheck { public string name; public Vector3 actual; public float positionErrorMeters,rotationErrorDegrees; }
        [Serializable] public class PoseCheck { public string pose; public float elevationDegrees, enginePitch, baselineLeftHandErrorMeters, newLeftHandErrorMeters, handDeltaMeters; }
        [Serializable] public class ValidationData
        {
            public string unityVersion=Application.unityVersion, shader, graphicsDevice;
            public int meshes,materialSlots,triangles,vertices,textureWidth,textureHeight,degenerateTriangles;
            public Vector3 min,max,relativeSizeError;
            public MarkerCheck[] markers;
            public List<PoseCheck> poses=new();
            public string cameraPreview="ShotgunGirl profile CameraPivot/CameraOffset through PrototypePlayer.CameraPosition, neutral camera rotation, FOV 60, square center framing.";
            public string boundary="Isolated Editor GPU rendering of evaluated original Animator poses. No live-project replacement, Play Mode, network or target-device performance claim.";
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static Vector3 ToDesign(Vector3 p,ReferenceData r)
        {p-=r.origin;return new Vector3(Vector3.Dot(p,r.forward),-Vector3.Dot(p,r.right),Vector3.Dot(p,r.up));}
        static Transform Find(GameObject obj,string name)=>obj.GetComponentsInChildren<Transform>(true).Single(t=>t.name==name);

        public static void ValidateAndRender()
        {
            var result=new ValidationData();
            try
            {
                Guard();
                Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"GPU required");
                result.graphicsDevice=SystemInfo.graphicsDeviceName;
                var reference=JsonUtility.FromJson<ReferenceData>(File.ReadAllText(Output+"/Reference/shotgun-reference.json"));
                var importer=(ModelImporter)AssetImporter.GetAtPath(ModelPath);
                importer.importAnimation=false;importer.animationType=ModelImporterAnimationType.None;
                importer.materialImportMode=ModelImporterMaterialImportMode.None;
                importer.globalScale=1;importer.useFileScale=true;
                importer.importNormals=ModelImporterNormals.Import;importer.isReadable=true;
                importer.SaveAndReimport();
                string texturePath="Assets/WaterShotgunValidation/WaterShotgun_Albedo.png";
                var ti=(TextureImporter)AssetImporter.GetAtPath(texturePath);ti.sRGBTexture=true;ti.maxTextureSize=1024;
                ti.textureCompression=TextureImporterCompression.Uncompressed;ti.mipmapEnabled=true;ti.SaveAndReimport();
                var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                result.textureWidth=texture.width;result.textureHeight=texture.height;
                var sourceMat=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Characters/ShotgunGirl/Materials/Weapon_Shotgun_Metal_9b8a7ab4.mat");
                var mat=new Material(sourceMat){name="WaterShotgun_Toon"};result.shader=mat.shader.name;
                foreach(string key in new[]{"_MainTex","_BaseMap","_1st_ShadeMap","_2nd_ShadeMap"})if(mat.HasProperty(key))mat.SetTexture(key,texture);
                foreach(string key in new[]{"_BumpMap","_NormalMap","_HighColor_Tex","_MatCap_Sampler","_Set_HighColorMask","_Set_MatcapMask","_OutlineTex"})if(mat.HasProperty(key))mat.SetTexture(key,null);
                void Float(string key,float v){if(mat.HasProperty(key))mat.SetFloat(key,v);}
                void ColorValue(string key,Color v){if(mat.HasProperty(key))mat.SetColor(key,v);}
                ColorValue("_BaseColor",Color.white);ColorValue("_Color",Color.white);
                ColorValue("_1st_ShadeColor",new Color(.62f,.72f,.76f));ColorValue("_2nd_ShadeColor",new Color(.40f,.51f,.58f));
                ColorValue("_Outline_Color",new Color(.025f,.07f,.09f));
                ColorValue("_HighColor",Color.black);
                Float("_BaseColor_Step",.55f);Float("_BaseShade_Feather",.025f);Float("_ShadeColor_Step",.12f);Float("_1st_ShadeColor_Step",.55f);
                Float("_Is_Filter_LightColor",1);Float("_Is_NormalMapToBase",0);Float("_Is_NormalMapToHighColor",0);Float("_Is_NormalMapToRimLight",0);
                Float("_HighColor_Power",0);Float("_RimLight",0);Float("_MatCap",0);Float("_Outline_Width",.8f);
                Float("_Tweak_HighColorMaskLevel",-1);
                string matPath="Assets/WaterShotgunValidation/WaterShotgun_Toon.mat";
                var oldMat=AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if(oldMat!=null){EditorUtility.CopySerialized(mat,oldMat);UnityEngine.Object.DestroyImmediate(mat);mat=oldMat;}
                else AssetDatabase.CreateAsset(mat,matPath);
                AssetDatabase.SaveAssets();
                importer.materialImportMode=ModelImporterMaterialImportMode.ImportStandard;
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material),"WaterShotgun"),mat);
                importer.isReadable=false;importer.SaveAndReimport();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                var model=UnityEngine.Object.Instantiate(loaded);model.name="ImportedWaterShotgun";
                var filters=model.GetComponentsInChildren<MeshFilter>(true);
                result.meshes=filters.Length;result.materialSlots=model.GetComponentsInChildren<MeshRenderer>().Sum(r=>r.sharedMaterials.Length);
                Require(model.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>m==mat)),"FBX must resolve the supplied Toon material automatically");
                result.triangles=filters.Sum(f=>f.sharedMesh.triangles.Length/3);result.vertices=filters.Sum(f=>f.sharedMesh.vertexCount);
                foreach(var f in filters)
                {
                    var mesh=f.sharedMesh;var v=mesh.vertices;var t=mesh.triangles;
                    for(int i=0;i<t.Length;i+=3)if(Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]).sqrMagnitude<1e-18f)result.degenerateTriangles++;
                }
                var points=filters.SelectMany(f=>f.sharedMesh.vertices.Select(v=>ToDesign(f.transform.TransformPoint(v),reference))).ToArray();
                result.min=new Vector3(points.Min(p=>p.x),points.Min(p=>p.y),points.Min(p=>p.z));
                result.max=new Vector3(points.Max(p=>p.x),points.Max(p=>p.y),points.Max(p=>p.z));
                var size=result.max-result.min;var expected=reference.max-reference.min;
                result.relativeSizeError=new Vector3(Mathf.Abs(size.x/expected.x-1),Mathf.Abs(size.y/expected.y-1),Mathf.Abs(size.z/expected.z-1));
                result.markers=reference.markers.Select(r=>{var m=Find(model,r.name);return new MarkerCheck{name=r.name,actual=m.position,positionErrorMeters=Vector3.Distance(m.position,r.position),rotationErrorDegrees=Quaternion.Angle(m.rotation,r.rotation)};}).ToArray();
                File.WriteAllText(Output+"/Validation/unity-validation.json",JsonUtility.ToJson(result,true));
                Require(result.meshes==1&&result.materialSlots==1,"One mesh / material slot required");
                Require(result.triangles<=4000&&result.degenerateTriangles==0,"Mesh triangle / degeneration limits");
                Require(Mathf.Max(result.relativeSizeError.x,result.relativeSizeError.y,result.relativeSizeError.z)<=.02f,"Dimensions exceeded 2%: "+result.relativeSizeError);
                foreach(var m in result.markers)Require(m.positionErrorMeters<=.001f&&m.rotationErrorDegrees<=.5f,"Anchor failed: "+m.name+" "+m.positionErrorMeters+"m / "+m.rotationErrorDegrees+"deg");
                Require(texture.width==1024&&texture.height==1024,"1024px texture required");
                model.GetComponentInChildren<MeshRenderer>().sharedMaterial=mat;
                // The new template exists only in the validation scene, and is not saved over any weapon prefab.
                var source=AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPath);
                var template=UnityEngine.Object.Instantiate(source);template.name="Watergun validation template";
                foreach(var f in template.GetComponentsInChildren<MeshFilter>())UnityEngine.Object.DestroyImmediate(f.gameObject);
                model.transform.SetParent(template.transform,false);
                var bindings=template.GetComponent<HeroWeaponBindings>();
                UnityEngine.Object.DestroyImmediate(bindings.LeftGrip.gameObject);UnityEngine.Object.DestroyImmediate(bindings.Nozzle.gameObject);
                bindings.LeftGrip=Find(model,"LeftGrip");bindings.Nozzle=Find(model,"Muzzle");
                template.SetActive(false);
                typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                    new cfg.Tables(n=>SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/"+n+".json"))));
                var sun=new GameObject("Preview key").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.05f;sun.transform.rotation=Quaternion.Euler(40,-30,0);sun.shadows=LightShadows.Soft;
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.62f,.68f,.72f);
                var camera=new GameObject("Validation camera").AddComponent<Camera>();camera.enabled=false;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.70f,.77f,.79f);camera.nearClipPlane=.01f;camera.farClipPlane=100;camera.fieldOfView=35;camera.aspect=1;
                camera.GetUniversalAdditionalCameraData().renderPostProcessing=false;
                Directory.CreateDirectory(Output+"/Previews/Unity");
                var character=AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
                foreach(var pose in new[]{("level",0f,false),("down45",-45f,false),("up35",35f,false),("shoot",0f,true)})
                {
                    var check=new PoseCheck{pose=pose.Item1,elevationDegrees=pose.Item2,enginePitch=-pose.Item2};
                    Vector3 baselineHand=Vector3.zero;
                    foreach(bool water in new[]{false,true})
                    {
                        var root=new GameObject("Pose "+pose.Item1);root.layer=8;
                        using(var binder=new HeroViewBinder(root.transform))
                        {
                            binder.Apply(new HeroContent(GameplayConfig.GetHero(3),character,water?template:source));
                            var view=binder.View;view.InitializeBindings();if(view.TeamMarker!=null)view.TeamMarker.enabled=false;
                            var state=new PlayerSnapshot{HeroId=3,Health=100,Ink=100,Grounded=true,Team=1,Revision=1,Pitch=-pose.Item2};
                            double now=0;
                            void Advance(int n){for(int i=0;i<n;i++){now+=1.0/60;view.Present(state,1f/60,now);view.Animator.Update(1f/60);view.ApplyAim();}}
                            Advance(40);
                            if(pose.Item3){state.RightShotAction=1;state.RightShotAt=now;Advance(5);}
                            var hand=view.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                            float err=Vector3.Distance(hand.position,view.LeftGrip.position);
                            if(water){check.newLeftHandErrorMeters=err;check.handDeltaMeters=Vector3.Distance(hand.position,baselineHand);}else{check.baselineLeftHandErrorMeters=err;baselineHand=hand.position;}
                            string prefix=(water?"water-":"original-")+pose.Item1;
                            camera.transform.position=new Vector3(2.45f,1.35f,.6f);camera.transform.LookAt(new Vector3(0,pose.Item2>0?1.18f:1.0f,.24f));camera.fieldOfView=35;
                            CombatGirlsGraphicsValidation.Render(camera,prefix,Output+"/Previews/Unity");
                            if(pose.Item1=="level")
                            {
                                Vector3 center=view.Weapon.TransformPoint(reference.origin)+Vector3.back*.46f;
                                camera.transform.position=center+new Vector3(1.8f,.25f,.20f);camera.transform.LookAt(center);camera.fieldOfView=36;
                                CombatGirlsGraphicsValidation.Render(camera,prefix+"-grip-close",Output+"/Previews/Unity");
                                camera.transform.SetPositionAndRotation(PrototypePlayer.CameraPosition(view.Profile.CameraPivot,Quaternion.identity,view.Profile),Quaternion.identity);camera.fieldOfView=60;
                                CombatGirlsGraphicsValidation.Render(camera,(water?"water":"original")+"-game-camera",Output+"/Previews/Unity");
                            }
                        }
                        UnityEngine.Object.DestroyImmediate(root);
                    }
                    result.poses.Add(check);
                    Require(check.handDeltaMeters<.001f&&check.newLeftHandErrorMeters<=check.baselineLeftHandErrorMeters+.001f,"Hand placement regressed in "+check.pose);
                }
                File.WriteAllText(Output+"/Validation/unity-validation.json",JsonUtility.ToJson(result,true));
                File.Copy(matPath,Output+"/Export/WaterShotgun_Toon.mat",true);
                File.Copy(matPath+".meta",Output+"/Export/WaterShotgun_Toon.mat.meta",true);
                File.Copy(texturePath+".meta",Output+"/Export/WaterShotgun_Albedo.png.meta",true);
                File.Copy(ModelPath+".meta",Output+"/Export/WaterShotgun.fbx.meta",true);
                Debug.Log("WATERGUN_VALIDATION_PASS triangles="+result.triangles+" poses="+result.poses.Count);
                EditorApplication.Exit(0);
            }
            catch(Exception ex)
            {
                File.WriteAllText(Output+"/Validation/unity-validation.json",JsonUtility.ToJson(result,true));
                Debug.LogException(ex);EditorApplication.Exit(1);
            }
        }
    }
}
#endif
