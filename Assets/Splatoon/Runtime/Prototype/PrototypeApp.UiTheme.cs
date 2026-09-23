using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public static class CombatUiLayout
    {
        public static float Scale(float width, float height) => Mathf.Min(width / 1280f, height / 720f);
        public static Rect Bounds(float width, float height)
        {
            float scale = Scale(width, height);
            return new Rect((1280 - width / scale) * .5f, (720 - height / scale) * .5f, width / scale, height / scale);
        }
        public static Vector2 Viewport(Vector2 point, float width, float height)
        {
            var bounds = Bounds(width, height);
            return new Vector2(bounds.x + point.x * bounds.width, bounds.y + (1 - point.y) * bounds.height);
        }
        public static Rect Vitals(float width, float height) { var b = Bounds(width, height); return new Rect(b.x + 20, b.yMax - 180, 372, 133); }
        public static Rect Skill(float width, float height, int index) { var b = Bounds(width, height); return index==0?new Rect(b.xMax-378,b.yMax-184,182,140):new Rect(b.xMax-188,b.yMax-184,172,140); }
    }

    public sealed partial class PrototypeApp
    {
        static readonly Color UiSurface = new(.070f, .103f, .153f, .94f);
        static readonly Color HudSurface = new(.26f, .28f, .33f, .82f);
        static readonly Color UiBorder = new(.42f, .50f, .60f, .65f);
        static readonly Color UiWarning = new(1, .78f, .30f);
        Texture2D _uiRound, _uiFade, _uiInk, _uiIcons, _uiWing, _uiProgress;
        int _uiProgressPercent=-1;
        GUIStyle _uiText, _uiMuted, _uiStrong, _uiClock, _uiScore, _uiRingValue, _uiCenter, _uiOption, _uiWrap;
        void UiStyles()
        {
            if (_uiRound != null) return;
            HeroStyles();
            _uiIcons=Resources.Load<Texture2D>("CompetitiveUi/Icons");
            _uiRound = new Texture2D(64, 64, TextureFormat.RGBA32, false) { name = "UI rounded gradient", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var pixels = new Color[64 * 64];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            {
                float shade=Mathf.Lerp(.68f,1,y/63f);
                pixels[y * 64 + x] = new Color(shade,shade,shade,1);
            }
            _uiRound.SetPixels(pixels); _uiRound.Apply(false, true);
            _uiWing=new Texture2D(128,64,TextureFormat.RGBA32,false){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            var wing=new Color[128*64];
            for(int y=0;y<64;y++)for(int x=0;x<128;x++)
            {
                float cut=127-y*.39f;float alpha=Mathf.Clamp01(cut-x);
                if(x<22){float dy=Mathf.Max(22-y,Mathf.Max(0,y-41));alpha*=Mathf.Clamp01(23-new Vector2(22-x,dy).magnitude);}
                float shade=Mathf.Lerp(.84f,1,y/63f);wing[y*128+x]=new Color(shade,shade,shade,alpha);
            }
            _uiWing.SetPixels(wing);_uiWing.Apply(false,true);
            _uiProgress=new Texture2D(128,128,TextureFormat.RGBA32,false){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            _uiFade=new Texture2D(1,64,TextureFormat.RGBA32,false){hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp};
            var fade=new Color[64];for(int i=0;i<64;i++)fade[i]=new Color(.018f,.029f,.052f,Mathf.Lerp(.98f,0,i/63f));_uiFade.SetPixels(fade);_uiFade.Apply(false,true);
            _uiInk=new Texture2D(128,128,TextureFormat.RGBA32,false){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            var ink=new Color[128*128];
            for(int y=0;y<128;y++)for(int x=0;x<128;x++)
            {
                float dx=x-64,dy=y-64,a=Mathf.Atan2(dy,dx),r=Mathf.Sqrt(dx*dx+dy*dy);
                float edge=24+6*Mathf.Sin(a*5)+4*Mathf.Cos(a*9)+13*Mathf.Pow(Mathf.Max(0,Mathf.Sin(a*7)),12);
                float alpha=Mathf.Clamp01(edge-r);
                for(int j=0;j<18;j++){float angle=j*2.39996f,dist=38+j%4*6;var p=new Vector2(64+Mathf.Cos(angle)*dist,64+Mathf.Sin(angle)*dist);alpha=Mathf.Max(alpha,Mathf.Clamp01(1+j%3-(new Vector2(x,y)-p).magnitude));}
                ink[y*128+x]=new Color(1,1,1,alpha);
            }
            _uiInk.SetPixels(ink);_uiInk.Apply(false,true);
            _uiText = HeroStyle(16, Color.white);
            _uiMuted = HeroStyle(14, HeroMuted);
            _uiStrong = HeroStyle(21, Color.white, true);
            _uiClock = HeroStyle(38, Color.white, true); _uiClock.fontStyle=FontStyle.BoldAndItalic; _uiClock.alignment = TextAnchor.MiddleCenter;
            _uiScore=HeroStyle(27,Color.white,true);_uiScore.fontStyle=FontStyle.BoldAndItalic;_uiScore.alignment=TextAnchor.MiddleCenter;
            _uiRingValue=HeroStyle(21,Color.white,true);_uiRingValue.alignment=TextAnchor.MiddleCenter;
            _uiCenter = HeroStyle(14, Color.white, true); _uiCenter.alignment = TextAnchor.MiddleCenter;
            _uiOption=HeroStyle(14,Color.white,true);
            _uiWrap = HeroStyle(14, Color.white); _uiWrap.wordWrap = true;
        }
        void UiPanel(Rect rect, Color color, float radius=7)
        {
            UiStyles();GUI.DrawTexture(rect,_uiRound,ScaleMode.StretchToFill,true,0,color,0,Mathf.Min(radius,rect.height*.5f));
        }
        void UiCard(Rect rect, Color color,float radius=8)
        {
            UiPanel(new Rect(rect.x-2,rect.y+3,rect.width+4,rect.height+4),new Color(0,0,0,.12f),radius+2);
            UiPanel(rect, UiBorder,radius);
            UiPanel(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), color,radius-1);
        }
        void UiProgressRing(Rect rect,float fraction)
        {
            int percent=Mathf.RoundToInt(Mathf.Clamp01(fraction)*100);
            if(Event.current.type==EventType.Repaint&&percent!=_uiProgressPercent)
            {
                _uiProgressPercent=percent;var pixels=new Color[128*128];
                for(int y=0;y<128;y++)for(int x=0;x<128;x++)
                {
                    float dx=x-63.5f,dy=y-63.5f,r=Mathf.Sqrt(dx*dx+dy*dy),angle=Mathf.Repeat(Mathf.Atan2(dx,dy),Mathf.PI*2);
                    Color c=angle<=percent*.01f*Mathf.PI*2?new Color(.18f,.85f,.96f):new Color(.13f,.18f,.24f);c.a=Mathf.Clamp01(7-Mathf.Abs(r-55));pixels[y*128+x]=c;
                }
                _uiProgress.SetPixels(pixels);_uiProgress.Apply(false,false);
            }
            GUI.DrawTexture(rect,_uiProgress);
        }
        void UiHeavyLabel(Rect rect,string text,GUIStyle style)
        {
            GUI.Label(new Rect(rect.x+.7f,rect.y,rect.width,rect.height),text,style);
            GUI.Label(new Rect(rect.x,rect.y+.4f,rect.width,rect.height),text,style);
            GUI.Label(rect,text,style);
        }
        void UiGlow(Rect rect,Color color)
        {
            for(int i=7;i>0;i--){var c=color;c.a=.025f;UiPanel(new Rect(rect.x-i,rect.y-i,rect.width+2*i,rect.height+2*i),c);}
            UiPanel(rect,color);
        }
        void UiInk(Rect rect,Color color)
        {
            if(_uiIcons!=null){bool pink=color.r>color.b;var tint=pink?new Color(1,1,1,color.a):new Color(Mathf.Max(.2f,color.r),color.g,color.b,color.a);UiAtlas(rect,pink?19:18,tint);return;}
            UiStyles();var old=GUI.color;GUI.color=color;GUI.DrawTexture(rect,_uiInk);GUI.color=old;
        }
        void UiAtlas(Rect rect,int index,Color color)
        {
            var old=GUI.color;GUI.color=color;
            GUI.DrawTextureWithTexCoords(rect,_uiIcons,new Rect((index%5+.04f)/5,1-(index/5+.98f)/4,.92f/5,.94f/4));GUI.color=old;
        }
        void UiKey(Rect rect,string key)
        {
            UiPanel(rect,new Color(.78f,.82f,.9f,.8f));UiPanel(new Rect(rect.x+1,rect.y+1,rect.width-2,rect.height-2),new Color(.20f,.22f,.26f,.9f));GUI.Label(rect,key,_uiCenter);
        }
        void UiPortrait(Rect rect,Texture texture)
        {
            if(texture==null)return;
            // Face-led framing like the approved cards, with matching UV aspect (no stretching).
            float cropHeight=.65f*rect.height/rect.width;
            GUI.DrawTextureWithTexCoords(rect,texture,new Rect(.175f,.90f-cropHeight,.65f,cropHeight));
        }
        void UiPortraitShade(Rect rect) => GUI.DrawTexture(rect,_uiFade,ScaleMode.StretchToFill);
        void UiWing(Rect rect,Color color,bool mirrored=false)
        {
            var old=GUI.color;GUI.color=color;GUI.DrawTextureWithTexCoords(rect,_uiWing,mirrored?new Rect(1,0,-1,1):new Rect(0,0,1,1));GUI.color=old;
        }
        void UiFit(Rect rect, string text, GUIStyle style)
        {
            GUI.Label(rect, new GUIContent(FitPlayerName(text ?? "", "", style, rect.width), text), style);
        }
        void UiBar(Rect rect, float fraction, Color color)
        {
            UiPanel(rect, new Color(.13f, .17f, .22f));
            if (fraction > 0) UiPanel(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fraction), rect.height), color);
        }
        void SkillNotice(Rect card, string text, bool warning = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            var r = new Rect(card.x, card.y - 88, card.width, 80);
            UiCard(r, UiSurface);
            var old = GUI.color; if (warning) GUI.color = UiWarning;
            GUI.Label(new Rect(r.x + 10, r.y + 5, r.width - 20, r.height - 10), text, _uiWrap); GUI.color = old;
        }
        string FireInstruction(WeaponRuntimeConfig weapon) => weapon.FireMode == WeaponFireMode.Splatling ? "左键按住蓄力／松开连射" :
            WeaponSimulation.IsSemi(weapon) ? "左键点击单发／长按连续" : WeaponSimulation.IsCharge(weapon) ? "左键蓄力／松开发射" : "左键按住射击";
        void DrawCombatChrome(PrototypePlayer local, MatchStateSnapshot state, PlayerSnapshot player)
        {
            UiStyles();
            var bounds=CombatUiLayout.Bounds(Screen.width,Screen.height);
            var hero=GameplayConfig.GetHero(player.HeroId);var weapon=GameplayConfig.GetWeapon(player.HeroId);
            float top=bounds.y+8;
            UiCard(new Rect(410,top,460,61),HudSurface,23);
            UiWing(new Rect(416,top+5,132,51),new Color(1,.30f,.65f));
            UiWing(new Rect(738,top+5,126,51),new Color(.17f,.57f,1),true);
            double total=System.Math.Max(.0001,state.TotalArea);
            GUI.Label(new Rect(433,top+6,100,20),"粉队",_uiCenter);
            GUI.Label(new Rect(433,top+24,100,30),$"{state.PinkArea/total:P1}",_uiScore);
            GUI.Label(new Rect(752,top+6,100,20),"蓝队",_uiCenter);
            GUI.Label(new Rect(752,top+24,100,30),$"{state.BlueArea/total:P1}",_uiScore);
            double remaining=state.Phase==MatchPhase.Playing?System.Math.Max(0,state.EndsAt-Manager.ServerTime.Time):0;
            string clock=state.Phase==MatchPhase.Practice?"热身":state.Phase==MatchPhase.Finished?"已结束":$"{(int)remaining/60:00}:{(int)remaining%60:00}";
            GUI.Label(new Rect(557,top+2,174,53),clock,_uiClock);
            var area=new Rect(431,top+69,420,13);
            UiCard(new Rect(area.x-5,area.y-4,area.width+10,area.height+8),HudSurface);
            UiBar(area,(float)(state.PinkArea/total),new Color(1,.30f,.65f));
            float blue=Mathf.Clamp01((float)(state.BlueArea/total));
            if(blue>0)UiPanel(new Rect(area.xMax-area.width*blue,area.y,area.width*blue,area.height),new Color(.17f,.57f,1));

            UiCard(new Rect(bounds.x+16,bounds.y+15,128,34),HudSurface);
            UiGlyph(new Rect(bounds.x+28,bounds.y+21,22,22),0,Color.white);
            GUI.Label(new Rect(bounds.x+59,bounds.y+17,80,30),$"{(Manager.IsHost?"房主":"房间")} · {state.PlayerCount}/{GameplayConfig.Mode.MaxPlayers}",_uiCenter);
            if(!Manager.IsHost&&local.GameLatency.TryRead(Time.realtimeSinceStartupAsDouble,out double ping))
            {
                UiPanel(new Rect(bounds.x+16,bounds.y+55,128,22),HudSurface);
                GUI.Label(new Rect(bounds.x+18,bounds.y+54,124,24),$"往返 {ping:0} ms",_uiCenter);
            }
            float right=bounds.xMax-219;
            UiCard(new Rect(right,bounds.y+15,96,34),HudSurface);UiKey(new Rect(right+10,bounds.y+21,35,22),"Tab");
            GUI.Label(new Rect(right+50,bounds.y+17,43,29),"战绩",_uiCenter);
            UiCard(new Rect(right+108,bounds.y+15,94,34),HudSurface);UiKey(new Rect(right+119,bounds.y+21,35,22),"Esc");
            GUI.Label(new Rect(right+156,bounds.y+17,43,29),"菜单",_uiCenter);

            var v=CombatUiLayout.Vitals(Screen.width,Screen.height);UiCard(v,HudSurface,14);
            var portrait=Heroes.Get(hero.Id).Portrait;
            UiPortrait(new Rect(v.x+10,v.y+10,104,113),portrait);
            UiInk(new Rect(v.x-13,v.y-14,39,39),new Color(.18f,.88f,1,.95f));
            UiFit(new Rect(v.x+128,v.y+10,230,28),hero.DisplayName,_uiStrong);
            UiFit(new Rect(v.x+128,v.y+41,230,23),hero.WeaponTypeName,_uiText);
            GUI.Label(new Rect(v.x+128,v.y+72,73,23),$"生命 {player.Health:0}",_uiText);
            UiBar(new Rect(v.x+202,v.y+73,156,16),player.Health/hero.MaxHealth,new Color(1,.30f,.65f));
            GUI.Label(new Rect(v.x+128,v.y+103,87,22),$"墨量 {player.Ink:0}/{hero.MaxInk:0}",_uiMuted);
            var ink=new Rect(v.x+222,v.y+103,136,15);UiBar(ink,player.Ink/hero.MaxInk,HeroAccent);
            if(player.SplatlingReservedInk>0)
            {
                float start=Mathf.Clamp01(player.Ink/hero.MaxInk),fill=Mathf.Clamp(player.SplatlingReservedInk/hero.MaxInk,0,1-start);
                UiPanel(new Rect(ink.x+ink.width*start,ink.y,ink.width*fill,ink.height),new Color(.85f,.9f,1));
            }
            string recovery=player.SplatlingReservedInk>0?$"预留 {player.SplatlingReservedInk:0.0} · Shift 取消":
                player.Ink<WeaponSimulation.InkCost(weapon)?weapon.FireMode==WeaponFireMode.Splatling?"墨量不足 · 可慢速蓄力":"墨量不足 · 松开射击回墨":
                player.InkRecoverAt>player.SimulatedAt?"射击后回墨锁定":player.HasInkRecovery?"潜墨中 · 快速回墨":player.Swimming?"弦化中 · 普通回墨":"";
            if(!string.IsNullOrEmpty(recovery)){UiPanel(new Rect(v.x,v.y-29,v.width,24),HudSurface);UiFit(new Rect(v.x+10,v.y-28,v.width-20,22),recovery,_uiMuted);}
            var help=new Rect(551,bounds.yMax-65,180,30);UiCard(help,HudSurface);
            UiKey(new Rect(help.x+9,help.y+5,33,20),"左键");GUI.Label(new Rect(help.x+45,help.y+3,36,24),WeaponSimulation.IsCharge(weapon)||WeaponSimulation.IsSplatling(weapon)?"蓄力":"射击",_uiCenter);
            UiKey(new Rect(help.x+88,help.y+5,38,20),"Shift");GUI.Label(new Rect(help.x+128,help.y+3,45,24),"弦化",_uiCenter);
        }
        void DrawControlHelp(WeaponRuntimeConfig weapon)
        {
            UiCard(new Rect(30, 175, 350, 310), UiSurface);
            GUI.Label(new Rect(50, 190, 310, 32), "操作说明", _uiStrong);
            GUI.Label(new Rect(50, 233, 310, 234), "W A S D  移动 · 空格 跳跃\n" + FireInstruction(weapon) +
                "\nShift  弦化／取消投掷\nE  按住瞄准，松开使用副武器\nQ  启动大招\nF  解救队友／彻底击杀\nH  热身或出生区更换配装\nTab  按住查看战绩\nEsc  关闭界面／房间菜单\n回车  开始比赛（房主）", _uiWrap);
        }
    }
}
