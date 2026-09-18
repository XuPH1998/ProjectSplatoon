#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Playables;
using UnityEngine.Animations;
using UnityEngine.Rendering.Universal;

namespace ChibiArt
{
    public static class RifleGirlChibiPipeline
    {
        const string Project = "D:/XPHUNITY/ProjectSplatoon";
        const string Art = Project + "/ArtSource/Characters/RifleGirlChibi";
        const string Original = "Assets/GameResource/Characters/RifleGirl/Prefabs/RifleGirlVisual.prefab";
        const string Output = "Assets/GameResource/Characters/RifleGirlChibi";
        const string Model = Output + "/Models/RifleGirlChibi.fbx";
        const string Controller = "Assets/GameResource/Characters/RifleGirl/Animations/RifleGirlCombat.controller";
        [Serializable] public class RigBone { public string name,parent; public Vector3 position; public Quaternion rotation; }
        [Serializable] public class MaterialData { public string name,path,texture; public Color color; }
        [Serializable] public class Part
        {
            public string name; public Vector3[] vertices,normals; public Vector2[] uv;
            public int[][] triangles; public List<IndexList> submeshes=new();
            public string[] bones; public BoneWeight[] weights; public MaterialData[] materials;
        }
        [Serializable] public class IndexList { public int[] indices; }
        [Serializable] public class SourceData { public List<RigBone> bones=new(); public List<Part> parts=new(); public HumanBone[] human; }
        static void Guard() { if(Path.GetFullPath(".").TrimEnd('\\','/').Equals(Project,StringComparison.OrdinalIgnoreCase))throw new Exception("Use isolated validation project."); }
        public static void ExportSource()
        {
            try
            {
                Guard(); Directory.CreateDirectory(Art+"/Reference");
                var go=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Original));
                var animator=go.GetComponent<Animator>(); animator.enabled=false;
                foreach(var mb in go.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(mb);
                var skinned=go.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r=>r.enabled).ToArray();
                var set=new HashSet<Transform>();
                foreach(var r in skinned)foreach(var b in r.bones){var t=b;while(t!=null&&t!=go.transform){set.Add(t);t=t.parent;}}
                foreach(var t in go.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="Hand_R_Socket"||t.name=="Hand_L_Socket"))set.Add(t);
                var data=new SourceData();
                foreach(var t in go.GetComponentsInChildren<Transform>(true).Where(set.Contains))data.bones.Add(new RigBone{name=t.name,parent=t.parent!=null&&set.Contains(t.parent)?t.parent.name:null,position=go.transform.InverseTransformPoint(t.position),rotation=Quaternion.Inverse(go.transform.rotation)*t.rotation});
                foreach(var r in skinned)
                {
                    var mesh=r.sharedMesh; var p=new Part{name=r.name,vertices=mesh.vertices.Select(v=>go.transform.InverseTransformPoint(r.transform.TransformPoint(v))).ToArray(),normals=mesh.normals.Select(v=>go.transform.InverseTransformDirection(r.transform.TransformDirection(v))).ToArray(),uv=mesh.uv,weights=mesh.boneWeights,bones=r.bones.Select(b=>b.name).ToArray()};
                    for(int s=0;s<mesh.subMeshCount;s++)p.submeshes.Add(new IndexList{indices=mesh.GetTriangles(s)});
                    p.materials=r.sharedMaterials.Select(m=>new MaterialData{name=m.name,path=AssetDatabase.GetAssetPath(m),texture=AssetDatabase.GetAssetPath(m.GetTexture("_MainTex")),color=m.HasProperty("_BaseColor")?m.GetColor("_BaseColor"):Color.white}).ToArray();
                    data.parts.Add(p);
                }
                var sourceImporter=(ModelImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(animator.avatar));
                data.human=sourceImporter.humanDescription.human;
                File.WriteAllText(Art+"/Reference/source-character.json",JsonUtility.ToJson(data));
                var map=new HumanMapping();
                for(int i=0;i<(int)HumanBodyBones.LastBone;i++){var t=animator.GetBoneTransform((HumanBodyBones)i);if(t!=null)map.human.Add(new HumanEntry{bone=t.name,human=HumanTrait.BoneName[i]});}
                File.WriteAllText(Art+"/Reference/source-human.json",JsonUtility.ToJson(map,true));
                Debug.Log("CHIBI_SOURCE_EXPORTED parts="+data.parts.Count+" bones="+data.bones.Count);
                UnityEngine.Object.DestroyImmediate(go); EditorApplication.Exit(0);
            }
            catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
        }
        [Serializable] public class HumanEntry { public string bone,human; }
        [Serializable] public class HumanMapping { public List<HumanEntry> human=new(); }
        [Serializable] public class SampleCheck { public string clip; public int samples; public float maximumVertexMagnitude; public float boneMotionDegrees; }
        [Serializable] public class CheckReport
        {
            public bool passed,avatarValid,avatarHuman; public string unityVersion=Application.unityVersion;
            public int renderers,vertices,triangles,bones,unweightedVertices; public float height,maximumSupportHandError,previewRifleScale;
            public List<SampleCheck> animations=new();
            public string boundary="Isolated Unity Editor: Humanoid retargeting sampled through PlayableGraph and GPU captures. Not multiplayer, target-device, or gameplay replacement acceptance.";
        }
        static void Require(bool pass,string message){if(!pass)throw new Exception(message);}
        internal static void Capture(Camera cam,string name,int size=1100)
        {
            var skins=UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Where(s=>s.enabled&&s.gameObject.activeInHierarchy).ToArray();
            var frozen=new List<GameObject>();var baked=new List<Mesh>();
            foreach(var skin in skins)
            {
                var mesh=new Mesh();skin.BakeMesh(mesh);baked.Add(mesh);
                var temp=new GameObject("Evaluated skin");temp.transform.SetPositionAndRotation(skin.transform.position,skin.transform.rotation);temp.transform.localScale=skin.transform.lossyScale;
                temp.AddComponent<MeshFilter>().sharedMesh=mesh;temp.AddComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials;frozen.Add(temp);skin.enabled=false;
            }
            var rt=new RenderTexture(size,size,24,RenderTextureFormat.ARGB32);rt.Create();cam.targetTexture=rt;
            var request=new UniversalRenderPipeline.SingleCameraRequest{destination=rt};
            RenderPipeline.SubmitRenderRequest(cam,request);
            var old=RenderTexture.active;RenderTexture.active=rt;
            var image=new Texture2D(size,size,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,size,size),0,0);image.Apply();
            File.WriteAllBytes(Art+"/Previews/"+name+".png",image.EncodeToPNG());
            RenderTexture.active=old;cam.targetTexture=null;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);
            foreach(var skin in skins)skin.enabled=true;
            foreach(var temp in frozen)UnityEngine.Object.DestroyImmediate(temp);
            foreach(var mesh in baked)UnityEngine.Object.DestroyImmediate(mesh);
        }
        static IEnumerable<AnimationClip> CurrentClips()=>AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Controller).animationClips.Distinct();
        public static void BuildAndValidate()
        {
            var report=new CheckReport();
            try
            {
                Guard();Directory.CreateDirectory(Art+"/Previews");Directory.CreateDirectory(Output+"/Prefabs");Directory.CreateDirectory(Output+"/Preview");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var importer=(ModelImporter)AssetImporter.GetAtPath(Model);
                // Rebuild the Avatar from the current FBX rest pose on every run.
                // Retaining an earlier HumanDescription skeleton would restore old
                // bone lengths after a proportion edit.
                importer.importAnimation=false;importer.animationType=ModelImporterAnimationType.Generic;
                importer.avatarSetup=ModelImporterAvatarSetup.NoAvatar;importer.SaveAndReimport();
                var raw=AssetDatabase.LoadAssetAtPath<GameObject>(Model);
                var rest=raw.GetComponentsInChildren<Transform>(true).Select(t=>new SkeletonBone{name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale}).ToArray();
                importer.importAnimation=false;importer.animationType=ModelImporterAnimationType.Human;
                importer.avatarSetup=ModelImporterAvatarSetup.CreateFromThisModel;importer.sourceAvatar=null;
                importer.isReadable=true;importer.optimizeGameObjects=false;importer.importBlendShapes=true;
                importer.importNormals=ModelImporterNormals.Import;importer.importTangents=ModelImporterTangents.CalculateMikk;
                importer.globalScale=1;importer.useFileScale=true;
                var mapping=JsonUtility.FromJson<HumanMapping>(File.ReadAllText(Art+"/Reference/source-human.json"));
                var hd=new HumanDescription{skeleton=rest};hd.human=mapping.human.Select(m=>new HumanBone{boneName=m.bone,humanName=m.human,limit=new HumanLimit{useDefaultValues=true}}).ToArray();
                hd.armStretch=0;hd.legStretch=0;hd.upperArmTwist=.5f;hd.lowerArmTwist=.5f;hd.upperLegTwist=.5f;hd.lowerLegTwist=.5f;hd.feetSpacing=0;
                importer.humanDescription=hd;
                var source=JsonUtility.FromJson<SourceData>(File.ReadAllText(Art+"/Reference/source-character.json"));
                var chibiRest=JsonUtility.FromJson<SourceData>(File.ReadAllText(Art+"/Reference/chibi-rest-frames.json"));
                foreach(var md in source.parts.SelectMany(p=>p.materials).GroupBy(m=>m.path).Select(g=>g.First()))
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material),Path.GetFileNameWithoutExtension(md.path)),AssetDatabase.LoadAssetAtPath<Material>(md.path));
                importer.SaveAndReimport();
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Model);
                var go=UnityEngine.Object.Instantiate(prefab);go.name="RifleGirlChibi";
                var animator=go.GetComponent<Animator>();
                report.avatarHuman=animator.avatar!=null&&animator.avatar.isHuman;report.avatarValid=animator.avatar!=null&&animator.avatar.isValid;
                Require(report.avatarHuman&&report.avatarValid,"Chibi Avatar invalid");
                animator.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Controller);
                go.AddComponent<RifleGirlChibiAnimationEvents>();
                animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.fireEvents=false;
                foreach(var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    r.updateWhenOffscreen=true;r.quality=SkinQuality.Bone4;report.renderers++;report.vertices+=r.sharedMesh.vertexCount;report.triangles+=r.sharedMesh.triangles.Length/3;
                    Require(r.bones.All(b=>b!=null),"Missing skin bone "+r.name);
                    foreach(var w in r.sharedMesh.boneWeights)if(w.weight0+w.weight1+w.weight2+w.weight3<.999f)report.unweightedVertices++;
                    Require(r.sharedMaterials.All(m=>m!=null&&m.shader.name=="Toon/Toon"),"Material remap failed "+r.name);
                    r.localBounds=new Bounds(new Vector3(0,.6f,0),Vector3.one*4);
                }
                Require(report.unweightedVertices==0,"Missing skin weights");report.bones=source.bones.Count;
                // FBX applies a bone-axis basis conversion; non-Humanoid sockets
                // need the original authored world frame restored at the bind pose.
                foreach(var b in chibiRest.bones.Where(b=>b.name=="Hand_R_Socket"||b.name=="Hand_L_Socket"))
                    go.GetComponentsInChildren<Transform>().Single(t=>t.name==b.name).rotation=b.rotation;
                PrefabUtility.SaveAsPrefabAsset(go,Output+"/Prefabs/RifleGirlChibi.prefab");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var light=new GameObject("Studio Key").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1;light.transform.rotation=Quaternion.Euler(35,-25,0);
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.7f,.72f,.78f);
                var cam=new GameObject("Main Camera").AddComponent<Camera>();cam.tag="MainCamera";cam.enabled=false;
                cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.15f,.17f,.22f);cam.nearClipPlane=.01f;cam.farClipPlane=30;cam.orthographic=true;cam.orthographicSize=.74f;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing=false;
                go=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Output+"/Prefabs/RifleGirlChibi.prefab"));animator=go.GetComponent<Animator>();animator.fireEvents=false;
                animator.enabled=false;
                foreach(var view in new[]{("unity-front",new Vector3(0,.64f,4)),("unity-quarter",new Vector3(2.7f,1.1f,4)),("unity-back",new Vector3(0,.65f,-4)),("unity-side",new Vector3(4,.65f,0))})
                {cam.transform.position=view.Item2;cam.transform.LookAt(new Vector3(0,.64f,0));Capture(cam,view.Item1);}
                var leftArm=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);var rightArm=animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                var leftRest=leftArm.rotation;var rightRest=rightArm.rotation;
                leftArm.rotation=Quaternion.AngleAxis(28,Vector3.forward)*leftRest;rightArm.rotation=Quaternion.AngleAxis(-28,Vector3.forward)*rightRest;
                cam.transform.position=new Vector3(0,.64f,4);cam.transform.LookAt(new Vector3(0,.64f,0));Capture(cam,"unity-relaxed-front");
                leftArm.rotation=leftRest;rightArm.rotation=rightRest;
                var bounds=new Bounds();bool first=true;
                foreach(var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {var mesh=new Mesh();r.BakeMesh(mesh);foreach(var p in mesh.vertices){var wp=r.transform.TransformPoint(p);if(first){bounds=new Bounds(wp,Vector3.zero);first=false;}else bounds.Encapsulate(wp);}UnityEngine.Object.DestroyImmediate(mesh);}
                report.height=bounds.size.y;Require(report.height>1.15f&&report.height<1.4f,"Unexpected scale "+report.height);
                var socket=go.GetComponentsInChildren<Transform>().Single(t=>t.name=="Hand_R_Socket");
                var rifle=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Weapons/RifleGirl/Prefabs/RifleGirlRifle.prefab"),socket);
                rifle.transform.localPosition=Vector3.zero;rifle.transform.localRotation=Quaternion.identity;rifle.transform.localScale=Vector3.one*.65f;
                var grip=rifle.GetComponentsInChildren<Transform>().Single(t=>t.name=="Left_Handle");
                var upper=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);var lower=animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);var hand=animator.GetBoneTransform(HumanBodyBones.LeftHand);
                var handBasis=Quaternion.Inverse(chibiRest.bones.Single(b=>b.name=="hand_l").rotation)*hand.rotation;
                animator.enabled=true;
                var scales=new[]{.5f,.55f,.6f,.65f,.7f,.75f,.8f,.85f,.9f};var reachErrors=new float[scales.Length];
                foreach(var clip in CurrentClips().Where(c=>!c.name.StartsWith("Die")))
                {
                    animator.Rebind();animator.fireEvents=false;
                    var graph=PlayableGraph.Create("Chibi grip reach calibration");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable=AnimationClipPlayable.Create(graph,clip);playable.SetApplyFootIK(false);
                    AnimationPlayableOutput.Create(graph,"Humanoid",animator).SetSourcePlayable(playable);graph.Play();
                    for(int s=0;s<=12;s++)
                    {
                        playable.SetTime(clip.length*s/12.0);graph.Evaluate(0);
                        float reach=(Vector3.Distance(upper.position,lower.position)+Vector3.Distance(lower.position,hand.position))*.999f;
                        for(int k=0;k<scales.Length;k++){rifle.transform.localScale=Vector3.one*scales[k];reachErrors[k]=Mathf.Max(reachErrors[k],Vector3.Distance(upper.position,grip.position)-reach);}
                    }
                    graph.Destroy();
                }
                int selected=Enumerable.Range(0,scales.Length).OrderBy(i=>reachErrors[i]>.001f?1:0).ThenBy(i=>reachErrors[i]>.001f?reachErrors[i]:Mathf.Abs(scales[i]-.65f)).First();
                report.previewRifleScale=scales[selected];rifle.transform.localScale=Vector3.one*report.previewRifleScale;
                Debug.Log("CHIBI_GRIP_SCALES "+string.Join(", ",Enumerable.Range(0,scales.Length).Select(i=>scales[i]+":"+reachErrors[i])));
                foreach(var clip in CurrentClips())
                {
                    animator.Rebind();animator.fireEvents=false;var check=new SampleCheck{clip=clip.name};
                    var graph=PlayableGraph.Create("Chibi retarget validation");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable=AnimationClipPlayable.Create(graph,clip);playable.SetApplyFootIK(false);
                    var animOut=AnimationPlayableOutput.Create(graph,"Humanoid",animator);animOut.SetSourcePlayable(playable);graph.Play();
                    var bone=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);Quaternion initial=bone.rotation;
                    for(int s=0;s<=12;s++)
                    {
                        playable.SetTime(clip.length*s/12.0);graph.Evaluate(0);check.samples++;
                        check.boneMotionDegrees=Mathf.Max(check.boneMotionDegrees,Quaternion.Angle(initial,bone.rotation));
                        foreach(var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                        {
                            var mesh=new Mesh();r.BakeMesh(mesh);
                            foreach(var p in mesh.vertices){var wp=go.transform.InverseTransformPoint(r.transform.TransformPoint(p));Require(float.IsFinite(wp.x)&&float.IsFinite(wp.y)&&float.IsFinite(wp.z),"Nonfinite skinned vertex");check.maximumVertexMagnitude=Mathf.Max(check.maximumVertexMagnitude,wp.magnitude);}
                            UnityEngine.Object.DestroyImmediate(mesh);
                        }
                        if(s==4){cam.transform.position=new Vector3(2.5f,1.1f,4);cam.transform.LookAt(new Vector3(0,.6f,0));Capture(cam,"animation-"+clip.name,850);}
                        if(!clip.name.StartsWith("Die"))
                        {
                            RifleGirlChibiPreview.SolveSupportArm(upper,lower,hand,grip.position,grip.rotation*handBasis);
                            report.maximumSupportHandError=Mathf.Max(report.maximumSupportHandError,Vector3.Distance(hand.position,grip.position));
                            if(s==4)Capture(cam,"grip-"+clip.name,850);
                        }
                    }
                    graph.Destroy();Require(check.maximumVertexMagnitude<3.5f,"Exploded mesh in "+clip.name);report.animations.Add(check);
                }
                Require(report.animations.Count>=10,"Expected current ten RifleGirl clips");
                Require(report.maximumSupportHandError<.005f,"Support grip error above 5 mm: "+report.maximumSupportHandError);
                go.SetActive(false);
                var reference=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Original));
                foreach(var mb in reference.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(mb);
                var refAnimator=reference.GetComponent<Animator>();refAnimator.fireEvents=false;refAnimator.applyRootMotion=false;
                var refGraph=PlayableGraph.Create("Original pose comparison");refGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var refClip=CurrentClips().First(c=>c.name=="AimIdle");var refPlayable=AnimationClipPlayable.Create(refGraph,refClip);refPlayable.SetApplyFootIK(false);
                AnimationPlayableOutput.Create(refGraph,"Source",refAnimator).SetSourcePlayable(refPlayable);refGraph.Play();refPlayable.SetTime(refClip.length/3);refGraph.Evaluate(0);
                cam.orthographicSize=1.05f;cam.transform.position=new Vector3(2.5f,1.45f,4);cam.transform.LookAt(new Vector3(0,.85f,0));Capture(cam,"source-aim-idle");
                refGraph.Destroy();UnityEngine.Object.DestroyImmediate(reference);go.SetActive(true);cam.orthographicSize=.74f;
                animator.Rebind();animator.Update(0);animator.enabled=false;
                var demo=go.AddComponent<RifleGirlChibiPreview>();demo.Target=animator;demo.Clips=CurrentClips().OrderBy(c=>c.name=="AimIdle"?0:1).ThenBy(c=>c.name).ToArray();demo.SupportGrip=grip;demo.SupportHandRotationOffset=handBasis;demo.ReviewCamera=cam;
                cam.enabled=true;cam.transform.position=new Vector3(2.5f,1.1f,4);cam.transform.LookAt(new Vector3(0,.62f,0));
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),Output+"/Preview/RifleGirlChibiPreview.unity");
                AssetDatabase.SaveAssets();report.passed=true;
                File.WriteAllText(Art+"/Validation/unity-validation.json",JsonUtility.ToJson(report,true));
                Debug.Log("CHIBI_VALIDATION_PASS clips="+report.animations.Count);EditorApplication.Exit(0);
            }
            catch(Exception ex){File.WriteAllText(Art+"/Validation/unity-validation.json",JsonUtility.ToJson(report,true));Debug.LogException(ex);EditorApplication.Exit(1);}
        }
    }

    [InitializeOnLoad]
    public static class ChibiPreviewPlayValidation
    {
        const string Key="ChibiArt.PlayValidation";
        const string Evidence="D:/XPHUNITY/ProjectSplatoon/ArtSource/Characters/RifleGirlChibi/Validation/play-mode.json";
        static RifleGirlChibiPreview demo;
        static int current=-1;
        static readonly List<string> checkedClips=new();
        static readonly List<string> errors=new();
        static readonly List<string> editorIssues=new();
        static float maxGripError;
        static double started;
        [Serializable] public class Report { public bool passed;public string[] clips,errors,editorIssues;public float maximumSupportHandError;public string boundary="Actual Play Mode in isolated Unity editor; preview scene only, no gameplay replacement or target-device test. Unrelated Unity Search startup exceptions are retained separately in editorIssues."; }
        static ChibiPreviewPlayValidation()
        {
            EditorApplication.playModeStateChanged+=Changed;
            if(SessionState.GetBool(Key+"Exit",false))EditorApplication.delayCall+=()=>{bool ok=SessionState.GetBool(Key+"Result",false);SessionState.SetBool(Key+"Exit",false);EditorApplication.Exit(ok?0:1);};
        }
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/GameResource/Characters/RifleGirlChibi/Preview/RifleGirlChibiPreview.unity");
            SessionState.SetBool(Key,true);EditorApplication.EnterPlaymode();
        }
        static void Changed(PlayModeStateChange state)
        {
            if(state==PlayModeStateChange.EnteredEditMode&&SessionState.GetBool(Key+"Exit",false))
            {bool ok=SessionState.GetBool(Key+"Result",false);SessionState.SetBool(Key+"Exit",false);EditorApplication.Exit(ok?0:1);return;}
            if(!SessionState.GetBool(Key,false)||state!=PlayModeStateChange.EnteredPlayMode)return;
            demo=UnityEngine.Object.FindFirstObjectByType<RifleGirlChibiPreview>();
            Application.logMessageReceived+=Log;started=EditorApplication.timeSinceStartup;
            EditorApplication.update+=Tick;
        }
        static void Log(string message,string stack,LogType type)
        {
            if(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert)return;
            if(message.StartsWith("ArgumentOutOfRangeException")&&stack.Contains("UnityEditor.Search.SearchDatabase")&&!stack.Contains("ChibiArt.")){editorIssues.Add(message+"\n"+stack);return;}
            errors.Add(message+"\n"+stack);
        }
        static void Tick()
        {
            try
            {
                if(EditorApplication.timeSinceStartup-started>90)throw new Exception("Play Mode preview timed out");
                if(demo==null)throw new Exception("Preview component not found");
                if(current<0){current=0;demo.Select(current);return;}
                if(demo.PreviewTime<.35)return;
                var name=demo.Clips[current].name;
                if(!name.StartsWith("Die"))
                {
                    float gap=Vector3.Distance(demo.Target.GetBoneTransform(HumanBodyBones.LeftHand).position,demo.SupportGrip.position);
                    maxGripError=Mathf.Max(maxGripError,gap);
                    if(gap>.005f)throw new Exception("Support grip gap in "+name+": "+gap);
                }
                if(name=="AimIdle"||name=="AimWalk_F"||name.Contains("AutoShoot"))RifleGirlChibiPipeline.Capture(demo.ReviewCamera,"play-"+name,1000);
                checkedClips.Add(name);current++;
                if(current>=demo.Clips.Length){Finish(errors.Count==0);return;}
                demo.Select(current);
            }
            catch(Exception e){errors.Add(e.ToString());Finish(false);}
        }
        static void Finish(bool passed)
        {
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            File.WriteAllText(Evidence,JsonUtility.ToJson(new Report{passed=passed,clips=checkedClips.ToArray(),errors=errors.ToArray(),editorIssues=editorIssues.ToArray(),maximumSupportHandError=maxGripError},true));
            Debug.Log("CHIBI_PLAYMODE_"+(passed?"PASS":"FAIL"));
            SessionState.SetBool(Key,false);SessionState.SetBool(Key+"Result",passed);SessionState.SetBool(Key+"Exit",true);EditorApplication.ExitPlaymode();
        }
    }
}
#endif
