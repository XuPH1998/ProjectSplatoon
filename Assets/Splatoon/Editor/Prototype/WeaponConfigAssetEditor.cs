#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(WeaponConfigAsset))]
    [CanEditMultipleObjects]
    public sealed class WeaponConfigAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("在编辑器的“单机武器调试”房间修改可实时试枪。普通联机房间使用入房时的配置。参数修改按正常资产保存流程持久化。", MessageType.Info);
            serializedObject.Update();
            float labelWidth = EditorGUIUtility.labelWidth;
            try
            {
                EditorGUIUtility.labelWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * .62f, 180, 430);
                var property = serializedObject.GetIterator();
                bool children = true;
                while (property.NextVisible(children))
                {
                    children = false;
                    if (property.name == "m_Script")
                    {
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(property, new GUIContent("脚本"));
                        continue;
                    }
                    var field = typeof(WeaponConfigAsset).GetField(property.name);
                    if (field == null) continue;
                    // Bubble volleys use an exact double period, avoiding a rounded reciprocal fire rate.
                    if (property.name == "fireRate" && !serializedObject.isEditingMultipleObjects &&
                        (((WeaponConfigAsset)target).fireMode == WeaponFireMode.BubbleVolley ||
                         ((WeaponConfigAsset)target).fireMode == WeaponFireMode.Blaster)) continue;
                    var label = new GUIContent(WeaponConfigLabels.Name(field.Name), field.GetCustomAttribute<TooltipAttribute>()?.tooltip);
                    if (field.FieldType.IsEnum)
                    {
                        var header = field.GetCustomAttribute<HeaderAttribute>();
                        if (header != null) { EditorGUILayout.Space(); EditorGUILayout.LabelField(header.header, EditorStyles.boldLabel); }
                        var names = Enum.GetNames(field.FieldType);
                        var options = names.Select(n => new GUIContent(field.FieldType.GetField(n).GetCustomAttribute<InspectorNameAttribute>()?.displayName ?? "未知模式")).ToArray();
                        var values = Enum.GetValues(field.FieldType).Cast<object>().Select(Convert.ToInt32).ToArray();
                        var rect = EditorGUILayout.GetControlRect();
                        EditorGUI.BeginProperty(rect, label, property);
                        EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                        EditorGUI.BeginChangeCheck();
                        int value = EditorGUI.IntPopup(rect, label, property.intValue, options, values);
                        if (EditorGUI.EndChangeCheck()) property.intValue = value;
                        EditorGUI.showMixedValue = false;
                        EditorGUI.EndProperty();
                    }
                    else EditorGUILayout.PropertyField(property, label, true);
                }
                serializedObject.ApplyModifiedProperties();
            }
            finally { EditorGUIUtility.labelWidth = labelWidth; }
            foreach (var asset in targets)
            {
                try { WeaponConfigValidation.Validate(((WeaponConfigAsset)asset).Snapshot()); }
                catch (Exception e) { EditorGUILayout.HelpBox("当前修改不可应用：" + e.Message, MessageType.Error); }
            }
        }
    }
}
#endif
