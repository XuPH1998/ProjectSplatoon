using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class WeaponDisplay
    {
        public static string Mechanism(cfg.WeaponConfig w) => w.FireMode switch { 1 => "三连发 / 长按连续", 2 => "蓄力 / 松开发射", _ => "全自动" };
        public static string Damage(cfg.WeaponConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0}" : $"{w.Damage:0} → {w.DamageMin:0}";
        public static string Cadence(cfg.WeaponConfig w) => w.FireMode switch
        {
            1 => $"组内 {w.FireIntervalFrames}F / 组间 {w.BurstRecoveryFrames}F",
            2 => $"蓄力 {w.ChargeFrames}F / 冷却 {w.FireIntervalFrames}F",
            _ => $"{w.FireIntervalFrames}F / {60f / w.FireIntervalFrames:0.##} 发/秒"
        };
        public static float SustainedRate(cfg.WeaponConfig w) => w.FireMode == 1 ? 60f * w.BurstCount / ((w.BurstCount - 1) * w.FireIntervalFrames + w.BurstRecoveryFrames) : 60f / w.FireIntervalFrames;
        public static string Ink(cfg.WeaponConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinInk:0.##} / 满蓄 {w.ShotInk:0.##}" : $"{w.ShotInk:0.##} / 发";
        public static string Range(cfg.WeaponConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinRange:0.#}–{w.EffectiveRange:0.#} 米" : $"{w.EffectiveRange:0.#} 米";
        public static string ListSummary(cfg.WeaponConfig w) => w.FireMode switch
        {
            1 => $"三连发 · {w.Damage:0}→{w.DamageMin:0} 伤害 · {w.FireIntervalFrames}F/{w.BurstRecoveryFrames}F\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒",
            2 => $"蓄力 {w.ChargeFrames}F · {w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0}/满 {w.Damage:0} 伤害\n{Range(w)} · {w.ChargeMinInk:0.#}–{w.ShotInk:0.#} 墨 · 冷却 {w.FireIntervalFrames}F",
            _ => $"全自动 · {w.Damage:0}→{w.DamageMin:0} 伤害 · {w.FireIntervalFrames}F\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒"
        };
        public static List<(string label, string value)> Details(cfg.WeaponConfig w) => new()
        {
            ("伤害", Damage(w)), ("射击节奏", Cadence(w)),
            ("持续射速／方式", WeaponSimulation.IsCharge(w) ? "按住蓄力，松开发射" : $"{SustainedRate(w):0.##} 发/秒"),
            ("耗墨", w.FireMode == 1 ? $"{w.ShotInk:0.##}/发 · {w.ShotInk*w.BurstCount:0.##}/组" : Ink(w)), ("满墨发数", WeaponSimulation.IsCharge(w) ? $"点射 {Mathf.FloorToInt(Splatoon.Config.GameplayConfig.Character.MaxInk / w.ChargeMinInk)} / 满蓄 {Mathf.FloorToInt(Splatoon.Config.GameplayConfig.Character.MaxInk / w.ShotInk)}" : $"{Mathf.FloorToInt(Splatoon.Config.GameplayConfig.Character.MaxInk / w.ShotInk)} 发"),
            ("有效伤害射程", Range(w)),
            ("地面／空中散布", WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinSpread:0.#}°/{w.ChargeMinJumpSpread:0.#}° → {w.SpreadDegrees:0.#}°/{w.JumpSpreadDegrees:0.#}°" : $"{w.SpreadDegrees:0.#}° / {w.JumpSpreadDegrees:0.#}°"),
            ("射击移动速度", $"{w.ShootMoveSpeed:0.#} 米/秒"), ("人形／出墨起手", $"{w.StartFrames}F / {w.EmergeStartFrames}F"),
            ("回墨锁定", $"{w.InkRecoverLockFrames}F"),
            ("水平涂地目标", WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinPaintRange:0.#}–{w.PaintRange:0.#} 米（待标定）" : $"{w.PaintRange:0.#} 米（待标定）")
        };
    }
}
