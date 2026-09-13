using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class WeaponDisplay
    {
        public static string Mechanism(cfg.HeroConfig w) => w.FireMode switch { 1 => "三连发 / 长按连续", 2 => "蓄力 / 松开发射", 3 => w.MuzzleMode == 1 ? "半自动 / 右左交替" : w.PelletCount > 1 ? "半自动 / 霰弹齐射" : "半自动 / 点击射击", _ => "全自动" };
        public static string Damage(cfg.HeroConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0}" : w.PelletCount > 1 ? $"{w.PelletCount} × {w.Damage:0}→{w.DamageMin:0}" : $"{w.Damage:0} → {w.DamageMin:0}";
        public static string Cadence(cfg.HeroConfig w) => w.FireMode switch
        {
            1 => $"组内 {w.FireIntervalFrames}F / 组间 {w.BurstRecoveryFrames}F",
            2 => $"蓄力 {w.ChargeFrames}F / 冷却 {w.FireIntervalFrames}F",
            _ => $"{w.FireIntervalFrames}F / {60f / w.FireIntervalFrames:0.##} 发/秒"
        };
        public static float SustainedRate(cfg.HeroConfig w) => w.FireMode == 1 ? 60f * w.BurstCount / ((w.BurstCount - 1) * w.FireIntervalFrames + w.BurstRecoveryFrames) : 60f / w.FireIntervalFrames;
        public static string Ink(cfg.HeroConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinInk:0.##} / 满蓄 {w.ShotInk:0.##}" : $"{w.ShotInk:0.##} / {(w.PelletCount > 1 ? "次齐射" : "发")}";
        public static string Range(cfg.HeroConfig w) => WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinRange:0.#}–{w.EffectiveRange:0.#} 米" : $"{w.EffectiveRange:0.#} 米";
        public static string ListSummary(cfg.HeroConfig w) => w.FireMode switch
        {
            3 => $"{Mechanism(w)} · {Damage(w)} 伤害\n{Range(w)} · {w.ShotInk:0.##} 墨/次 · 最快 {SustainedRate(w):0.#} 次/秒",
            1 => $"三连发 · {w.Damage:0}→{w.DamageMin:0} 伤害 · {w.FireIntervalFrames}F/{w.BurstRecoveryFrames}F\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒",
            2 => $"蓄力 {w.ChargeFrames}F · {w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0}/满 {w.Damage:0} 伤害\n{Range(w)} · {w.ChargeMinInk:0.#}–{w.ShotInk:0.#} 墨 · 冷却 {w.FireIntervalFrames}F",
            _ => $"全自动 · {w.Damage:0}→{w.DamageMin:0} 伤害 · {w.FireIntervalFrames}F\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒"
        };
        public static List<(string label, string value)> Details(cfg.HeroConfig w) => new()
        {
            ("生命／墨量上限", $"{w.MaxHealth:0.#} / {w.MaxInk:0.#}"),
            ("人形／潜墨移速", $"{w.MoveSpeed:0.#} / {w.SwimSpeed:0.#} 米/秒"),
            ("人形／潜墨回墨", $"{w.RecoverInk:0.##} / {w.SwimRecoverInk:0.##} 点/秒"),
            ("伤害", Damage(w)), ("射击节奏", Cadence(w)),
            ("射速上限／方式", WeaponSimulation.IsCharge(w) ? "按住蓄力，松开发射" : WeaponSimulation.IsSemi(w) ? $"最快 {SustainedRate(w):0.##} 次/秒，每次点击一发" : $"{SustainedRate(w):0.##} 发/秒"),
            ("耗墨", w.FireMode == 1 ? $"{w.ShotInk:0.##}/发 · {w.ShotInk*w.BurstCount:0.##}/组" : Ink(w)), ("满墨发数", WeaponSimulation.IsCharge(w) ? $"点射 {Mathf.FloorToInt(w.MaxInk / w.ChargeMinInk)} / 满蓄 {Mathf.FloorToInt(w.MaxInk / w.ShotInk)}" : $"{Mathf.FloorToInt(w.MaxInk / w.ShotInk)} 发"),
            ("有效伤害射程", Range(w)),
            ("地面／空中散布", WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinSpread:0.#}°/{w.ChargeMinJumpSpread:0.#}° → {w.SpreadDegrees:0.#}°/{w.JumpSpreadDegrees:0.#}°" : $"{w.SpreadDegrees:0.#}° / {w.JumpSpreadDegrees:0.#}°"),
            ("射击移动速度", $"{w.ShootMoveSpeed:0.#} 米/秒"), ("人形／出墨起手", $"{w.StartFrames}F / {w.EmergeStartFrames}F"),
            ("回墨锁定", $"{w.InkRecoverLockFrames}F"),
            ("水平涂地参考", WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinPaintRange:0.#}–{w.PaintRange:0.#} 米" : $"{w.PaintRange:0.#} 米")
        };
    }
}
