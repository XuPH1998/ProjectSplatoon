#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Combat;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        readonly Dictionary<int, string> _observedWeapons = new();
        readonly Dictionary<int, (WeaponRuntimeConfig config, HeroContent content)> _pendingWeapons = new();
        readonly List<int> _appliedWeapons = new();
        CancellationTokenSource _weaponReload;
        int _weaponReloadGeneration;
        public string WeaponDebugStatus { get; private set; } = "修改武器或弹药资产即可实时试枪";
        public async UniTask StartWeaponDebugRoom()
        {
            if (!Ready || Busy || InRoom) return;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            ushort port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
            socket.Close();
            await Connect(true, "127.0.0.1", port, true);
        }
        void ClearDebugWeaponChanges()
        {
            _weaponReloadGeneration++;
            _weaponReload?.Cancel(); _weaponReload?.Dispose(); _weaponReload = null;
            _observedWeapons.Clear(); _pendingWeapons.Clear();
            WeaponDebugStatus = "修改武器或弹药资产即可实时试枪";
        }
        void ObserveDebugWeaponAssets()
        {
            if (!IsWeaponDebugRoom || !InRoom || Busy) return;
            _weaponReload ??= CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                var source = WeaponConfigService.Current.Source(hero.Id);
                if (source == null) continue;
                string json = JsonUtility.ToJson(source) + "|" + (source.ammoConfig != null ? JsonUtility.ToJson(source.ammoConfig) : "null");
                if (_observedWeapons.TryGetValue(hero.Id, out var prior) && prior == json) continue;
                _observedWeapons[hero.Id] = json;
                QueueDebugWeaponChange(hero.Id, source.Snapshot(), json, _weaponReloadGeneration, _weaponReload.Token).Forget();
            }
        }
        async UniTask QueueDebugWeaponChange(int heroId, WeaponRuntimeConfig candidate, string json, int generation, CancellationToken token)
        {
            _pendingWeapons.Remove(heroId);
            try
            {
                WeaponConfigValidation.Validate(candidate);
                WeaponConfigService.Current.ValidateAmmoId(heroId, candidate.Ammo);
                var old = GameplayConfig.GetWeapon(heroId);
                if (old.SameValues(candidate)) return;
                WeaponDebugStatus = "正在验证武器修改…";
                // Includes fire-mode compatibility with the character's animation and weapon bindings.
                var content = await Heroes.PrepareWeaponAsync(heroId, candidate, token);
                token.ThrowIfCancellationRequested();
                if (generation != _weaponReloadGeneration || !_observedWeapons.TryGetValue(heroId, out var latest) || latest != json) return;
                _pendingWeapons[heroId] = (candidate, content);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (generation == _weaponReloadGeneration && _observedWeapons.TryGetValue(heroId, out var latest) && latest == json)
                    WeaponDebugStatus = "修改未应用：" + e.Message;
            }
        }
        public void ApplyDebugWeaponChanges()
        {
            if (!IsWeaponDebugRoom || !InRoom || Manager == null || !Manager.IsHost || PrototypeMatch.Current == null) return;
            _appliedWeapons.Clear();
            foreach (var pair in _pendingWeapons)
            {
                var prior = GameplayConfig.GetWeapon(pair.Key); var next = pair.Value.config;
                // Another pending weapon may have claimed this ID since asynchronous validation.
                try { WeaponConfigService.Current.ValidateAmmoId(pair.Key, next.Ammo); }
                catch (Exception e)
                {
                    WeaponDebugStatus = "修改未应用：" + e.Message;
                    _appliedWeapons.Add(pair.Key);
                    continue;
                }
                if (InkPresentation.Current != null && !InkPresentation.Current.TryPrepareAmmo(next.Ammo, prior.Ammo))
                {
                    WeaponDebugStatus = "等待旧墨弹结束 · 已保留最新修改";
                    continue;
                }
                bool restart = prior.RequiresRestart(next);
                if (restart)
                    foreach (var player in PrototypeMatch.Current.Players)
                        if (player != null && player.IsSpawned && player.Snapshot.Value.HeroId == pair.Key) player.CancelForWeaponReload();
                WeaponConfigService.Current.Replace(pair.Key, next);
                Heroes.Replace(pair.Value.content);
                foreach (var player in PrototypeMatch.Current.Players)
                    if (player != null && player.IsSpawned && player.Snapshot.Value.HeroId == pair.Key) player.RefreshWeaponConfiguration();
                _appliedWeapons.Add(pair.Key);
                WeaponDebugStatus = restart ? "已应用 · 旧动作已取消并退回预留墨，请松开后重新射击" : "已应用 · 后续射击使用新参数";
            }
            foreach (int id in _appliedWeapons) _pendingWeapons.Remove(id);
        }
        void DrawWeaponDebugHud(PlayerSnapshot state)
        {
            Panel(new Rect(20, 180, 450, 128), new Color(.035f, .05f, .07f, .88f));
            GUI.Label(new Rect(32, 190, 426, 30), $"散布：水平 {state.CurrentSpread:0.00}° / 垂直 {state.CurrentVerticalSpread:0.00}° · {state.SpreadProgress:P0}", _small);
            GUI.Label(new Rect(32, 222, 426, 52), WeaponDebugStatus, _small);
            if (GUI.Button(new Rect(32, 272, 220, 27), "定位当前武器配置资产", _small))
            {
                var asset = WeaponConfigService.Current.Source(state.HeroId);
                Selection.activeObject = asset; EditorGUIUtility.PingObject(asset);
            }
        }
    }
}
#endif
