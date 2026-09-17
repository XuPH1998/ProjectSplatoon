#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Splatoon.Combat;
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
            var ids = new HashSet<ushort>(); var assets = new HashSet<object>(); var flights = new HashSet<object>(); var muzzles = new HashSet<object>();
            foreach (var name in Weapons)
            {
                var ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>($"Assets/GameResource/Weapons/{name}/{name}AmmoConfig.asset");
                Assert.That(ammo, Is.Not.Null, name + " 缺少 AmmoConfig");
                Assert.That(ammo.flightPrefab, Is.Not.Null); Assert.That(ammo.muzzlePrefab, Is.Not.Null);
                Assert.That(ids.Add(ammo.ammoId), Is.True, "ammoId 重复");
                Assert.That(assets.Add(ammo), Is.True, "AmmoConfig 被共享");
                Assert.That(ammo.flightMigrationVersion, Is.EqualTo(1));
                new AmmoRuntimeConfig(ammo).Validate();
                Assert.That(flights.Add(ammo.flightPrefab), Is.True, "flightPrefab 被共享");
                Assert.That(muzzles.Add(ammo.muzzlePrefab), Is.True, "muzzlePrefab 被共享");
                var weapon = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>($"Assets/GameResource/Weapons/{name}/{name}WeaponConfig.asset");
                Assert.That(weapon, Is.Not.Null); Assert.That(weapon.ammoConfig, Is.SameAs(ammo));
            }
        }

        [Test]
        public void ChangesToEveryAmmoScalarAreFrozenAndDetectedWithoutRestartingWeaponActions()
        {
            var source = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset");
            var weapon = UnityEngine.Object.Instantiate(source);
            var ammo = UnityEngine.Object.Instantiate(source.ammoConfig); weapon.ammoConfig = ammo;
            try
            {
                foreach (var field in typeof(AmmoConfigAsset).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.Name == "flightMigrationVersion" || typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;
                    var prior = weapon.Snapshot(); var value = field.GetValue(ammo);
                    if (field.FieldType == typeof(float)) field.SetValue(ammo, (float)value + .001f);
                    else if (field.FieldType == typeof(int)) field.SetValue(ammo, (int)value + 1);
                    else if (field.FieldType == typeof(ushort)) field.SetValue(ammo, (ushort)((ushort)value + 1));
                    else if (field.FieldType == typeof(bool)) field.SetValue(ammo, !(bool)value);
                    var next = weapon.Snapshot();
                    Assert.That(prior.SameValues(next), Is.False, field.Name);
                    Assert.That(prior.RequiresRestart(next), Is.False, field.Name + " must not cancel charge/burst");
                    field.SetValue(ammo, value);
                    Assert.That(prior.SameValues(weapon.Snapshot()), Is.True, field.Name + " frozen old value");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(weapon); UnityEngine.Object.DestroyImmediate(ammo); }
        }

        [Test]
        public void SameNamedPrefabAndAmmoReplacementAreDifferentVersions()
        {
            var source = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset");
            var ammo = UnityEngine.Object.Instantiate(source);
            var other = UnityEngine.Object.Instantiate(source); other.name = ammo.name;
            var first = new GameObject("Same prefab"); var second = new GameObject("Same prefab");
            try
            {
                ammo.flightPrefab = first.AddComponent<ParticleSystem>(); var before = new AmmoRuntimeConfig(ammo);
                ammo.flightPrefab = second.AddComponent<ParticleSystem>(); var after = new AmmoRuntimeConfig(ammo);
                Assert.That(before.SameValues(after), Is.False);
                Assert.That(before.FlightPrefab, Is.SameAs(first.GetComponent<ParticleSystem>()));
                other.flightPrefab = ammo.flightPrefab;
                Assert.That(after.SameValues(new AmmoRuntimeConfig(other)), Is.False, "Replacing the Ammo asset itself is observed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(ammo); UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [TestCase("burstInterval", 0f)]
        [TestCase("visualLifetime", float.NaN)]
        [TestCase("muzzleInterval", float.PositiveInfinity)]
        [TestCase("maxRibbonGap", -1f)]
        [TestCase("satelliteSpread", .21f)]
        [TestCase("explosionDamage", -1f)]
        [TestCase("explosionRadius", float.NaN)]
        [TestCase("explosionPaintRadiusMin", -1f)]
        public void InvalidAmmoValuesAreRejectedWithoutClamping(string field, float value)
        {
            var ammo = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset"));
            try { typeof(AmmoConfigAsset).GetField(field).SetValue(ammo, value); Assert.Throws<InvalidOperationException>(() => new AmmoRuntimeConfig(ammo).Validate()); }
            finally { UnityEngine.Object.DestroyImmediate(ammo); }
        }

        [Test]
        public void SerializedInspectorUndoRedoRestoresMergedParameters()
        {
            var ammo = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset"));
            try
            {
                var original = new AmmoRuntimeConfig(ammo);
                var serialized = new SerializedObject(ammo);
                serialized.FindProperty("muzzleBurstCount").intValue = 31;
                serialized.FindProperty("visualLifetime").floatValue = .73f;
                serialized.ApplyModifiedProperties(); Undo.FlushUndoRecordObjects();
                Assert.That(ammo.muzzleBurstCount, Is.EqualTo(31));
                Undo.PerformUndo(); Assert.That(original.SameValues(new AmmoRuntimeConfig(ammo)), Is.True);
                Undo.PerformRedo(); Assert.That(ammo.visualLifetime, Is.EqualTo(.73f));
            }
            finally { Undo.ClearUndo(ammo); UnityEngine.Object.DestroyImmediate(ammo); }
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

        [TestCase("particlesPerBurst", 0)]
        [TestCase("particlesPerBurst", 17)]
        [TestCase("muzzleBurstCount", -1)]
        [TestCase("muzzleBurstCount", 161)]
        public void InvalidCountsAreRejected(string field, int value)
        {
            var ammo = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset"));
            try { typeof(AmmoConfigAsset).GetField(field).SetValue(ammo, value); Assert.Throws<InvalidOperationException>(() => new AmmoRuntimeConfig(ammo).Validate()); }
            finally { UnityEngine.Object.DestroyImmediate(ammo); }
        }

        [Test]
        public void SavedAmmoReloadsAllMergedValuesAndRejectsReversedPaintRange()
        {
            string path = "Assets/AmmoMergeTest-" + Guid.NewGuid().ToString("N") + ".asset";
            var ammo = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlAmmoConfig.asset"));
            try
            {
                ammo.burstInterval = .073f; ammo.particlesPerBurst = 7; ammo.visualLifetime = .85f;
                ammo.muzzleInterval = .12f; ammo.muzzleBurstCount = 27; ammo.maxRibbonGap = 1.1f; ammo.satelliteSpread = .04f;
                AssetDatabase.CreateAsset(ammo, path); AssetDatabase.SaveAssets();
                string saved = EditorJsonUtility.ToJson(ammo);
                Resources.UnloadAsset(ammo); ammo = AssetDatabase.LoadAssetAtPath<AmmoConfigAsset>(path);
                Assert.That(EditorJsonUtility.ToJson(ammo), Is.EqualTo(saved));
                ammo.explosionPaintRadiusMin = 2; ammo.explosionPaintRadiusMax = 1;
                Assert.Throws<InvalidOperationException>(() => new AmmoRuntimeConfig(ammo).Validate());
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }
    }
}
#endif
