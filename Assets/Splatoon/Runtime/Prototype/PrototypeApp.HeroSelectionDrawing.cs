using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public static class HeroSelectionLayout
    {
        public static readonly Rect Window = new(60, 40, 1160, 640);
        public static readonly Rect Confirm = new(958, 616, 234, 42);
        public static readonly Rect Close = new(1146, 62, 46, 36);
        public static Rect Card(int index) => new(88 + index % 2 * 318, 154 + index / 2 * 144, 304, 132);
        public static Rect Portrait(int index) { var r = Card(index); return new Rect(r.x + 12, r.y + 16, 100, 100); }
        public static Matrix4x4 Matrix(float width, float height)
        {
            float scale = Mathf.Min(width / 1280f, height / 720f);
            return Matrix4x4.TRS(new Vector3((width - 1280 * scale) * .5f, (height - 720 * scale) * .5f), Quaternion.identity, new Vector3(scale, scale, 1));
        }
    }

    public sealed partial class PrototypeApp
    {
        readonly HeroSelectionStatsCache _heroStats = new();
        GUIStyle _heroHeading, _heroName, _heroCaption, _heroValue, _heroNote, _heroBadge, _heroAction;
        static readonly Color HeroAccent = new(.36f, .87f, .89f);
        static readonly Color HeroMuted = new(.62f, .69f, .79f);

        GUIStyle HeroStyle(int size, Color color, bool bold = false)
        {
            var style = new GUIStyle(_small) { fontSize = size, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            style.normal.textColor = color; return style;
        }
        void HeroStyles()
        {
            if (_heroHeading != null) return;
            _heroHeading = HeroStyle(30, Color.white, true);
            _heroName = HeroStyle(24, Color.white, true);
            _heroCaption = HeroStyle(15, HeroMuted);
            _heroValue = HeroStyle(25, Color.white, true);
            _heroNote = HeroStyle(13, HeroMuted);
            _heroBadge = HeroStyle(12, HeroAccent, true); _heroBadge.alignment = TextAnchor.MiddleCenter;
            _heroAction = HeroStyle(18, new Color(.055f, .11f, .16f), true); _heroAction.alignment = TextAnchor.MiddleCenter;
        }
        void DrawHeroSelection()
        {
            var player = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            if (player == null || match == null) return;
            HeroStyles();
            var matrix = GUI.matrix; var color = GUI.color; bool enabled = GUI.enabled;
            try
            {
                GUI.matrix = Matrix4x4.identity; GUI.color = Color.white;
                Panel(new Rect(0, 0, Screen.width, Screen.height), new Color(.015f, .025f, .05f, .78f));
                GUI.matrix = HeroSelectionLayout.Matrix(Screen.width, Screen.height);
                Panel(HeroSelectionLayout.Window, new Color(.055f, .075f, .115f, .99f));
                Panel(new Rect(88, 120, 1104, 1), new Color(.19f, .24f, .31f));
                GUI.Label(new Rect(88, 60, 560, 42), "选择英雄", _heroHeading);
                GUI.Label(new Rect(274, 67, 670, 32), _heroOrigin == HeroSelectionOrigin.Debug ? "调试切换 · 点击卡片查看主武器" :
                    _heroOrigin == HeroSelectionOrigin.SpawnArea ? "出生区换装 · 选择你的出战搭档" : "热身准备 · 选择你的出战搭档", _heroCaption);
                if (GUI.Button(HeroSelectionLayout.Close, "×", _button)) { _fireInputBlocked = true; CloseOverlay(); }
                int currentId = player.Snapshot.Value.HeroId, index = 0;
                foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
                {
                    var rect = HeroSelectionLayout.Card(index);
                    bool selected = hero.Id == _previewHeroId;
                    bool hover = rect.Contains(Event.current.mousePosition);
                    Panel(rect, selected ? HeroAccent : new Color(.18f, .23f, .31f));
                    Panel(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), selected ? new Color(.11f, .23f, .28f) : hover ? new Color(.13f, .18f, .25f) : new Color(.095f, .13f, .19f));
                    var portrait = Heroes.Get(hero.Id).Portrait;
                    if (portrait != null) GUI.DrawTexture(HeroSelectionLayout.Portrait(index), portrait, ScaleMode.ScaleToFit);
                    float textX = rect.x + 126;
                    GUI.Label(new Rect(textX, rect.y + 21, 160, 34), hero.DisplayName, _heroName);
                    GUI.Label(new Rect(textX, rect.y + 58, 166, 26), hero.WeaponTypeName, _heroCaption);
                    if (hero.Id == currentId)
                    {
                        Panel(new Rect(textX, rect.y + 94, 76, 23), new Color(.12f, .29f, .33f));
                        GUI.Label(new Rect(textX, rect.y + 94, 76, 23), "当前使用", _heroBadge);
                    }
                    else if (selected) GUI.Label(new Rect(textX, rect.y + 94, 76, 23), "已选中", _heroBadge);
                    if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) { _previewHeroId = hero.Id; _fireInputBlocked = true; }
                    index++;
                }
                var chosen = GameplayConfig.GetHero(_previewHeroId);
                var content = Heroes.Get(chosen.Id);
                GUI.Label(new Rect(742, 142, 430, 34), chosen.DisplayName + "  /  " + chosen.WeaponTypeName, _heroName);
                var global = GameplayConfig.Global;
                var stats = _heroStats.Get(chosen.Id, WeaponConfigService.Current.Revision(chosen.Id), GameplayConfig.GetWeapon(chosen.Id), content.Profile,
                    global.AimCorrectionDistance, global.AimFarCorrectionDistance);
                for (int i = 0; i < stats.Length; i++)
                {
                    float y = 190 + i * 97;
                    Panel(new Rect(738, y, 454, 88), new Color(.09f, .12f, .18f));
                    Panel(new Rect(738, y, 3, 88), i == 3 ? HeroAccent : new Color(.23f, .3f, .39f));
                    GUI.Label(new Rect(756, y + 8, 104, 27), stats[i].Label, _heroCaption);
                    GUI.Label(new Rect(870, y + 7, 308, 36), stats[i].Value, _heroValue);
                    GUI.Label(new Rect(756, y + 50, 422, 25), stats[i].Note, _heroNote);
                }
                Panel(new Rect(88, 602, 1104, 1), new Color(.19f, .24f, .31f));
                string unavailable = HeroSelectionUnavailableReason(_heroOrigin);
                string status = unavailable ?? (player.HeroChangePending || !string.IsNullOrEmpty(player.HeroChangeMessage)
                    ? player.HeroChangeMessage : match.State.Value.Phase == MatchPhase.Practice ? "热身切换补满墨量 · H / Esc 关闭后试射" :
                    _heroOrigin == HeroSelectionOrigin.SpawnArea ? "保留生命与墨量 · H / Esc 返回比赛" : "对局切换保留墨量 · Esc 返回调试菜单");
                GUI.Label(new Rect(88, 618, 838, 38), status, _heroCaption);
                bool canSelect = unavailable == null && !player.HeroChangePending && !player.TeamChangePending && chosen.Id != currentId;
                Panel(HeroSelectionLayout.Confirm, canSelect ? HeroAccent : new Color(.24f, .32f, .39f));
                GUI.enabled = canSelect;
                string action = player.HeroChangePending ? "切换中…" : chosen.Id == currentId ? "当前英雄" : "选择英雄";
                if (GUI.Button(HeroSelectionLayout.Confirm, action, _heroAction)) { _fireInputBlocked = true; player.RequestHeroChange(chosen.Id, _heroOrigin); }
            }
            finally { GUI.enabled = enabled; GUI.color = color; GUI.matrix = matrix; }
        }
    }
}
