using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public enum GameplayOverlay { Game, RoomMenu, Heroes, Debug }
    public sealed partial class PrototypeApp
    {
        GameplayOverlay _overlay;
        HeroSelectionOrigin _heroOrigin;
        int _previewHeroId = 1;
        Vector2 _heroDetailsScroll;
        bool _fireInputBlocked;
        MatchPhase? _overlayPhase;
        public GameplayOverlay Overlay => _overlay;
        public bool CanFireInput => HasControl && !_fireInputBlocked;
        public void OpenHeroSelection(HeroSelectionOrigin origin)
        {
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (!InRoom || Busy || match == null || player == null || match.State.Value.Phase == MatchPhase.Finished) return;
            if (origin == HeroSelectionOrigin.Warmup && match.State.Value.Phase != MatchPhase.Practice) return;
            if (origin == HeroSelectionOrigin.Debug && !HeroSelectionRules.Development) return;
            _heroOrigin = origin; _previewHeroId = player.Snapshot.Value.HeroId;
            _overlay = GameplayOverlay.Heroes; CaptureMouse(false);
        }
        public void CloseOverlay()
        {
            if (_overlay == GameplayOverlay.Heroes && _heroOrigin == HeroSelectionOrigin.Debug)
                _overlay = GameplayOverlay.Debug;
            else if (InRoom) CaptureMouse(true);
            else _overlay = GameplayOverlay.Game;
        }
        void UpdateOverlayInput()
        {
            if (Mouse.current == null || !Mouse.current.leftButton.isPressed) _fireInputBlocked = false;
            PrototypePlayer.Local?.RefreshHeroChangeStatus();
            PrototypePlayer.Local?.RefreshTeamChangeStatus();
            var k = Keyboard.current;
            var match = PrototypeMatch.Current;
            if (InRoom && match != null)
            {
                var phase = match.State.Value.Phase;
                if (_overlayPhase != phase)
                {
                    _overlayPhase = phase;
                    if (phase == MatchPhase.Finished) { _overlay = GameplayOverlay.RoomMenu; CaptureMouse(false); }
                    else if (_overlay == GameplayOverlay.Heroes && _heroOrigin == HeroSelectionOrigin.Warmup && phase != MatchPhase.Practice) CaptureMouse(true);
                }
                if (k != null && k.hKey.wasPressedThisFrame && !Busy && match.State.Value.Phase == MatchPhase.Practice)
                {
                    if (_overlay == GameplayOverlay.Heroes && _heroOrigin == HeroSelectionOrigin.Warmup) CloseOverlay();
                    else OpenHeroSelection(HeroSelectionOrigin.Warmup);
                }
            }
            else _overlayPhase = null;
            if (k != null && k.escapeKey.wasPressedThisFrame && !Busy)
            {
                if (_overlay == GameplayOverlay.Heroes || _overlay == GameplayOverlay.Debug) CloseOverlay();
                else if (InRoom) CaptureMouse(!_captured);
            }
        }
        private void OnGUI()
        {
            DrawAppGUI();
            if (!Ready) return;
            GUI.enabled = true;
            if (_overlay == GameplayOverlay.Heroes) DrawHeroSelection();
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
            if (GUI.Button(new Rect(42, 122, 266, 44), "切换英雄", _button)) OpenHeroSelection(HeroSelectionOrigin.Debug);
            GUI.enabled = true;
            string text = !InRoom ? "进入房间后可切换英雄" : player != null && player.Snapshot.Value.Health <= 0 ? "重生后可切换英雄" : match != null && match.State.Value.Phase == MatchPhase.Finished ? "结算阶段不可切换英雄" : "仅更换自己的英雄，比赛继续运行";
            GUI.Label(new Rect(42, 182, 270, 48), text, _small);
        }
#endif
        void DrawHeroSelection()
        {
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            if (player == null || match == null) return;
            var current = GameplayConfig.GetHero(player.Snapshot.Value.HeroId);
            var selected = GameplayConfig.GetHero(_previewHeroId);
            Panel(new Rect(125, 92, 1030, 570), new Color(.04f, .065f, .09f, .99f));
            GUI.Label(new Rect(150, 108, 850, 40), _heroOrigin == HeroSelectionOrigin.Debug ? "调试切换英雄" : "热身选择英雄", _label);
            if (GUI.Button(new Rect(1083, 107, 48, 34), "×", _button)) CloseOverlay();
            int row = 0;
            foreach (var w in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                var rect = new Rect(150, 160 + row++ * 82, 327, 80);
                bool active = selected.Id == w.Id;
                Panel(rect, active ? new Color(.13f, .25f, .32f) : new Color(.10f, .13f, .17f));
                if (GUI.Button(rect, "", GUIStyle.none)) _previewHeroId = w.Id;
                GUI.Label(new Rect(rect.x + 12, rect.y + 2, 300, 32), w.DisplayName + (w.Id == current.Id ? "  当前英雄" : ""), _label);
                HeroText(new Rect(rect.x + 12, rect.y + 30, 305, 47), WeaponDisplay.ListSummary(w));
            }
            GUI.Label(new Rect(498, 159, 150, 30), "性能参数", _small);
            GUI.Label(new Rect(656, 159, 230, 30), "所选：" + selected.DisplayName, _small);
            GUI.Label(new Rect(898, 159, 230, 30), "当前：" + current.DisplayName, _small);
            var values = WeaponDisplay.Details(selected); var previous = WeaponDisplay.Details(current);
            _heroDetailsScroll = GUI.BeginScrollView(new Rect(490, 194, 642, 350), _heroDetailsScroll, new Rect(0, 0, 620, values.Count * 32));
            for (int i = 0; i < values.Count; i++)
            {
                float y = i * 32;
                GUI.Label(new Rect(8, y, 156, 30), values[i].label, _small);
                GUI.color = values[i].value == previous[i].value ? Color.white : new Color(.5f, .88f, 1);
                HeroText(new Rect(166, y, 223, 30), values[i].value); GUI.color = Color.white;
                HeroText(new Rect(398, y, 220, 30), previous[i].value);
            }
            GUI.EndScrollView();
            GUI.Label(new Rect(498, 551, 630, 45), "蓝色表示与当前英雄不同。涂地距离为水平射击参考。\nF 按 60Hz 计时；打开列表不会暂停比赛。", _small);
            GUI.enabled = !player.HeroChangePending && !player.TeamChangePending && player.Snapshot.Value.Health > 0 && selected.Id != current.Id;
            if (GUI.Button(new Rect(895, 604, 232, 40), player.HeroChangePending ? "切换中…" : selected.Id == current.Id ? "当前英雄" : "选择英雄", _button)) player.RequestHeroChange(selected.Id, _heroOrigin);
            GUI.enabled = true;
            GUI.Label(new Rect(150, 596, 725, 55), player.Snapshot.Value.Health <= 0 ? "重生后才能切换英雄" : player.HeroChangePending || !string.IsNullOrEmpty(player.HeroChangeMessage) ? player.HeroChangeMessage : match.State.Value.Phase == MatchPhase.Practice ? "热身切换英雄补满墨量。H / Esc 关闭列表后试射。" : "对局切换英雄保留墨量。Esc 返回 DEBUG。", _small);
        }
        void HeroText(Rect rect, string text)
        {
            var style = new GUIStyle(_small) { wordWrap = false };
            foreach (string line in text.Split('\n'))
                while (style.fontSize > 12 && style.CalcSize(new GUIContent(line)).x > rect.width) style.fontSize--;
            while (style.fontSize > 12 && style.CalcHeight(new GUIContent(text), rect.width) > rect.height) style.fontSize--;
            GUI.Label(rect, text, style);
        }
    }
}
