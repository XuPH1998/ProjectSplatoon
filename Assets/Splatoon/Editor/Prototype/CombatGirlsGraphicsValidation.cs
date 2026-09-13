#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class CombatGirlsGraphicsValidation
    {
        const string Output = "Docs/CombatGirls/Screenshots";
        static readonly List<string> Report = new();
        public static void CaptureTrainingAndExit()
        {
            try
            {
                typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                    new cfg.Tables(n => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + n + ".json"))));
                CombatGirlsBuilder.ValidateInstalled();
                EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity");
                var go = (GameObject)PrefabUtility.InstantiatePrefab(CombatGirlsBuilder.Load<GameObject>(CombatGirlsBuilder.CharacterPath));
                var view = go.GetComponent<InkCharacterView>(); view.InitializeBindings();
                var camera = Camera.main; camera.enabled = false; camera.fieldOfView = 35; camera.aspect = 1;
                var state = new PlayerSnapshot { Health = 100, Ink = 100, Grounded = true, Team = 1 };
                view.Present(state, 1f / 60, 0); view.Animator.Update(0);
                Physics.SyncTransforms();
                Vector3 bright = FindCaptureFloor(false), shadow = FindCaptureFloor(true);
                foreach (var pose in new[] { ("bright-front", bright, 0f, 0f, false), ("bright-back", bright, 0f, 180f, false),
                    ("bright-face", bright, 0f, 15f, true), ("shadow-front", shadow, 0f, 0f, false),
                    ("aim-up", bright, -65f, 40f, false), ("aim-down", bright, 75f, 40f, false) })
                {
                    go.transform.position = pose.Item2; state.Pitch = pose.Item3;
                    for (int i = 0; i < 60; i++) { view.Present(state, 1f / 60, i / 60.0); view.Animator.Update(1f / 60); view.ApplyAim(); }
                    Vector3 center = go.transform.position + Vector3.up * (pose.Item5 ? 1.36f : .8f);
                    camera.transform.position = center + Quaternion.Euler(0, pose.Item4, 0) * new Vector3(0, pose.Item5 ? .015f : .15f, pose.Item5 ? 1.1f : 3.4f);
                    camera.transform.LookAt(center); Render(camera, "training-" + pose.Item1);
                }
                File.WriteAllText("Docs/CombatGirls/training-graphics-validation.txt", $"Final gameplay materials: bright front/back/face, occluder shadow, pitch -65/+75; actual TrainingGround lights and ink renderer. Captured in Editor using evaluated Animator and baked per-pose skinning.\nBright floor={bright:R}; shadow floor={shadow:R}; shadow classification uses a ray towards the scene sun.\n");
                Debug.Log("[CombatGirls-GPU] Training light/shadow/pitch captures complete"); EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        internal static Vector3 FindCaptureFloor(bool shadow)
        {
            var sun = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).First(l => l.type == LightType.Directional);
            for (int z = -20; z <= 20; z += 2)
            for (int x = -10; x <= 10; x += 2)
            {
                if (!Physics.Raycast(new Vector3(x, .5f, z), Vector3.down, out var ground, 2, ~(1 << 8)) || ground.normal.y < .9f) continue;
                Vector3 feet = ground.point + Vector3.up * .02f, center = feet + Vector3.up;
                if (Physics.CheckCapsule(feet + Vector3.up * .4f, feet + Vector3.up * 1.5f, .3f, ~(1 << 8))) continue;
                if (Physics.Raycast(center, -sun.transform.forward, 100, ~(1 << 8)) != shadow) continue;
                if (new[] { 0f, 40f, 180f }.Any(yaw => Physics.Raycast(center, Quaternion.Euler(0, yaw, 0) * Vector3.forward, 3.6f, ~(1 << 8)))) continue;
                return feet;
            }
            throw new InvalidOperationException("No unobstructed capture floor: shadow=" + shadow);
        }
        public static void RebuildAndRun() { CombatGirlsBuilder.Install(); Run(); }
        public static void Run()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("GPU required");
                var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + name + ".json")));
                typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
                GameplayConfig.Validate(); CombatGirlsBuilder.ValidateInstalled();
                Directory.CreateDirectory(Output); Report.Clear();
                EditorSceneManager.OpenScene(CombatGirlsBuilder.PreviewPath);
                var target = UnityEngine.Object.FindFirstObjectByType<InkCharacterView>();
                var camera = Camera.main; camera.enabled = false;
                var data = camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing = false;
                target.TeamMarker.enabled = false; target.SwimEffect.gameObject.SetActive(false);
                var source = CombatGirlsBuilder.CreateSourceCharacter();
                var constraint = source.GetComponentsInChildren<ParentConstraint>(true).First();
                var weapon = constraint.transform; UnityEngine.Object.DestroyImmediate(constraint);
                var socket = source.GetComponentsInChildren<Transform>(true).First(t => t.name == "Hand_R_Socket");
                weapon.SetParent(socket, false); weapon.localPosition = Vector3.zero; weapon.localRotation = Quaternion.identity;
                var clip = CombatGirlsBuilder.Clip("R_AimIdle");
                clip.SampleAnimation(source, .3f); clip.SampleAnimation(target.gameObject, .3f);
                foreach (var angle in new[] { ("front", 0f), ("side", 90f), ("back", 180f), ("face", 0f) })
                {
                    bool face = angle.Item1 == "face";
                    Vector3 center = new(0, face ? 1.36f : .8f, 0);
                    camera.transform.position = center + Quaternion.Euler(0, angle.Item2, 0) * new Vector3(0, face ? .015f : .15f, face ? 1.1f : 3.4f);
                    camera.transform.LookAt(center);
                    source.SetActive(true); target.gameObject.SetActive(false); Render(camera, "source-" + angle.Item1);
                    source.SetActive(false); target.gameObject.SetActive(true); Render(camera, "target-" + angle.Item1);
                }
                UnityEngine.Object.DestroyImmediate(source);
                target.InitializeBindings();
                var state = new PlayerSnapshot { Health = 100, Ink = 100, Grounded = true, Team = 1 };
                foreach (var direction in new[] { ("idle", Vector3.zero), ("forward", Vector3.forward), ("backward", Vector3.back), ("left", Vector3.left), ("right", Vector3.right) })
                {
                    state.Velocity = direction.Item2 * 5; state.Firing = false;
                    Advance(target, state, 1, 60);
                    camera.transform.position = new Vector3(2.2f, 1.6f, 2.7f); camera.transform.LookAt(new Vector3(0, .85f, 0));
                    Render(camera, "runtime-" + direction.Item1);
                    CheckAim(target, state, direction.Item1);
                }
                state.Firing = true; state.FireStartedAt = 2; state.Velocity = Vector3.left * 5;
                Advance(target, state, 2, 240); Render(camera, "runtime-moving-autoshoot"); CheckAim(target, state, "moving-autoshoot");
                Require(target.Animator.GetCurrentAnimatorStateInfo(1).normalizedTime > 2, "AutoShoot did not continue looping");
                CheckShootLoop(target, state);
                state.Firing = false; Advance(target, state, 6, 12); Require(target.Animator.GetLayerWeight(1) < .001f, "Shot layer did not stop");
                state.Velocity = Vector3.zero; state.Yaw = 80; state.Pitch = -30; state.BodyYaw = 45; state.TurnDirection = 1; state.TurnStartedAt = 7;
                Advance(target, state, 7.4, 1); Render(camera, "runtime-turn-aim"); CheckAim(target, state, "turn-aim");
                state.TurnDirection = 0; state.Grounded = false; state.Velocity = new Vector3(3, 5, 0); Advance(target, state, 8, 12); Render(camera, "runtime-air");
                Require(target.Animator.GetCurrentAnimatorStateInfo(0).IsName("Air"), "Air did not use held rifle pose");
                state.Grounded = true; state.Swimming = true; Advance(target, state, 9, 2);
                Require(!target.Animator.enabled, "Swimming did not hide character animation");
                state.Health = 0; state.Swimming = false; state.Yaw = state.BodyYaw = state.Pitch = 0; state.Velocity = Vector3.zero; state.DiedAt = 10; state.DeathDirection = 0;
                Advance(target, state, 10, 60); Render(camera, "runtime-die-backward");
                Require(target.Animator.enabled && target.Animator.GetLayerWeight(1) == 0, "Death hidden or still firing");
                Advance(target, state, 11, 120); Render(camera, "runtime-dead-hold");
                state.DiedAt = 14; state.DeathDirection = 1; Advance(target, state, 14, 100); Render(camera, "runtime-die-forward");
                state.Health = 100; state.DiedAt = 0; state.Revision++; Advance(target, state, 17, 30); Render(camera, "runtime-respawn"); CheckAim(target, state, "respawn");
                Require(target.Animator.GetCurrentAnimatorStateInfo(0).IsName("Locomotion"), "Respawn did not restore locomotion");
                File.WriteAllLines("Docs/CombatGirls/graphics-validation.txt", Report);
                Debug.Log("[CombatGirls-GPU] Source/target captures, directional poses, continuous fire, aim/IK, death/respawn PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }

        static void Advance(InkCharacterView target, PlayerSnapshot state, double start, int frames)
        {
            target.transform.rotation = Quaternion.Euler(0, state.BodyYaw, 0);
            for (int i = 0; i < frames; i++)
            {
                target.Present(state, 1f / 60, start + i / 60.0);
                if (target.Animator.enabled) target.Animator.Update(1f / 60);
                target.ApplyAim();
            }
            Require(target.transform.position.sqrMagnitude < .00001f, "Animation moved the character root");
        }
        static void CheckAim(InkCharacterView view, PlayerSnapshot state, string label)
        {
            var hand = view.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            float error = Vector3.Distance(hand.position, view.LeftGrip.position);
            float angle = Vector3.Angle(view.Nozzle.forward, Quaternion.Euler(state.Pitch, state.Yaw, 0) * Vector3.forward);
            Report.Add($"{label}: leftGripError={error:F6}m; muzzleAimError={angle:F4}deg");
            Require(error < .04f && angle < 2, label + " aim/IK error: " + error + ", " + angle);
        }
        static void Require(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
        static void CheckShootLoop(InkCharacterView view, PlayerSnapshot state)
        {
            var bones = new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand }
                .Select(view.Animator.GetBoneTransform).ToArray();
            var previous = bones.Select(b => b.localRotation).ToArray();
            float oldTime = view.Animator.GetCurrentAnimatorStateInfo(1).normalizedTime;
            float seamMax = 0, interiorMax = 0; int wraps = 0;
            // Idle lower body isolates the authored shot seam from changing gait.
            state.Velocity = Vector3.zero;
            for (int frame = 0; frame < 300; frame++)
            {
                Advance(view, state, 6 + frame / 60.0, 1);
                float time = view.Animator.GetCurrentAnimatorStateInfo(1).normalizedTime;
                bool seam = Mathf.Floor(time) != Mathf.Floor(oldTime);
                if (seam) wraps++;
                for (int i = 0; i < bones.Length; i++)
                {
                    float angle = Quaternion.Angle(previous[i], bones[i].localRotation);
                    if (seam) seamMax = Mathf.Max(seamMax, angle);
                    else if (frame > 15) interiorMax = Mathf.Max(interiorMax, angle);
                    previous[i] = bones[i].localRotation;
                }
                oldTime = time;
            }
            Report.Add($"AutoShoot 300-frame arm/wrist seam: wraps={wraps}, maxSeam={seamMax:F4}deg, maxInterior={interiorMax:F4}deg");
            Require(wraps >= 4 && seamMax < Mathf.Max(5, interiorMax * 2), "AutoShoot arm/wrist seam discontinuity");
        }
        internal static void Render(Camera camera, string name, string output = Output)
        {
            // Multiple diagnostic poses are sampled within one editor frame. Bake
            // each mesh so GPU skinning cannot reuse a previous pose's frame cache.
            var baked = new List<(SkinnedMeshRenderer skin, GameObject go, Mesh mesh)>();
            foreach (var skin in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Where(s => s.enabled))
            {
                var mesh = new Mesh(); skin.BakeMesh(mesh);
                var go = new GameObject("CapturePose", typeof(MeshFilter), typeof(MeshRenderer));
                go.layer = skin.gameObject.layer; go.transform.SetParent(skin.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh; var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = skin.sharedMaterials; renderer.shadowCastingMode = skin.shadowCastingMode; renderer.receiveShadows = skin.receiveShadows;
                skin.enabled = false; baked.Add((skin, go, mesh));
            }
            var target = RenderTexture.GetTemporary(960, 960, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                Directory.CreateDirectory(output);
                File.WriteAllBytes(output + "/" + name + ".png", image.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(image);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                foreach (var item in baked) { item.skin.enabled = true; UnityEngine.Object.DestroyImmediate(item.go); UnityEngine.Object.DestroyImmediate(item.mesh); }
            }
        }
    }
}
#endif
