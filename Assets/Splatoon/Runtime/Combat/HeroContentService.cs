using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Combat
{
    public interface IHeroAssetSource
    {
        UniTask<GameObject> LoadAsync(string address, CancellationToken token);
        void ReleaseAll();
    }

    public sealed class AddressableHeroAssetSource : IHeroAssetSource
    {
        readonly List<AsyncOperationHandle<GameObject>> _handles = new();
        public async UniTask<GameObject> LoadAsync(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var handle = Addressables.LoadAssetAsync<GameObject>(address); _handles.Add(handle);
            while (!handle.IsDone) await UniTask.Yield(token);
            token.ThrowIfCancellationRequested();
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                throw new InvalidOperationException("英雄模型加载失败：" + address, handle.OperationException);
            return handle.Result;
        }
        public void ReleaseAll()
        {
            foreach (var handle in _handles) if (handle.IsValid()) Addressables.Release(handle);
            _handles.Clear();
        }
    }

    public sealed class HeroContent
    {
        public cfg.HeroConfig Config { get; }
        public GameObject CharacterPrefab { get; }
        public GameObject WeaponPrefab { get; }
        public CharacterPresentationProfile Profile { get; }
        public HeroContent(cfg.HeroConfig config, GameObject character, GameObject weapon)
        {
            Config = config; CharacterPrefab = character; WeaponPrefab = weapon;
            var view = character != null ? character.GetComponent<InkCharacterView>() : null;
            var bindings = weapon != null ? weapon.GetComponent<HeroWeaponBindings>() : null;
            if (view == null || view.Profile == null || view.Animator == null || !view.Animator.isHuman ||
                view.Animator.runtimeAnimatorController == null || view.WeaponSocket == null ||
                view.TeamMarker == null || view.SwimEffect == null)
                throw new InvalidOperationException($"英雄 {config.Id} 角色模型缺少人形动画、表现配置、武器挂点或墨水表现绑定：{config.CharacterPrefabAddress}");
            if (bindings == null || bindings.Nozzle == null || !bindings.Nozzle.IsChildOf(weapon.transform) ||
                (bindings.SupportLeftHand && (bindings.LeftGrip == null || !bindings.LeftGrip.IsChildOf(weapon.transform))))
                throw new InvalidOperationException($"英雄 {config.Id} 武器模型缺少有效枪口或左手握点：{config.WeaponPrefabAddress}");
            if (config.MuzzleMode == 1 && (view.LeftWeaponSocket == null || !view.Profile.DualWield || bindings.SupportLeftHand ||
                bindings.LeftPart == null || bindings.LeftNozzle == null || !bindings.LeftPart.IsChildOf(weapon.transform) || !bindings.LeftNozzle.IsChildOf(bindings.LeftPart)))
                throw new InvalidOperationException($"英雄 {config.Id} 双枪挂点、部件或枪口绑定无效");
            Profile = view.Profile;
            if (config.FireMode == (int)WeaponFireMode.Splatling && (!Profile.Splatling || Profile.SingleShot ||
                Profile.ShootDuration <= 0 || Profile.ShootEndDuration <= 0 || !bindings.SupportLeftHand))
                throw new InvalidOperationException($"英雄 {config.Id} 缺少旋转枪循环、结束动作或支撑握持配置");
            if (Profile.Paper == null || Profile.Paper.CapturePrefab == null || Profile.Paper.DisplayMesh == null ||
                Profile.Paper.Material == null || Profile.Paper.CaptureHeight <= 0)
                throw new InvalidOperationException($"英雄 {config.Id} 缺少实时纸片拍摄模型与画面配置");
        }
    }

    // Room lifetime cache. Every selectable hero is ready before NGO can spawn players.
    public sealed class HeroContentService : IDisposable
    {
        readonly IHeroAssetSource _source;
        readonly Dictionary<string, GameObject> _assets = new(StringComparer.Ordinal);
        readonly Dictionary<int, HeroContent> _heroes = new();
        public IEnumerable<HeroContent> All => _heroes.Values.OrderBy(h => h.Config.Id);
        public int AssetCount => _assets.Count;
        public HeroContentService(IHeroAssetSource source = null) => _source = source ?? new AddressableHeroAssetSource();
        public HeroContent Get(int id) => _heroes.TryGetValue(id, out var hero) ? hero : throw new InvalidOperationException("英雄资源尚未准备就绪：" + id);
        public async UniTask InitializeAsync(IEnumerable<cfg.HeroConfig> heroes, CancellationToken token)
        {
            Clear();
            try
            {
                foreach (var hero in heroes.OrderBy(h => h.Id))
                {
                    var character = await Load(hero.CharacterPrefabAddress, token);
                    var weapon = await Load(hero.WeaponPrefabAddress, token);
                    token.ThrowIfCancellationRequested();
                    _heroes.Add(hero.Id, new HeroContent(hero, character, weapon));
                }
            }
            catch { Clear(); throw; }
        }
        async UniTask<GameObject> Load(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_assets.TryGetValue(address, out var asset)) return asset;
            asset = await _source.LoadAsync(address, token); _assets.Add(address, asset); return asset;
        }
        public void Clear() { _heroes.Clear(); _assets.Clear(); _source.ReleaseAll(); }
        public void Dispose() => Clear();
    }
}
