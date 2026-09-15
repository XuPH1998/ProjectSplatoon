using UnityEngine;

namespace Splatoon.Combat
{
    [CreateAssetMenu(menuName = "喷墨对战/飞行墨水表现")]
    public sealed class InkFlightProfile : ScriptableObject
    {
        public ParticleSystem FlightPrefab, MuzzlePrefab;
        [Min(.001f)] public float BurstInterval = .03f;
        [Min(1)] public int ParticlesPerBurst = 2;
        [Min(.01f)] public float VisualLifetime = 1;
        [Min(.01f)] public float MuzzleInterval = .13f;
        [Min(1)] public int MuzzleBurstCount = 40;
        [Min(.01f)] public float MaxRibbonGap = 1.25f;
        [Range(0, .2f)] public float SatelliteSpread = .035f;
        public const int Layer = 11;
    }
}
