#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class SplooshSummerBuilder
    {
        public const string Root = SplooshGirlBuilder.Root;
        public const string Model = Root + "/Models/SummerCuteness.fbx";
        public const string Report = "Reports/SplooshGirlSummer";
        const string Art = "ArtSource/Characters/SplooshGirlSummer";
        const string Controller = "Assets/GameResource/Characters/RifleGirl/Animations/RifleGirlCombat.controller";
        static SplooshSummerBuilder()
        {
            EditorApplication.update += Poll;
        }
        static void Poll()
        {
            const string request = "Temp/SplooshSummer/install";
            if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            File.Delete(request); Directory.CreateDirectory(Report);
            try { Install(); File.WriteAllText(Report + "/install-status.txt", "PASS"); }
            catch (Exception e) { File.WriteAllText(Report + "/install-status.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/角色/重建铃芽 SummerCuteness 模型")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first");
            Directory.CreateDirectory(Report);
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
            ImportModel();
            var scene = EditorSceneManager.NewPreviewScene();
            try { BuildCharacter(scene); }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            PaperBodyBuilder.ConfigureHero("SplooshGirl", new Vector2(.75f, 1.5f));
            AssetDatabase.SaveAssets();
        }
        public static void InstallBatch()
        { try { Install(); EditorApplication.Exit(0); } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); } }
        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new Exception("Missing " + path);
        static HumanBone Bone(string human, string name) => new() { humanName = human, boneName = name, limit = new HumanLimit { useDefaultValues = true } };
        public static void PrepareModel()
        {
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
            Directory.CreateDirectory(Report);
            ImportModel();
        }
        static void ImportModel()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(Model);
            importer.animationType = ModelImporterAnimationType.Generic; importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.importAnimation = false; importer.isReadable = true; importer.optimizeGameObjects = false;
            importer.importBlendShapes = true; importer.importNormals = ModelImporterNormals.Import;
            importer.SaveAndReimport();
            var raw = Load<GameObject>(Model);
            var map = new List<HumanBone> {
                Bone("Hips", "Bip001 Pelvis"), Bone("Spine", "Bip001 Spine"), Bone("Chest", "Bip001 Spine1"),
                Bone("UpperChest", "Bip001 Spine2"), Bone("Neck", "Bip001 Neck"), Bone("Head", "Bip001 Head") };
            foreach (string side in new[] { "Left", "Right" })
            {
                string p = "Bip001 " + side[0] + " ";
                foreach (var pair in new[] { ("Shoulder","Clavicle"),("UpperArm","UpperArm"),("LowerArm","Forearm"),("Hand","Hand"),
                    ("UpperLeg","Thigh"),("LowerLeg","Calf"),("Foot","Foot"),("Toes","Toe0") }) map.Add(Bone(side + pair.Item1, p + pair.Item2));
                string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" }, segments = { "Proximal", "Intermediate", "Distal" };
                for (int finger = 0; finger < 5; finger++) for (int segment = 0; segment < 3; segment++)
                    map.Add(Bone(side + " " + fingers[finger] + " " + segments[segment], p + "Finger" + finger + (segment == 0 ? "" : segment.ToString())));
            }
            importer.animationType = ModelImporterAnimationType.Human; importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; importer.sourceAvatar = null;
            importer.humanDescription = new HumanDescription {
                human = map.ToArray(), skeleton = raw.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone {
                    name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                armStretch = 0, legStretch = 0, upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f };
            foreach (var path in Directory.GetFiles(Art + "/Source/Avatar_Female_Size01_SummerCuteness_UI/Materials", "*.json"))
            {
                var data = SimpleJSON.JSONNode.Parse(File.ReadAllText(path)); string name = Path.GetFileNameWithoutExtension(path);
                string materialPath = Root + "/Materials/" + name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (mat == null) { mat = new Material(Shader.Find("Toon/Toon")) { name = name }; AssetDatabase.CreateAsset(mat, materialPath); }
                string texture = data["m_SavedProperties"]["m_TexEnvs"]["_MainTex"]["m_Texture"]["Name"];
                if (!string.IsNullOrEmpty(texture))
                {
                    var tex = Load<Texture2D>(Root + "/Textures/" + texture + ".png");
                    mat.SetTexture("_MainTex", tex); mat.SetTexture("_BaseMap", tex); mat.SetTexture("_1st_ShadeMap", tex); mat.SetTexture("_2nd_ShadeMap", tex);
                }
                mat.SetColor("_BaseColor", Color.white); mat.SetColor("_1st_ShadeColor", new Color(.82f,.82f,.88f)); mat.SetColor("_2nd_ShadeColor", new Color(.7f,.7f,.78f));
                mat.SetFloat("_BaseColor_Step", .35f); mat.SetFloat("_BaseShade_Feather", .2f); mat.SetFloat("_Outline_Width", 0);
                mat.SetFloat("_Is_LightColor_Base", 1); mat.SetFloat("_Is_LightColor_1st_Shade", 1); mat.SetFloat("_Is_LightColor_2nd_Shade", 1);
                bool cutout = name.Contains("Hair_T") || name.Contains("Eyebrows");
                mat.SetFloat("_ClippingMode", cutout ? 1 : 0); mat.SetFloat("_IsBaseMapAlphaAsClippingMask", 1);
                mat.SetFloat("_Clipping_Level", .1f); mat.SetFloat("_CullMode", 0);
                if (cutout) { mat.EnableKeyword("_IS_CLIPPING_MODE"); mat.DisableKeyword("_IS_CLIPPING_OFF"); mat.renderQueue = 2450; }
                EditorUtility.SetDirty(mat);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), mat);
            }
            importer.SaveAndReimport();
            var avatar = Load<GameObject>(Model).GetComponent<Animator>().avatar;
            if (avatar == null || !avatar.isValid || !avatar.isHuman) throw new Exception("SummerCuteness Humanoid Avatar is invalid");
        }
        public static void BuildCharacter(Scene scene)
        {
            var go = Object.Instantiate(Load<GameObject>(Model)); go.name = "SplooshGirlVisual"; SceneManager.MoveGameObjectToScene(go, scene);
            var animator = go.GetComponent<Animator>(); animator.applyRootMotion = false;
            animator.runtimeAnimatorController = Load<RuntimeAnimatorController>(Controller); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var profile = Load<CharacterPresentationProfile>(Root + "/SplooshGirlPresentation.asset");
            profile.AnimationReferenceSpeed = GameplayConfig.GetHero(8).MoveSpeed;
            animator.runtimeAnimatorController = CalibrateController(go, profile);
            var view = go.AddComponent<InkCharacterView>(); view.Animator = animator; view.Profile = profile;
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // Source overlay meshes depended on a missing stencil/face shader.
                r.enabled = r.name != "Summer_Cuteness_HairShadow" && r.name != "Summer_Shy";
                r.updateWhenOffscreen = true; r.quality = SkinQuality.Bone4;
                r.localBounds = new Bounds(Vector3.up * .75f, Vector3.one * 4);
            }
            Transform Socket(HumanBodyBones bone, string name) { var t = new GameObject(name).transform; t.SetParent(animator.GetBoneTransform(bone), false); return t; }
            view.WeaponSocket = Socket(HumanBodyBones.RightHand, "Hand_R_Socket"); view.LeftWeaponSocket = Socket(HumanBodyBones.LeftHand, "Hand_L_Socket");
            var reference = Object.Instantiate(Load<GameObject>(CombatGirlsBuilder.CharacterPath)); SceneManager.MoveGameObjectToScene(reference, scene);
            var old = reference.GetComponent<InkCharacterView>();
            view.TeamMarker = Object.Instantiate(old.TeamMarker.gameObject, go.transform).GetComponent<Renderer>();
            view.TeamMarker.transform.localPosition = new Vector3(0,.025f,0); view.TeamMarker.transform.localScale *= 1.5f / 1.8f;
            view.SwimEffect = Object.Instantiate(old.SwimEffect.gameObject, go.transform).GetComponent<ParticleSystem>(); view.SwimEffect.transform.localScale *= 1.5f / 1.8f;
            void Idle(Animator a) { a.Rebind(); a.Play("Base Layer.Locomotion",0,0); for(int i=1;i<a.layerCount;i++) a.SetLayerWeight(i,0); a.Update(.1f); }
            Idle(old.Animator); Idle(animator);
            var right = animator.GetBoneTransform(HumanBodyBones.RightHand); var oldRight = old.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            view.WeaponSocket.rotation = old.WeaponSocket.rotation;
            view.WeaponSocket.position = right.position + (old.WeaponSocket.position - oldRight.position) * (1.5f / 1.8f);
            var gun = Object.Instantiate(Load<GameObject>(CombatGirlsBuilder.WeaponPath), view.WeaponSocket, false); gun.name = "SplooshGun"; gun.transform.localScale = Vector3.one * .68f;
            var bindings = gun.GetComponent<HeroWeaponBindings>();
            bindings.LeftGrip.rotation = animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation * Quaternion.Inverse(old.Animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation) * old.LeftGrip.rotation;
            var weaponPrefab = PrefabUtility.SaveAsPrefabAsset(gun, SplooshGirlBuilder.WeaponPath);
            view.BindWeapon(bindings, weaponPrefab);
            Object.DestroyImmediate(reference);
            foreach(var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
            var state = new PlayerSnapshot { HeroId=8, Health=100, Ink=100, Grounded=true, Team=1, Revision=1 };
            view.Present(state,1,0); Idle(animator); view.ApplyAim();
            profile.AimPivot = go.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Chest).position);
            profile.MuzzlePosition = go.transform.InverseTransformPoint(view.Nozzle.position); profile.LeftMuzzlePosition = profile.MuzzlePosition;
            CameraFramingBuilder.Apply(view); EditorUtility.SetDirty(profile);
            // Keep the authored bind transforms, never the sampled calibration pose.
            animator.Rebind();
            PrefabUtility.SaveAsPrefabAsset(go, SplooshGirlBuilder.CharacterPath);
            ValidateAndCapture(view,scene);
            Object.DestroyImmediate(go);
        }
        static RuntimeAnimatorController CalibrateController(GameObject character, CharacterPresentationProfile profile)
        {
            string folder = Root + "/Animations", path = folder + "/SplooshGirlCombat.controller";
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            if (!File.Exists(path) && !AssetDatabase.CopyAsset(Controller,path)) throw new Exception("Cannot copy Summer controller");
            var controller = Load<AnimatorController>(path);
            var tree = (BlendTree)controller.layers[0].stateMachine.states.Single(s=>s.state.name=="Locomotion").state.motion;
            var children = tree.children; var animator = character.GetComponent<Animator>();
            var rows = new List<string> {"clip,nativeStrideMetresPerSecond,playbackAtReferenceSpeed"};
            for(int i=1;i<children.Length;i++)
            {
                var clip=(AnimationClip)children[i].motion; Vector3 axis=i<=2?Vector3.forward:Vector3.right;
                float[] low={float.PositiveInfinity,float.PositiveInfinity},high={float.NegativeInfinity,float.NegativeInfinity};
                animator.Rebind();
                var graph=PlayableGraph.Create("Summer stride calibration");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                try
                {
                    var playable=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"pose",animator).SetSourcePlayable(playable);graph.Play();
                    for(int frame=0;frame<=60;frame++)
                    {
                        playable.SetTime(clip.length*frame/60);graph.Evaluate(0);
                        for(int foot=0;foot<2;foot++)
                        {
                            var bone=animator.GetBoneTransform(foot==0?HumanBodyBones.LeftFoot:HumanBodyBones.RightFoot);
                            float at=Vector3.Dot(character.transform.InverseTransformPoint(bone.position),axis);
                            low[foot]=Mathf.Min(low[foot],at);high[foot]=Mathf.Max(high[foot],at);
                        }
                    }
                }
                finally{graph.Destroy();}
                float native=((high[0]-low[0])+(high[1]-low[1]))/clip.length;
                if(!float.IsFinite(native)||native<.05f)throw new Exception("Cannot measure retargeted stride: "+clip.name);
                profile.WalkPlayback[i-1]=profile.AnimationReferenceSpeed/native;
                children[i].timeScale=profile.WalkPlayback[i-1];
                rows.Add(FormattableString.Invariant($"{clip.name},{native:R},{children[i].timeScale:R}"));
            }
            tree.children=children;EditorUtility.SetDirty(tree);
            string maskPath=folder+"/SummerUpperBody.mask";var mask=AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if(mask==null){mask=new AvatarMask{name="SummerUpperBody"};AssetDatabase.CreateAsset(mask,maskPath);}
            for(int i=0;i<(int)AvatarMaskBodyPart.LastBodyPart;i++)mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i,
                i==(int)AvatarMaskBodyPart.Body||i==(int)AvatarMaskBodyPart.Head||i==(int)AvatarMaskBodyPart.LeftArm||i==(int)AvatarMaskBodyPart.RightArm||i==(int)AvatarMaskBodyPart.LeftFingers||i==(int)AvatarMaskBodyPart.RightFingers);
            mask.transformCount=0;mask.AddTransformPath(character.transform,true);
            for(int i=0;i<mask.transformCount;i++)mask.SetTransformActive(i,mask.GetTransformPath(i).Contains("/Bip001 Spine"));
            var layers=controller.layers;layers[1].avatarMask=mask;controller.layers=layers;
            EditorUtility.SetDirty(mask);EditorUtility.SetDirty(controller);animator.Rebind();
            File.WriteAllLines(Report+"/stride.csv",rows);return controller;
        }
        public static void ValidateAndCapture(InkCharacterView view, Scene scene)
        {
            var animator = view.Animator; float maxGrip=0; int samples=0;
            var clips=animator.runtimeAnimatorController.animationClips.Distinct().ToArray();
            foreach(var clip in clips) foreach(float pitch in new[]{-65f,0f,75f})
            {
                bool dead=clip.name.StartsWith("Die");
                view.Present(new PlayerSnapshot {HeroId=8,Health=dead?0:100,Grounded=true,Team=1,Revision=1,Pitch=pitch},0,0);
                animator.Rebind(); animator.fireEvents=false;
                var graph=PlayableGraph.Create("Summer animation validation");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                try
                {
                    var playable=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"pose",animator).SetSourcePlayable(playable);graph.Play();
                    for(int i=0;i<=24;i++)
                    {
                        playable.SetTime(clip.length*i/24);graph.Evaluate(0);view.ApplyAim();samples++;
                        if(!dead) maxGrip=Mathf.Max(maxGrip,Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,view.LeftGrip.position));
                        if(pitch==0&&i==12) Capture(view,scene,clip.name,new Vector3(2,.9f,3.5f),new Vector3(0,.75f,0));
                    }
                }
                finally{graph.Destroy();}
            }
            view.Present(new PlayerSnapshot {HeroId=8,Health=100,Grounded=true,Team=1,Revision=2},1,0); animator.Update(.1f);view.ApplyAim();
            foreach(var pose in new[]{("front",new Vector3(0,.85f,4)),("back",new Vector3(0,.85f,-4)),("side",new Vector3(4,.85f,0)),("face",new Vector3(.15f,1.35f,1.5f))})
                Capture(view,scene,pose.Item1,pose.Item2,new Vector3(0,pose.Item1=="face"?1.3f:.75f,0));
            File.WriteAllText(Report+"/animation.json",$"{{\"clips\":{clips.Length},\"samples\":{samples},\"maxGripError\":{maxGrip.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"avatarValid\":{animator.avatar.isValid.ToString().ToLowerInvariant()}}}");
            if(maxGrip>=.01f)throw new Exception("Support grip error "+maxGrip);
        }
        static void Capture(InkCharacterView view,Scene scene,string name,Vector3 position,Vector3 target)
        {
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)throw new Exception("Rendered validation requires a GPU");
            var cameraObject=new GameObject("Summer validation camera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
            var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene);
            camera.fieldOfView=name=="face"?24:30;camera.nearClipPlane=.01f;camera.transform.position=position;camera.transform.LookAt(target);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.16f,.18f,.23f);camera.GetUniversalAdditionalCameraData().renderPostProcessing=false;
            var lightObject=new GameObject("Summer validation light");SceneManager.MoveGameObjectToScene(lightObject,scene);
            var light=lightObject.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(30,-25,0);
            // Freeze the evaluated skinned geometry so editor preview rendering cannot resample it.
            var frozen=new List<GameObject>();var baked=new List<Mesh>();var skins=view.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s=>s.enabled).ToArray();
            foreach(var skin in skins)
            {
                var mesh=new Mesh();skin.BakeMesh(mesh);baked.Add(mesh);var go=new GameObject("Evaluated summer skin");SceneManager.MoveGameObjectToScene(go,scene);
                go.transform.SetPositionAndRotation(skin.transform.position,skin.transform.rotation);go.transform.localScale=skin.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials;frozen.Add(go);skin.enabled=false;
            }
            var rt=new RenderTexture(1000,1000,24);rt.Create();camera.targetTexture=rt;
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=rt});
            var prior=RenderTexture.active;RenderTexture.active=rt;var image=new Texture2D(1000,1000,TextureFormat.RGB24,false);
            image.ReadPixels(new Rect(0,0,1000,1000),0,0);image.Apply();File.WriteAllBytes(Report+"/"+name+".png",image.EncodeToPNG());
            RenderTexture.active=prior;camera.targetTexture=null;rt.Release();Object.DestroyImmediate(rt);Object.DestroyImmediate(image);
            foreach(var skin in skins)skin.enabled=true;foreach(var go in frozen)Object.DestroyImmediate(go);foreach(var mesh in baked)Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(cameraObject);Object.DestroyImmediate(lightObject);
        }
    }
}
#endif
