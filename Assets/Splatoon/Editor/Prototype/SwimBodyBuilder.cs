using System;
using System.Collections.Generic;
using System.IO;
using Splatoon.Combat;
using Splatoon.Prototype;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class SwimBodyBuilder
    {
        public const string Folder = "Assets/GameResource/Characters/Shared/Swim";
        public const string PrefabPath = Folder + "/SwimBody.prefab";
        const string PlayerPath = "Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab";
        static SwimBodyBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            const string request = "Temp/SwimAdjustment/install-assets";
            if (!File.Exists(request)) return;
            File.Delete(request);
            Directory.CreateDirectory("Reports/SwimAdjustment");
            try { Install(); File.WriteAllText("Reports/SwimAdjustment/assets-result.txt", "PASS"); }
            catch (Exception e) { File.WriteAllText("Reports/SwimAdjustment/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }

        [MenuItem("喷墨对战/内容/安装无色潜墨模型")]
        public static void Install()
        {
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            var mesh = CreateMesh();
            string meshPath = Folder + "/SwimCapsule.asset";
            var savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (savedMesh == null) { AssetDatabase.CreateAsset(mesh, meshPath); savedMesh = mesh; }
            else { EditorUtility.CopySerialized(mesh, savedMesh); Object.DestroyImmediate(mesh); }
            string materialPath = Folder + "/SwimBody.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "SwimBody" };
                material.SetColor("_BaseColor", PrototypeArena.TeamColor(1)); material.SetFloat("_Smoothness", .65f);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            var root = new GameObject("SwimBody"); root.SetActive(false); root.layer = 8;
            try
            {
                var body = root.AddComponent<SwimBody>();
                var visual = Child(root.transform, "Visual", Vector3.up * .2f);
                visual.AddComponent<MeshFilter>().sharedMesh = savedMesh;
                body.BodyRenderer = visual.AddComponent<MeshRenderer>(); body.BodyRenderer.sharedMaterial = material;
                body.BodyRenderer.enabled = false;
                var hit = Child(root.transform, "HitVolume", Vector3.up * .2f);
                body.HitVolume = hit.AddComponent<MeshCollider>(); body.HitVolume.sharedMesh = savedMesh;
                body.HitVolume.convex = true; body.HitVolume.isTrigger = true; body.HitVolume.enabled = false;
                body.CapsuleHitVolume = Child(root.transform, "CapsuleHitVolume", Vector3.zero).AddComponent<CapsuleCollider>();
                body.CapsuleHitVolume.height = 1.8f; body.CapsuleHitVolume.radius = .35f; body.CapsuleHitVolume.center = Vector3.up * .9f;
                body.CapsuleHitVolume.isTrigger = true; body.CapsuleHitVolume.enabled = false;
                root.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
            var player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                var runtime = player.GetComponent<PrototypePlayer>();
                if (runtime.SwimBody != null) Object.DestroyImmediate(runtime.SwimBody.gameObject);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), player.transform);
                runtime.SwimBody = instance.GetComponent<SwimBody>();
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
            AssetDatabase.SaveAssets();
        }
        static GameObject Child(Transform root, string name, Vector3 position)
        {
            var child = new GameObject(name) { layer = 8 }; child.transform.SetParent(root, false); child.transform.localPosition = position; return child;
        }
        static Mesh CreateMesh()
        {
            const int sides = 12, halfRings = 4;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int end = 0; end < 2; end++)
                for (int ring = 0; ring <= halfRings; ring++)
                {
                    float theta = (end + ring / (float)halfRings) * Mathf.PI * .5f;
                    for (int side = 0; side < sides; side++)
                    {
                        float phi = side * Mathf.PI * 2 / sides;
                        vertices.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi) * .35f,
                            Mathf.Sin(theta) * Mathf.Sin(phi) * .2f, Mathf.Cos(theta) * .35f + (end == 0 ? .15f : -.15f)));
                    }
                }
            for (int ring = 0; ring < 2 * (halfRings + 1) - 1; ring++)
                for (int side = 0; side < sides; side++)
                {
                    int a = ring * sides + side, b = ring * sides + (side + 1) % sides, c = a + sides, d = b + sides;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            var mesh = new Mesh { name = "SwimCapsule" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
    }
}
