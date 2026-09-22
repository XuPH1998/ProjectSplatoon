using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
namespace Splatoon.Config
{
    public sealed class SpecialWeaponConfigService
    {
        public static SpecialWeaponConfigService Current {get;}=new();
        readonly Dictionary<int,SpecialWeaponRuntimeConfig> _values=new();
        readonly List<AsyncOperationHandle<SpecialWeaponConfigAsset>> _handles=new();
        public IEnumerable<int> Ids=>_values.Keys;
        public bool Contains(int id)=>_values.ContainsKey(id);
        public SpecialWeaponRuntimeConfig Get(int id)=>_values.TryGetValue(id==0?1:id,out var c)?c:throw new InvalidOperationException("大招未加载："+id);
        public void Set(int id,SpecialWeaponConfigAsset asset){var c=asset.Snapshot();c.Validate();if((int)c.Type!=id)throw new InvalidOperationException("大招ID不匹配");_values[id]=c;}
        public async UniTask InitializeAsync(CancellationToken token,bool editorLive=false)
        {
            Clear();try{foreach(var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
            {
                token.ThrowIfCancellationRequested();SpecialWeaponConfigAsset asset=null;
#if UNITY_EDITOR
                if(editorLive)asset=UnityEditor.AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath);else
#endif
                {var h=Addressables.LoadAssetAsync<SpecialWeaponConfigAsset>(row.ConfigPath);_handles.Add(h);while(!h.IsDone)await UniTask.Yield(token);if(h.Status==AsyncOperationStatus.Succeeded)asset=h.Result;}
                if(asset==null)throw new InvalidOperationException("大招配置加载失败："+row.ConfigPath);Set(row.Id,asset);
            }}catch{Clear();throw;}
        }
        public void Clear(){_values.Clear();foreach(var h in _handles)if(h.IsValid())Addressables.Release(h);_handles.Clear();}
    }
}
