using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        readonly Dictionary<ulong, string> _admittedNames = new();
        string _usernameInput, _usernameStatus = "";
        GUIStyle _nameTag, _scoreName;
        Camera _nameCamera;
        public string SavedUsername { get; private set; }

        public bool SaveUsername(string input)
        {
            if (InRoom || Busy) return false;
            if (!PlayerNames.TryNormalize(input, out var name, out var error))
            { _usernameStatus = error; return false; }
            PlayerNames.Save(name); SavedUsername = _usernameInput = name; _usernameStatus = "已保存";
            return true;
        }
        public string AdmittedUsername(ulong clientId) => _admittedNames.TryGetValue(clientId, out var name) ? name : $"玩家 {clientId + 1}";

        void DrawUsernameField()
        {
            GUI.Label(new Rect(50, 96, 76, 34), "用户名", _small);
            GUI.enabled = !Busy;
            string value = GUI.TextField(new Rect(125, 96, 270, 34), _usernameInput ?? "", _lobbyField);
            if (value != _usernameInput) { _usernameInput = value; _usernameStatus = ""; }
            if (GUI.Button(new Rect(405, 96, 74, 34), "保存", _small)) SaveUsername(_usernameInput);
            GUI.enabled = true;
            GUI.Label(new Rect(489, 98, 350, 48), string.IsNullOrEmpty(_usernameStatus) ? "1～16 字 · 入房时自动保存" : _usernameStatus, _small);
        }

        public static bool NameTagEligible(bool local, PlayerSnapshot state) => !local && state.Health > 0 && !state.ShowsSwimBody;

        public static bool TryNameTagPosition(Camera camera, Vector3 head, Vector3 anchor, out Vector2 guiPosition)
        {
            guiPosition = default;
            if (camera == null) return false;
            Vector3 viewport = camera.WorldToViewportPoint(anchor);
            if (viewport.z <= 0 || viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane ||
                viewport.x <= 0 || viewport.x >= 1 || viewport.y <= 0 || viewport.y >= 1) return false;
            // Trace to the actual head, not the elevated label: labels must not reveal heads behind cover.
            if (Physics.Linecast(camera.transform.position, head, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return false;
            Vector3 screen = camera.WorldToScreenPoint(anchor);
            guiPosition = new Vector2(screen.x * 1280f / Screen.width, (Screen.height - screen.y) * 720f / Screen.height);
            return true;
        }

        void NameStyles()
        {
            _nameTag ??= new GUIStyle(_small) { fontSize = 18, alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = false, clipping = TextClipping.Clip };
            _scoreName ??= new GUIStyle(_label) { richText = false, wordWrap = false, clipping = TextClipping.Clip };
        }

        // Never split a surrogate pair when fitting user text to a fixed-width scoreboard column.
        public static string FitPlayerName(string name, string suffix, GUIStyle style, float width)
        {
            if (style.CalcSize(new GUIContent(name + suffix)).x <= width) return name + suffix;
            int length = name.Length;
            while (length > 0)
            {
                length--;
                if (length > 0 && char.IsLowSurrogate(name[length]) && char.IsHighSurrogate(name[length - 1])) length--;
                string fitted = name.Substring(0, length) + "…" + suffix;
                if (style.CalcSize(new GUIContent(fitted)).x <= width) return fitted;
            }
            return "…" + suffix;
        }

        void DrawPlayerNames()
        {
            if (Busy || Event.current.type != EventType.Repaint) return;
            if (_nameCamera == null || !_nameCamera.isActiveAndEnabled) _nameCamera = Camera.main;
            NameStyles();
            var old = GUI.color;
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                if (player == null || !player.IsSpawned || !NameTagEligible(player == PrototypePlayer.Local, player.PresentedState)) continue;
                if (!TryNameTagPosition(_nameCamera, player.NameHeadPosition, player.NameTagPosition, out var point)) continue;
                string text = FitPlayerName(player.DisplayName, "", _nameTag, 300);
                var rect = new Rect(point.x - 150, point.y - 28, 300, 28);
                GUI.color = new Color(.025f, .025f, .035f, .95f);
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                    if (x != 0 || y != 0) GUI.Label(new Rect(rect.x + x, rect.y + y, rect.width, rect.height), text, _nameTag);
                GUI.color = PrototypeArena.TeamColor(player.PresentedState.Team);
                GUI.Label(rect, text, _nameTag);
            }
            GUI.color = old;
        }
    }
}
