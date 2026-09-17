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
            EditorGUILayout.HelpBox("此配置仅保留用于迁移和回滚。请在武器引用的弹药配置中修改飞行墨水与枪口参数。", MessageType.Info);
            using (new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
        }

        void Draw(string propertyName, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName), new GUIContent(label), true);
    }
}
#endif
