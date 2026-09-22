using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEngine;
namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        int EquipmentGrid(Rect bounds,int selected,string[] labels,int columns,int fontSize)
        {
            var style=HeroStyle(fontSize,Color.white);style.alignment=TextAnchor.MiddleCenter;
            int rows=Mathf.CeilToInt(labels.Length/(float)columns);float width=bounds.width/columns,height=bounds.height/rows;
            for(int i=0;i<labels.Length;i++)
            {
                var rect=new Rect(bounds.x+i%columns*width,bounds.y+i/columns*height,width-4,height-4);
                Panel(rect,i==selected?new Color(.12f,.32f,.37f):new Color(.10f,.14f,.20f));
                if(i==selected)Panel(new Rect(rect.x,rect.y,3,rect.height),HeroAccent);
                if(GUI.Button(rect,labels[i],style))selected=i;
            }
            return selected;
        }
        void DrawLoadoutChoices()
        {
            var subs=LubanConfigService.Current.Tables.TbSubWeapon.DataList;
            var specials=LubanConfigService.Current.Tables.TbSpecialWeapon.DataList;
            GUI.Label(new Rect(742,382,445,24),"副武器 · 按住 E 瞄准，松开使用",_heroCaption);
            int sub=subs.FindIndex(x=>x.Id==_previewSubId);
            int next=EquipmentGrid(new Rect(742,410,450,92),sub,subs.Select(x=>x.DisplayName).ToArray(),4,12);
            if(next>=0&&next<subs.Count)_previewSubId=subs[next].Id;
            var loadout=new PlayerLoadout(_previewHeroId,_previewSubId,_previewSpecialId);
            GUI.Label(new Rect(742,508,450,24),$"大招 · Q 启动 · {loadout.RequiredPoints:0} 点充满",_heroCaption);
            int special=specials.FindIndex(x=>x.Id==_previewSpecialId);
            next=EquipmentGrid(new Rect(742,537,450,54),special,specials.Select(x=>x.DisplayName).ToArray(),3,15);
            if(next>=0&&next<specials.Count)_previewSpecialId=specials[next].Id;
        }
        void DrawSpecialHud(PlayerSnapshot s)
        {
            var c=SpecialWeaponConfigService.Current.Get(s.SpecialWeaponId);float cost=PlayerLoadout.From(s).RequiredPoints;
            Panel(new Rect(940,463,320,105),new Color(.035f,.05f,.07f,.9f));
            if(c.Icon!=null)GUI.DrawTexture(new Rect(951,471,30,30),c.Icon.texture,ScaleMode.ScaleToFit);
            GUI.Label(new Rect(990,469,260,25),$"Q  {c.Name}",_small);
            bool active=SpecialWeaponSimulation.Active(s);
            string status=active?(c.Type==SpecialWeaponType.Reefslider?(s.SpecialRideStage<0?"等待落地":s.SpecialRideStage==0?"准备冲刺":s.SpecialRideStage==2?"制动蓄爆":s.SpecialPhase==SpecialPhase.Recovering?"爆炸后恢复":"冲刺中 · 左键提前制动"):c.Type==SpecialWeaponType.WaveBreaker||c.Type==SpecialWeaponType.InkStorm?"持握中 · 左键瞄准投出":$"剩余 {s.SpecialRemaining} 次 · {System.Math.Max(0,s.SpecialUntil-s.SimulatedAt):0.0}s"):
                s.SpecialPoints+1e-7>=cost?"已就绪 · 按 Q 启动":$"{s.SpecialPoints:0.0} / {cost:0} · 涂地充能";
            if(!s.IsAlive)status=s.IsBubble?"倒地中 · 能量保留":"死亡 · 能量已减半";
            else if(!active&&s.SpecialChargeLockedUntil>s.SimulatedAt)status=$"充能锁定 {s.SpecialChargeLockedUntil-s.SimulatedAt:0.0}s";
            else if(s.SpecialPoints+1e-7<cost&&s.SpecialFailure==SpecialFailure.NotReady)status=$"能量不足 · {s.SpecialPoints:0.0} / {cost:0}";
            else if(s.SpecialFailure==SpecialFailure.NoSpace)status="空间不足，无法启动";
            else if(s.SpecialFailure==SpecialFailure.Busy)status="动作恢复中";
            GUI.Label(new Rect(951,502,303,25),status,_small);
            Panel(new Rect(952,535,296,9),new Color(.17f,.2f,.26f));
            float fill=active&&c.Type==SpecialWeaponType.Reefslider&&s.SpecialRideStage<0?1:active?Mathf.Clamp01((float)((s.SpecialUntil-s.SimulatedAt)/c.P.duration)):Mathf.Clamp01((float)s.SpecialPoints/cost);
            Panel(new Rect(952,535,296*fill,9),PrototypeArena.TeamColor(s.Team));
            if(active&&c.Type!=SpecialWeaponType.Reefslider)GUI.Label(new Rect(951,547,303,20),c.Type==SpecialWeaponType.Trizooka?"左键射击":"按住左键瞄准，松开投出",_small);
        }
    }
}
