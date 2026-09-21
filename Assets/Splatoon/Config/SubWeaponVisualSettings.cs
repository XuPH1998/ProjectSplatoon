using System;
using UnityEngine;

namespace Splatoon.Config
{
    [Serializable]
    public struct SubVisualCommon
    {
        [InspectorName("预览材质")] public Material previewMaterial;
        [InspectorName("飞行拖尾材质")] public Material trailMaterial;
        [InspectorName("命中／生效特效")] public GameObject impactPrefab;
        [InspectorName("结束／破坏特效")] public GameObject endPrefab;
        [InspectorName("拖尾宽度（米）")] public float trailWidth;
        [InspectorName("拖尾消退（秒）")] public float trailLifetime;
        [InspectorName("预览线宽（米）")] public float previewWidth;
        [InspectorName("预览透明度")] public float previewAlpha;
        [InspectorName("瞬时特效寿命（秒）")] public float impactLifetime;
        public static SubVisualCommon Defaults => new() { trailWidth = .075f, trailLifetime = .18f, previewWidth = .035f, previewAlpha = .35f, impactLifetime = .35f };
    }

    [Serializable]
    public struct SubVisualSpecific
    {
        [InspectorName("部署模型"), SubTypes(SubWeaponType.SuctionBomb, SubWeaponType.InkMine, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public GameObject deployedPrefab;
        [InspectorName("持续效果"), SubTypes(SubWeaponType.PointSensor, SubWeaponType.ToxicMist, SubWeaponType.InkMine, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public GameObject persistentPrefab;
        [InspectorName("引信／触发警告特效"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.CurlingBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.InkMine)] public GameObject warningPrefab;
        [InspectorName("墨滴模型"), SubTypes(SubWeaponType.Torpedo, SubWeaponType.Sprinkler)] public GameObject dropletPrefab;
        [InspectorName("变形后模型"), SubTypes(SubWeaponType.Torpedo)] public GameObject trackingPrefab;
        [InspectorName("毒雾粒子密度"), SubTypes(SubWeaponType.ToxicMist)] public float mistEmission;
        [InspectorName("墨幕流动速度"), SubTypes(SubWeaponType.SplashWall)] public float wallFlow;
        [InspectorName("洒墨旋转节点名"), SubTypes(SubWeaponType.Sprinkler)] public string rotorName;
        public static SubVisualSpecific Defaults => new() { mistEmission = 40, wallFlow = 2, rotorName = "Rotor" };
    }
}
