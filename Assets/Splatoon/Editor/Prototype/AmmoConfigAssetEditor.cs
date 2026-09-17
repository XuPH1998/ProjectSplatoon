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
            EditorGUILayout.HelpBox("单机武器调试中，修改本资产会应用到后续射击；已经飞出的墨弹保留旧参数。预制体内部模块修改需重新进入房间。", MessageType.Info);
            EditorGUILayout.LabelField("弹药标识", EditorStyles.boldLabel);
            Draw("ammoId", "弹药编号");
            EditorGUILayout.Space(); EditorGUILayout.LabelField("飞行墨水", EditorStyles.boldLabel);
            Draw("flightPrefab", "飞行墨弹预制体");
            Draw("burstInterval", "飞行发射间隔（秒）");
            Draw("particlesPerBurst", "每次发射粒子数");
            Draw("visualLifetime", "飞行显示寿命（秒）");
            Draw("maxRibbonGap", "拖尾最大连接间距（米）");
            Draw("satelliteSpread", "附加墨团扰动（米）");
            EditorGUILayout.Space(); EditorGUILayout.LabelField("枪口喷溅", EditorStyles.boldLabel);
            Draw("muzzlePrefab", "枪口喷溅预制体");
            Draw("muzzleInterval", "枪口喷溅间隔（秒）");
            Draw("muzzleBurstCount", "每次枪口喷溅粒子数");
            EditorGUILayout.Space(); EditorGUILayout.LabelField("爆炸", EditorStyles.boldLabel);
            Draw("explosionEnabled", "启用爆炸");
            Draw("explosionPrefab", "爆炸特效预制体");
            Draw("explosionRadius", "爆炸范围（米）");
            Draw("explosionDamage", "爆炸伤害");
            Draw("explosionPaint", "爆炸是否涂墨");
            Draw("explosionPaintRadiusMin", "涂墨最小半径（米）");
            Draw("explosionPaintRadiusMax", "涂墨最大半径（米）");
            serializedObject.ApplyModifiedProperties();
            var ammo = (AmmoConfigAsset)target;
            try { new AmmoRuntimeConfig(ammo).Validate(); }
            catch (System.Exception e) { EditorGUILayout.HelpBox("修改未应用：" + e.Message, MessageType.Error); }
            if (ammo.explosionEnabled && ammo.explosionPrefab == null)
                EditorGUILayout.HelpBox("未绑定爆炸预制体，继续使用普通命中效果。", MessageType.Info);
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
