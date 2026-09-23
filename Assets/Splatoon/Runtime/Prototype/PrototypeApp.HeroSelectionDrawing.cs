using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public static class HeroSelectionLayout
    {
        public static readonly Rect Window = new(49, 25, 1182, 670);
        public static readonly Rect Confirm = new(1028, 655, 185, 36);
        public static readonly Rect Close = new(1180, 42, 32, 32);
        public static readonly Rect CardViewport = new(68, 96, 568, 538);
        public static readonly Rect Equipment = new(644, 420, 570, 189);
        public static Rect Tab(int index) => new(644 + index * 277, 375, index==0?269:293, 34);
        public static Rect Stat(int index) => new(654 + index % 2 * 269, 202 + index / 2 * 55, 260, 47);
        public static Rect EquipmentCard(int index, int columns) => new(Equipment.x + index % columns * (Equipment.width / columns), Equipment.y + index / columns * 48, Equipment.width / columns - 7, 41);
        public static float ContentHeight(int count) => Mathf.Max(CardViewport.height, Mathf.CeilToInt(count / 3f) * 180-2);
        public static Rect Card(int index) => new(72 + index % 3 * 184, 100 + index / 3 * 180, 178, 174);
        public static Rect Portrait(int index) { var r = Card(index); return new Rect(r.x + 5, r.y + 2, 170, 170); }
        public static Matrix4x4 Matrix(float width, float height)
        {
            float scale = Mathf.Min(width / 1280f, height / 720f);
            return Matrix4x4.TRS(new Vector3((width - 1280 * scale) * .5f, (height - 720 * scale) * .5f), Quaternion.identity, new Vector3(scale, scale, 1));
        }
    }

    public sealed partial class PrototypeApp
    {
        readonly HeroSelectionStatsCache _heroStats = new();
        Vector2 _heroCardScroll; int _previewSubId=1,_previewSpecialId=1;
        GUIStyle _heroHeading, _heroName, _heroCaption, _heroValue, _heroNote, _heroBadge, _heroAction;
        static readonly Color HeroAccent = new(.12f, .95f, 1);
        static readonly Color HeroMuted = new(.74f, .79f, .86f);

        GUIStyle HeroStyle(int size, Color color, bool bold = false)
        {
            var style = new GUIStyle(_small) { fontSize = size, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                wordWrap = false, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            style.normal.textColor = color; return style;
        }
        void HeroStyles()
        {
            if (_heroHeading != null) return;
            _heroHeading = HeroStyle(35, Color.white, true); _heroHeading.fontStyle=FontStyle.BoldAndItalic;
            _heroName = HeroStyle(24, Color.white, true);
            _heroCaption = HeroStyle(15, HeroMuted);
            _heroValue = HeroStyle(25, Color.white, true);
            _heroNote = HeroStyle(13, HeroMuted);
            _heroBadge = HeroStyle(13, Color.white, true); _heroBadge.alignment = TextAnchor.MiddleCenter;
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
                Panel(new Rect(0, 0, Screen.width, Screen.height), new Color(.015f, .025f, .05f, .48f));
                GUI.matrix = HeroSelectionLayout.Matrix(Screen.width, Screen.height);
                UiCard(HeroSelectionLayout.Window, new Color(.065f, .10f, .155f, .96f));
                UiInk(new Rect(51,29,72,72),new Color(1,.25f,.67f));
                UiHeavyLabel(new Rect(110, 38, 180, 46), "选择配装", _heroHeading);
                Panel(new Rect(272,46,1,31),UiBorder);
                GUI.Label(new Rect(286, 46, 800, 32), _heroOrigin == HeroSelectionOrigin.Debug ? "调试切换 · 点击卡片查看主武器" :
                    _heroOrigin == HeroSelectionOrigin.SpawnArea ? "出生区换装 · 选择你的出战搭档" : "热身准备 · 选择你的出战搭档", _heroCaption);
                var close=HeroSelectionLayout.Close;UiLine(close.min+new Vector2(4,4),close.max-new Vector2(4,4),new Color(.85f,.9f,1),3);UiLine(new Vector2(close.x+4,close.yMax-4),new Vector2(close.xMax-4,close.y+4),new Color(.85f,.9f,1),3);
                if (GUI.Button(close, GUIContent.none, GUIStyle.none)) { _fireInputBlocked = true; CloseOverlay(); }
                int currentId = player.Snapshot.Value.HeroId, index = 0;
                var heroRows = LubanConfigService.Current.Tables.TbHero.DataList;
                _heroCardScroll = GUI.BeginScrollView(HeroSelectionLayout.CardViewport, _heroCardScroll,
                    new Rect(68, 96, 550, HeroSelectionLayout.ContentHeight(heroRows.Count)), false, false);
                bool editable = !player.HeroChangePending && !player.TeamChangePending;
                GUI.enabled = editable;
                foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
                {
                    var rect = HeroSelectionLayout.Card(index);
                    bool selected = hero.Id == _previewHeroId;
                    bool hover = rect.Contains(Event.current.mousePosition);
                    if(selected)UiGlow(rect,HeroAccent);else UiPanel(rect,hover?Color.white:UiBorder);
                    var portrait = Heroes.Get(hero.Id).Portrait;
                    UiPortrait(new Rect(rect.x+2,rect.y+2,rect.width-4,rect.height-4),portrait);
                    UiPortraitShade(new Rect(rect.x+2,rect.y+82,rect.width-4,rect.height-84));
                    float textX = rect.x + 10;
                    GUI.Label(new Rect(textX, rect.y + 128, 162, 25), hero.DisplayName, _uiStrong);
                    UiFit(new Rect(textX, rect.y + 152, 162, 20), hero.WeaponTypeName, _uiText);
                    if (hero.Id == currentId)
                    {
                        UiCard(new Rect(rect.x + 101, rect.y + 6, 74, 23), new Color(.18f,.22f,.29f,.94f));
                        GUI.Label(new Rect(rect.x + 101, rect.y + 6, 74, 23), "当前使用", _heroBadge);
                    }
                    else if (selected) { UiPanel(new Rect(rect.x + 126, rect.y + 6, 49, 23), HeroAccent); GUI.Label(new Rect(rect.x + 126, rect.y + 6, 49, 23), "预览", _heroAction); }
                    if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) { if (_previewHeroId != hero.Id) { _previewHeroId = hero.Id; var saved=PlayerLoadout.Remembered(hero.Id); _previewSubId=saved.SubWeaponId; _previewSpecialId=saved.SpecialWeaponId; } _fireInputBlocked = true; }
                    index++;
                }
                GUI.EndScrollView();
                var chosen = GameplayConfig.GetHero(_previewHeroId);
                var content = Heroes.Get(chosen.Id);
                UiCard(new Rect(643,94,571,269),UiSurface);
                var banner=new Rect(645,96,567,105);
                if(content.Portrait!=null)
                {
                    GUI.BeginGroup(banner);GUI.DrawTexture(new Rect(0,-130,567,567),content.Portrait,ScaleMode.ScaleAndCrop);
                    Panel(new Rect(0,0,567,105),new Color(.035f,.045f,.075f,.70f));
                    GUI.DrawTexture(new Rect(190,-85,390,390),content.Portrait,ScaleMode.ScaleAndCrop);GUI.EndGroup();
                    UiPortraitShade(banner);
                }
                GUI.Label(new Rect(660,151,105,38),chosen.DisplayName,_heroHeading);
                UiFit(new Rect(760,158,436,32),"/  "+chosen.WeaponTypeName,_heroName);
                var global = GameplayConfig.Global;
                var stats = _heroStats.Get(chosen.Id, WeaponConfigService.Current.Revision(chosen.Id), GameplayConfig.GetWeapon(chosen.Id), content.Profile,
                    global.AimCorrectionDistance, global.AimFarCorrectionDistance);
                for (int i = 0; i < stats.Length; i++)
                {
                    var tile = HeroSelectionLayout.Stat(i);
                    UiCard(tile, UiSurface);
                    UiGlyph(new Rect(tile.x+14,tile.y+11,25,25),100+i,new Color(.53f,.63f,.76f));
                    GUI.Label(new Rect(tile.x + 64, tile.y + 3, tile.width - 74, 20), new GUIContent(stats[i].Label, stats[i].Note), _heroCaption);
                    UiFit(new Rect(tile.x + 64, tile.y + 23, tile.width - 74, 23), stats[i].Value, _uiStrong);

                }
                DrawLoadoutChoices();
                GUI.enabled = true;
                UiInk(new Rect(943,560,257,157),new Color(.08f,.17f,.27f,.18f));
                Panel(new Rect(64, 649, 1148, 1), UiBorder);
                string unavailable = HeroSelectionUnavailableReason(_heroOrigin);
                string status = unavailable ?? (player.HeroChangePending || (!string.IsNullOrEmpty(player.HeroChangeMessage)&&player.HeroChangeMessage!="配装已确认")
                    ? player.HeroChangeMessage : "点击预览，确认后生效  ·  H / Esc 返回");
                UiFit(new Rect(74, 661, 930, 28), status, _heroCaption);
                bool canSelect = unavailable == null && !player.HeroChangePending && !player.TeamChangePending && !PlayerLoadout.From(player.Snapshot.Value).Matches(new PlayerLoadout(chosen.Id,_previewSubId,_previewSpecialId));
                if(canSelect)UiGlow(HeroSelectionLayout.Confirm,HeroAccent);
                UiPanel(HeroSelectionLayout.Confirm, canSelect ? HeroAccent : new Color(.24f, .32f, .39f));
                if(canSelect){UiInk(new Rect(995,642,57,57),HeroAccent);UiInk(new Rect(1189,650,35,40),HeroAccent);}
                GUI.enabled = canSelect;
                string action = player.HeroChangePending || player.TeamChangePending ? "切换中…" : unavailable != null ? "暂不可换装" : canSelect ? "确认配装  »" : "当前配装";
                if (GUI.Button(HeroSelectionLayout.Confirm, action, _heroAction)) { _fireInputBlocked = true; player.RequestLoadoutChange(new PlayerLoadout(chosen.Id,_previewSubId,_previewSpecialId), _heroOrigin); }
            }
            finally { GUI.enabled = enabled; GUI.color = color; GUI.matrix = matrix; }
        }
    }
}
