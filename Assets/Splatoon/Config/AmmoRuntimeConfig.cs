using System;
using System.IO;
using UnityEngine;

namespace Splatoon.Config
{
    /// <summary>发射时冻结的弹药参数；历史弹丸不读取源资产中的可变数值。</summary>
    public sealed class AmmoRuntimeConfig
    {
        public readonly AmmoConfigAsset Source;
        public readonly ushort AmmoId;
        public readonly GameObject BubblePrefab;
        public readonly AudioClip BubbleShotAudio, BubbleBounceAudio, BubblePopAudio;
        public readonly ParticleSystem FlightPrefab, MuzzlePrefab;
        public readonly GameObject ExplosionPrefab;
        public readonly float BurstInterval, VisualLifetime, MuzzleInterval, MaxRibbonGap, SatelliteSpread;
        public readonly int ParticlesPerBurst, MuzzleBurstCount;
        public readonly bool ExplosionEnabled, ExplosionPaint;
        public readonly float ExplosionRadius, ExplosionDamage, ExplosionPaintRadiusMin, ExplosionPaintRadiusMax;
        public readonly bool ExplosionConstantDamage;
        public readonly float CollisionExplosionRadiusRate;
        public readonly float CollisionExplosionDamageRate;
        public readonly bool ExcludeDirectHitFromExplosion;
        public bool HasExplosion => ExplosionEnabled;
        public bool HasExplosionVisual => ExplosionEnabled && ExplosionPrefab != null;

        public AmmoRuntimeConfig(AmmoConfigAsset source)
        {
            if (source == null) return;
            Source = source; AmmoId = source.ammoId;
            BubblePrefab = source.bubblePrefab; BubbleShotAudio = source.bubbleShotAudio; BubbleBounceAudio = source.bubbleBounceAudio; BubblePopAudio = source.bubblePopAudio;
            FlightPrefab = source.flightPrefab; MuzzlePrefab = source.muzzlePrefab; ExplosionPrefab = source.explosionPrefab;
            BurstInterval = source.burstInterval; ParticlesPerBurst = source.particlesPerBurst; VisualLifetime = source.visualLifetime;
            MuzzleInterval = source.muzzleInterval; MuzzleBurstCount = source.muzzleBurstCount;
            MaxRibbonGap = source.maxRibbonGap; SatelliteSpread = source.satelliteSpread;
            ExplosionConstantDamage = source.explosionConstantDamage;
            CollisionExplosionRadiusRate = source.collisionExplosionRadiusRate;
            CollisionExplosionDamageRate = source.collisionExplosionDamageRate;
            ExcludeDirectHitFromExplosion = source.excludeDirectHitFromExplosion;
            ExplosionEnabled = source.explosionEnabled; ExplosionRadius = source.explosionRadius; ExplosionDamage = source.explosionDamage;
            ExplosionPaint = source.explosionPaint; ExplosionPaintRadiusMin = source.explosionPaintRadiusMin; ExplosionPaintRadiusMax = source.explosionPaintRadiusMax;
        }
        public bool SameValues(AmmoRuntimeConfig other) => other != null &&
            ReferenceEquals(Source, other.Source) && AmmoId == other.AmmoId &&
            BubblePrefab == other.BubblePrefab && BubbleShotAudio == other.BubbleShotAudio && BubbleBounceAudio == other.BubbleBounceAudio && BubblePopAudio == other.BubblePopAudio && FlightPrefab == other.FlightPrefab && MuzzlePrefab == other.MuzzlePrefab && ExplosionPrefab == other.ExplosionPrefab &&
            BurstInterval == other.BurstInterval && ParticlesPerBurst == other.ParticlesPerBurst && VisualLifetime == other.VisualLifetime &&
            MuzzleInterval == other.MuzzleInterval && MuzzleBurstCount == other.MuzzleBurstCount && MaxRibbonGap == other.MaxRibbonGap && SatelliteSpread == other.SatelliteSpread &&
            ExplosionConstantDamage == other.ExplosionConstantDamage &&
            CollisionExplosionRadiusRate == other.CollisionExplosionRadiusRate &&
            CollisionExplosionDamageRate == other.CollisionExplosionDamageRate &&
            ExcludeDirectHitFromExplosion == other.ExcludeDirectHitFromExplosion &&
            ExplosionEnabled == other.ExplosionEnabled && ExplosionRadius == other.ExplosionRadius && ExplosionDamage == other.ExplosionDamage &&
            ExplosionPaint == other.ExplosionPaint && ExplosionPaintRadiusMin == other.ExplosionPaintRadiusMin && ExplosionPaintRadiusMax == other.ExplosionPaintRadiusMax;

        public void Validate()
        {
            Require(Source != null && AmmoId > 0, "必须绑定有效弹药配置及非零弹药编号");
            Require(FlightPrefab != null && MuzzlePrefab != null, "必须绑定飞行墨弹和枪口喷溅预制体");
            Require(Finite(BurstInterval) && BurstInterval >= .001f, "飞行发射间隔必须为至少 0.001 秒的有限数值");
            Require(ParticlesPerBurst >= 1 && ParticlesPerBurst <= 16, "每次发射粒子数必须为 1 至 16");
            Require(Finite(VisualLifetime) && VisualLifetime >= .01f, "飞行显示寿命必须为至少 0.01 秒的有限数值");
            Require(Finite(MuzzleInterval) && MuzzleInterval >= .01f, "枪口喷溅间隔必须为至少 0.01 秒的有限数值");
            Require(MuzzleBurstCount >= 1 && MuzzleBurstCount <= 160, "每次枪口喷溅粒子数必须为 1 至 160");
            Require(Finite(MaxRibbonGap) && MaxRibbonGap >= .01f, "拖尾最大连接间距必须为至少 0.01 米的有限数值");
            Require(Finite(SatelliteSpread) && SatelliteSpread >= 0 && SatelliteSpread <= .2f, "附加墨团扰动必须为 0 至 0.2 米的有限数值");
            Require(Finite(CollisionExplosionRadiusRate) && CollisionExplosionRadiusRate > 0 && CollisionExplosionRadiusRate <= 1 && Finite(CollisionExplosionDamageRate) && CollisionExplosionDamageRate >= 0 && CollisionExplosionDamageRate <= 1, "碰撞早爆范围倍率须大于0且不超过1，伤害倍率须为0到1");
            Require(Finite(ExplosionRadius) && ExplosionRadius >= 0 && Finite(ExplosionDamage) && ExplosionDamage >= 0, "爆炸范围和伤害必须为有限非负数");
            Require(Finite(ExplosionPaintRadiusMin) && Finite(ExplosionPaintRadiusMax) && ExplosionPaintRadiusMin >= 0 && ExplosionPaintRadiusMax >= ExplosionPaintRadiusMin, "爆炸涂墨半径必须为有限数值且满足 0 <= 最小值 <= 最大值");
        }
        static bool Finite(float value) => float.IsFinite(value);
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public void Write(BinaryWriter writer)
        {
            writer.Write(AmmoId);
            writer.Write(BubblePrefab != null ? BubblePrefab.name : "");
            writer.Write(BubbleShotAudio != null ? BubbleShotAudio.name : ""); writer.Write(BubbleBounceAudio != null ? BubbleBounceAudio.name : ""); writer.Write(BubblePopAudio != null ? BubblePopAudio.name : "");
            writer.Write(FlightPrefab != null ? FlightPrefab.name : ""); writer.Write(MuzzlePrefab != null ? MuzzlePrefab.name : "");
            writer.Write(ExplosionPrefab != null ? ExplosionPrefab.name : "");
            writer.Write(BurstInterval); writer.Write(ParticlesPerBurst); writer.Write(VisualLifetime);
            writer.Write(MuzzleInterval); writer.Write(MuzzleBurstCount); writer.Write(MaxRibbonGap); writer.Write(SatelliteSpread);
            writer.Write(ExplosionConstantDamage);
            writer.Write(CollisionExplosionRadiusRate);
            writer.Write(CollisionExplosionDamageRate);
            writer.Write(ExcludeDirectHitFromExplosion);
            writer.Write(ExplosionEnabled); writer.Write(ExplosionRadius); writer.Write(ExplosionDamage);
            writer.Write(ExplosionPaint); writer.Write(ExplosionPaintRadiusMin); writer.Write(ExplosionPaintRadiusMax);
        }
    }
}
