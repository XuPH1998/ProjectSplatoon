using System;
using System.IO;
using UnityEngine;

namespace Splatoon.Config
{
    public enum SubWeaponKind : byte { Torpedo, SpeedPad, JumpPad, CurlingBomb, InkCurtain, InkMine, Sprinkler }
    public enum SubWeaponDeploymentMode : byte { Immediate, HoldPreviewRelease }
    [Flags] public enum SubWeaponSurfaceMask : byte { None = 0, Floor = 1, Wall = 2, Ceiling = 4, All = Floor | Wall | Ceiling }

    [CreateAssetMenu(menuName = "喷墨对战/副武器配置", fileName = "SubWeaponConfig")]
    public sealed class SubWeaponConfigAsset : ScriptableObject
    {
        [Header("标识与投放")]
        public string stableId;
        public string displayName;
        public SubWeaponKind kind;
        public SubWeaponDeploymentMode deploymentMode;
        public SubWeaponSurfaceMask attachSurfaces = SubWeaponSurfaceMask.Floor;
        [Min(0)] public float inkCost = 30;
        [Min(0)] public float cooldownSeconds = 1;
        [Min(1)] public int maxActive = 1;

        [Header("实体")]
        [Min(.05f)] public float lifetimeSeconds = 5;
        [Min(0)] public float maxHealth = 30;
        [Min(.01f)] public float collisionRadius = .25f;
        [Min(.1f)] public float placementRange = 8;
        [Min(0)] public float launchSpeed = 10;
        [Min(0)] public float gravity = 18;
        [Min(0)] public float maxTravelDistance = 20;
        [Range(0, 1)] public float bounceRetention = .7f;

        [Header("触发与战斗")]
        [Min(0)] public float scanRadius;
        [Min(0)] public float triggerRadius;
        [Min(0)] public float armingSeconds;
        [Min(0)] public float damage;
        [Min(0)] public float explosionRadius;
        [Min(.01f)] public float effectInterval = .35f;

        [Header("移动效果")]
        [Min(0)] public float boostSpeed;
        [Min(0)] public float boostSeconds;
        [Min(0)] public float jumpUpSpeed;
        [Min(0)] public float jumpForwardSpeed;
        [Min(0)] public float perPlayerTriggerCooldown = .35f;

        [Header("范围与涂墨")]
        [Min(.1f)] public float width = 1;
        [Min(.1f)] public float height = 1;
        [Min(0)] public float paintRadius = 1.5f;
        [Range(0, 1)] public float paintHardness = .55f;
        [Range(0, 1)] public float paintStrength = 1;

        public SubWeaponRuntimeConfig Snapshot() => new(this);
    }

    public sealed class SubWeaponRuntimeConfig
    {
        public string StableId { get; }
        public string DisplayName { get; }
        public SubWeaponKind Kind { get; }
        public SubWeaponDeploymentMode DeploymentMode { get; }
        public SubWeaponSurfaceMask AttachSurfaces { get; }
        public float InkCost { get; }
        public float CooldownSeconds { get; }
        public int MaxActive { get; }
        public float LifetimeSeconds { get; }
        public float MaxHealth { get; }
        public float CollisionRadius { get; }
        public float PlacementRange { get; }
        public float LaunchSpeed { get; }
        public float Gravity { get; }
        public float MaxTravelDistance { get; }
        public float BounceRetention { get; }
        public float ScanRadius { get; }
        public float TriggerRadius { get; }
        public float ArmingSeconds { get; }
        public float Damage { get; }
        public float ExplosionRadius { get; }
        public float EffectInterval { get; }
        public float BoostSpeed { get; }
        public float BoostSeconds { get; }
        public float JumpUpSpeed { get; }
        public float JumpForwardSpeed { get; }
        public float PerPlayerTriggerCooldown { get; }
        public float Width { get; }
        public float Height { get; }
        public float PaintRadius { get; }
        public float PaintHardness { get; }
        public float PaintStrength { get; }

        public SubWeaponRuntimeConfig(SubWeaponConfigAsset source)
        {
            StableId = source.stableId ?? ""; DisplayName = source.displayName ?? "";
            Kind = source.kind; DeploymentMode = source.deploymentMode; AttachSurfaces = source.attachSurfaces;
            InkCost = source.inkCost; CooldownSeconds = source.cooldownSeconds; MaxActive = source.maxActive;
            LifetimeSeconds = source.lifetimeSeconds; MaxHealth = source.maxHealth; CollisionRadius = source.collisionRadius;
            PlacementRange = source.placementRange; LaunchSpeed = source.launchSpeed; Gravity = source.gravity;
            MaxTravelDistance = source.maxTravelDistance; BounceRetention = source.bounceRetention;
            ScanRadius = source.scanRadius; TriggerRadius = source.triggerRadius; ArmingSeconds = source.armingSeconds;
            Damage = source.damage; ExplosionRadius = source.explosionRadius; EffectInterval = source.effectInterval;
            BoostSpeed = source.boostSpeed; BoostSeconds = source.boostSeconds; JumpUpSpeed = source.jumpUpSpeed;
            JumpForwardSpeed = source.jumpForwardSpeed; PerPlayerTriggerCooldown = source.perPlayerTriggerCooldown;
            Width = source.width; Height = source.height; PaintRadius = source.paintRadius;
            PaintHardness = source.paintHardness; PaintStrength = source.paintStrength;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(StableId); w.Write(DisplayName); w.Write((byte)Kind); w.Write((byte)DeploymentMode); w.Write((byte)AttachSurfaces);
            w.Write(InkCost); w.Write(CooldownSeconds); w.Write(MaxActive); w.Write(LifetimeSeconds); w.Write(MaxHealth);
            w.Write(CollisionRadius); w.Write(PlacementRange); w.Write(LaunchSpeed); w.Write(Gravity); w.Write(MaxTravelDistance);
            w.Write(BounceRetention); w.Write(ScanRadius); w.Write(TriggerRadius); w.Write(ArmingSeconds); w.Write(Damage);
            w.Write(ExplosionRadius); w.Write(EffectInterval); w.Write(BoostSpeed); w.Write(BoostSeconds); w.Write(JumpUpSpeed);
            w.Write(JumpForwardSpeed); w.Write(PerPlayerTriggerCooldown); w.Write(Width); w.Write(Height); w.Write(PaintRadius);
            w.Write(PaintHardness); w.Write(PaintStrength);
        }
    }

    public static class SubWeaponConfigValidation
    {
        public static void Validate(SubWeaponRuntimeConfig c)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.StableId) || string.IsNullOrWhiteSpace(c.DisplayName)) throw new InvalidOperationException("副武器缺少稳定标识或显示名");
            if (!Finite(c.InkCost, c.CooldownSeconds, c.LifetimeSeconds, c.MaxHealth, c.CollisionRadius, c.PlacementRange,
                c.LaunchSpeed, c.Gravity, c.MaxTravelDistance, c.BounceRetention, c.ScanRadius, c.TriggerRadius, c.ArmingSeconds,
                c.Damage, c.ExplosionRadius, c.EffectInterval, c.BoostSpeed, c.BoostSeconds, c.JumpUpSpeed, c.JumpForwardSpeed,
                c.PerPlayerTriggerCooldown, c.Width, c.Height, c.PaintRadius, c.PaintHardness, c.PaintStrength))
                throw new InvalidOperationException($"副武器 {c.DisplayName} 含无效数值");
            if (c.MaxActive < 1 || c.LifetimeSeconds <= 0 || c.CollisionRadius <= 0 || c.PlacementRange <= 0 || c.Width <= 0 || c.Height <= 0 || c.EffectInterval <= 0)
                throw new InvalidOperationException($"副武器 {c.DisplayName} 的实体参数无效");
            if (c.InkCost < 0 || c.CooldownSeconds < 0 || c.BounceRetention > 1 || c.PaintHardness > 1 || c.PaintStrength > 1)
                throw new InvalidOperationException($"副武器 {c.DisplayName} 的消耗或比例参数无效");
        }

        static bool Finite(params float[] values)
        { foreach (float value in values) if (!float.IsFinite(value) || value < 0) return false; return true; }
    }
}
