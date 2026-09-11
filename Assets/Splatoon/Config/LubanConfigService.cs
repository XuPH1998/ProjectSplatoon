using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using SimpleJSON;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Splatoon.Config
{
    public interface IConfigService
    {
        bool IsReady { get; }
        UniTask InitializeAsync(CancellationToken cancellationToken);
    }
    public sealed class LubanConfigService : IConfigService
    {
        public static LubanConfigService Current { get; } = new();
        public bool IsReady { get; private set; }
        public cfg.Tables Tables { get; private set; }
        public byte[] ContentSignature { get; private set; }
        public async UniTask InitializeAsync(CancellationToken token)
        {
            if (IsReady) return;
            token.ThrowIfCancellationRequested();
            var handle = Addressables.LoadAssetsAsync<TextAsset>("Luban", null);
            try
            {
                while (!handle.IsDone) await UniTask.Yield(token);
                token.ThrowIfCancellationRequested();
                if (handle.Status != AsyncOperationStatus.Succeeded) throw new InvalidOperationException("缺少 Luban 资源，请运行菜单：喷墨对战/构建/Windows 正式资源版本。", handle.OperationException);
                var json = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in handle.Result) json.Add(asset.name, asset.text);
                Tables = new cfg.Tables(name => json.TryGetValue(name, out var text) ? JSONNode.Parse(text) : throw new InvalidOperationException("缺少 Luban 配置表：" + name));
                var names = new List<string>(json.Keys); names.Sort(StringComparer.Ordinal);
                var content = new System.Text.StringBuilder("ink-lan-v3\n");
                foreach (var name in names) content.Append(name).Append('\n').Append(json[name]).Append('\n');
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    ContentSignature = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content.ToString()));
                IsReady = true;
            }
            finally { if (handle.IsValid()) Addressables.Release(handle); }
        }
        public void Reset() { Tables = null; ContentSignature = null; IsReady = false; }
    }
}
