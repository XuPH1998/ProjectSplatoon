using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public enum GameplayOverlay { Game, RoomMenu, Weapons, Debug }
    public sealed partial class PrototypeApp
    {
        GameplayOverlay _overlay;
        WeaponSelectionOrigin _weaponOrigin;
        int _previewWeaponId = 1;
        bool _fireInputBlocked;
        MatchPhase? _overlayPhase;
        public GameplayOverlay Overlay => _overlay;
        public bool CanFireInput => HasControl && !_fireInputBlocked;
        public void OpenWeaponSelection(WeaponSelectionOrigin origin)
        {
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (!InRoom || Busy || match == null || player == null || match.State.Value.Phase == MatchPhase.Finished) return;
            if (origin == WeaponSelectionOrigin.Warmup && match.State.Value.Phase != MatchPhase.Practice) return;
            if (origin == WeaponSelectionOrigin.Debug && !WeaponSelectionRules.Development) return;
            _weaponOrigin = origin; _previewWeaponId = player.Snapshot.Value.WeaponId;
            _overlay = GameplayOverlay.Weapons; CaptureMouse(false);
        }
        public void CloseOverlay()
        {
            if (_overlay == GameplayOverlay.Weapons && _weaponOrigin == WeaponSelectionOrigin.Debug)
                _overlay = GameplayOverlay.Debug;
            else if (InRoom) CaptureMouse(true);
            else _overlay = GameplayOverlay.Game;
        }
        void UpdateOverlayInput()
        {
            if (Mouse.current == null || !Mouse.current.leftButton.isPressed) _fireInputBlocked = false;
            PrototypePlayer.Local?.RefreshWeaponChangeStatus();
            var k = Keyboard.current;
            var match = PrototypeMatch.Current;
            if (InRoom && match != null)
            {
                var phase = match.State.Value.Phase;
                if (_overlayPhase != phase)
                {
                    _overlayPhase = phase;
                    if (phase == MatchPhase.Finished) { _overlay = GameplayOverlay.RoomMenu; CaptureMouse(false); }
                    else if (_overlay == GameplayOverlay.Weapons && _weaponOrigin == WeaponSelectionOrigin.Warmup && phase != MatchPhase.Practice) CaptureMouse(true);
                }
                if (k != null && k.hKey.wasPressedThisFrame && !Busy && match.State.Value.Phase == MatchPhase.Practice)
                {
                    if (_overlay == GameplayOverlay.Weapons && _weaponOrigin == WeaponSelectionOrigin.Warmup) CloseOverlay();
                    else OpenWeaponSelection(WeaponSelectionOrigin.Warmup);
                }
            }
            else _overlayPhase = null;
            if (k != null && k.escapeKey.wasPressedThisFrame && !Busy)
            {
                if (_overlay == GameplayOverlay.Weapons || _overlay == GameplayOverlay.Debug) CloseOverlay();
                else if (InRoom) CaptureMouse(!_captured);
            }
        }
        private void OnGUI()
        {
            DrawAppGUI();
            if (!Ready) return;
            GUI.enabled = true;
            if (_overlay == GameplayOverlay.Weapons) DrawWeaponSelection();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_overlay == GameplayOverlay.Debug) DrawDebugWindow();
            if (GUI.Button(new Rect(24, 18, 118, 38), "DEBUG", _button))
            {
                if (_overlay == GameplayOverlay.Debug) CloseOverlay();
                else { _overlay = GameplayOverlay.Debug; CaptureMouse(false); }
            }
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void DrawDebugWindow()
        {
            Panel(new Rect(24, 65, 304, 178), new Color(.045f, .065f, .09f, .99f));
            GUI.Label(new Rect(42, 78, 190, 32), "开发调试", _label);
            if (GUI.Button(new Rect(266, 76, 42, 30), "×", _button)) CloseOverlay();
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            GUI.enabled = InRoom && !Busy && player != null && player.Snapshot.Value.Health > 0 && match != null && match.State.Value.Phase != MatchPhase.Finished;
            if (GUI.Button(new Rect(42, 122, 266, 44), "换枪", _button)) OpenWeaponSelection(WeaponSelectionOrigin.Debug);
            GUI.enabled = true;
            string text = !InRoom ? "进入房间后可换枪" : player != null && player.Snapshot.Value.Health <= 0 ? "重生后可换枪" : match != null && match.State.Value.Phase == MatchPhase.Finished ? "结算阶段不可换枪" : "仅更换自己的枪械，比赛继续运行";
            GUI.Label(new Rect(42, 182, 270, 48), text, _small);
        }
#endif
        void DrawWeaponSelection()
        {
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            if (player == null || match == null) return;
            var current = GameplayConfig.GetWeapon(player.Snapshot.Value.WeaponId);
            var selected = GameplayConfig.GetWeapon(_previewWeaponId);
            Panel(new Rect(125, 92, 1030, 570), new Color(.04f, .065f, .09f, .99f));
            GUI.Label(new Rect(150, 108, 850, 40), _weaponOrigin == WeaponSelectionOrigin.Debug ? "调试换枪" : "热身选择枪械", _label);
            if (GUI.Button(new Rect(1083, 107, 48, 34), "×", _button)) CloseOverlay();
            int row = 0;
            foreach (var w in LubanConfigService.Current.Tables.TbWeapon.DataList)
            {
                var rect = new Rect(150, 160 + row++ * 82, 327, 76);
                bool active = selected.Id == w.Id;
                Panel(rect, active ? new Color(.13f, .25f, .32f) : new Color(.10f, .13f, .17f));
                if (GUI.Button(rect, "", GUIStyle.none)) _previewWeaponId = w.Id;
                GUI.Label(new Rect(rect.x + 12, rect.y + 5, 300, 25), w.DisplayName + (w.Id == current.Id ? "  已装备" : ""), _label);
                WeaponText(new Rect(rect.x + 12, rect.y + 33, 305, 42), WeaponDisplay.ListSummary(w));
            }
            GUI.Label(new Rect(498, 159, 150, 30), "性能参数", _small);
            GUI.Label(new Rect(656, 159, 230, 30), "所选：" + selected.DisplayName, _small);
            GUI.Label(new Rect(898, 159, 230, 30), "当前：" + current.DisplayName, _small);
            var values = WeaponDisplay.Details(selected); var previous = WeaponDisplay.Details(current);
            for (int i = 0; i < values.Count; i++)
            {
                float y = 194 + i * 32;
                GUI.Label(new Rect(498, y, 156, 30), values[i].label, _small);
                GUI.color = values[i].value == previous[i].value ? Color.white : new Color(.5f, .88f, 1);
                WeaponText(new Rect(656, y, 232, 30), values[i].value); GUI.color = Color.white;
                WeaponText(new Rect(898, y, 232, 30), previous[i].value);
            }
            GUI.Label(new Rect(498, 551, 630, 45), "蓝色表示与当前枪械不同。涂地距离为水平试射目标。\nF 按 60Hz 计时；打开列表不会暂停比赛。", _small);
            GUI.enabled = !player.WeaponChangePending && player.Snapshot.Value.Health > 0 && selected.Id != current.Id;
            if (GUI.Button(new Rect(895, 604, 232, 40), player.WeaponChangePending ? "切换中…" : selected.Id == current.Id ? "已装备" : "装备", _button)) player.RequestWeaponChange(selected.Id, _weaponOrigin);
            GUI.enabled = true;
            GUI.Label(new Rect(150, 596, 725, 55), player.Snapshot.Value.Health <= 0 ? "重生后才能换枪" : player.WeaponChangePending || !string.IsNullOrEmpty(player.WeaponChangeMessage) ? player.WeaponChangeMessage : match.State.Value.Phase == MatchPhase.Practice ? "热身换枪补满墨量。H / Esc 关闭列表后试射。" : "对局换枪保留墨量。Esc 返回 DEBUG。", _small);
        }
        void WeaponText(Rect rect, string text)
        {
            var style = new GUIStyle(_small) { wordWrap = false };
            foreach (string line in text.Split('\n'))
                while (style.fontSize > 12 && style.CalcSize(new GUIContent(line)).x > rect.width) style.fontSize--;
            GUI.Label(rect, text, style);
        }
    }
}
