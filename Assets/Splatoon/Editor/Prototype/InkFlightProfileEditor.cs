#if UNITY_EDITOR
using Splatoon.Combat;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(InkFlightProfile))]
    [CanEditMultipleObjects]
    public sealed class InkFlightProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true)) Draw("m_Script", "脚本");
            Draw("FlightPrefab", "飞行墨弹预制体");
            Draw("MuzzlePrefab", "枪口喷溅预制体");
            Draw("BurstInterval", "飞行发射间隔（秒）");
            Draw("ParticlesPerBurst", "每次发射粒子数");
            Draw("VisualLifetime", "飞行显示寿命（秒）");
            Draw("MuzzleInterval", "枪口喷溅间隔（秒）");
            Draw("MuzzleBurstCount", "每次枪口喷溅粒子数");
            Draw("MaxRibbonGap", "拖尾最大连接间距（米）");
            Draw("SatelliteSpread", "附加墨团扰动（米）");
            serializedObject.ApplyModifiedProperties();
        }

        void Draw(string propertyName, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName), new GUIContent(label), true);
    }
}
#endif
