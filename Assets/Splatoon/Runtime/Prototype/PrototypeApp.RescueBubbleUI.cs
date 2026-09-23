using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        GUIStyle _bubblePrompt, _bubbleKey, _bubbleTimer, _bubbleRescueHint, _bubbleMoveHint;
        void DrawBubbleHud(PrototypePlayer local, PlayerSnapshot state)
        {
            if (local == null || !HasControl || PrototypeMatch.Current.State.Value.Phase == Splatoon.Networking.MatchPhase.Finished) return;
            _bubblePrompt ??= new GUIStyle(_label) { fontSize = 30, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _bubbleKey ??= new GUIStyle(_label) { fontSize = 42, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _bubbleTimer ??= new GUIStyle(_label) { fontSize = 27, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            if (state.IsBubble)
            {
                _bubbleRescueHint ??= new GUIStyle(_label) { fontSize = 36, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _bubbleRescueHint.normal.textColor = new Color(1f, .85f, .3f);
                _bubbleMoveHint ??= new GUIStyle(_label) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
                UiCard(new Rect(310, 205, 660, 142), UiSurface);
                Panel(new Rect(310, 205, 660, 4), new Color(1f, .85f, .3f));
                GUI.Label(new Rect(325, 217, 630, 52), "靠近队友，等待解救", _bubbleRescueHint);
                GUI.Label(new Rect(325, 271, 630, 36), $"剩余等待时间  {System.Math.Max(0, state.BubbleUntil - Manager.ServerTime.Time):0.0} 秒", _bubbleTimer);
                GUI.Label(new Rect(325, 307, 630, 32), "W A S D 缓慢移动 · 队友靠近按 F 解救", _bubbleMoveHint);
                return;
            }
            var target = local.BubbleInteractionTarget;
            if (target == null || !target.PresentedState.IsBubble) return;
            bool friendly = target.PresentedState.Team == state.Team;
            Color old = GUI.color;
            GUI.color = friendly ? new Color(.45f, 1, .75f) : new Color(1, .55f, .35f);
            UiCard(new Rect(495, 382, 290, 50), UiSurface);
            GUI.Label(new Rect(505, 387, 40, 38), "F", _uiStrong);
            GUI.Label(new Rect(550, 387, 225, 38), friendly ? "解救队友" : "彻底击杀", _uiStrong);
            GUI.color = old;
        }
    }
}
