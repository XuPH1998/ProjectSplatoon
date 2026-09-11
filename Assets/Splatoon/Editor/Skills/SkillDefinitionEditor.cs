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
            EditorGUILayout.HelpBox($"Resolved duration: {skill.ResolveDuration():0.00}s | Clips: {skill.Clips?.Count ?? 0}", MessageType.Info);
            if (GUILayout.Button("Validate Skill"))
            {
                int errors = 0; foreach (var c in skill.Clips) if (c == null || c.Duration <= 0) errors++;
                EditorUtility.DisplayDialog("Skill Validation", errors == 0 ? "Skill is valid." : $"Found {errors} invalid clip(s).", "OK");
            }
            if (GUILayout.Button("Export JSON")) { var path = EditorUtility.SaveFilePanel("Export Skill", "", skill.name + ".json", "json"); if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, EditorJsonUtility.ToJson(skill, true)); }
            if (GUILayout.Button("Import JSON")) { var path = EditorUtility.OpenFilePanel("Import Skill", "", "json"); if (!string.IsNullOrEmpty(path)) { Undo.RecordObject(skill, "Import Skill JSON"); EditorJsonUtility.FromJsonOverwrite(System.IO.File.ReadAllText(path), skill); EditorUtility.SetDirty(skill); } }
        }
    }
}
#endif
