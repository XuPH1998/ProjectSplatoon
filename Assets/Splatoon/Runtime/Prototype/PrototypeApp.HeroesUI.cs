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
        bool _fireInputBlocked;
        MatchPhase? _overlayPhase;
        public GameplayOverlay Overlay => _overlay;
        public bool CanFireInput => HasControl && !_fireInputBlocked;
        public string HeroSelectionUnavailableReason(HeroSelectionOrigin origin)
        {
            var match = PrototypeMatch.Current; var player = PrototypePlayer.Local;
            if (!InRoom || Busy || match == null || player == null || !player.IsSpawned || !match.InitialSyncComplete) return "正在同步房间";
            var state = player.PresentedState;
            return HeroSelectionRules.Availability(state, origin, match.State.Value.Phase, HeroSelectionRules.Development,
                PrototypeArena.Current != null && PrototypeArena.Current.IsInHeroChangeZone(state.Team, state.Position));
        }
        public void OpenHeroSelection(HeroSelectionOrigin origin)
        {
            if (HeroSelectionUnavailableReason(origin) != null) return;
            var player = PrototypePlayer.Local;
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
                    var previous = _overlayPhase;
                    _overlayPhase = phase;
                    if (phase == MatchPhase.Finished) { _overlay = GameplayOverlay.RoomMenu; CaptureMouse(false); }
                    else if (phase == MatchPhase.Playing)
                    { _overlay = GameplayOverlay.Game; CaptureMouse(Application.isFocused); }
                    else if (phase == MatchPhase.Practice && previous == MatchPhase.Finished)
                    { _overlay = GameplayOverlay.RoomMenu; CaptureMouse(false); }
                }
                if (_overlay == GameplayOverlay.Heroes && _heroOrigin == HeroSelectionOrigin.SpawnArea &&
                    HeroSelectionUnavailableReason(_heroOrigin) != null) CloseOverlay();
                if (k != null && k.hKey.wasPressedThisFrame && !Busy && phase != MatchPhase.Finished)
                {
                    if (_overlay == GameplayOverlay.Heroes && _heroOrigin != HeroSelectionOrigin.Debug) CloseOverlay();
                    else OpenHeroSelection(phase == MatchPhase.Practice ? HeroSelectionOrigin.Warmup : HeroSelectionOrigin.SpawnArea);
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
            if (ScoreboardVisible) DrawScoreboard();
            DrawStartNotice();
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
