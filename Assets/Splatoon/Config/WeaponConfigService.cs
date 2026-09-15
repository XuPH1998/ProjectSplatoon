using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Config
{
    /// <summary>Room-scoped snapshots. Source assets are only observed by the editor debug room.</summary>
    public sealed class WeaponConfigService
    {
        public static WeaponConfigService Current { get; } = new();
        readonly Dictionary<int, WeaponRuntimeConfig> _values = new();
        readonly Dictionary<int, WeaponConfigAsset> _sources = new();
        readonly Dictionary<int, uint> _revisions = new();
        readonly Dictionary<(int hero, uint revision), WeaponRuntimeConfig> _history = new();
        readonly Dictionary<string, WeaponConfigAsset> _assets = new(StringComparer.Ordinal);
        readonly List<AsyncOperationHandle<WeaponConfigAsset>> _handles = new();
        public WeaponRuntimeConfig Get(cfg.HeroConfig hero)
        {
            if (_values.TryGetValue(hero.Id, out var value)) return value;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(hero.WeaponConfigPath);
                if (asset != null) { SetForEditor(hero.Id, asset.Snapshot(), asset); return _values[hero.Id]; }
            }
#endif
            throw new InvalidOperationException($"英雄 {hero.Id} 武器配置未加载：{hero.WeaponConfigPath}");
        }
        public WeaponConfigAsset Source(int heroId) => _sources.TryGetValue(heroId, out var source) ? source : null;
        public uint Revision(int heroId) => _revisions.TryGetValue(heroId, out var revision) ? revision : 0;
        public WeaponRuntimeConfig ForShot(int heroId, uint revision) => revision != 0 && _history.TryGetValue((heroId, revision), out var value)
            ? value : Get(GameplayConfig.GetHero(heroId));
        void Store(int heroId, WeaponRuntimeConfig value)
        {
            uint revision = Revision(heroId) + 1;
            _values[heroId] = value; _revisions[heroId] = revision; _history[(heroId, revision)] = value;
        }
        public async UniTask InitializeAsync(IEnumerable<cfg.HeroConfig> heroes, CancellationToken token, bool editorLive = false)
        {
            Clear();
            try
            {
                foreach (var hero in heroes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_assets.TryGetValue(hero.WeaponConfigPath, out var asset))
                    {
#if UNITY_EDITOR
                        if (editorLive) asset = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(hero.WeaponConfigPath);
                        else
#endif
                        {
                            var handle = Addressables.LoadAssetAsync<WeaponConfigAsset>(hero.WeaponConfigPath); _handles.Add(handle);
                            while (!handle.IsDone) await UniTask.Yield(token);
                            if (handle.Status == AsyncOperationStatus.Succeeded) asset = handle.Result;
                        }
                        if (asset == null) throw new InvalidOperationException($"武器配置加载失败：{hero.WeaponConfigPath}");
                        _assets.Add(hero.WeaponConfigPath, asset);
                    }
                    var snapshot = asset.Snapshot(); WeaponConfigValidation.Validate(snapshot);
                    _sources.Add(hero.Id, asset); Store(hero.Id, snapshot);
                }
            }
            catch { Clear(); throw; }
        }
        public void Replace(int heroId, WeaponRuntimeConfig snapshot)
        {
            if (!_values.ContainsKey(heroId)) throw new InvalidOperationException("武器尚未加载");
            WeaponConfigValidation.Validate(snapshot); Store(heroId, snapshot);
        }
#if UNITY_EDITOR
        // Test fixtures and editor validation can inject independent snapshots without changing assets.
        public void SetForEditor(int heroId, WeaponRuntimeConfig snapshot, WeaponConfigAsset source = null)
        { Store(heroId, snapshot); if (source != null) _sources[heroId] = source; }
#endif
        public void Clear()
        {
            _values.Clear(); _sources.Clear(); _assets.Clear(); _history.Clear(); _revisions.Clear();
            foreach (var h in _handles) if (h.IsValid()) Addressables.Release(h);
            _handles.Clear();
        }
    }
}
