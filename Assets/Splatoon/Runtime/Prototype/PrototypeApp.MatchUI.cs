using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        public const double StartNoticeSeconds = 2;
        PrototypeMatch _feedbackMatch;
        uint? _noticedRound;
        AudioSource _startAudio;
        AudioClip _startClip;
        GUIStyle _scoreHeading, _scoreNumber, _startTitle, _centeredSmall;
        public bool ScoreboardVisible => HasControl && Keyboard.current != null && Keyboard.current.tabKey.isPressed &&
            PrototypeMatch.Current != null && PrototypeMatch.Current.State.Value.Phase == MatchPhase.Playing;
        public static bool StartNoticeActive(MatchStateSnapshot state, double now) =>
            state.Phase == MatchPhase.Playing && now >= state.StartsAt && now - state.StartsAt < StartNoticeSeconds;

        void UpdateMatchFeedback()
        {
            var match = InRoom ? PrototypeMatch.Current : null;
            if (_feedbackMatch != match) { _feedbackMatch = match; _noticedRound = null; }
            if (match == null) return;
            var state = match.State.Value;
            if (state.Phase != MatchPhase.Playing || _noticedRound == state.Round) return;
            // Wait for the server clock if a start snapshot arrives slightly ahead of it.
            if (Manager.ServerTime.Time < state.StartsAt) return;
            _noticedRound = state.Round;
            if (!StartNoticeActive(state, Manager.ServerTime.Time)) return;
            if (_startAudio == null)
            { _startAudio = gameObject.AddComponent<AudioSource>(); _startAudio.playOnAwake = false; _startAudio.spatialBlend = 0; }
            if (_startClip == null)
            {
                const int rate = 22050; const float duration = .5f;
                var samples = new float[(int)(rate * duration)];
                for (int i = 0; i < samples.Length; i++)
                {
                    float t = i / (float)rate;
                    float envelope = Mathf.Clamp01(t / .012f) * Mathf.Pow(1 - t / duration, 2);
                    samples[i] = envelope * (Mathf.Sin(2 * Mathf.PI * 523.25f * t) + Mathf.Sin(2 * Mathf.PI * 659.25f * t) + Mathf.Sin(2 * Mathf.PI * 783.99f * t)) / 3;
                }
                _startClip = AudioClip.Create("游戏开始", samples.Length, 1, rate, false); _startClip.SetData(samples, 0);
            }
            _startAudio.PlayOneShot(_startClip, .65f);
        }
        void MatchStyles()
        {
            if (_scoreHeading != null) return;
            _scoreHeading = new GUIStyle(_label) { fontSize = 28, fontStyle = FontStyle.Bold };
            _scoreNumber = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _startTitle = new GUIStyle(_title) { fontSize = 76, alignment = TextAnchor.MiddleCenter };
            _centeredSmall = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
        }
        void DrawScoreboard()
        {
            MatchStyles();
            Panel(new Rect(96, 156, 1088, 410), new Color(.025f, .04f, .065f, .95f));
            DrawTeamScore(1, 120); DrawTeamScore(2, 660);
            GUI.Label(new Rect(96, 530, 1088, 28), "本局战绩 · K 击杀 / D 死亡 / A 助攻 · 松开 Tab 关闭", _centeredSmall);
        }
        void DrawTeamScore(byte team, float x)
        {
            NameStyles();
            var color = PrototypeArena.TeamColor(team);
            Panel(new Rect(x, 176, 500, 5), color);
            GUI.color = color; GUI.Label(new Rect(x + 12, 191, 280, 40), team == 1 ? "粉队" : "蓝队", _scoreHeading); GUI.color = Color.white;
            for (int i = 0; i < 3; i++) GUI.Label(new Rect(x + 320 + i * 56, 191, 56, 40), new[] { "K", "D", "A" }[i], _scoreNumber);
            var players = PrototypePlayer.ByOwner.Values.Where(p => p != null && p.IsSpawned && p.Snapshot.Value.Team == team)
                .OrderBy(p => p.Snapshot.Value.Slot).ThenBy(p => p.PlayerId);
            int row = 0;
            foreach (var player in players)
            {
                if (row == Combat.TeamSelectionRules.Capacity) break;
                float y = 242 + row++ * 68;
                bool local = player == PrototypePlayer.Local;
                Panel(new Rect(x, y, 500, 60), local ? new Color(color.r, color.g, color.b, .18f) : new Color(1, 1, 1, .04f));
                if (local) Panel(new Rect(x, y, 4, 60), color);
                GUI.Label(new Rect(x + 14, y + 2, 300, 30),
                    new GUIContent(FitPlayerName(player.DisplayName, local ? "  ·  你" : "", _scoreName, 300), player.DisplayName), _scoreName);
                GUI.Label(new Rect(x + 14, y + 33, 300, 25), GameplayConfig.GetHero(player.Snapshot.Value.HeroId).DisplayName, _small);
                var stats = player.Stats.Value;
                if (stats.Round != PrototypeMatch.Current.State.Value.Round) stats = default;
                int[] values = { stats.Kills, stats.Deaths, stats.Assists };
                for (int i = 0; i < 3; i++) GUI.Label(new Rect(x + 320 + i * 56, y + 10, 56, 40), values[i].ToString(), _scoreNumber);
            }
        }
        void DrawStartNotice()
        {
            var match = PrototypeMatch.Current;
            if (!InRoom || Busy || Manager == null || match == null || !StartNoticeActive(match.State.Value, Manager.ServerTime.Time)) return;
            MatchStyles();
            float elapsed = (float)(Manager.ServerTime.Time - match.State.Value.StartsAt);
            float alpha = Mathf.Clamp01(elapsed / .07f) * Mathf.Clamp01((2 - elapsed) / .3f);
            float scale = elapsed < .16f ? Mathf.Lerp(.8f, 1.07f, elapsed / .16f) : Mathf.Lerp(1.07f, 1, Mathf.Clamp01((elapsed - .16f) / .18f));
            var matrix = GUI.matrix; var old = GUI.color;
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), new Vector2(640, 336));
            GUI.color = new Color(1, 1, 1, alpha);
            Panel(new Rect(300, 247, 680, 178), new Color(.025f, .04f, .065f, .96f * alpha));
            Panel(new Rect(300, 247, 340, 8), new Color(PrototypeArena.Pink.r, PrototypeArena.Pink.g, PrototypeArena.Pink.b, alpha));
            Panel(new Rect(640, 247, 340, 8), new Color(PrototypeArena.Blue.r, PrototypeArena.Blue.g, PrototypeArena.Blue.b, alpha));
            GUI.Label(new Rect(300, 260, 680, 113), "游戏开始！", _startTitle);
            GUI.Label(new Rect(300, 378, 680, 30), "为你的队伍涂出胜利", _centeredSmall);
            GUI.matrix = matrix; GUI.color = old;
        }
    }
}
