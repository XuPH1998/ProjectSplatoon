#if UNITY_EDITOR
using System.Linq;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(AmmoConfigAsset))]
    public sealed class AmmoConfigAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("弹药配置由武器资源引用。爆炸默认关闭；启用后会替换普通 InkImpact，并叠加范围伤害与涂墨。", MessageType.Info);
            Draw("ammoId", "弹药编号");
            Draw("flightProfile", "飞行表现配置");
            Draw("flightPrefab", "飞行墨弹预制体");
            Draw("muzzlePrefab", "枪口喷溅预制体");
            Draw("explosionEnabled", "启用爆炸");
            Draw("explosionPrefab", "爆炸特效预制体");
            Draw("explosionRadius", "爆炸范围（米）");
            Draw("explosionDamage", "爆炸伤害");
            Draw("explosionPaint", "爆炸是否涂墨");
            Draw("explosionPaintRadiusMin", "涂墨最小半径（米）");
            Draw("explosionPaintRadiusMax", "涂墨最大半径（米）");
            serializedObject.ApplyModifiedProperties();
            var ammo = (AmmoConfigAsset)target;
            if (ammo.explosionPaintRadiusMin < 0 || ammo.explosionPaintRadiusMax < ammo.explosionPaintRadiusMin)
                EditorGUILayout.HelpBox("涂墨半径必须满足 0 <= min <= max。", MessageType.Error);
            if (ammo.explosionEnabled && ammo.explosionPrefab == null)
                EditorGUILayout.HelpBox("启用爆炸时必须绑定爆炸预制体。", MessageType.Error);
            var duplicate = AssetDatabase.FindAssets("t:AmmoConfigAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path != AssetDatabase.GetAssetPath(ammo))
                .Select(AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>)
                .Any(other => other != null && other.ammoId == ammo.ammoId);
            if (duplicate) EditorGUILayout.HelpBox("弹药编号必须在运行时内容中唯一。", MessageType.Error);
        }

        void Draw(string propertyName, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName), new GUIContent(label), true);
    }
}
#endif
