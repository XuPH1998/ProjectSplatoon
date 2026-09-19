using Splatoon.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class WeaponDisplay
    {
        public static string Mechanism(WeaponRuntimeConfig w) => WeaponSimulation.IsFloatingBubble(w) ? $"{w.PelletCount} 颗齐射、缓速漂浮、接触或到期爆炸" : DualiesNormalSimulation.Enabled(w) ? "全自动 / 右左交替" : w.FireMode switch { WeaponFireMode.Explosher => "抛射穿透，触地爆炸",
            WeaponFireMode.Blaster => "快速爆破 / 点击或长按射击", WeaponFireMode.BubbleVolley => "泡泡四连发 / 弹跳与墙面反射", WeaponFireMode.Splatling => "旋转枪 / 两段蓄力连射", WeaponFireMode.Burst => "三连发 / 长按连续", WeaponFireMode.Charge => "蓄力 / 松开发射", WeaponFireMode.SemiAutomatic => w.MuzzleMode == WeaponMuzzleMode.AlternatingRightLeft ? "半自动 / 右左交替" : w.PelletCount > 1 ? "半自动 / 霰弹齐射" : "半自动 / 点击或长按射击", _ => "全自动" };
        public static string Damage(WeaponRuntimeConfig w) => WeaponSimulation.IsFloatingBubble(w) ? $"每颗直击 {w.Damage:0} / 爆风 0–{w.Ammo.ExplosionDamage:0}（直击不重复）" : WeaponSimulation.IsExplosher(w) ? $"直击 {w.Damage:0}／爆风 {w.Ammo.ExplosionDamage:0}／组合 {w.Damage + w.Ammo.ExplosionDamage:0}" : WeaponSimulation.IsBlaster(w) ? $"直击 {w.Damage:0} / 爆风 {w.Ammo.ExplosionDamage:0} / 碰撞爆风 {w.Ammo.ExplosionDamage*w.Ammo.CollisionExplosionDamageRate:0.#}" : WeaponSimulation.IsSplatling(w) ? $"未满蓄 {w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0} → 远端 {w.DamageMin:0}" : WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0} / 满蓄 {w.Damage:0}" : w.PelletCount > 1 ? $"{w.PelletCount} × {w.Damage:0}→{w.DamageMin:0}" : $"{w.Damage:0} → {w.DamageMin:0}";
        public static string Cadence(WeaponRuntimeConfig w) => WeaponSimulation.IsFloatingBubble(w) ? $"{WeaponSimulation.FireInterval(w):0.###} 秒/轮 · 每轮 {w.PelletCount} 颗" : w.FireMode switch
        {
            WeaponFireMode.Explosher => $"{WeaponSimulation.FireInterval(w):0.###} 秒/发 · 潜墨限制 {w.ExplosherPostSeconds:0.###} 秒 · 移速限制 {w.ExplosherMoveLimitSeconds:0.###} 秒",
            WeaponFireMode.Blaster => $"{w.BlasterRepeatSeconds:0.###} 秒/发 · 射后限制 {w.BlasterPostSeconds:0.###} 秒",
            WeaponFireMode.Splatling => $"一圈 {w.SplatlingFirstChargeSeconds:0.###} 秒 / 满蓄 {w.ChargeSeconds:0.###} 秒 · {w.FireRate:0} 发/秒",
            WeaponFireMode.BubbleVolley => $"{w.BubbleVolleySeconds:0.###} 秒/组，颗间 {w.BubbleIntervalSeconds:0.###} 秒",
            WeaponFireMode.Burst => $"组内 {w.FireRate:0.##} 发/秒 / 组间 {w.BurstRecoverySeconds:0.###} 秒",
            WeaponFireMode.Charge => $"蓄力 {w.ChargeSeconds:0.###} 秒 / 冷却 {WeaponSimulation.FireInterval(w):0.###} 秒",
            _ => $"{w.FireRate:0.##} 发/秒"
        };
        public static float SustainedRate(WeaponRuntimeConfig w) => w.FireMode == WeaponFireMode.Burst ? (float)(w.BurstCount / ((w.BurstCount - 1) * WeaponSimulation.FireInterval(w) + w.BurstRecoverySeconds)) : w.FireRate;
        public static string Ink(WeaponRuntimeConfig w) => WeaponSimulation.IsBubble(w) ? $"{w.ShotInk:0.##} / 组（{w.BurstCount} 颗）" : WeaponSimulation.IsSplatling(w) ? $"满蓄 {w.ShotInk*SplatlingSimulation.Rounds(w,w.ChargeSeconds):0.##} / {SplatlingSimulation.Rounds(w,w.ChargeSeconds)} 发，取消返还未发射部分" : WeaponSimulation.IsCharge(w) ? $"{w.ChargeMinInk:0.##} / 满蓄 {w.ShotInk:0.##}" : $"{w.ShotInk:0.##} / {(w.PelletCount > 1 ? "次齐射" : "发")}";
        public static string Range(WeaponRuntimeConfig w)
        {
            var r = new WeaponRangeMetrics(w);
            return $"平地{((WeaponSimulation.IsBlaster(w) || WeaponSimulation.IsFloatingBubble(w)) ? "爆炸中心" : "落点")} {r.Flat:0.##} 米";
        }
        public static string FloatingHorizontalSpread(WeaponRuntimeConfig w) => w.PelletCount > 1 ? $"水平间隔 {w.SpreadDegrees * 2 / (w.PelletCount - 1):0.#}°" : "单发水平居中";
        public static string Spread(WeaponRuntimeConfig w) => WeaponSimulation.IsFloatingBubble(w) ? $"并排 {w.PelletCount} 发 · {FloatingHorizontalSpread(w)} · 垂直 ±{w.FloatingPitchSpreadDegrees:0.#}°" : w.ReferenceRules ?
            (WeaponSimulation.IsSplatling(w) ? $"地面 {w.SpreadDegrees:0.##}°水平 / {w.SplatlingPitchSpread:0.##}°垂直；空中 {w.JumpSpreadDegrees:0.##}°" : $"地面 {w.SpreadDegrees:0.##}° / 空中 {w.JumpSpreadDegrees:0.##}°") : WeaponSimulation.IsBlaster(w) ? $"地面 {w.SpreadDegrees:0.#}° / 空中 {w.JumpSpreadDegrees:0.#}°（固定）" : DualiesNormalSimulation.Enabled(w) ? $"地面最大 {w.SpreadDegrees:0.#}° / 空中最大 {w.JumpSpreadDegrees:0.#}°；首发较集中，连射偏差增大" : WeaponSimulation.IsCharge(w)
            ? $"{w.ChargeMinSpread:0.#}°/{w.ChargeMinJumpSpread:0.#}° → {w.SpreadDegrees:0.#}°/{w.JumpSpreadDegrees:0.#}°（随蓄力收紧）"
            : WeaponSimulation.IsSplatling(w) ? $"地面 0 → {w.SpreadDegrees:0.#}°水平 / {w.SplatlingPitchSpread:0.#}°垂直；空中 0 → {w.JumpSpreadDegrees:0.#}°"
            : $"地面 {w.BaseSpreadDegrees:0.#}° → {w.SpreadDegrees:0.#}° / 空中 {w.BaseJumpSpreadDegrees:0.#}° → {w.JumpSpreadDegrees:0.#}°";
        public static string ListSummary(WeaponRuntimeConfig w) => WeaponSimulation.IsFloatingBubble(w) ? $"{w.PelletCount} 颗齐射 · 直击 {w.Damage:0} / 爆风 0–{w.Ammo.ExplosionDamage:0}\n{w.Lifetime:0.#} 秒漂浮 · {WeaponSimulation.FireInterval(w):0.#} 秒/轮 · {w.ShotInk:0} 墨/轮" : w.FireMode switch
        {
            WeaponFireMode.Explosher => $"直击 {w.Damage:0}／爆风 {w.Ammo.ExplosionDamage:0}／组合 {w.Damage + w.Ammo.ExplosionDamage:0} · {WeaponSimulation.FireInterval(w):0.###} 秒/发\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · 满墨 {Mathf.FloorToInt(100 / w.ShotInk)} 发",
            WeaponFireMode.Blaster => $"直击 {w.Damage:0} / 爆风 {w.Ammo.ExplosionDamage:0} · {w.BlasterRepeatSeconds:0.###} 秒/发\n{Range(w)} · {w.ShotInk:0.##} 墨/发",
            WeaponFireMode.BubbleVolley => $"四颗弹跳泡泡 · 每颗 {w.Damage:0} 伤害 · {w.ShotInk:0.##} 墨/组\n{w.BubbleVolleySeconds:0.###} 秒/组 · 转向甩射、借墙反射",
            WeaponFireMode.Splatling => $"两段蓄力 {w.SplatlingFirstChargeSeconds:0.###}/{w.ChargeSeconds:0.###} 秒 · {w.ChargePartialMaxDamage:0}/满蓄 {w.Damage:0} 伤害\n{Range(w)} · 满蓄 {SplatlingSimulation.Rounds(w,w.ChargeSeconds)} 发 · {w.ShotInk*SplatlingSimulation.Rounds(w,w.ChargeSeconds):0} 墨",
            WeaponFireMode.SemiAutomatic => $"{Mechanism(w)} · {Damage(w)} 伤害\n{Range(w)} · {w.ShotInk:0.##} 墨/次 · 最快 {SustainedRate(w):0.#} 次/秒",
            WeaponFireMode.Burst => $"三连发 · {w.Damage:0}→{w.DamageMin:0} 伤害 · 组内 {w.FireRate:0.##} 发/秒 / 组间 {w.BurstRecoverySeconds:0.###} 秒\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒",
            WeaponFireMode.Charge => $"蓄力 {w.ChargeSeconds:0.###} 秒 · {w.ChargeMinDamage:0}–{w.ChargePartialMaxDamage:0}/满 {w.Damage:0} 伤害\n{Range(w)} · {w.ChargeMinInk:0.#}–{w.ShotInk:0.#} 墨 · 冷却 {WeaponSimulation.FireInterval(w):0.###} 秒",
            _ => $"{Mechanism(w)} · {w.Damage:0}→{w.DamageMin:0} 伤害\n{Range(w)} · {w.ShotInk:0.##} 墨/发 · {SustainedRate(w):0.#} 发/秒"
        };
        public static List<(string label, string value)> Details(cfg.HeroConfig hero) => Details(hero, GameplayConfig.GetWeapon(hero.Id));
        public static List<(string label, string value)> Details(cfg.HeroConfig hero, WeaponRuntimeConfig w) => new()
        {
            ("生命／墨量上限", $"{hero.MaxHealth:0.#} / {hero.MaxInk:0.#}"),
            ("人形／潜墨移速", $"{hero.MoveSpeed:0.#} / {hero.SwimSpeed:0.#} 米/秒"),
            ("人形／潜墨回墨", $"{hero.RecoverInk:0.##} / {hero.SwimRecoverInk:0.##} 点/秒"),
            ("伤害", Damage(w)), ("射击节奏", Cadence(w)),
            ("射速上限／方式", WeaponSimulation.IsBubble(w) ? "点击一组，长按连续；组内转向可甩射" : WeaponSimulation.IsSplatling(w) ? "按住蓄力，松开持续射击；Shift 取消" : WeaponSimulation.IsCharge(w) ? "按住蓄力，松开发射" : WeaponSimulation.IsSemi(w) ? $"最快 {SustainedRate(w):0.##} 次/秒，点击单发，长按连续" : $"{SustainedRate(w):0.##} 发/秒"),
            ("耗墨", w.FireMode == WeaponFireMode.Burst ? $"{w.ShotInk:0.##}/发 · {w.ShotInk*w.BurstCount:0.##}/组" : Ink(w)), ("满墨发数", WeaponSimulation.IsBubble(w) ? $"{Mathf.FloorToInt(hero.MaxInk / w.ShotInk)} 组" : WeaponSimulation.IsCharge(w) ? $"点射 {Mathf.FloorToInt(hero.MaxInk / w.ChargeMinInk)} / 满蓄 {Mathf.FloorToInt(hero.MaxInk / w.ShotInk)}" : $"{Mathf.FloorToInt(hero.MaxInk / w.ShotInk)} 发"),
            ("平地参考落点", Range(w) + "（枪口高 1.4 米、无散布）"),
            ("直进距离", $"{new WeaponRangeMetrics(w).Straight:0.##} 米"),
            ("伤害衰减距离", WeaponSimulation.IsBlaster(w) || WeaponSimulation.IsBubble(w) || WeaponSimulation.IsExplosher(w) || WeaponSimulation.IsFloatingBubble(w) ? "飞行期间不衰减" : $"{new WeaponRangeMetrics(w).FullDamage:0.##}～{new WeaponRangeMetrics(w).MinimumDamage:0.##} 米；其后保持最低伤害至终止"),
            ("平地涂墨最远上界", $"{new WeaponRangeMetrics(w).PaintEnvelope:0.##} 米（几何上界，实际墨迹随轮廓与散布变化）"),
            ("地面／空中散布", Spread(w)),
            ("散布扩大／完全恢复", WeaponSimulation.IsFloatingBubble(w) ? "固定水平间距；每颗独立上下偏转" : ReferenceSpreadSimulation.Enabled(w) ? $"逐发偏置累积；跳跃 {w.ReferenceJumpStart:0.###}～{w.ReferenceJumpEnd:0.###} 秒恢复" : WeaponSimulation.IsBlaster(w) ? "固定角度；连续射击不扩散" : DualiesNormalSimulation.Enabled(w) ? "随发数增大，停火逐步恢复；首发保留少量偏差" : WeaponSimulation.IsCharge(w) ? "蓄力控制；不使用时间扩散" : $"{w.SpreadExpandSeconds:0.###} / {w.SpreadRecoverSeconds:0.###} 秒"),
            (WeaponSimulation.IsSplatling(w) ? "蓄力／射击移动速度" : "射击移动速度", WeaponSimulation.IsSplatling(w) ? $"{w.SplatlingChargeMoveSpeed:0.##} / {w.ShootMoveSpeed:0.##} 米/秒" : $"{w.ShootMoveSpeed:0.#} 米/秒"), ("人形／出墨起手", $"{w.StartSeconds:0.###} 秒 / {w.EmergeStartSeconds:0.###} 秒"),
            ("回墨锁定", $"{w.InkRecoverLockSeconds:0.###} 秒")
        };
    }
}
