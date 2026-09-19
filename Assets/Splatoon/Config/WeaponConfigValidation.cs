using System;

namespace Splatoon.Config
{
    public static class WeaponConfigValidation
    {
        public static void Validate(WeaponRuntimeConfig w)
        {
            if (w == null) throw new InvalidOperationException("缺少武器配置");
            Require(w.ReferenceFootDepth > 0, "脚下墨迹纵深比例必须大于0");
            if (w.ShooterDetails)
            {
                Require(w.ReferenceRules && w.ReferenceSpreadEnabled && w.FireMode == WeaponFireMode.Automatic &&
                    w.MotionMode == ProjectileMotionMode.ReferencePhased && w.PelletCount == 1 && w.MuzzleMode == WeaponMuzzleMode.Single && !w.Ammo.ExplosionEnabled, "射手详细规则要求单发自动参考墨弹");
                Require(w.ShooterSplitNum > 0 && w.ShooterSplitNum <= 64 && w.ShooterPaintNearRadius > 0 &&
                    w.PaintDistanceMiddle > w.ShooterPaintNearDistance && w.PaintDistanceFar > w.PaintDistanceMiddle &&
                    w.ShooterPaintAngleMax > w.ShooterPaintAngleMin && w.ShooterPaintAngleMax <= 90 &&
                    w.ShooterFallHeightMax > w.ShooterFallHeightMin && w.ShooterSplashHeightMax > w.ShooterSplashHeightMin &&
                    w.ShooterSplashDepthMin > 0 && w.ShooterSplashDepthMax >= w.ShooterSplashDepthMin &&
                    w.ShooterSplashForwardMax >= w.ShooterSplashForwardMin, "射手落墨节点、循环或纵深无效");
                Require(w.ShooterWallFirstMin > 0 && w.ShooterWallFirstMax >= w.ShooterWallFirstMin && w.ShooterWallMiddle > 0 &&
                    w.ShooterWallLastMin > 0 && w.ShooterWallLastMax >= w.ShooterWallLastMin && w.ShooterWallGravity > 0 &&
                    w.ShooterWallFirstSpeed > 0 && w.ShooterWallShockRadius > 0 && w.WallDropSpeed > 0, "射手墙墨阶段无效");
            }
            w.Ammo.Validate();
            if (w.MotionMode == ProjectileMotionMode.FloatingBubble)
                Require(w.FireMode == WeaponFireMode.SemiAutomatic && !w.ReferenceRules && !w.ShooterDetails &&
                    w.MuzzleMode == WeaponMuzzleMode.Single && w.Ammo.BubblePrefab != null &&
                    w.Ammo.HasExplosion && w.Ammo.HasExplosionVisual && w.Ammo.ExplosionPaint &&
                    w.Ammo.ExplosionRadius > 0 && w.Ammo.ExplosionPaintRadiusMin > 0 &&
                    !w.Ammo.ExplosionConstantDamage && w.Ammo.ExcludeDirectHitFromExplosion &&
                    w.Ammo.CollisionExplosionRadiusRate == 1 && w.Ammo.CollisionExplosionDamageRate == 1 &&
                    w.Damage == w.DamageMin && w.SpeedMin == w.SpeedMax &&
                    w.BrakeSpeedMultiplier < 1 && w.Lifetime > w.StraightSeconds + w.BrakeSeconds &&
                    w.BaseSpreadDegrees == w.SpreadDegrees && w.BaseJumpSpreadDegrees == w.JumpSpreadDegrees &&
                    w.SpreadDegrees == w.JumpSpreadDegrees && w.FloatingPitchSpreadDegrees <= 45,
                    "漂浮泡泡须为半自动齐射、恒定直击、减速飞行、独立衰减爆风和爆炸涂墨");
            foreach (var f in typeof(WeaponRuntimeConfig).GetFields())
            {
                string name = WeaponConfigLabels.Name(f.Name);
                if (f.FieldType == typeof(float)) Require(float.IsFinite((float)f.GetValue(w)) && (float)f.GetValue(w) >= 0, name + "必须为有限非负数");
                if (f.FieldType == typeof(double)) Require(double.IsFinite((double)f.GetValue(w)) && (double)f.GetValue(w) >= 0, name + "必须为有限非负数");
                if (f.FieldType == typeof(int)) Require((int)f.GetValue(w) >= 0, name + "必须非负");
                if (f.FieldType.IsEnum) Require(Enum.IsDefined(f.FieldType, f.GetValue(w)), name + "必须选择有效选项");
            }

            if (w.FireMode == WeaponFireMode.Explosher || w.MotionMode == ProjectileMotionMode.Explosher)
            {
                Require(w.FireMode == WeaponFireMode.Explosher && w.MotionMode == ProjectileMotionMode.Explosher && w.ReferenceRules &&
                    w.PelletCount == 1 && w.MuzzleMode == WeaponMuzzleMode.Single && w.Damage == w.DamageMin && w.SpreadDegrees == 0 && w.JumpSpreadDegrees == 0 &&
                    w.Ammo.HasExplosion && w.Ammo.ExplosionConstantDamage && !w.Ammo.ExcludeDirectHitFromExplosion && w.Ammo.CollisionExplosionDamageRate == 1 && w.Ammo.CollisionExplosionRadiusRate == 1,
                    "爆炸泼桶须为单颗穿透墨弹、恒定直击与爆风伤害、零散布");
                Require(w.ExplosherAirSpeed > 0 && w.ExplosherFieldInitialRadius > 0 && w.ExplosherFieldInitialRadius <= w.CollisionRadius &&
                    w.ExplosherPlayerInitialRadius > 0 && w.ExplosherPlayerInitialRadius <= w.ReferencePlayerRadius && w.ExplosherFieldGrowSeconds > 0 && w.ExplosherPlayerGrowSeconds > 0 &&
                    w.ExplosherPostSeconds > 0 && w.ExplosherMoveLimitSeconds >= w.ExplosherPostSeconds && w.ExplosherPaintFarDistance > w.ExplosherPaintNearDistance &&
                    w.ExplosherTrailPhaseMax >= w.ReferenceTrailStart / w.TrailSpacing && w.ExplosherTrailPhaseMax <= 1 && w.ExplosherFootDepth > 0,
                    "爆炸泼桶速度、碰撞成长、动作限制或涂墨参数无效");
            }
            if (w.MotionMode == ProjectileMotionMode.DualiesNormal)
            {
                Require(w.FireMode == WeaponFireMode.Automatic && w.MuzzleMode == WeaponMuzzleMode.AlternatingRightLeft &&
                    w.PelletCount == 1 && !w.Ammo.ExplosionEnabled, "普通双枪须全自动交替单发，不启用爆炸");
                Require(w.DualiesBrakeEndSpeed > 0 && w.DualiesBrakeEndSpeed <= w.SpeedMin &&
                    w.DualiesBrakeDrag > 0 && w.DualiesBrakeDrag < 1 && w.DualiesBrakeGravity > 0 &&
                    w.DualiesPlayerRadius >= w.CollisionRadius, "双枪制动或玩家半径无效");
                Require(w.DualiesSpreadMinBias > 0 && w.DualiesSpreadMinBias <= w.DualiesSpreadMaxBias &&
                    w.DualiesSpreadMaxBias <= w.DualiesJumpBias && w.DualiesJumpBias < 1 &&
                    w.DualiesSpreadPerShot > 0 && w.DualiesSpreadRecoverPerSecond > 0 &&
                    w.DualiesJumpRecoverEndSeconds > w.DualiesJumpRecoverStartSeconds, "双枪散布偏置或恢复时间无效");
                Require(w.DualiesFootEvery > 0 && w.DualiesFootRadius > 0 && w.DualiesTrailCount > 0 &&
                    w.DualiesTrailCount <= 16 && w.DualiesTrailStartDistance > 0, "双枪落墨配置无效");
            }
            if (w.FireMode == WeaponFireMode.Blaster || w.MotionMode == ProjectileMotionMode.TimedBlaster)
                Require(w.FireMode == WeaponFireMode.Blaster && w.MotionMode == ProjectileMotionMode.TimedBlaster &&
                    w.BlasterRepeatSeconds >= 1.0 / 60 && w.BlasterPostSeconds > 0 && w.BlasterPostSeconds < w.BlasterRepeatSeconds &&
                    Math.Abs(w.BlasterRepeatSeconds * w.FireRate - 1) < .00001 &&
                    w.BlasterPlayerRadius >= w.CollisionRadius && w.BlasterBrakeEndSpeed > 0 && w.BlasterBrakeEndSpeed <= w.SpeedMin &&
                    w.BlasterBrakeDrag >= 0 && w.BlasterBrakeDrag < 1 && w.BlasterBrakeGravity > 0 &&
                    w.BlasterTrailCount > 0 && w.BlasterTrailCount <= 64 && w.Lifetime > w.StraightSeconds &&
                    w.Ammo.ExplosionEnabled && w.Ammo.ExplosionRadius > 0 && w.Ammo.ExplosionConstantDamage && w.Ammo.ExcludeDirectHitFromExplosion &&
                    w.Damage == w.DamageMin && w.ChargeSeconds == 0 && w.PelletCount == 1 && w.BurstCount == 1,
                    "爆破枪发射、制动、碰撞或爆风配置无效");
            if (w.FireMode == WeaponFireMode.BubbleVolley || w.MotionMode == ProjectileMotionMode.BouncingBubble)
                Require(w.FireMode == WeaponFireMode.BubbleVolley && w.MotionMode == ProjectileMotionMode.BouncingBubble &&
                    w.BurstCount >= 1 && w.BurstCount <= 8 && w.BubbleIntervalSeconds >= 1.0 / 60 &&
                    w.BubbleVolleySeconds > (w.BurstCount - 1) * w.BubbleIntervalSeconds &&
                    w.BubbleGroundBounces >= 0 && w.BubbleMaxBounces >= w.BubbleGroundBounces && w.BubbleMaxBounces <= 16 &&
                    w.BubbleNormalRetention > 0 && w.BubbleNormalRetention <= 1 && w.BubbleTangentRetention > 0 && w.BubbleTangentRetention <= 1 &&
                    w.BubbleWallRetention > 0 && w.BubbleWallRetention <= 1 && !w.Ammo.ExplosionEnabled && w.PelletCount == 1 &&
                    w.Damage == w.DamageMin && w.SpreadDegrees == 0 && w.JumpSpreadDegrees == 0 &&
                    (w.ReferenceRules || w.StraightSeconds == 0) && w.BrakeSpeedMultiplier == 1 && w.Ammo.BubblePrefab != null, "泡泡连发、弹跳或伤害配置无效");
            if (w.ReferenceRules)
            {
                Require(w.ReferenceBrakeEndSpeed > 0 && w.ReferenceBrakeDrag >= 0 && w.ReferenceBrakeDrag < 1 && w.ReferenceFreeDrag >= 0 && w.ReferenceFreeDrag < 1 && w.ReferenceBrakeGravity >= 0 && w.ReferencePlayerRadius >= w.CollisionRadius, "参考弹道或碰撞无效");
                Require(w.ReferenceTrailBudget >= 0 && w.ReferenceTrailBudget <= 64 && w.ReferenceFootEvery > 0 && w.ReferenceFootRadius >= 0 && w.PaintDepthMin > 0 && w.PaintDepthMax >= w.PaintDepthMin && w.PaintDepthBreakMin > 0 && w.PaintDepthBreakMax >= w.PaintDepthBreakMin && w.PaintDistanceFar > w.PaintDistanceMiddle && w.TrailDepthScale > 0 && w.PaintDropGravity > 0 && w.PaintDropLifetime > 0 && w.PaintDropLifetime <= 10, "参考涂墨配置无效");
                if (w.ReferenceSpreadEnabled) Require(w.ReferenceBiasMin > 0 && w.ReferenceBiasMax >= w.ReferenceBiasMin && w.ReferenceBiasMax < 1 && w.ReferenceJumpBias >= w.ReferenceBiasMax && w.ReferenceJumpBias < 1 && w.ReferenceJumpEnd > w.ReferenceJumpStart && w.ReferencePitchBias > 0 && w.ReferencePitchBias < 1, "参考散布配置无效");
                if (w.MotionMode == ProjectileMotionMode.BouncingBubble) Require(w.BubbleAirSpeed > 0 && w.BubbleLaterSpeed > 2*w.BubbleSpeedDecrement && w.BubbleLaterAirSpeed > 2*w.BubbleSpeedDecrement && w.BubbleLaterFieldRadius > 2*w.BubbleRadiusDecrement && w.BubbleLaterPlayerRadius >= w.BubbleLaterFieldRadius && w.BubbleInitialRadiusRate > 0 && w.BubbleInitialRadiusRate <= 1 && w.BubbleFieldGrowSeconds > 0 && w.BubblePlayerGrowSeconds > 0 && w.BubbleBounceRadiusRate > 0 && w.BubbleBounceRadiusRate <= 1 && w.BubbleBouncePaintRate > 0 && w.BubbleBouncePaintRate <= 1, "参考泡泡参数无效");
            }
                Require(w.ShootMoveSpeed > 0 && w.BurstCount > 0, "射击移动速度与每组发数必须大于零");
                if (w.FireMode == WeaponFireMode.Splatling) Require(w.SplatlingMinChargeSeconds > 0 && w.SplatlingFirstChargeSeconds > w.SplatlingMinChargeSeconds && w.ChargeSeconds > w.SplatlingFirstChargeSeconds &&
                    w.SplatlingFullShootSeconds > w.SplatlingFirstShootSeconds && w.SplatlingSlowChargeMultiplier >= 1 && w.SplatlingChargeMoveSpeed > 0 && w.SplatlingChargeJumpSpeed > 0 &&
                    w.SplatlingPostSeconds > 0 && w.SplatlingFootEvery > 0 && w.SplatlingTrailCount > 0 && w.SplatlingFootRadius > 0 && w.SplatlingPlayerRadius >= w.CollisionRadius &&
                    w.ChargePartialMaxDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.SplatlingSpeedBias > 0 && w.SplatlingSpeedBias < 1 &&
                    w.SplatlingSpreadBias > 0 && w.SplatlingSpreadBias < 1 && w.ChargeMinSpeed > 0 && w.ChargeMinRange > 0, "旋转枪分段蓄力或弹道配置无效");
                Require(w.PelletCount >= 1 && w.PelletCount <= 8 && w.SemiBufferSeconds * w.FireRate <= 1 + .00001 / 60, "弹丸数量须为1至8，点击缓存时长不得超过一次射击间隔");
                Require((w.FireMode == WeaponFireMode.SemiAutomatic || w.FireMode == WeaponFireMode.Explosher) || (w.PelletCount == 1 && w.SemiBufferSeconds == 0 && (w.MuzzleMode == WeaponMuzzleMode.Single || w.FireMode == WeaponFireMode.Automatic)), "齐射和点击缓存仅用于半自动；交替枪口支持半自动或全自动");
                Require(w.MuzzleMode != WeaponMuzzleMode.AlternatingRightLeft || w.PelletCount == 1, "双枪每次只发射一颗墨弹");
                Require(w.FireMode == WeaponFireMode.Burst || w.FireMode == WeaponFireMode.BubbleVolley || w.BurstCount == 1, "非三连发武器每次只发射一颗");
                if (w.FireMode == WeaponFireMode.Burst) Require(w.BurstCount == 3 && w.BurstRecoverySeconds * w.FireRate >= 1 - .00001 / 60, "三连发须为每组3发，组间恢复时长不得短于一次射击间隔");
                if (w.FireMode == WeaponFireMode.Charge) Require(w.ChargeSeconds > 0 && w.ChargeMinDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.ChargePartialMaxDamage >= w.ChargeMinDamage && w.ChargeMinInk > 0 && w.ChargeMinInk < w.ShotInk && w.ChargeMinRange > 0 && w.ChargeMinRange <= w.EffectiveRange && w.ChargeMinSpeed > 0 && w.ChargeMinSpeed <= w.SpeedMin && w.ChargeMinJumpSpread >= w.ChargeMinSpread, "蓄力端点配置无效");
                Require(w.FireRate > 0 && w.FireRate <= 240 && w.Lifetime > 0 && w.Lifetime <= 10 && w.ShotInk > 0 && w.Damage > 0, "武器射击配置无效");
                Require(w.SpeedMin > 0 && w.SpeedMax >= w.SpeedMin && w.CollisionRadius > 0 && w.ProjectileGravity >= 0, "弹道配置无效");
                Require(w.PaintRadiusMin > 0 && w.PaintRadiusMax >= w.PaintRadiusMin && w.PaintHardness <= 1 && w.PaintStrength <= 1 && w.PaintStrength > 0 && w.SpreadDegrees <= 45, "笔刷或散布配置无效");
                Require(!string.IsNullOrWhiteSpace(w.WeaponPrefabAddress), "武器缺少资源地址");
                Require(w.EmergeStartSeconds >= w.StartSeconds, "出墨起手时长不得短于人形起手时长");
                Require(w.DamageMin > 0 && w.DamageMin <= w.Damage && w.DamageReduceStartSeconds >= 0 && w.DamageReduceEndSeconds > w.DamageReduceStartSeconds && w.StraightSeconds >= 0 && w.BrakeSeconds > 0 && w.BrakeSpeedMultiplier > 0 && w.BrakeSpeedMultiplier <= 1 && (w.FireMode != WeaponFireMode.Charge || w.LandingSpreadRecoverSeconds > 0) && w.JumpSpreadDegrees <= 45 && w.TrailSpacing > 0 && w.TrailRadiusMin > 0 && !float.IsInfinity(w.TrailRadiusMin) && w.TrailRadiusMax >= w.TrailRadiusMin && !float.IsInfinity(w.TrailRadiusMax) && w.EffectiveRange > 0, "武器弹道或落墨配置无效");
            Require(w.SplatlingPitchSpread <= 45 && w.ChargeMinSpread <= 45 && w.ChargeMinJumpSpread <= 45, "散布角度不得超过45度");
            Require(w.BaseSpreadDegrees <= w.SpreadDegrees && w.BaseJumpSpreadDegrees <= w.JumpSpreadDegrees && (w.MotionMode == ProjectileMotionMode.FloatingBubble || w.PelletCount > 1 || (w.BaseSpreadDegrees == 0 && w.BaseJumpSpreadDegrees == 0)), "基础散布仅用于霰弹或漂浮泡泡且不得超过最大散布");
        }
        static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
