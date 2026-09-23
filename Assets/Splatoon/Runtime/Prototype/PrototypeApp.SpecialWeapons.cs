using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEngine;
namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        int _loadoutTab;
        int EquipmentGrid(int selected,string[] labels,int columns)
        {
            for(int i=0;i<labels.Length;i++)
            {
                var rect=HeroSelectionLayout.EquipmentCard(i,columns);
                if(i==selected)UiGlow(rect,HeroAccent);else UiPanel(rect,UiBorder);
                UiPanel(new Rect(rect.x+1.5f,rect.y+1.5f,rect.width-3,rect.height-3),i==selected?new Color(.09f,.24f,.31f):new Color(.075f,.105f,.155f));
                UiGlyph(new Rect(rect.x+6,rect.y+5,32,32),i+1,i==selected?HeroAccent:Color.white,_loadoutTab==1);
                UiFit(new Rect(rect.x+38,rect.y+5,rect.width-40,31),labels[i],_uiOption);
                if(GUI.Button(rect,GUIContent.none,GUIStyle.none)) { selected=i; _fireInputBlocked=true; }
            }
            return selected;
        }
        void DrawLoadoutChoices()
        {
            var subs=LubanConfigService.Current.Tables.TbSubWeapon.DataList;
            var specials=LubanConfigService.Current.Tables.TbSpecialWeapon.DataList;
            UiPanel(new Rect(654,316,550,38),new Color(.13f,.17f,.24f,.8f));
            UiGlyph(new Rect(665,324,25,25),_previewSubId,Color.white);
            UiFit(new Rect(702,321,226,29),$"副武器：{subs.Find(x=>x.Id==_previewSubId)?.DisplayName}",_uiText);
            UiGlyph(new Rect(930,324,25,25),_previewSpecialId,new Color(.78f,.40f,1),true);
            UiFit(new Rect(968,321,230,29),$"大招：{specials.Find(x=>x.Id==_previewSpecialId)?.DisplayName}",_uiText);
            for(int i=0;i<2;i++)
            {
                var tab=HeroSelectionLayout.Tab(i);UiPanel(tab,_loadoutTab==i?HeroAccent:new Color(.20f,.25f,.34f));
                if(GUI.Button(tab,i==0?$"副武器   {subs.Count}":$"大招   {specials.Count}",_loadoutTab==i?_heroAction:_uiCenter)){_loadoutTab=i;_fireInputBlocked=true;}
            }
            int sub=subs.FindIndex(x=>x.Id==_previewSubId);
            if(_loadoutTab==0) { int next=EquipmentGrid(sub,subs.Select(x=>x.DisplayName).ToArray(),4); if(next>=0&&next<subs.Count)_previewSubId=subs[next].Id; }
            var loadout=new PlayerLoadout(_previewHeroId,_previewSubId,_previewSpecialId);
            GUI.Label(new Rect(644,610,560,23),_loadoutTab==0?"E 按住瞄准，松开使用 · Shift 取消":$"Q 启动 · {loadout.RequiredPoints:0} 点充满",_uiMuted);
            int special=specials.FindIndex(x=>x.Id==_previewSpecialId);
            if(_loadoutTab==1) { int next=EquipmentGrid(special,specials.Select(x=>x.DisplayName).ToArray(),3); if(next>=0&&next<specials.Count)_previewSpecialId=specials[next].Id; }
        }
        void DrawSpecialHud(PlayerSnapshot s)
        {
            var c=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId);float cost=PlayerLoadout.From(s).RequiredPoints;
            var card=CombatUiLayout.Skill(Screen.width,Screen.height,1);UiCard(card,HudSurface,14);
            UiKey(new Rect(card.x+11,card.y+7,25,25),"Q");GUI.Label(new Rect(card.x+46,card.y+7,110,25),"大招",_uiText);
            UiInk(new Rect(card.xMax-24,card.y-10,34,34),HeroAccent);
            bool active=SpecialWeaponSimulation.Active(s);
            string status=active?(c.Type==SpecialWeaponType.Reefslider?(s.SpecialRideStage<0?"等待落地":s.SpecialRideStage==0?"准备冲刺":s.SpecialRideStage==2?"制动蓄爆":s.SpecialPhase==SpecialPhase.Recovering?"爆炸后恢复":"冲刺中 · 左键提前制动"):c.Type==SpecialWeaponType.WaveBreaker||c.Type==SpecialWeaponType.InkStorm?"持握中 · 左键瞄准投出":$"剩余 {s.SpecialRemaining} 次 · {System.Math.Max(0,s.SpecialUntil-s.SimulatedAt):0.0}s"):
                s.SpecialPoints+1e-7>=cost?"已就绪 · 按 Q 启动":$"{s.SpecialPoints:0.0} / {cost:0} · 涂地充能";
            if(!s.IsAlive)status=s.IsBubble?"倒地中 · 能量保留":"死亡 · 能量已减半";
            else if(!active&&s.SpecialChargeLockedUntil>s.SimulatedAt)status=$"充能锁定 {s.SpecialChargeLockedUntil-s.SimulatedAt:0.0}s";
            else if(s.SpecialPoints+1e-7<cost&&s.SpecialFailure==SpecialFailure.NotReady)status=$"能量不足 · {s.SpecialPoints:0.0} / {cost:0}";
            else if(s.SpecialFailure==SpecialFailure.NoSpace)status="空间不足，无法启动";
            else if(s.SpecialFailure==SpecialFailure.Busy)status="动作恢复中";
            float fill=active&&c.Type==SpecialWeaponType.Reefslider&&s.SpecialRideStage<0?1:active?Mathf.Clamp01((float)((s.SpecialUntil-s.SimulatedAt)/c.P.duration)):Mathf.Clamp01((float)s.SpecialPoints/cost);
            Vector2 center=new(card.center.x,card.y+64);
            UiProgressRing(new Rect(center.x-38,center.y-38,76,76),fill);
            GUI.Label(new Rect(center.x-35,center.y-15,70,30),active?"使用中":$"{fill:P0}",_uiRingValue);
            UiFit(new Rect(card.x+10,card.y+104,card.width-20,25),c.Name,_uiCenter);
            if(s.SpecialPoints>=cost&&!active&&s.IsAlive)GUI.Label(new Rect(card.x+8,card.y+122,card.width-16,17),"已就绪 · 按 Q",_uiCenter);
            if(active||!s.IsAlive||s.SpecialFailure!=SpecialFailure.None||s.SpecialChargeLockedUntil>s.SimulatedAt)
                SkillNotice(card,status+(active&&c.Type!=SpecialWeaponType.Reefslider?c.Type==SpecialWeaponType.Trizooka?"\n左键射击":"\n按住左键瞄准，松开投出":""),s.SpecialFailure!=SpecialFailure.None);
        }
    }
}
