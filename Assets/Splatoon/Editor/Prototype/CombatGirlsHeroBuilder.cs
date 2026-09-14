#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Splatoon.Editor
{
    // Separate per-pack definitions keep reinstallation from overwriting RifleGirl or gameplay data.
    public static class CombatGirlsHeroBuilder
    {
        [Serializable] public sealed class Pack
        {
            public int id;
            public string pack, name, weapon, avatar, prefab, scene;
            public string[] clips;
            public string Art => CombatGirlsBuilder.SourceRoot + "/" + pack;
            public string Root => "Assets/GameResource/Characters/" + name;
            public string CharacterPath => Root + "/Prefabs/" + name + "Visual.prefab";
            public string WeaponPath => "Assets/GameResource/Weapons/" + name + "/Prefabs/" + weapon + ".prefab";
            public string ClipPath(string clip) => Art + "/Animations/" + (clip.Contains("Die") ? "Normal/" : "Aiming/") + clip + ".fbx";
        }
        [Serializable] sealed class Packs { public Pack[] items; }
        [Serializable] sealed class PaintMeasurement { public int id; public float range; }
        public static Pack[] Definitions => JsonUtility.FromJson<Packs>("{\"items\":" + File.ReadAllText("Tools/CombatGirls/hero-packs.json") + "}").items;
        const string ReportRoot = "Reports/CombatGirls/FourHeroes";
        static readonly List<string> Report = new();
        static readonly Dictionary<Material, Material> Materials = new();
        static AnimationClip Clip(Pack p, string name) => AssetDatabase.LoadAllAssetsAtPath(p.ClipPath(name)).OfType<AnimationClip>().Single(c => !c.name.StartsWith("__preview__"));
        static T Load<T>(string path) where T : UnityEngine.Object => CombatGirlsBuilder.Load<T>(path);

        [MenuItem("喷墨对战/角色/安装四位 CombatGirls 英雄")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Cannot rebuild while playing");
            Directory.CreateDirectory(ReportRoot + "/Screenshots"); Report.Clear(); Materials.Clear();
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                foreach (var pack in Definitions) Build(pack, preview);
                PrototypeBuilder.ConfigureAddressables(); AssetDatabase.SaveAssets();
                Validate();
                File.WriteAllLines(ReportRoot + "/asset-validation.txt", Report);
                Debug.Log("[FourHeroes] Assets, 41 animations, bindings and material validation PASS");
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        [MenuItem("喷墨对战/角色/重建霰弹枪握持与骨架绑定")]
        public static void RebuildShotgun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Cannot rebuild while playing");
            Directory.CreateDirectory(ReportRoot + "/Screenshots"); Report.Clear(); Materials.Clear();
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                Build(Definitions.Single(p => p.id == 3), preview);
                AssetDatabase.SaveAssets(); Validate();
                File.WriteAllLines(ReportRoot + "/shotgun-binding-validation.txt", Report);
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        static void Build(Pack p, Scene preview)
        {
            foreach (var folder in new[] { p.Root + "/Animations", p.Root + "/Materials", p.Root + "/Prefabs", Path.GetDirectoryName(p.WeaponPath) }) Directory.CreateDirectory(folder);
            AssetDatabase.Refresh(); Materials.Clear();
            string avatarPath = CombatGirlsBuilder.SourceRoot + "/Humanoid_Bot/Models/" + p.avatar + ".fbx";
            string isolatedAvatar = CombatGirlsBuilder.SourceRoot + "/SharedFourHeroes/Humanoid_Bot/Models/" + p.avatar + ".fbx";
            avatarPath = File.Exists(isolatedAvatar) ? isolatedAvatar : avatarPath;
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            // FePistol is authored with the shared female Humanoid mapping. Unity's
            // automatic CreateFromThisModel mapping chooses "root" as Hips instead
            // of "pelvis", leaving the animated weapon outside the retargeted body.
            // Preserve the explicit source mapping when creating its local Avatar.
            bool incorrectHips = avatar != null && avatar.humanDescription.human.Any(b => b.humanName == "Hips" && b.boneName != "pelvis");
            if (avatar == null || (p.avatar == "Humanoid_FePistol" && incorrectHips))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(avatarPath);
                var description = importer.humanDescription;
                if (p.avatar == "Humanoid_FePistol")
                {
                    var shared = (ModelImporter)AssetImporter.GetAtPath(CombatGirlsBuilder.SourceRoot + "/SharedFourHeroes/Humanoid_Bot/Models/Humanoid_F.fbx");
                    description = shared.humanDescription;
                    if (!description.human.Any(b => b.humanName == "Hips" && b.boneName == "pelvis"))
                        throw new InvalidOperationException("Missing authored female pelvis mapping");
                }
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.humanDescription = description;
                importer.SaveAndReimport(); avatar = Load<Avatar>(avatarPath);
                Report.Add(p.name + ": regenerated source Avatar from its authored Humanoid mapping");
            }
            if (!avatar.isValid || !avatar.isHuman) throw new InvalidOperationException("Invalid Avatar: " + p.avatar);
            foreach (var name in p.clips)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(p.ClipPath(name));
                importer.animationType = ModelImporterAnimationType.Human; importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; importer.sourceAvatar = avatar;
                var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
                if (clips.Length != 1) throw new InvalidOperationException("Expected one authored take: " + name);
                clips[0].lockRootRotation = clips[0].lockRootHeightY = clips[0].lockRootPositionXZ = false;
                clips[0].keepOriginalOrientation = false; clips[0].keepOriginalPositionY = true; clips[0].keepOriginalPositionXZ = false;
                clips[0].loopTime = clips[0].loopPose = false;
                importer.clipAnimations = clips; importer.SaveAndReimport();
            }
            var raw = CreateSource(p, avatar, preview);
            var profilePath = p.Root + "/" + p.name + "Presentation.asset";
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(profilePath);
            if (profile == null) { profile = ScriptableObject.CreateInstance<CharacterPresentationProfile>(); AssetDatabase.CreateAsset(profile, profilePath); }
            profile.SingleShot = true; profile.DualWield = p.id == 2;
            var tables = new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json")));
            var weaponConfig = tables.TbHero.Get(p.id);
            profile.ShotPlaybackSeconds = weaponConfig.FireIntervalFrames / 60f * (p.id == 2 ? 2 : 1) * .9f;
            profile.TurnLeftDuration = Clip(p, p.clips[4]).length; profile.TurnRightDuration = Clip(p, p.clips[5]).length;
            profile.TurnLeftProgress = CombatGirlsBuilder.ExtractTurnCurve(Clip(p, p.clips[4]), raw);
            profile.TurnRightProgress = CombatGirlsBuilder.ExtractTurnCurve(Clip(p, p.clips[5]), raw);
            for (int i = 0; i < 4; i++)
            {
                var clip = Clip(p, p.clips[i]);
                float speed = clip.averageSpeed.magnitude;
                if (speed < .05f) speed = CombatGirlsBuilder.MeasureStrideSpeed(clip, raw, i < 2 ? Vector3.forward : Vector3.right);
                if (!float.IsFinite(speed) || speed < .05f) throw new InvalidOperationException("Cannot measure stride " + p.name + "/" + clip.name);
                profile.WalkPlayback[i] = 5 / speed;
                Report.Add($"{p.name} {p.clips[i]}: speed={speed:F5}, playback={profile.WalkPlayback[i]:F5}");
            }
            UnityEngine.Object.DestroyImmediate(raw);
            foreach (var name in p.clips)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(p.ClipPath(name)); var clips = importer.clipAnimations;
                bool loop = name.Contains("AimWalk") || name.EndsWith("AimIdle");
                clips[0].loopTime = clips[0].loopPose = loop;
                clips[0].lockRootRotation = !name.Contains("AimTurn"); clips[0].keepOriginalOrientation = true;
                clips[0].lockRootPositionXZ = clips[0].lockRootHeightY = true;
                clips[0].keepOriginalPositionXZ = clips[0].keepOriginalPositionY = true;
                if (name.Contains("Shoot")) { clips[0].firstFrame = 0; clips[0].lastFrame = 29; }
                importer.clipAnimations = clips; importer.SaveAndReimport();
            }
            var character = CreateSource(p, avatar, preview);
            try
            {
                var animator = character.GetComponent<Animator>();
                string firstDeath = p.clips[^2], secondDeath = p.clips[^1];
                float firstDirection = DeathOffset(character, Clip(p, firstDeath));
                float secondDirection = DeathOffset(character, Clip(p, secondDeath));
                if (firstDirection * secondDirection >= 0) throw new InvalidOperationException($"Death directions ambiguous {p.name}: {firstDirection}, {secondDirection}");
                string forward = firstDirection > 0 ? firstDeath : secondDeath, backward = firstDirection < 0 ? firstDeath : secondDeath;
                Report.Add($"{p.name}: DieForward={forward}, DieBackward={backward}; signed torso offset={firstDirection:F4}/{secondDirection:F4}");
                profile.DieForwardDuration = Clip(p, forward).length; profile.DieBackwardDuration = Clip(p, backward).length;
                profile.ShootDuration = Clip(p, p.clips[7]).length;
                animator.Rebind(); Clip(p, p.clips[6]).SampleAnimation(character, 0);
                // The reference uses the same original materials and source scene part selection.
                Capture(p, character, "source", preview);
                ConvertMaterials(p, character);
                var rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                var leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                var rightSocket = Child("RightWeaponSocket", rightHand);
                var leftSocket = p.id == 2 ? Child("LeftWeaponSocket", leftHand) : null;
                var weapon = MakeWeapon(p, character, rightSocket, leftHand, preview);
                var weaponPrefab = PrefabUtility.SaveAsPrefabAsset(weapon.gameObject, p.WeaponPath);
                var view = character.AddComponent<InkCharacterView>();
                view.Animator = animator; view.Profile = profile; view.WeaponSocket = rightSocket; view.LeftWeaponSocket = leftSocket;
                view.AimReference = Child("AimReference", animator.GetBoneTransform(HumanBodyBones.Chest)); view.AimReference.rotation = Quaternion.identity;
                var rifle = Load<GameObject>(CombatGirlsBuilder.CharacterPath).GetComponent<InkCharacterView>();
                view.TeamMarker = UnityEngine.Object.Instantiate(rifle.TeamMarker, character.transform, false);
                view.SwimEffect = UnityEngine.Object.Instantiate(rifle.SwimEffect, character.transform, false);
                view.BindWeapon(weapon, weaponPrefab);
                profile.CameraPivot = new Vector3(0, animator.GetBoneTransform(HumanBodyBones.Head).position.y - .04f, 0);
                profile.AimPivot = new Vector3(0, animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position.y, 0);
                profile.MuzzlePosition = character.transform.InverseTransformPoint(view.Nozzle.position);
                if (p.id == 2) profile.LeftMuzzlePosition = character.transform.InverseTransformPoint(view.LeftNozzle.position);
                animator.runtimeAnimatorController = Controller(p, character, profile, forward, backward);
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var t in character.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
                character.name = p.name + "Visual";
                Report.Add($"{p.name}: avatar={p.avatar}, camera={profile.CameraPivot:R}, muzzleR={profile.MuzzlePosition:R}, muzzleL={profile.LeftMuzzlePosition:R}");
                Clip(p, p.clips[6]).SampleAnimation(character, 0); Capture(p, character, "target", preview);
                foreach (var death in new[] { forward, backward })
                { Clip(p, death).SampleAnimation(character, Clip(p, death).length - .001f); CaptureOne(character, preview, ReportRoot + "/Screenshots/" + p.name + "-" + death + ".png", new Vector3(2.6f,1.5f,3), new Vector3(0,.6f,0)); }
                animator.Rebind();
                PrefabUtility.SaveAsPrefabAsset(character, p.CharacterPath);
                EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
            }
            finally { UnityEngine.Object.DestroyImmediate(character); }
        }

        static float DeathOffset(GameObject go, AnimationClip clip)
        {
            var animator = go.GetComponent<Animator>(); animator.Rebind(); clip.SampleAnimation(go, clip.length - .001f);
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            var feet = (animator.GetBoneTransform(HumanBodyBones.LeftFoot).position + animator.GetBoneTransform(HumanBodyBones.RightFoot).position) / 2;
            return go.transform.InverseTransformVector(chest.position - feet).z;
        }
        static Transform Child(string name, Transform parent)
        { var go = new GameObject(name); go.transform.SetParent(parent, false); return go.transform; }
        static bool Under(Transform t, string fragment)
        { for (; t != null; t = t.parent) if (t.name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }

        static HeroWeaponBindings MakeWeapon(Pack p, GameObject character, Transform socket, Transform leftHand, Scene scene)
        {
            var allMeshes = character.GetComponentsInChildren<MeshFilter>(true);
            var gunMeshes = allMeshes.Where(f => p.id == 5 ? Under(f.transform, "Weapon_Rocket_Launcher") && !Under(f.transform, "Bullet")
                : Under(f.transform, "add_weapon_r") || (p.id == 2 && Under(f.transform, "add_weapon_l"))).ToArray();
            Report.Add(p.name + " weapon meshes: " + string.Join(",", gunMeshes.Select(f => f.name)));
            if (gunMeshes.Length == 0) throw new InvalidOperationException("No weapon meshes " + p.name + ": " + string.Join(",", allMeshes.Select(f => f.name)));
            var root = Child(p.weapon, socket);
            var binding = root.gameObject.AddComponent<HeroWeaponBindings>(); binding.SupportLeftHand = p.id != 2;
            Transform left = p.id == 2 ? Child("LeftPistol", leftHand) : null;
            foreach (var mesh in gunMeshes)
            {
                bool isLeft = p.id == 2 && Under(mesh.transform, "add_weapon_l");
                var clone = UnityEngine.Object.Instantiate(mesh.gameObject); SceneManager.MoveGameObjectToScene(clone, scene);
                clone.transform.SetPositionAndRotation(mesh.transform.position, mesh.transform.rotation); clone.transform.localScale = mesh.transform.lossyScale;
                clone.transform.SetParent(isLeft ? left : root, true);
                foreach (var component in clone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(component);
                foreach (var component in clone.GetComponentsInChildren<ParentConstraint>(true)) UnityEngine.Object.DestroyImmediate(component);
                // Preserve inactive alternative weapon meshes exactly as selected in the source scene.
                clone.SetActive(mesh.gameObject.activeInHierarchy);
                mesh.gameObject.SetActive(false);
            }
            foreach (var constraint in character.GetComponentsInChildren<ParentConstraint>(true)) UnityEngine.Object.DestroyImmediate(constraint);
            binding.Nozzle = Child("Muzzle", root);
            binding.Nozzle.position = MeasureActiveMuzzle(root); binding.Nozzle.rotation = Quaternion.identity;
            if (left != null)
            {
                binding.LeftNozzle = Child("LeftMuzzle", left);
                binding.LeftNozzle.position = MeasureActiveMuzzle(left); binding.LeftNozzle.rotation = Quaternion.identity;
                binding.LeftPart = left;
                left.SetParent(root, false); // Stored in left-hand local space; binder mounts it to the other hand.
            }
            else
            {
                binding.LeftGrip = Child("LeftGrip", root);
                binding.LeftGrip.SetPositionAndRotation(leftHand.position, leftHand.rotation);
            }
            foreach (var mesh in gunMeshes) UnityEngine.Object.DestroyImmediate(mesh.gameObject);
            return binding;
        }
        static Vector3 MeasureActiveMuzzle(Transform root)
        {
            var vertices = root.GetComponentsInChildren<MeshFilter>(false).SelectMany(f => f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            if (vertices.Length == 0) throw new InvalidOperationException("No active gun geometry " + root.name);
            float tip = vertices.Max(v => v.z); var end = vertices.Where(v => v.z >= tip - .012f).ToArray();
            return end.Aggregate(Vector3.zero, (a,b) => a+b) / end.Length + Vector3.forward * .005f;
        }

        static GameObject CreateSource(Pack p, Avatar avatar, Scene scene)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(p.Art + "/" + p.prefab), scene);
            ApplySceneAppearance(p, go);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach (var collider in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var animator = go.GetComponent<Animator>(); animator.avatar = avatar; animator.applyRootMotion = false;
            return go;
        }
        static void ApplySceneAppearance(Pack p, GameObject go)
        {
            // Scene overrides select clothing and expressions. No demo scripts or controllers are imported.
            string sourceRoot = Environment.GetEnvironmentVariable("COMBAT_GIRLS_SOURCE") ?? "D:/XPHUNITY/CombatGirls/Assets/CombatGirlsCharacterPack";
            string scene = File.ReadAllText(Path.Combine(sourceRoot, p.pack, p.scene));
            var entries = Regex.Matches(scene, @"target: \{fileID: (-?\d+), guid: ([a-f0-9]{32}), type: \d+\}\s+propertyPath: ([^\r\n]+)\s+value:([^\r\n]*)\s+objectReference: \{([^\r\n]+)\}");
            var objects = go.GetComponentsInChildren<Transform>(true).SelectMany(t => new UnityEngine.Object[] {t.gameObject}.Concat(t.GetComponents<Component>())).Where(o => o != null).ToArray();
            foreach (var obj in objects)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
                if (source == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id)) continue;
                foreach (Match match in entries)
                {
                    if (match.Groups[1].Value != id.ToString() || match.Groups[2].Value != guid) continue;
                    string property = match.Groups[3].Value.Trim();
                    if (property != "m_IsActive" && !property.StartsWith("m_Materials.Array.data[") && !property.StartsWith("m_BlendShapeWeights.Array.data[")) continue;
                    var serialized = new SerializedObject(obj); var value = serialized.FindProperty(property); if (value == null) continue;
                    if (value.propertyType == SerializedPropertyType.Boolean) value.boolValue = match.Groups[4].Value.Trim() == "1";
                    else if (value.propertyType == SerializedPropertyType.Float) value.floatValue = float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                    else if (value.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        var target = Regex.Match(match.Groups[5].Value, "guid: ([a-f0-9]{32})");
                        if (target.Success)
                        {
                            string path=AssetDatabase.GUIDToAssetPath(target.Groups[1].Value);
                            if(path.StartsWith(CombatGirlsBuilder.SourceRoot+"/",StringComparison.Ordinal))
                            {
                                string fork=CombatGirlsBuilder.SourceRoot+"/SharedFourHeroes/"+path[(CombatGirlsBuilder.SourceRoot.Length+1)..];
                                if(File.Exists(fork))path=fork;
                            }
                            value.objectReferenceValue=AssetDatabase.LoadAssetAtPath<Material>(path);
                        }
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }
        static void ConvertMaterials(Pack p, GameObject go)
        {
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var array = renderer.sharedMaterials;
                for (int i = 0; i < array.Length; i++)
                {
                    var source = array[i]; if (source == null) throw new InvalidOperationException("Missing source material " + renderer.name);
                    if (!Materials.TryGetValue(source, out var material))
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
                        string path = p.Root + "/Materials/" + source.name + "_" + guid[..8] + ".mat";
                        material = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (material == null) { material = new Material(source); AssetDatabase.CreateAsset(material, path); }
                        else EditorUtility.CopySerialized(source, material);
                        if (material.shader.name == "Toon/Toon") material.SetFloat("_Is_Filter_LightColor", 1);
                        EditorUtility.SetDirty(material); Materials.Add(source, material);
                        Report.Add($"{p.name} material {source.name}: {source.shader.name}; " + (material.shader.name == "Toon/Toon" ? "only _Is_Filter_LightColor=1" : "unchanged"));
                    }
                    array[i] = material;
                }
                renderer.sharedMaterials = array;
            }
        }

        static AnimatorController Controller(Pack p, GameObject character, CharacterPresentationProfile profile, string forward, string backward)
        {
            string path = p.Root + "/Animations/" + p.name + "Combat.controller";
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (c == null) { c = new AnimatorController(); AssetDatabase.CreateAsset(c,path); }
            else
            {
                c.layers = Array.Empty<AnimatorControllerLayer>(); c.parameters = Array.Empty<AnimatorControllerParameter>();
                foreach (var child in AssetDatabase.LoadAllAssetsAtPath(path)) if (child != c) UnityEngine.Object.DestroyImmediate(child,true);
            }
            foreach (var parameter in new[] {"MoveX","MoveY","MovePlayback"}) c.AddParameter(parameter,AnimatorControllerParameterType.Float);
            c.AddLayer("Base Layer"); var machine = c.layers[0].stateMachine;
            var locomotion = machine.AddState("Locomotion"); machine.defaultState = locomotion; locomotion.writeDefaultValues = false;
            locomotion.speedParameter = "MovePlayback"; locomotion.speedParameterActive = true;
            var tree = new BlendTree { name="AimWalkFourDirections",blendType=BlendTreeType.SimpleDirectional2D,blendParameter="MoveX",blendParameterY="MoveY",useAutomaticThresholds=false };
            AssetDatabase.AddObjectToAsset(tree,c); locomotion.motion=tree; tree.AddChild(Clip(p,p.clips[6]),Vector2.zero);
            var directions = new[] {Vector2.up,Vector2.down,Vector2.left,Vector2.right};
            for(int i=0;i<4;i++) tree.AddChild(Clip(p,p.clips[i]),directions[i]);
            var children=tree.children;for(int i=0;i<4;i++)children[i+1].timeScale=profile.WalkPlayback[i];tree.children=children;
            foreach(var pair in new[]{("Air",p.clips[6]),("TurnLeft",p.clips[4]),("TurnRight",p.clips[5]),("DieForward",forward),("DieBackward",backward)})
            {var state=machine.AddState(pair.Item1);state.motion=Clip(p,pair.Item2);state.writeDefaultValues=false;}
            for(int hand=0;hand<(p.id==2?2:1);hand++)
            {
                string maskPath=p.Root+"/Animations/"+(p.id==2?(hand==0?"RightArm":"LeftArm"):"UpperBody")+".mask";
                var mask=AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
                if(mask==null){mask=new AvatarMask();AssetDatabase.CreateAsset(mask,maskPath);}
                for(int part=0;part<(int)AvatarMaskBodyPart.LastBodyPart;part++)
                {
                    var body=(AvatarMaskBodyPart)part;
                    bool active=p.id==2 ? hand==0 ? body==AvatarMaskBodyPart.RightArm||body==AvatarMaskBodyPart.RightFingers : body==AvatarMaskBodyPart.LeftArm||body==AvatarMaskBodyPart.LeftFingers
                        : body==AvatarMaskBodyPart.Body||body==AvatarMaskBodyPart.Head||body==AvatarMaskBodyPart.LeftArm||body==AvatarMaskBodyPart.RightArm||body==AvatarMaskBodyPart.LeftFingers||body==AvatarMaskBodyPart.RightFingers;
                    mask.SetHumanoidBodyPartActive(body,active);
                }
                mask.transformCount=0;mask.AddTransformPath(character.transform,true);
                for(int i=0;i<mask.transformCount;i++)
                { string bone=mask.GetTransformPath(i); mask.SetTransformActive(i,p.id==2 ? bone.Contains(hand==0?"/clavicle_r":"/clavicle_l") : bone.Contains("/spine_01")); }
                EditorUtility.SetDirty(mask);c.AddLayer(hand==0?"Shooting":"ShootingLeft");var layers=c.layers;
                layers[0].defaultWeight=1;layers[hand+1].avatarMask=mask;layers[hand+1].defaultWeight=0;layers[hand+1].blendingMode=AnimatorLayerBlendingMode.Override;
                var shot=layers[hand+1].stateMachine.AddState("Shot");shot.motion=Clip(p,p.clips[7+hand]);shot.speed=((AnimationClip)shot.motion).length/profile.ShotPlaybackSeconds;shot.writeDefaultValues=false;
                layers[hand+1].stateMachine.defaultState=shot;c.layers=layers;
            }
            EditorUtility.SetDirty(c);return c;
        }

        static void Capture(Pack p, GameObject character, string suffix, Scene scene)
        {
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            var marker=character.GetComponent<InkCharacterView>()?.TeamMarker;
            bool markerEnabled=marker!=null&&marker.enabled;if(marker!=null)marker.enabled=false;
            try
            {
            foreach(var view in new[]{("front",new Vector3(0,1.2f,3.8f),new Vector3(0,1,0)),("side",new Vector3(3.8f,1.2f,0),new Vector3(0,1,0)),("back",new Vector3(0,1.2f,-3.8f),new Vector3(0,1,0)),("face",new Vector3(0,1.65f,1),new Vector3(0,1.6f,0))})
                CaptureOne(character,scene,ReportRoot+"/Screenshots/"+p.name+"-"+suffix+"-"+view.Item1+".png",view.Item2,view.Item3);
            }
            finally{if(marker!=null)marker.enabled=markerEnabled;}
        }
        public static void CaptureComparisons()
        {
            var scene=EditorSceneManager.NewPreviewScene();
            try
            {
                foreach(var p in Definitions)
                {
                    var prefab=Load<GameObject>(p.CharacterPath);
                    var source=CreateSource(p,prefab.GetComponent<Animator>().avatar,scene);
                    Clip(p,p.clips[6]).SampleAnimation(source,0);Capture(p,source,"source",scene);UnityEngine.Object.DestroyImmediate(source);
                    var target=(GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);
                    Clip(p,p.clips[6]).SampleAnimation(target,0);Capture(p,target,"target",scene);UnityEngine.Object.DestroyImmediate(target);
                }
            }
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
        static void CaptureOne(GameObject character, Scene scene, string path, Vector3 position, Vector3 target)
        {
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            var cameraObject=new GameObject("ComparisonCamera",typeof(Camera));SceneManager.MoveGameObjectToScene(cameraObject,scene);
            var lightObject=new GameObject("ComparisonLight",typeof(Light));SceneManager.MoveGameObjectToScene(lightObject,scene);
            var camera=cameraObject.GetComponent<Camera>();camera.scene=scene;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.36f,.39f,.43f);camera.fieldOfView=35;
            camera.transform.position=position;camera.transform.LookAt(target);camera.nearClipPlane=.05f;
            var light=lightObject.GetComponent<Light>();light.type=LightType.Directional;light.intensity=1;light.color=Color.white;light.transform.rotation=Quaternion.Euler(35,-25,0);
            var rt=new RenderTexture(800,800,24,RenderTextureFormat.ARGB32);var prior=RenderTexture.active;
            try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;var texture=new Texture2D(800,800,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,800,800),0,0);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);}
            finally{camera.targetTexture=null;RenderTexture.active=prior;UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(cameraObject);UnityEngine.Object.DestroyImmediate(lightObject);}
        }
        public static void Validate()
        {
            var tables=new cfg.Tables(n=>SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/"+n+".json")));
            GameplayConfig.Validate(tables);int count=0;
            foreach(var p in Definitions)
            {
                var character=Load<GameObject>(p.CharacterPath);var view=character.GetComponent<InkCharacterView>();var weapon=Load<GameObject>(p.WeaponPath);
                _=new HeroContent(tables.TbHero.Get(p.id),character,weapon);
                if(!view.Animator.avatar.isValid||view.Animator.applyRootMotion)throw new InvalidOperationException("Avatar/root motion invalid "+p.name);
                foreach(var t in character.GetComponentsInChildren<Transform>(true))if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)!=0)throw new InvalidOperationException("Missing component "+t.name);
                var clips=view.Animator.runtimeAnimatorController.animationClips.Distinct().ToArray();count+=clips.Length;
                if(clips.Length!=p.clips.Length)throw new InvalidOperationException("Wrong clip count "+p.name+": "+clips.Length);
                foreach(var clip in clips)
                {
                    string name=Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip));
                    if(!p.clips.Contains(name)||clip.isLooping!=(name.Contains("AimWalk")||name.EndsWith("AimIdle")))throw new InvalidOperationException("Clip whitelist/loop mismatch "+name);
                }
                foreach(var dependency in AssetDatabase.GetDependencies(p.CharacterPath,true))if(dependency.Contains("_Incoming")||dependency.Contains("Jammo"))throw new InvalidOperationException("Old dependency "+dependency);
                Report.Add(p.name+": bindings, humanoid, whitelist, loops and missing scripts PASS");
            }
            if(count!=41)throw new InvalidOperationException("Expected 41 new clips");
        }
    }
}
#endif
