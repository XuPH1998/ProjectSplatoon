using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Config
{
    public sealed class SubWeaponConfigService
    {
        public static SubWeaponConfigService Current { get; } = new();
        readonly Dictionary<int, SubWeaponRuntimeConfig> _values = new();
        readonly Dictionary<int, SubWeaponConfigAsset> _sources = new();
        readonly Dictionary<int, uint> _revisions = new();
        readonly Dictionary<(int, uint), SubWeaponRuntimeConfig> _history = new();
        readonly List<AsyncOperationHandle<SubWeaponConfigAsset>> _handles = new();
        public SubWeaponRuntimeConfig Get(cfg.HeroConfig hero)
        {
            if (_values.TryGetValue(hero.Id, out var value)) return value;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(hero.SubWeaponConfigPath);
                if (asset != null) { Set(hero.Id, asset); return _values[hero.Id]; }
            }
#endif
            throw new InvalidOperationException($"英雄{hero.Id}副武器未加载：{hero.SubWeaponConfigPath}");
        }
        public SubWeaponConfigAsset Source(int hero) => _sources.TryGetValue(hero, out var a) ? a : null;
        public uint Revision(int hero) => _revisions.TryGetValue(hero, out var n) ? n : 0;
        public SubWeaponRuntimeConfig ForEntity(int hero, uint revision) => _history.TryGetValue((hero, revision), out var c) ? c : Get(GameplayConfig.GetHero(hero));
        public void Set(int hero, SubWeaponConfigAsset asset)
        {
            var c = asset.Snapshot(); c.Validate();
            _sources[hero] = asset;
            if (_values.TryGetValue(hero, out var old) && Same(old,c)) return;
            uint revision = Revision(hero) + 1;
            _values[hero] = c; _revisions[hero] = revision; _history[(hero, revision)] = c;
        }
        public async UniTask InitializeAsync(IEnumerable<cfg.HeroConfig> heroes, CancellationToken token, bool editorLive = false)
        {
            Clear();
            try
            {
                foreach (var hero in heroes)
                {
                    token.ThrowIfCancellationRequested(); SubWeaponConfigAsset asset = null;
#if UNITY_EDITOR
                    if (editorLive) asset = UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(hero.SubWeaponConfigPath);
                    else
#endif
                    {
                        var handle = Addressables.LoadAssetAsync<SubWeaponConfigAsset>(hero.SubWeaponConfigPath); _handles.Add(handle);
                        while (!handle.IsDone) await UniTask.Yield(token);
                        if (handle.Status == AsyncOperationStatus.Succeeded) asset = handle.Result;
                    }
                    if (asset == null) throw new InvalidOperationException("副武器配置加载失败：" + hero.SubWeaponConfigPath);
                    Set(hero.Id, asset);
                }
            }
            catch { Clear(); throw; }
        }
        public void Clear()
        {
            _values.Clear(); _sources.Clear(); _revisions.Clear(); _history.Clear();
            foreach (var h in _handles) if (h.IsValid()) Addressables.Release(h);
            _handles.Clear();
        }
        public static bool Same(SubWeaponRuntimeConfig a,SubWeaponRuntimeConfig b) => a.ContentHash==b.ContentHash&&a.Common.entityPrefab==b.Common.entityPrefab&&a.Common.heldPrefab==b.Common.heldPrefab&&a.Common.icon==b.Common.icon&&a.Common.useAudio==b.Common.useAudio&&a.Common.effectAudio==b.Common.effectAudio&&a.Common.effectMaterial==b.Common.effectMaterial&&a.Visuals.Equals(b.Visuals)&&a.TypeVisuals.Equals(b.TypeVisuals);
    }
}
