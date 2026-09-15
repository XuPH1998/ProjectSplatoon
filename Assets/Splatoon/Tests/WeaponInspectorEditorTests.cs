#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed class WeaponInspectorEditorTests
    {
        const string TemporaryAsset = "Assets/WeaponSecondsInspectorValidation.asset";
        [UnityTest] public IEnumerator ChineseInspectorDrawsAndSerializedEditsUndoAndPersist()
        {
            Assert.That(AssetDatabase.LoadMainAssetAtPath(TemporaryAsset), Is.Null, "Do not overwrite any existing asset");
            var source = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/MachineGunGirl/MachineGunGirlWeaponConfig.asset");
            var copy = UnityEngine.Object.Instantiate(source);
            AssetDatabase.CreateAsset(copy, TemporaryAsset);
            UnityEditor.Editor editor = null;
            WeaponInspectorTestWindow window = null;
            try
            {
                editor = UnityEditor.Editor.CreateEditor(copy);
                Assert.That(editor.GetType().Name, Is.EqualTo("WeaponConfigAssetEditor"));
                double original = copy.splatlingMinChargeSeconds;
                var serialized = editor.serializedObject;
                var time = serialized.FindProperty("splatlingMinChargeSeconds");
                var mode = serialized.FindProperty("fireMode");
                var muzzle = serialized.FindProperty("muzzleMode");
                Assert.That(mode.propertyType, Is.EqualTo(SerializedPropertyType.Enum));
                Assert.That(muzzle.propertyType, Is.EqualTo(SerializedPropertyType.Enum));
                Undo.IncrementCurrentGroup();
                time.doubleValue = .137123456789;
                serialized.ApplyModifiedProperties();
                Undo.FlushUndoRecordObjects();
                Assert.That(copy.splatlingMinChargeSeconds, Is.EqualTo(.137123456789));
                Undo.PerformUndo(); serialized.Update();
                Assert.That(copy.splatlingMinChargeSeconds, Is.EqualTo(original));
                Undo.PerformRedo(); serialized.Update();
                Assert.That(copy.splatlingMinChargeSeconds, Is.EqualTo(.137123456789));
                AssetDatabase.SaveAssetIfDirty(copy);
                AssetDatabase.ImportAsset(TemporaryAsset, ImportAssetOptions.ForceUpdate);
                var loaded = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(TemporaryAsset);
                Assert.That(loaded.splatlingMinChargeSeconds, Is.EqualTo(.137123456789));
                var yaml = File.ReadAllText(TemporaryAsset);
                StringAssert.Contains("splatlingMinChargeSeconds: 0.137123456789", yaml);
                Assert.That(loaded.fireMode, Is.EqualTo(WeaponFireMode.Splatling));
                if (!Application.isBatchMode)
                {
                    window = ScriptableObject.CreateInstance<WeaponInspectorTestWindow>();
                    window.Editor = editor;
                    window.titleContent = new GUIContent("武器配置检查");
                    window.position = new Rect(40, 60, 900, 900);
                    window.ShowUtility();
                    foreach (float scroll in new[] { 0f, 650f, 1350f })
                    {
                        window.Scroll = new Vector2(0, scroll);
                        for (int frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                        Assert.That(window.DrawCount, Is.GreaterThan(0));
                        Assert.That(window.DrawException, Is.Null);
                        Capture(window, "inspector-" + scroll);
                    }
                }
                else yield return null;
            }
            finally
            {
                if (window != null) window.Close();
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                Undo.ClearUndo(copy);
                AssetDatabase.DeleteAsset(TemporaryAsset);
            }
        }
        static void Capture(EditorWindow window, string name)
        {
            var rect = window.position;
            int width = Mathf.RoundToInt(rect.width * EditorGUIUtility.pixelsPerPoint);
            int height = Mathf.RoundToInt(rect.height * EditorGUIUtility.pixelsPerPoint);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            target.Create();
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            var raw = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            bool srgb = GL.sRGBWrite;
            try
            {
                // Read this EditorWindow's own rendered content, even when another
                // application is foreground. Never capture the desktop behind it.
                // The built-in CaptureEditorWindow uses a point-sized grab rectangle;
                // provide device pixels here so high-DPI windows are not cropped.
                var parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(window);
                var grab = parent.GetType().GetMethod("GrabPixels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(grab, Is.Not.Null);
                grab.Invoke(parent, new object[] { raw, new Rect(0, 0, width, height) });
                var material = (Material)EditorGUIUtility.Load("SceneView/BlitSceneViewCapture.mat");
                Assert.That(material, Is.Not.Null);
                GL.sRGBWrite = false;
                Graphics.Blit(raw, target, material);
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                Directory.CreateDirectory("Reports/WeaponSeconds");
                File.WriteAllBytes("Reports/WeaponSeconds/" + name + ".png", texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = srgb;
                RenderTexture.ReleaseTemporary(raw);
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release(); UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
    public sealed class WeaponInspectorTestWindow : EditorWindow
    {
        public UnityEditor.Editor Editor;
        public Vector2 Scroll;
        public int DrawCount;
        public Exception DrawException;
        void OnGUI()
        {
            if (Editor == null) return;
            Scroll = EditorGUILayout.BeginScrollView(Scroll);
            try { Editor.OnInspectorGUI(); if (Event.current.type == EventType.Repaint) DrawCount++; }
            catch (Exception error) { DrawException = error; throw; }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }
}
#endif
