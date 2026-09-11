using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using SimpleJSON;
using Unity.Addressables;
using UnityEngine;

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
        private readonly Dictionary<string, TextAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
        public async UniTask InitializeAsync(CancellationToken cancellationToken)
        {
            if (IsReady) return;
            await Addressables.InitializeAsync().Task;
            var locations = await Addressables.LoadResourceLocationsAsync("Assets/GameResource/Bootstrap/Config/Luban").Task;
            foreach (var location in locations)
            {
                var asset = await Addressables.LoadAssetAsync<TextAsset>(location).Task;
                if (asset != null) _assets[location.PrimaryKey] = asset;
            }
            Tables = new cfg.Tables(name =>
            {
                foreach (var pair in _assets) if (pair.Key.EndsWith(name + ".json", StringComparison.OrdinalIgnoreCase)) return JSON.Parse(pair.Value.text);
                throw new InvalidOperationException("Missing Luban table: " + name);
            });
            IsReady = true;
        }
    }
}
