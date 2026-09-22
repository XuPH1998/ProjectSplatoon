using System;
using Splatoon.Config;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        void DrawSubWeaponHud(PrototypePlayer local, PlayerSnapshot state)
        {
            DrawSpecialHud(state);
            var c = PlayerLoadout.SubWeapon(state);
            Panel(new Rect(940, 578, 320, 105), new Color(.035f, .05f, .07f, .88f));
            if (c.Common.icon != null) GUI.DrawTexture(new Rect(951, 589, 45, 45), c.Common.icon.texture, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(1000, 585, 248, 25), $"E  {c.Common.displayName}  · {c.Common.inkCost:0} 墨", _small);
            string status = SubWeaponSimulation.FailureText(state.SubFailure);
            if (state.Ink < c.Common.inkCost) status = "墨水不足";
            if (state.SubPhase == SubWeaponPhase.Holding) status = c.Type == SubWeaponType.FizzyBomb ? $"蓄力：{1 + Mathf.RoundToInt(state.SubCharge * 2)} 次爆炸 · 松开投出" : c.Type == SubWeaponType.CurlingBomb ? $"蓄力：{state.SubCharge:P0} · 松开投出" : "瞄准中 · 松开 E 使用 · Shift 取消";
            if (state.SubPhase == SubWeaponPhase.Starting) status = "准备投出 · 仍可调整方向";
            GUI.Label(new Rect(951, 618, 303, 24), status, _small);
            string recovery = state.InkRecoverAt > state.SimulatedAt ? $"回墨等待 {state.InkRecoverAt - state.SimulatedAt:0.0}s" : "可以回墨";
            if (state.SubReadyAt > state.SimulatedAt) recovery += $"  再使用 {state.SubReadyAt - state.SimulatedAt:0.0}s";
            if (c.Type == SubWeaponType.Torpedo || SubWeaponService.IsPersistent(c.Type)) recovery += $"  场上 {PrototypeMatch.Current.SubPresentation?.Count(local.PlayerId, c.Type) ?? 0}";
            GUI.Label(new Rect(951, 648, 303, 24), recovery, _small);
            if ((state.SubPhase == SubWeaponPhase.Holding || state.SubPhase == SubWeaponPhase.Starting) &&
                (c.Type == SubWeaponType.Autobomb || c.Type == SubWeaponType.Torpedo))
                GUI.Label(new Rect(465, 505, 380, 26), "投掷参考 · 索敌后落点会改变", _small);
            if (state.SubPhase == SubWeaponPhase.Holding && (c.Type == SubWeaponType.FizzyBomb || c.Type == SubWeaponType.CurlingBomb))
            {
                float charge = c.Type == SubWeaponType.FizzyBomb ? Mathf.Clamp01((float)(state.SubChargeSeconds / c.Fizzy.thirdCharge)) : state.SubCharge;
                Panel(new Rect(555, 480, 170, 7), new Color(.12f, .15f, .19f)); Panel(new Rect(555, 480, charge * 170, 7), PrototypeArena.TeamColor(state.Team));
            }
            if (state.MistUntil > state.SimulatedAt) GUI.Label(new Rect(510, 548, 300, 28), "毒雾：减速／持续耗墨", _small);
            var camera = Camera.main; if (camera == null) return;
            foreach (var p in PrototypePlayer.ByOwner.Values)
            {
                if (p == null || p == local || p.PresentedState.Health <= 0 || p.PresentedState.Team == state.Team) continue;
                var target = p.PresentedState; double until = state.Team == 1 ? target.MarkedUntilPink : target.MarkedUntilBlue;
                if (until <= Manager.ServerTime.Time) continue;
                Vector3 point = camera.WorldToViewportPoint(p.transform.position + Vector3.up * 2);
                if (point.z <= 0) continue;
                GUI.Label(new Rect(point.x * 1280 - 40, (1 - point.y) * 720 - 20, 160, 30), $"◆ 标记 {until - Manager.ServerTime.Time:0.0}s", _small);
            }
        }
#if UNITY_EDITOR
        string _subDebugStatus = "编辑当前副武器 Asset 可实时应用";
        GUIStyle _subChoiceStyle;
        internal void ApplyDebugSubWeaponChanges()
        {
            if (!IsWeaponDebugRoom || !InRoom) return;
            foreach (int id in new System.Collections.Generic.List<int>(SubWeaponConfigService.Current.Ids))
            {
                var a=SubWeaponConfigService.Current.SourceById(id);if(a==null)continue;
                try{var next=a.Snapshot();next.Validate();if(SubWeaponConfigService.Same(next,SubWeaponConfigService.Current.GetById(id)))continue;
                    foreach(var p in PrototypeMatch.Current.Players)if(PlayerLoadout.From(p.Snapshot.Value).SubWeaponId==id)p.CancelForSubWeaponReload();
                    SubWeaponConfigService.Current.SetById(id,a);_subDebugStatus="已应用，现存实体保留投出时参数";
                }catch(Exception e){_subDebugStatus=e.Message;}
            }
        }

        void DrawSubWeaponDebug(PlayerSnapshot state)
        {
            Panel(new Rect(20, 315, 450, 173), new Color(.035f, .05f, .07f, .88f));
            GUI.Label(new Rect(32, 320, 426, 40), _subDebugStatus, _small);
            var current = PlayerLoadout.SubWeapon(state);
            _subChoiceStyle ??= new GUIStyle(GUI.skin.button) { font=_small.font, fontSize=13, alignment=TextAnchor.MiddleCenter };
            int selection = GUI.SelectionGrid(new Rect(32, 358, 426, 88), (int)current.Type, Array.ConvertAll((SubWeaponType[])Enum.GetValues(typeof(SubWeaponType)), SubWeaponFields.TypeName), 4, _subChoiceStyle);
            if (selection != (int)current.Type)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path((SubWeaponType)selection));
                if (asset != null) { PrototypePlayer.Local.CancelForSubWeaponReload(); PrototypeMatch.Current.SubWeapons.ClearOwner(PrototypePlayer.Local.PlayerId); PrototypePlayer.Local.RequestLoadoutChange(new PlayerLoadout(state.HeroId,selection+1,PlayerLoadout.From(state).SpecialWeaponId),HeroSelectionOrigin.Debug); }
            }
            if (GUI.Button(new Rect(32, 452, 205, 27), "定位副武器 Asset", _small))
            { var a = SubWeaponConfigService.Current.SourceById(PlayerLoadout.From(state).SubWeaponId); UnityEditor.Selection.activeObject = a; UnityEditor.EditorGUIUtility.PingObject(a); }
            if (GUI.Button(new Rect(244, 452, 205, 27), "清空场上副武器", _small)) PrototypeMatch.Current.SubWeapons.Clear();
        }
#endif
    }
}
