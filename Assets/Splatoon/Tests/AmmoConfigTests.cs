#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using Splatoon.Config;

namespace Splatoon.Tests
{
    public sealed class AmmoConfigTests
    {
        static readonly string[] Weapons = { "RifleGirl", "PistolGirl", "DualPistolGirl", "MachineGunGirl", "ShotgunGirl", "RocketLauncherGirl" };

        [Test]
        public void SixWeaponsHaveIndependentAmmoAndVisualResources()
        {
            var ids = new HashSet<ushort>(); var profiles = new HashSet<object>(); var flights = new HashSet<object>(); var muzzles = new HashSet<object>();
            foreach (var name in Weapons)
            {
                var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>($"Assets/GameResource/Weapons/{name}/{name}AmmoConfig.asset");
                Assert.That(ammo, Is.Not.Null, name + " 缺少 AmmoConfig");
                Assert.That(ammo.flightProfile, Is.Not.Null); Assert.That(ammo.flightPrefab, Is.Not.Null); Assert.That(ammo.muzzlePrefab, Is.Not.Null);
                Assert.That(ids.Add(ammo.ammoId), Is.True, "ammoId 重复");
                Assert.That(profiles.Add(ammo.flightProfile), Is.True, "flightProfile 被共享");
                Assert.That(flights.Add(ammo.flightPrefab), Is.True, "flightPrefab 被共享");
                Assert.That(muzzles.Add(ammo.muzzlePrefab), Is.True, "muzzlePrefab 被共享");
                var weapon = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>($"Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset");
                Assert.That(weapon, Is.Not.Null); Assert.That(weapon.ammoConfig, Is.SameAs(ammo));
            }
        }

        [Test]
        public void SnapshotRetainsAmmoRules()
        {
            var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RocketLauncherGirl/RocketLauncherGirlWeaponConfig.asset");
            var snapshot = new WeaponRuntimeConfig(asset);
            Assert.That(snapshot.Ammo, Is.Not.Null);
            Assert.That(snapshot.Ammo.AmmoId, Is.EqualTo(asset.ammoConfig.ammoId));
            Assert.That(snapshot.SameValues(new WeaponRuntimeConfig(asset)), Is.True);
        }
    }
}
#endif
