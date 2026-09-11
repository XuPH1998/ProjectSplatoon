#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Splatoon.Editor
{
    public static class AddressablesProjectSetup
    {
        [MenuItem("喷墨对战/配置/注册本地 Addressables 资源")]
        public static void Setup()
        {
            // 与构建入口复用同一逐项注册逻辑，保证 Luban 标签和场景地址一致。
            PrototypeBuilder.ConfigureAddressables();
            EditorUtility.DisplayDialog("本地资源配置", "已注册原型场景、网络预制体和 Luban 配置。首次使用请先搭建灰盒场景。", "确定");
        }
    }
}
#endif
