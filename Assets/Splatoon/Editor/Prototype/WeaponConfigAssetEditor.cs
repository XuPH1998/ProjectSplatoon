#if UNITY_EDITOR
using System;
using Splatoon.Config;
using UnityEditor;

namespace Splatoon.Editor
{
    [CustomEditor(typeof(WeaponConfigAsset))]
    public sealed class WeaponConfigAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("在编辑器的“单机武器调试”房间修改可实时试枪。普通联机房间使用入房时的配置。参数修改按正常资产保存流程持久化。", MessageType.Info);
            DrawDefaultInspector();
            try { WeaponConfigValidation.Validate(((WeaponConfigAsset)target).Snapshot()); }
            catch (Exception e) { EditorGUILayout.HelpBox("当前修改不可应用：" + e.Message, MessageType.Error); }
        }
    }
}
#endif
