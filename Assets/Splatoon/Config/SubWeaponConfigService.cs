using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Config
{
    public sealed class SubWeaponConfigService
    {
        public static SubWeaponConfigService Current { get; } = new();
        readonly Dictionary<int, SubWeaponRuntimeConfig> _values = new();
        readonly Dictionary<string, SubWeaponConfigAsset> _assets = new(StringComparer.Ordinal);
        readonly List<AsyncOperationHandle<SubWeaponConfigAsset>> _handles = new();

        public SubWeaponRuntimeConfig Get(cfg.HeroConfig hero) => hero != null && _values.TryGetValue(hero.Id, out var value) ? value : null;
        public SubWeaponRuntimeConfig Get(int heroId) => Get(GameplayConfig.GetHero(heroId));

        public async UniTask InitializeAsync(IEnumerable<cfg.HeroConfig> heroes, CancellationToken token, bool editorLive = false)
        {
            Clear();
            try
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hero in heroes)
                {
                    string path = hero.SubWeaponConfigPath;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    token.ThrowIfCancellationRequested();
                    if (!_assets.TryGetValue(path, out var asset))
                    {
#if UNITY_EDITOR
                        if (editorLive) asset = UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(path);
                        else
#endif
                        {
                            var handle = Addressables.LoadAssetAsync<SubWeaponConfigAsset>(path); _handles.Add(handle);
                            while (!handle.IsDone) await UniTask.Yield(token);
                            if (handle.Status == AsyncOperationStatus.Succeeded) asset = handle.Result;
                        }
                        if (asset == null) throw new InvalidOperationException($"副武器配置加载失败：{path}");
                        _assets.Add(path, asset);
                    }
                    var snapshot = asset.Snapshot(); SubWeaponConfigValidation.Validate(snapshot);
                    if (!ids.Add(snapshot.StableId)) throw new InvalidOperationException("副武器稳定标识重复：" + snapshot.StableId);
                    _values.Add(hero.Id, snapshot);
                }
            }
            catch { Clear(); throw; }
        }

#if UNITY_EDITOR
        public void SetForEditor(int heroId, SubWeaponRuntimeConfig value) { SubWeaponConfigValidation.Validate(value); _values[heroId] = value; }
#endif

        public void Clear()
        {
            _values.Clear(); _assets.Clear();
            foreach (var handle in _handles) if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
        }
    }
}
