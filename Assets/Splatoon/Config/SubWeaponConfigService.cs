using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
namespace Splatoon.Config
{
    // Catalog IDs, not hero IDs, own configuration and historical snapshots.
    public sealed class SubWeaponConfigService
    {
        public static SubWeaponConfigService Current { get; } = new();
        readonly Dictionary<int, SubWeaponRuntimeConfig> _values = new();
        readonly Dictionary<int, SubWeaponConfigAsset> _sources = new();
        readonly Dictionary<int, uint> _revisions = new();
        readonly Dictionary<(int, uint), SubWeaponRuntimeConfig> _history = new();
        readonly Dictionary<int,int> _defaults = new();
        readonly List<AsyncOperationHandle<SubWeaponConfigAsset>> _handles = new();
        public IEnumerable<int> Ids => _values.Keys;
        public static int Id(SubWeaponType type) => (int)type + 1;
        public int DefaultId(int hero)
        {
            if (_defaults.TryGetValue(hero, out int id)) return id;
            var row=GameplayConfig.GetHero(hero);
            foreach(var e in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                if(e.ConfigPath==row.SubWeaponConfigPath)return e.Id;
            throw new InvalidOperationException("英雄默认副武器未在目录注册："+row.SubWeaponConfigPath);
        }
        public int Resolve(int hero,int selected) => selected>0?selected:DefaultId(hero);
        public SubWeaponRuntimeConfig GetById(int id)
        {
            if (_values.TryGetValue(id, out var value)) return value;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var row=LubanConfigService.Current.Tables.TbSubWeapon.Get(id);
                var asset=UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath);
                if(asset!=null){SetById(id,asset);return _values[id];}
            }
#endif
            throw new InvalidOperationException("副武器未加载："+id);
        }
        public bool Contains(int id)=>_values.ContainsKey(id);
        public SubWeaponRuntimeConfig Get(cfg.HeroConfig hero)=>GetById(DefaultId(hero.Id));
        public SubWeaponConfigAsset SourceById(int id)=>_sources.TryGetValue(id,out var a)?a:null;
        public uint RevisionById(int id)=>_revisions.TryGetValue(id,out var n)?n:0;
        public SubWeaponRuntimeConfig ForEntityId(int id,uint revision)=>_history.TryGetValue((id,revision),out var c)?c:throw new InvalidOperationException("未知副武器历史版本");
        // Compatibility helpers for existing editor fixtures. Runtime selections use explicit IDs.
        public SubWeaponConfigAsset Source(int hero)=>SourceById(DefaultId(hero));
        public uint Revision(int hero)=>RevisionById(DefaultId(hero));
        public SubWeaponRuntimeConfig ForEntity(int hero,uint revision)=>ForEntityId(DefaultId(hero),revision);
        public void Set(int hero,SubWeaponConfigAsset asset){int id=Id(asset.type);SetById(id,asset);_defaults[hero]=id;}
        public void SetById(int id,SubWeaponConfigAsset asset)
        {
            if(asset==null||Id(asset.type)!=id)throw new InvalidOperationException("副武器稳定ID与资产类型不匹配");
            var c=asset.Snapshot();c.Validate();_sources[id]=asset;
            if(_values.TryGetValue(id,out var old)&&Same(old,c))return;
            uint revision=RevisionById(id)+1;_values[id]=c;_revisions[id]=revision;_history[(id,revision)]=c;
        }
        public async UniTask InitializeAsync(IEnumerable<cfg.HeroConfig> heroes,CancellationToken token,bool editorLive=false)
        {
            Clear();
            try
            {
                foreach(var row in LubanConfigService.Current.Tables.TbSubWeapon.DataList)
                {
                    token.ThrowIfCancellationRequested();SubWeaponConfigAsset asset=null;
#if UNITY_EDITOR
                    if(editorLive)asset=UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(row.ConfigPath);
                    else
#endif
                    {
                        var h=Addressables.LoadAssetAsync<SubWeaponConfigAsset>(row.ConfigPath);_handles.Add(h);
                        while(!h.IsDone)await UniTask.Yield(token);
                        if(h.Status==AsyncOperationStatus.Succeeded)asset=h.Result;
                    }
                    if(asset==null||Id(asset.type)!=row.Id)throw new InvalidOperationException("副武器目录与资产不匹配："+row.ConfigPath);
                    SetById(row.Id,asset);
                }
                foreach(var hero in heroes)_defaults[hero.Id]=DefaultId(hero.Id);
            }
            catch{Clear();throw;}
        }
        public void Clear(){_values.Clear();_sources.Clear();_revisions.Clear();_history.Clear();_defaults.Clear();foreach(var h in _handles)if(h.IsValid())Addressables.Release(h);_handles.Clear();}
        public static bool Same(SubWeaponRuntimeConfig a,SubWeaponRuntimeConfig b)=>a.ContentHash==b.ContentHash&&a.Common.entityPrefab==b.Common.entityPrefab&&a.Common.heldPrefab==b.Common.heldPrefab&&a.Common.icon==b.Common.icon&&a.Common.useAudio==b.Common.useAudio&&a.Common.effectAudio==b.Common.effectAudio&&a.Common.effectMaterial==b.Common.effectMaterial&&a.Visuals.Equals(b.Visuals)&&a.TypeVisuals.Equals(b.TypeVisuals);
    }
}
