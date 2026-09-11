#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Splatoon.Skills;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(SkillDefinition))]
    public sealed class SkillDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var skill = (SkillDefinition)target;
            EditorGUILayout.HelpBox($"实际时长：{skill.ResolveDuration():0.00} 秒 | 片段数：{skill.Clips?.Count ?? 0}", MessageType.Info);
            if (GUILayout.Button("校验技能"))
            {
                int errors = 0; foreach (var c in skill.Clips) if (c == null || c.Duration <= 0) errors++;
                EditorUtility.DisplayDialog("技能校验", errors == 0 ? "技能配置有效。" : $"发现 {errors} 个无效片段。", "确定");
            }
            if (GUILayout.Button("导出 JSON")) { var path = EditorUtility.SaveFilePanel("导出技能", "", skill.name + ".json", "json"); if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, EditorJsonUtility.ToJson(skill, true)); }
            if (GUILayout.Button("导入 JSON")) { var path = EditorUtility.OpenFilePanel("导入技能", "", "json"); if (!string.IsNullOrEmpty(path)) { Undo.RecordObject(skill, "导入技能 JSON"); EditorJsonUtility.FromJsonOverwrite(System.IO.File.ReadAllText(path), skill); EditorUtility.SetDirty(skill); } }
        }
    }
}
#endif
