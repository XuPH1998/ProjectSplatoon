using Splatoon.Config;

namespace Splatoon.Combat
{
    /// <summary>11.3.0 versus object damage categories. See the pinned DamageRates.json reference.</summary>
    public static class SubWeaponObjectDamage
    {
        public static float Multiplier(WeaponRuntimeConfig weapon, SubWeaponType target, bool explosion = false)
        {
            if (weapon == null || target == SubWeaponType.Torpedo) return 1;
            bool wall = target == SubWeaponType.SplashWall;
            if (!wall && target != SubWeaponType.Sprinkler) return 1;
            return weapon.WeaponPrefabAddress switch
            {
                "Weapon/Shotgun" => explosion ? (wall ? 2f : 4f) : (wall ? 2.5f : 5f), // Slosher_Washtub / BombCore
                "Weapon/BubbleGun" => wall ? 2f : 2.5f, // Bloblobber / Slosher_Bathtub
                "Weapon/RocketLauncher" => wall ? 2f : 2.4f, // Blaster
                "Weapon/SplooshGun" => wall ? 1.1f : 1f, // Shooter_Short
                // Shooter, Maneuver, Shooter_Precision and Spinner use the table default (1).
                // The project's custom floating bubble shotgun also deliberately uses 1.
                _ => 1f
            };
        }
        public static float SubMultiplier(SubWeaponType source,SubWeaponType target,bool droplet=false,bool contact=false)
        {
            if(target!=SubWeaponType.Torpedo&&target!=SubWeaponType.Sprinkler&&target!=SubWeaponType.SplashWall)return 1;
            if(droplet||source==SubWeaponType.AngleShooter)return 1;
            if(source==SubWeaponType.Sprinkler)return target==SubWeaponType.Torpedo?.5f:1;
            if(contact&&source==SubWeaponType.CurlingBomb)return target==SubWeaponType.Torpedo?.5f:target==SubWeaponType.Sprinkler?100:1;
            if(source==SubWeaponType.Torpedo)return target==SubWeaponType.Torpedo?1:target==SubWeaponType.Sprinkler?3:4;
            if(target==SubWeaponType.Torpedo)return .6f;
            if(source==SubWeaponType.FizzyBomb)return target==SubWeaponType.Sprinkler?3.2f:3.6f;
            return 2;
        }
    }
}
