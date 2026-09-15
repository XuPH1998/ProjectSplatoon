using Splatoon.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class WeaponDisplay
    {
        public static string Mechanism(WeaponRuntimeConfig w) => w.FireMode switch { 4 => "旋转枪 / 两段蓄力连射", 1 => "三连发 / 长按连续", 2 => "蓄力 / 松开发射", 3 => w.MuzzleMode == 1 ? "半自动 / 右左交替" : w.PelletCount > 1 ? "半自动 / 霰弹齐射" : "半自动 / 点击或长按射击", _ => "全自动" };
        public static string Damage(WeaponRuntimeConfig w) => WeaponSimulation.IsSplatling(w) ? $"未满蓄 {w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0} → 远端 {w.DamageMin:0}" : WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0}" : w.PelletCount > 1 ? $"{w.PelletCount} × {w.Damage:0}→{w.DamageMin:0}" : $"{w.Damage:0} → {w.DamageMin:0}";
        public static string Cadence(WeaponRuntimeConfig w) => w.FireMode switch
        {
            4 => $"一圈 {w.SplatlingFirstChargeFrames}F / 满蓄 {w.ChargeFrames}F · {w.FireRate:0} 发/秒",
            1 => $"组内 {w.FireRate:0.##} 发/秒 / 组间 {w.BurstRecoveryFrames}F",
            2 => $"蓄力 {w.ChargeFrames}F / 冷却 {WeaponSimulation.FireInterval(w):0.###} 秒",
            _ => $"{w.FireRate:0.##} 发/秒"
        };
        public static float SustainedRate(WeaponRuntimeConfig w) => w.FireMode == 1 ? (float)(w.BurstCount / ((w.BurstCount - 1) * WeaponSimulation.FireInterval(w) + WeaponSimulation.Seconds(w.BurstRecoveryFrames))) : w.FireRate;
        public static string Ink(WeaponRuntimeConfig w) => WeaponSimulation.IsSplatling(w) ? $"满蓄 {w.ShotInk*SplatlingSimulation.Rounds(w,w.ChargeFrames):0.##} / {SplatlingSimulation.Rounds(w,w.ChargeFrames)} 发，取消返还未发射部分" : WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinInk:0.##} / 满蓄 {w.ShotInk:0.##}" : $"{w.ShotInk:0.##} / {(w.PelletCount > 1 ? "次齐射" : "发")}";
        public static string Range(WeaponRuntimeConfig w) => WeaponSimulation.IsCharge(w) || WeaponSimulation.IsSplatling(w) ? $"{w.ChargeMinRange:0.#}–{w.EffectiveRange:0.#} 米" : $"{w.EffectiveRange:0.#} 米";
        public static string Spread(WeaponRuntimeConfig w) => WeaponSimulation.IsCharge(w)
            ? $"{w.ChargeMinSpread:0.#}°/{w.ChargeMinJumpSpread:0.#}° → {w.SpreadDegrees:0.#}°/{w.JumpSpreadDegrees:0.#}°（随蓄力收紧）"
            : WeaponSimulation.IsSplatling(w) ? $"地面 0 → {w.SpreadDegrees:0.#}°水平 / {w.SplatlingPitchSpread:0.#}°垂直；空中 0 → {w.JumpSpreadDegrees:0.#}°"
            : $"地面 {w.BaseSpreadDegrees:0.#}° → {w.SpreadDegrees:0.#}° / 空中 {w.BaseJumpSpreadDegrees:0.#}° → {w.JumpSpreadDegrees:0.#}°";
        public static string ListSummary(WeaponRuntimeConfig w) => w.FireMode switch
        {
            4 => $"两段蓄力 {w.SplatlingFirstChargeFrames}/{w.ChargeFrames}F · {w.ChargePartialMaxDamage:0}/满蓄 {w.Damage:0} 伤害\n{Range(w)} · 满蓄 {SplatlingSimulation.Rounds(w,w.ChargeFrames)} 发 · {w.ShotInk*SplatlingSimulation.Rounds(w,w.ChargeFrames):0} 墨",
            3 => $"{Mechanism(w)} · {Damage(w)} 伤害\n{Range(w)} · {w.ShotInk:0.##} 墨/次 · 最快 {SustainedRate(w):0.#} 次/秒",
            1 => $"三连发 · {w.Damage:0}→{w.DamageMin:0} 伤害 · 组内 {w.FireRate:0.##} 发/秒 / 组间 {w.BurstRecoveryFrames}F\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒",
            2 => $"蓄力 {w.ChargeFrames}F · {w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0}/满 {w.Damage:0} 伤害\n{Range(w)} · {w.ChargeMinInk:0.#}–{w.ShotInk:0.#} 墨 · 冷却 {WeaponSimulation.FireInterval(w):0.###} 秒",
            _ => $"全自动 · {w.Damage:0}→{w.DamageMin:0} 伤害\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒"
        };
        public static List<(string label, string value)> Details(cfg.HeroConfig hero) => Details(hero, GameplayConfig.GetWeapon(hero.Id));
        public static List<(string label, string value)> Details(cfg.HeroConfig hero, WeaponRuntimeConfig w) => new()
        {
            ("生命／墨量上限", $"{hero.MaxHealth:0.#} / {hero.MaxInk:0.#}"),
            ("人形／潜墨移速", $"{hero.MoveSpeed:0.#} / {hero.SwimSpeed:0.#} 米/秒"),
            ("人形／潜墨回墨", $"{hero.RecoverInk:0.##} / {hero.SwimRecoverInk:0.##} 点/秒"),
            ("伤害", Damage(w)), ("射击节奏", Cadence(w)),
            ("射速上限／方式", WeaponSimulation.IsSplatling(w) ? "按住蓄力，松开持续射击；Shift 取消" : WeaponSimulation.IsCharge(w) ? "按住蓄力，松开发射" : WeaponSimulation.IsSemi(w) ? $"最快 {SustainedRate(w):0.##} 次/秒，点击单发，长按连续" : $"{SustainedRate(w):0.##} 发/秒"),
            ("耗墨", w.FireMode == 1 ? $"{w.ShotInk:0.##}/发 · {w.ShotInk*w.BurstCount:0.##}/组" : Ink(w)), ("满墨发数", WeaponSimulation.IsCharge(w) ? $"点射 {Mathf.FloorToInt(hero.MaxInk / w.ChargeMinInk)} / 满蓄 {Mathf.FloorToInt(hero.MaxInk / w.ShotInk)}" : $"{Mathf.FloorToInt(hero.MaxInk / w.ShotInk)} 发"),
            ("有效伤害射程", Range(w)),
            ("地面／空中散布", Spread(w)),
            ("散布扩大／完全恢复", WeaponSimulation.IsCharge(w) ? "蓄力控制；不使用时间扩散" : $"{w.SpreadExpandSeconds:0.###} / {w.SpreadRecoverSeconds:0.###} 秒"),
            (WeaponSimulation.IsSplatling(w) ? "蓄力／射击移动速度" : "射击移动速度", WeaponSimulation.IsSplatling(w) ? $"{w.SplatlingChargeMoveSpeed:0.##} / {w.ShootMoveSpeed:0.##} 米/秒" : $"{w.ShootMoveSpeed:0.#} 米/秒"), ("人形／出墨起手", $"{w.StartFrames}F / {w.EmergeStartFrames}F"),
            ("回墨锁定", $"{w.InkRecoverLockFrames}F")
        };
    }
}
