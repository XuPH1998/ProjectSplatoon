using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Addressables;
using UnityEngine;
using Splatoon.Config;

namespace Splatoon
{
    public sealed class SplatoonBootstrap : MonoBehaviour
    {
        private CancellationTokenSource _lifetime;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Create()
        {
            if (FindFirstObjectByType<SplatoonBootstrap>() != null) return;
            var go = new GameObject("SplatoonBootstrap");
            DontDestroyOnLoad(go);
            go.AddComponent<SplatoonBootstrap>();
        }
        private void Awake() { _lifetime = new CancellationTokenSource(); InitializeAsync(_lifetime.Token).Forget(); }
        private async UniTaskVoid InitializeAsync(CancellationToken token)
        {
            try
            {
                await Addressables.InitializeAsync().Task;
                Debug.Log("[Splatoon] Addressables initialized.");
                await LubanConfigService.Current.InitializeAsync(token);
                Debug.Log("[Splatoon] Luban configuration initialized.");
                var locator = Addressables.ResourceLocators.Count;
                Debug.Log($"[Splatoon] Resource locators: {locator}");
            }
            catch (Exception e) { Debug.LogException(e); }
        }
        private void OnDestroy() { _lifetime?.Cancel(); _lifetime?.Dispose(); }
    }
}
