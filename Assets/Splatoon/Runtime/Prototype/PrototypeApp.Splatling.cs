using UnityEngine;
using Splatoon.Combat;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        void DrawSplatlingHud(PlayerSnapshot state, cfg.HeroConfig weapon, Vector2 center)
        {
            bool firing=state.SplatlingRemaining>0;
            float first=Mathf.Clamp01(state.SplatlingCharge/weapon.SplatlingFirstChargeFrames);
            float second=Mathf.InverseLerp(weapon.SplatlingFirstChargeFrames,weapon.ChargeFrames,state.SplatlingCharge);
            if(firing) { float remaining=state.SplatlingRemaining/(float)SplatlingSimulation.Rounds(weapon,weapon.ChargeFrames);first=Mathf.Clamp01(remaining*2);second=Mathf.Clamp01(remaining*2-1); }
            var color=PrototypeArena.TeamColor(state.Team);
            for(int ring=0;ring<2;ring++) for(int dot=0;dot<64;dot++)
            {
                float angle=dot/64f*Mathf.PI*2-Mathf.PI*.5f,radius=ring==0?28:35;
                bool fill=dot/64f<(ring==0?first:second);
                Panel(new Rect(center.x+Mathf.Cos(angle)*radius-1.5f,center.y+Mathf.Sin(angle)*radius-1.5f,3,3),fill?color:new Color(.25f,.28f,.32f));
            }
            string status=firing?$"连射 · 剩余 {state.SplatlingRemaining} 发":SplatlingSimulation.Charging(state)
                ? state.SplatlingCharge>=weapon.ChargeFrames?"满蓄 · 松开连射":state.SplatlingSlow?"慢速蓄力 · 松开连射":"蓄力 · 松开连射":"按住蓄力 · 松开连射";
            GUI.Label(new Rect(center.x-120,center.y+51,330,30),status,_small);
            if(state.SplatlingReservedInk>0)
                Panel(new Rect(42+285*state.Ink/weapon.MaxInk,635,285*state.SplatlingReservedInk/weapon.MaxInk,15),new Color(.85f,.9f,1,.85f));
            if(SplatlingSimulation.Charging(state)||firing)
                GUI.Label(new Rect(42,662,420,26),$"可用 {state.Ink:0.0} · 预留 {state.SplatlingReservedInk:0.0} · Shift 取消",_small);
        }
    }
}
