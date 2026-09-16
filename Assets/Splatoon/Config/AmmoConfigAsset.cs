using UnityEngine;
using Splatoon.Combat;

namespace Splatoon.Config
{
    /// <summary>Weapon-owned projectile presentation and optional explosion rules.</summary>
    [CreateAssetMenu(menuName = "喷墨对战/弹药配置", fileName = "AmmoConfig")]
    public sealed class AmmoConfigAsset : ScriptableObject
    {
        [InspectorName("弹药 ID")] public ushort ammoId;
        [InspectorName("飞行表现配置")] public InkFlightProfile flightProfile;
        [InspectorName("飞行墨弹预制体")] public ParticleSystem flightPrefab;
        [InspectorName("枪口喷溅预制体")] public ParticleSystem muzzlePrefab;
        [Header("爆炸")]
        [InspectorName("启用爆炸")] public bool explosionEnabled;
        [InspectorName("爆炸预制体")] public GameObject explosionPrefab;
        [InspectorName("爆炸范围")] public float explosionRadius;
        [InspectorName("爆炸伤害")] public float explosionDamage;
        [InspectorName("爆炸涂墨")] public bool explosionPaint;
        [InspectorName("涂墨半径最小值")] public float explosionPaintRadiusMin;
        [InspectorName("涂墨半径最大值")] public float explosionPaintRadiusMax;

        public void OnValidate()
        {
            if (explosionPaintRadiusMin < 0) explosionPaintRadiusMin = 0;
            if (explosionPaintRadiusMax < explosionPaintRadiusMin) explosionPaintRadiusMax = explosionPaintRadiusMin;
        }
    }
}
