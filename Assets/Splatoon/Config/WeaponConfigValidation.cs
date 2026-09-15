using System;

namespace Splatoon.Config
{
    public static class WeaponConfigValidation
    {
        public static void Validate(WeaponRuntimeConfig w)
        {
            if (w == null) throw new InvalidOperationException("缺少武器配置");
            foreach (var f in typeof(WeaponRuntimeConfig).GetFields())
            {
                if (f.FieldType == typeof(float)) Require(float.IsFinite((float)f.GetValue(w)) && (float)f.GetValue(w) >= 0, f.Name + " 必须为有限非负数");
                if (f.FieldType == typeof(int)) Require((int)f.GetValue(w) >= 0, f.Name + " 必须非负");
            }
                Require(w.FireMode >= 0 && w.FireMode <= 4 && w.ShootMoveSpeed > 0 && w.BurstCount > 0, "武器名称、机制或移动配置无效");
                if (w.FireMode == 4) Require(w.SplatlingMinChargeFrames > 0 && w.SplatlingFirstChargeFrames > w.SplatlingMinChargeFrames && w.ChargeFrames > w.SplatlingFirstChargeFrames &&
                    w.SplatlingFullShootFrames > w.SplatlingFirstShootFrames && w.SplatlingSlowChargeMultiplier >= 1 && w.SplatlingChargeMoveSpeed > 0 && w.SplatlingChargeJumpSpeed > 0 &&
                    w.SplatlingPostFrames > 0 && w.SplatlingFootEvery > 0 && w.SplatlingTrailCount > 0 && w.SplatlingFootRadius > 0 && w.SplatlingPlayerRadius >= w.CollisionRadius &&
                    w.ChargePartialMaxDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.SplatlingSpeedBias > 0 && w.SplatlingSpeedBias < 1 &&
                    w.SplatlingSpreadBias > 0 && w.SplatlingSpreadBias < 1 && w.ChargeMinSpeed > 0 && w.ChargeMinRange > 0, "旋转枪分段蓄力或弹道配置无效");
                Require(w.PelletCount >= 1 && w.PelletCount <= 8 && w.MuzzleMode >= 0 && w.MuzzleMode <= 1 && w.SemiBufferFrames >= 0 && w.SemiBufferFrames * (double)w.FireRate <= 60 + .00001, "齐射、枪口或点击缓存配置无效");
                Require(w.FireMode == 3 || (w.PelletCount == 1 && w.MuzzleMode == 0 && w.SemiBufferFrames == 0), "新增齐射和轮播仅用于半自动");
                Require(w.MuzzleMode != 1 || w.PelletCount == 1, "双枪每次只发射一颗墨弹");
                Require(w.FireMode == 1 || w.BurstCount == 1, "非三连发武器每次只发射一颗");
                if (w.FireMode == 1) Require(w.BurstCount == 3 && w.BurstRecoveryFrames * (double)w.FireRate >= 60 - .00001, "三连发组间冷却无效");
                if (w.FireMode == 2) Require(w.ChargeFrames > 0 && w.ChargeMinDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.ChargePartialMaxDamage >= w.ChargeMinDamage && w.ChargeMinInk > 0 && w.ChargeMinInk < w.ShotInk && w.ChargeMinRange > 0 && w.ChargeMinRange <= w.EffectiveRange && w.ChargeMinSpeed > 0 && w.ChargeMinSpeed <= w.SpeedMin && w.ChargeMinJumpSpread >= w.ChargeMinSpread, "蓄力端点配置无效");
                Require(w.FireRate > 0 && w.FireRate <= 240 && w.Lifetime > 0 && w.Lifetime <= 10 && w.ShotInk > 0 && w.Damage > 0, "武器射击配置无效");
                Require(w.SpeedMin > 0 && w.SpeedMax >= w.SpeedMin && w.CollisionRadius > 0 && w.ProjectileGravity >= 0, "弹道配置无效");
                Require(w.PaintRadiusMin > 0 && w.PaintRadiusMax >= w.PaintRadiusMin && w.PaintHardness <= 1 && w.PaintStrength <= 1 && w.PaintStrength > 0 && w.SpreadDegrees <= 45, "笔刷或散布配置无效");
                Require(!string.IsNullOrWhiteSpace(w.WeaponPrefabAddress), "武器缺少资源地址");
                Require(w.StartFrames >= 0 && w.EmergeStartFrames >= w.StartFrames && w.InkRecoverLockFrames >= 0, "武器起手或回墨锁定帧数无效");
                Require(w.DamageMin > 0 && w.DamageMin <= w.Damage && w.DamageReduceStartFrames >= 0 && w.DamageReduceEndFrames > w.DamageReduceStartFrames && w.StraightFrames >= 0 && w.BrakeFrames > 0 && w.BrakeSpeedMultiplier > 0 && w.BrakeSpeedMultiplier <= 1 && (w.FireMode != 2 || w.SpreadRecoverFrames > 0) && w.JumpSpreadDegrees <= 45 && w.TrailSpacing > 0 && w.TrailRadiusMin > 0 && !float.IsInfinity(w.TrailRadiusMin) && w.TrailRadiusMax >= w.TrailRadiusMin && !float.IsInfinity(w.TrailRadiusMax) && w.EffectiveRange > 0, "武器弹道或落墨配置无效");
            Require(w.SplatlingPitchSpread <= 45 && w.ChargeMinSpread <= 45 && w.ChargeMinJumpSpread <= 45, "散布角度不得超过45度");
            Require(w.BaseSpreadDegrees <= w.SpreadDegrees && w.BaseJumpSpreadDegrees <= w.JumpSpreadDegrees && (w.PelletCount > 1 || (w.BaseSpreadDegrees == 0 && w.BaseJumpSpreadDegrees == 0)), "基础散布仅用于霰弹且不得超过最大散布");
        }
        static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
