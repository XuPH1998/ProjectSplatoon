using System;

namespace Splatoon.Config
{
    public static class WeaponConfigValidation
    {
        public static void Validate(WeaponRuntimeConfig w)
        {
            if (w == null) throw new InvalidOperationException("缺少武器配置");
            if (w.Ammo != null)
            {
                Require(w.Ammo.ExplosionRadius >= 0 && w.Ammo.ExplosionDamage >= 0, "弹药爆炸范围与伤害必须非负");
                Require(w.Ammo.ExplosionPaintRadiusMin >= 0 && w.Ammo.ExplosionPaintRadiusMax >= w.Ammo.ExplosionPaintRadiusMin, "弹药爆炸涂墨半径无效");
                if (w.Ammo.ExplosionEnabled) Require(w.Ammo.ExplosionPrefab != null, "启用爆炸时必须绑定爆炸预制体");
            }
            foreach (var f in typeof(WeaponRuntimeConfig).GetFields())
            {
                string name = WeaponConfigLabels.Name(f.Name);
                if (f.FieldType == typeof(float)) Require(float.IsFinite((float)f.GetValue(w)) && (float)f.GetValue(w) >= 0, name + "必须为有限非负数");
                if (f.FieldType == typeof(double)) Require(double.IsFinite((double)f.GetValue(w)) && (double)f.GetValue(w) >= 0, name + "必须为有限非负数");
                if (f.FieldType == typeof(int)) Require((int)f.GetValue(w) >= 0, name + "必须非负");
                if (f.FieldType.IsEnum) Require(Enum.IsDefined(f.FieldType, f.GetValue(w)), name + "必须选择有效选项");
            }
                Require(w.ShootMoveSpeed > 0 && w.BurstCount > 0, "射击移动速度与每组发数必须大于零");
                if (w.FireMode == WeaponFireMode.Splatling) Require(w.SplatlingMinChargeSeconds > 0 && w.SplatlingFirstChargeSeconds > w.SplatlingMinChargeSeconds && w.ChargeSeconds > w.SplatlingFirstChargeSeconds &&
                    w.SplatlingFullShootSeconds > w.SplatlingFirstShootSeconds && w.SplatlingSlowChargeMultiplier >= 1 && w.SplatlingChargeMoveSpeed > 0 && w.SplatlingChargeJumpSpeed > 0 &&
                    w.SplatlingPostSeconds > 0 && w.SplatlingFootEvery > 0 && w.SplatlingTrailCount > 0 && w.SplatlingFootRadius > 0 && w.SplatlingPlayerRadius >= w.CollisionRadius &&
                    w.ChargePartialMaxDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.SplatlingSpeedBias > 0 && w.SplatlingSpeedBias < 1 &&
                    w.SplatlingSpreadBias > 0 && w.SplatlingSpreadBias < 1 && w.ChargeMinSpeed > 0 && w.ChargeMinRange > 0, "旋转枪分段蓄力或弹道配置无效");
                Require(w.PelletCount >= 1 && w.PelletCount <= 8 && w.SemiBufferSeconds * w.FireRate <= 1 + .00001 / 60, "弹丸数量须为1至8，点击缓存时长不得超过一次射击间隔");
                Require(w.FireMode == WeaponFireMode.SemiAutomatic || (w.PelletCount == 1 && w.MuzzleMode == WeaponMuzzleMode.Single && w.SemiBufferSeconds == 0), "新增齐射和轮播仅用于半自动");
                Require(w.MuzzleMode != WeaponMuzzleMode.AlternatingRightLeft || w.PelletCount == 1, "双枪每次只发射一颗墨弹");
                Require(w.FireMode == WeaponFireMode.Burst || w.BurstCount == 1, "非三连发武器每次只发射一颗");
                if (w.FireMode == WeaponFireMode.Burst) Require(w.BurstCount == 3 && w.BurstRecoverySeconds * w.FireRate >= 1 - .00001 / 60, "三连发须为每组3发，组间恢复时长不得短于一次射击间隔");
                if (w.FireMode == WeaponFireMode.Charge) Require(w.ChargeSeconds > 0 && w.ChargeMinDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.ChargePartialMaxDamage >= w.ChargeMinDamage && w.ChargeMinInk > 0 && w.ChargeMinInk < w.ShotInk && w.ChargeMinRange > 0 && w.ChargeMinRange <= w.EffectiveRange && w.ChargeMinSpeed > 0 && w.ChargeMinSpeed <= w.SpeedMin && w.ChargeMinJumpSpread >= w.ChargeMinSpread, "蓄力端点配置无效");
                Require(w.FireRate > 0 && w.FireRate <= 240 && w.Lifetime > 0 && w.Lifetime <= 10 && w.ShotInk > 0 && w.Damage > 0, "武器射击配置无效");
                Require(w.SpeedMin > 0 && w.SpeedMax >= w.SpeedMin && w.CollisionRadius > 0 && w.ProjectileGravity >= 0, "弹道配置无效");
                Require(w.PaintRadiusMin > 0 && w.PaintRadiusMax >= w.PaintRadiusMin && w.PaintHardness <= 1 && w.PaintStrength <= 1 && w.PaintStrength > 0 && w.SpreadDegrees <= 45, "笔刷或散布配置无效");
                Require(!string.IsNullOrWhiteSpace(w.WeaponPrefabAddress), "武器缺少资源地址");
                Require(w.EmergeStartSeconds >= w.StartSeconds, "出墨起手时长不得短于人形起手时长");
                Require(w.DamageMin > 0 && w.DamageMin <= w.Damage && w.DamageReduceStartSeconds >= 0 && w.DamageReduceEndSeconds > w.DamageReduceStartSeconds && w.StraightSeconds >= 0 && w.BrakeSeconds > 0 && w.BrakeSpeedMultiplier > 0 && w.BrakeSpeedMultiplier <= 1 && (w.FireMode != WeaponFireMode.Charge || w.LandingSpreadRecoverSeconds > 0) && w.JumpSpreadDegrees <= 45 && w.TrailSpacing > 0 && w.TrailRadiusMin > 0 && !float.IsInfinity(w.TrailRadiusMin) && w.TrailRadiusMax >= w.TrailRadiusMin && !float.IsInfinity(w.TrailRadiusMax) && w.EffectiveRange > 0, "武器弹道或落墨配置无效");
            Require(w.SplatlingPitchSpread <= 45 && w.ChargeMinSpread <= 45 && w.ChargeMinJumpSpread <= 45, "散布角度不得超过45度");
            Require(w.BaseSpreadDegrees <= w.SpreadDegrees && w.BaseJumpSpreadDegrees <= w.JumpSpreadDegrees && (w.PelletCount > 1 || (w.BaseSpreadDegrees == 0 && w.BaseJumpSpreadDegrees == 0)), "基础散布仅用于霰弹且不得超过最大散布");
        }
        static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
