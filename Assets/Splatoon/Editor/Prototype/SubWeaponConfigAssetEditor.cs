using System;
using System.Linq;
using System.Reflection;
using Splatoon.Config;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(SubWeaponConfigAsset)), CanEditMultipleObjects]
    public sealed class SubWeaponConfigAssetEditor : UnityEditor.Editor
    {
        public static bool IsVisible(string path, SubWeaponType type)
        {
            Type t = typeof(SubWeaponConfigAsset);
            foreach (string segment in path.Split('.'))
            { var f = t.GetField(segment); if (f == null || !SubWeaponFields.Visible(f, type)) return false; t = f.FieldType; }
            return true;
        }
        SubWeaponType[] SelectionTypes()
        {
            var p = serializedObject.FindProperty("type");
            return targets.Cast<SubWeaponConfigAsset>().Select(a => p.hasMultipleDifferentValues ? a.type : (SubWeaponType)p.intValue).ToArray();
        }
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("耗墨、出手时序与回墨锁定分别生效。切换类型保留隐藏值；专用参数只在对应类型显示。调试房中有效修改会实时应用。", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("type"), new GUIContent("副武器类型"));
            if (serializedObject.isEditingMultipleObjects) EditorGUILayout.HelpBox("多选仅显示各资产共同适用的参数。", MessageType.Info);
            var selection = SelectionTypes();
            foreach (var group in SubWeaponFields.Fields(typeof(SubWeaponConfigAsset)))
            {
                if (group.Name == "type" || !selection.All(t => SubWeaponFields.Visible(group, t))) continue;
                var fields = SubWeaponFields.Fields(group.FieldType).Where(f => selection.All(t => SubWeaponFields.Visible(f, t))).ToArray();
                if (fields.Length == 0) continue;
                EditorGUILayout.Space(); EditorGUILayout.LabelField(SubWeaponFields.Name(group), EditorStyles.boldLabel);
                foreach (var f in fields)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(group.Name + "." + f.Name), new GUIContent(SubWeaponFields.Name(f), f.GetCustomAttribute<TooltipAttribute>()?.tooltip), true);
            }
            serializedObject.ApplyModifiedProperties();
            foreach (SubWeaponConfigAsset a in targets)
                try { a.Snapshot().Validate(); } catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
        }
    }
}
