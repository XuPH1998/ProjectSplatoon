using UnityEngine;

namespace Splatoon.Config
{
    /// <summary>Weapon-owned projectile presentation and optional explosion rules.</summary>
    [CreateAssetMenu(menuName = "喷墨对战/弹药配置", fileName = "AmmoConfig")]
    public sealed class AmmoConfigAsset : ScriptableObject
    {
        [InspectorName("弹药 ID")] public ushort ammoId;
        [HideInInspector] public int flightMigrationVersion;
        [InspectorName("飞行墨弹预制体")] public ParticleSystem flightPrefab;
        [InspectorName("枪口喷溅预制体")] public ParticleSystem muzzlePrefab;
        [InspectorName("飞行发射间隔（秒）")] public float burstInterval = .03f;
        [InspectorName("每次发射粒子数")] public int particlesPerBurst = 2;
        [InspectorName("飞行显示寿命（秒）")] public float visualLifetime = 1;
        [InspectorName("枪口喷溅间隔（秒）")] public float muzzleInterval = .13f;
        [InspectorName("每次枪口喷溅粒子数")] public int muzzleBurstCount = 40;
        [InspectorName("拖尾最大连接间距（米）")] public float maxRibbonGap = 1.25f;
        [InspectorName("附加墨团扰动（米）")] public float satelliteSpread = .035f;
        [InspectorName("启用爆炸")] public bool explosionEnabled;
        [InspectorName("爆炸预制体")] public GameObject explosionPrefab;
        [InspectorName("爆炸范围")] public float explosionRadius;
        [InspectorName("爆炸伤害")] public float explosionDamage;
        [InspectorName("爆炸涂墨")] public bool explosionPaint;
        [InspectorName("涂墨半径最小值")] public float explosionPaintRadiusMin;
        [InspectorName("涂墨半径最大值")] public float explosionPaintRadiusMax;

    }
}
