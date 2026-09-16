using System.IO;
using UnityEngine;
using Splatoon.Combat;

namespace Splatoon.Config
{
    /// <summary>Immutable ammo snapshot retained by every shot revision.</summary>
    public sealed class AmmoRuntimeConfig
    {
        public readonly ushort AmmoId;
        public readonly InkFlightProfile FlightProfile;
        public readonly ParticleSystem FlightPrefab;
        public readonly ParticleSystem MuzzlePrefab;
        public readonly GameObject ExplosionPrefab;
        public readonly bool ExplosionEnabled;
        public readonly float ExplosionRadius;
        public readonly float ExplosionDamage;
        public readonly bool ExplosionPaint;
        public readonly float ExplosionPaintRadiusMin;
        public readonly float ExplosionPaintRadiusMax;

        public bool HasExplosion => ExplosionEnabled && ExplosionPrefab != null;

        public AmmoRuntimeConfig(AmmoConfigAsset source)
        {
            if (source == null) return;
            AmmoId = source.ammoId;
            FlightProfile = source.flightProfile;
            FlightPrefab = source.flightPrefab != null ? source.flightPrefab : source.flightProfile != null ? source.flightProfile.FlightPrefab : null;
            MuzzlePrefab = source.muzzlePrefab != null ? source.muzzlePrefab : source.flightProfile != null ? source.flightProfile.MuzzlePrefab : null;
            ExplosionPrefab = source.explosionPrefab;
            ExplosionEnabled = source.explosionEnabled;
            ExplosionRadius = Mathf.Max(0, source.explosionRadius);
            ExplosionDamage = Mathf.Max(0, source.explosionDamage);
            ExplosionPaint = source.explosionPaint;
            ExplosionPaintRadiusMin = Mathf.Max(0, source.explosionPaintRadiusMin);
            ExplosionPaintRadiusMax = Mathf.Max(ExplosionPaintRadiusMin, source.explosionPaintRadiusMax);
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(AmmoId);
            writer.Write(FlightProfile != null ? FlightProfile.name : "");
            writer.Write(FlightPrefab != null ? FlightPrefab.name : "");
            writer.Write(MuzzlePrefab != null ? MuzzlePrefab.name : "");
            writer.Write(ExplosionPrefab != null ? ExplosionPrefab.name : "");
            writer.Write(ExplosionEnabled); writer.Write(ExplosionRadius); writer.Write(ExplosionDamage);
            writer.Write(ExplosionPaint); writer.Write(ExplosionPaintRadiusMin); writer.Write(ExplosionPaintRadiusMax);
        }
    }
}
