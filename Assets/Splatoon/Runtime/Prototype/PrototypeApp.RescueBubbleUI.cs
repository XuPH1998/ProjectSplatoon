using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        GUIStyle _bubblePrompt, _bubbleKey, _bubbleTimer;
        void DrawBubbleHud(PrototypePlayer local, PlayerSnapshot state)
        {
            if (local == null || !HasControl || PrototypeMatch.Current.State.Value.Phase == Splatoon.Networking.MatchPhase.Finished) return;
            _bubblePrompt ??= new GUIStyle(_label) { fontSize = 30, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _bubbleKey ??= new GUIStyle(_label) { fontSize = 42, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _bubbleTimer ??= new GUIStyle(_label) { fontSize = 27, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            if (state.IsBubble)
            {
                GUI.Box(new Rect(350, 215, 580, 105), GUIContent.none);
                GUI.Label(new Rect(355, 220, 570, 50), $"等待队友解救  {System.Math.Max(0, state.BubbleUntil - Manager.ServerTime.Time):0.0} 秒", _bubbleTimer);
                GUI.Label(new Rect(365, 271, 550, 36), "W A S D 缓慢移动 · 队友靠近按 F 解救", _small);
                return;
            }
            var target = local.BubbleInteractionTarget;
            if (target == null || !target.PresentedState.IsBubble) return;
            bool friendly = target.PresentedState.Team == state.Team;
            Color old = GUI.color;
            GUI.color = friendly ? new Color(.45f, 1, .75f) : new Color(1, .55f, .35f);
            GUI.Box(new Rect(435, 435, 410, 86), GUIContent.none);
            GUI.Box(new Rect(446, 446, 66, 64), GUIContent.none);
            GUI.Label(new Rect(446, 442, 66, 68), "F", _bubbleKey);
            GUI.Label(new Rect(520, 445, 310, 65), friendly ? "解救队友" : "彻底击杀", _bubblePrompt);
            GUI.color = old;
        }
    }
}
