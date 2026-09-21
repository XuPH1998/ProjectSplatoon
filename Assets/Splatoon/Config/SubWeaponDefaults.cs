using UnityEngine;

namespace Splatoon.Config
{
    /// <summary>11.3.0 versus/no gear. Distances follow the main weapon reference scale. See Docs/SubWeapons.md for adaptations.</summary>
    public static class SubWeaponDefaults
    {
        public const float Scale = 18f / 24.037f;
        public const string Root = "Assets/GameResource/SubWeapons/";
        public static string Path(SubWeaponType type) => Root + type + "/" + type + ".asset";
        public static void Apply(SubWeaponConfigAsset a, SubWeaponType type)
        {
            float s = Scale; a.type = type;
            float[] costs = { 70, 70, 45, 65, 55, 60, 65, 60, 45, 55, 40, 60, 60 };
            int[] locks = { 60, 60, 60, 70, 85, 85, 88, 0, 75, 80, 50, 60, 85 };
            a.common = new SubCommon { id = 100 + (int)type, displayName = SubWeaponFields.TypeName(type), inkCost = costs[(int)type], inkLock = locks[(int)type] / 60d, startup = .1, recovery = .2, reuse = .3 };
            a.flight = new SubFlight { speed = 1.4f * 60 * s, lift = .24f * 60 * s, gravity = .016f * 3600 * s, drag = -60 * Mathf.Log(1-.05866f), radius = .15f, lifetime = 8, bounce = .45f };
            a.blast = new SubBlast { innerDamage = 180, outerDamage = 30, innerRadius = 3.6f * s, outerRadius = 7 * s };
            a.paint = new SubPaint { radius = 4 * s, hardness = .65f, strength = 1, trailSpacing = .6f * s, trailRadius = .75f * s };
            a.tracking = new SubTracking { radius = 10 * s, speed = 5 * s, duration = 4, triggerRadius = 1.5f * s };
            a.mark = new SubMark { duration = 5, radius = 7 * s };
            a.deployment.maxCount = 1;
            a.splat.groundFuse = 1; a.suction.fuse = 2;
            a.curling = new CurlingBombSettings { charge = 1, fuse = new Vector2(3.5f, 1.5f), speed = new Vector2(.46f, .2f) * 60 * s, innerRadius = 4.6f * s, outerRadius = 8 * s, paintRadius = 5 * s, fullInkLock = .5, contactDamage = 20, contactInterval = 40d / 60 };
            a.autobomb = new AutobombSettings { searchDelay = .2, fuse = .6 };
            a.fizzy = new FizzyBombSettings { secondCharge = 40d / 60, thirdCharge = 80d / 60, fuse = .5, interval = 15d / 60, hopSpeed = .24f * 60 * s, hopForward = .12f * 60 * s, innerRadii = new Vector2(1.95f, 2.6f) * s, outerRadii = new Vector2(4.5f, 5.45f) * s };
            a.torpedo = new TorpedoSettings { transform = .5, rollFuse = .5, maxActive = 1, droplets = 8, dropletDamage = 12, dropletRadius = 4 * s, dropletBlastRadius = 2.6f * s, dropletPaintRadius = 2 * s, dropletFuse = .5 };
            a.mine = new MineSettings { arm = .5, fuse = .25, triggerRadius = 3 * s };
            a.sensor.duration = 2;
            a.mist = new MistSettings { radius = 5 * s, duration = 5, moveRate = .6f, slowRamp = 1, drainThresholds = new Vector2(3, 5), drainRates = new Vector3(10, 20, 40) };
            a.angle = new AngleShooterSettings { speed = 6.55f * 60 * s, range = 60 * s, reflections = 8, damage = 40, trailDuration = 1.5, trailRadius = .18f };
            a.sprinkler = new SprinklerSettings { phases = new Vector2(8, 15), intervals = new Vector3(.1f, .2f, .35f), range = 7 * s, damage = 20, paintRadius = .9f * s, rotationSpeed = 360, dropletSpeed=12, dropletGravity=20, dropletLifetime=1.5 };
            a.wall = new WallSettings { expand = .5, lifetime = 7, size = new Vector3(4, 3.5f, .15f) * s, damage = 30, interval = .5 };
            switch (type)
            {
                case SubWeaponType.SuctionBomb: a.blast.innerRadius = 4.6f * s; a.blast.outerRadius = 8 * s; a.paint.radius = 5 * s; break;
                case SubWeaponType.BurstBomb:
                    a.blast = new SubBlast { directDamage = 60, innerDamage = 35, innerRadius = 2.8f * s, middleDamage = 35, middleRadius = 2.8f * s, outerDamage = 25, outerRadius = 4 * s }; a.flight.speed = 1.4f * 60 * s; break;
                case SubWeaponType.CurlingBomb: a.blast.innerRadius = 1.6f * s; a.blast.outerRadius = 5 * s; a.paint.radius = 2.133f * s; break;
                case SubWeaponType.FizzyBomb: a.blast.innerDamage = 50; a.blast.outerDamage = 35; a.blast.innerRadius = 1.6f * s; a.blast.outerRadius = 3.8f * s; a.paint.radius = 3 * s; a.flight.speed = 1.6f * 60 * s; break;
                case SubWeaponType.Autobomb: a.blast.innerRadius = 2.85f * s; a.blast.outerRadius = 6.5f * s; a.tracking.speed = .09f * 60 * s; a.tracking.duration = 2.5; a.autobomb.searchDelay = 40d / 60; a.autobomb.fuse = 1; break;
                case SubWeaponType.Torpedo: a.flight.speed = 1.65f * 60 * s; a.blast.innerDamage = a.blast.directDamage = 60; a.blast.outerDamage = 35; a.blast.innerRadius = 2.6f * s; a.blast.outerRadius = 6 * s; a.paint.radius = 3.5f * s; a.durability.health = 20; break;
                case SubWeaponType.InkMine: a.blast.innerDamage = 45; a.blast.outerDamage = 35; a.blast.innerRadius = 3.6f * s; a.blast.outerRadius = 8 * s; a.paint.radius = 5 * s; a.deployment.maxCount = 2; break;
                case SubWeaponType.PointSensor: a.flight.speed = 1.64f * 60 * s; a.mark.duration = 8; a.mark.radius = 6 * s; break;
                case SubWeaponType.AngleShooter: a.paint.radius = 2.6f * s; a.angle.range = a.angle.speed * 7f / 60; break;
                case SubWeaponType.Sprinkler: a.flight.speed = 1.12f * 60 * s; a.durability.health = 120; break;
                case SubWeaponType.SplashWall: a.flight.speed = .3f * 60 * s; a.flight.lift = .07f * 60 * s; a.flight.gravity = .009f * 3600 * s; a.flight.drag = -60 * Mathf.Log(.999f); a.durability.health = 800; break;
            }
        }
    }
}
