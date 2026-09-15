using UnityEngine;

namespace Splatoon.Config
{
    [CreateAssetMenu(menuName = "喷墨对战/武器配置", fileName = "WeaponConfig")]
    public sealed class WeaponConfigAsset : ScriptableObject
    {
        [Header("模型")]
        [InspectorName("正式武器地址"), Tooltip("模型：正式武器地址")] public string weaponPrefabAddress = "";
        [Header("发射")]
        [InspectorName("0全自动／1三连发／2蓄力松开发射／3半自动／4旋转枪"), Tooltip("发射：0全自动／1三连发／2蓄力松开发射／3半自动／4旋转枪")] public int fireMode;
        [InspectorName("权威射速（发/秒）"), Tooltip("发射：权威射速（发/秒）")] public float fireRate;
        [InspectorName("每次有效发射总耗墨（霰弹整组）"), Tooltip("发射：每次有效发射总耗墨（霰弹整组）")] public float shotInk;
        [InspectorName("人形起手（60Hz参考帧）"), Tooltip("发射：人形起手（60Hz参考帧）")] public int startFrames;
        [InspectorName("出墨起手（60Hz参考帧）"), Tooltip("发射：出墨起手（60Hz参考帧）")] public int emergeStartFrames;
        [InspectorName("最后一发后回墨锁定（60Hz参考帧）"), Tooltip("发射：最后一发后回墨锁定（60Hz参考帧）")] public int inkRecoverLockFrames;
        [InspectorName("射击或蓄力移动速度（米/秒）"), Tooltip("发射：射击或蓄力移动速度（米/秒）")] public float shootMoveSpeed;
        [InspectorName("每组发数（全自动为1）"), Tooltip("发射：每组发数（全自动为1）")] public int burstCount;
        [InspectorName("末发到下一组首发（60Hz参考帧）"), Tooltip("发射：末发到下一组首发（60Hz参考帧）")] public int burstRecoveryFrames;
        [Header("弹道")]
        [InspectorName("最低初速（米/秒）"), Tooltip("弹道：最低初速（米/秒）")] public float speedMin;
        [InspectorName("最高初速（米/秒）"), Tooltip("弹道：最高初速（米/秒）")] public float speedMax;
        [InspectorName("墨弹重力（米/秒²，0为无重力）"), Tooltip("弹道：墨弹重力（米/秒²，0为无重力）")] public float projectileGravity;
        [InspectorName("有效寿命（秒）"), Tooltip("弹道：有效寿命（秒）")] public float lifetime;
        [InspectorName("扫掠半径（米）"), Tooltip("弹道：扫掠半径（米）")] public float collisionRadius;
        [InspectorName("地面最大散布半角（度）"), Tooltip("弹道：散布半角（度）")] public float spreadDegrees;
        [InspectorName("直行阶段（60Hz参考帧）"), Tooltip("弹道：直行阶段（60Hz参考帧）")] public int straightFrames;
        [InspectorName("减速过渡（60Hz参考帧）"), Tooltip("弹道：减速过渡（60Hz参考帧）")] public int brakeFrames;
        [InspectorName("减速后的速度比例"), Tooltip("弹道：减速后的速度比例")] public float brakeSpeedMultiplier;
        [InspectorName("空中最大散布半角（度）"), Tooltip("弹道：跳跃散布半角（度）")] public float jumpSpreadDegrees;
        [InspectorName("落地散布恢复（仅蓄力火箭筒，60Hz参考帧）"), Tooltip("弹道：落地散布恢复（仅蓄力火箭筒，60Hz参考帧）")] public int spreadRecoverFrames;
        [InspectorName("伤害弹最大前向射程（本项目米）"), Tooltip("弹道：伤害弹最大前向射程（本项目米）")] public float effectiveRange;
        [Header("伤害")]
        [InspectorName("每颗伤害"), Tooltip("伤害：每颗伤害")] public float damage;
        [InspectorName("衰减后伤害（HP）"), Tooltip("伤害：衰减后伤害（HP）")] public float damageMin;
        [InspectorName("伤害衰减开始（飞行60Hz参考帧）"), Tooltip("伤害：伤害衰减开始（飞行60Hz参考帧）")] public int damageReduceStartFrames;
        [InspectorName("伤害衰减结束（飞行60Hz参考帧）"), Tooltip("伤害：伤害衰减结束（飞行60Hz参考帧）")] public int damageReduceEndFrames;
        [Header("涂色")]
        [InspectorName("最小涂色半径（米）"), Tooltip("涂色：最小涂色半径（米）")] public float paintRadiusMin;
        [InspectorName("最大涂色半径（米）"), Tooltip("涂色：最大涂色半径（米）")] public float paintRadiusMax;
        [InspectorName("笔刷硬度（0 到 1）"), Tooltip("涂色：笔刷硬度（0 到 1）")] public float paintHardness;
        [InspectorName("笔刷强度（0 到 1）"), Tooltip("涂色：笔刷强度（0 到 1）")] public float paintStrength;
        [InspectorName("沿途落墨间隔（米）"), Tooltip("涂色：沿途落墨间隔（米）")] public float trailSpacing;
        [InspectorName("沿途落墨最小笔刷半径（米）"), Tooltip("涂色：沿途落墨最小笔刷半径（米）")] public float trailRadiusMin;
        [InspectorName("沿途落墨最大笔刷半径（米）"), Tooltip("涂色：沿途落墨最大笔刷半径（米）")] public float trailRadiusMax;
        [InspectorName("沿途落墨向下探测范围（米）"), Tooltip("涂色：沿途落墨向下探测范围（米）")] public float trailMaxDrop;
        [Header("蓄力")]
        [InspectorName("满蓄时间（60Hz参考帧，非蓄力为0）"), Tooltip("蓄力：满蓄时间（60Hz参考帧，非蓄力为0）")] public int chargeFrames;
        [InspectorName("点射伤害（HP）"), Tooltip("蓄力：点射伤害（HP）")] public float chargeMinDamage;
        [InspectorName("未满蓄伤害上限（HP）"), Tooltip("蓄力：未满蓄伤害上限（HP）")] public float chargePartialMaxDamage;
        [InspectorName("点射耗墨（点）"), Tooltip("蓄力：点射耗墨（点）")] public float chargeMinInk;
        [InspectorName("点射伤害射程（米）"), Tooltip("蓄力：点射伤害射程（米）")] public float chargeMinRange;
        [InspectorName("点射初速（米/秒）"), Tooltip("蓄力：点射初速（米/秒）")] public float chargeMinSpeed;
        [InspectorName("点射地面散布半角（度）"), Tooltip("蓄力：点射地面散布半角（度）")] public float chargeMinSpread;
        [InspectorName("点射空中散布半角（度）"), Tooltip("蓄力：点射空中散布半角（度）")] public float chargeMinJumpSpread;
        [Header("发射")]
        [InspectorName("每次有效发射的弹丸数量"), Tooltip("发射：每次有效发射的弹丸数量")] public int pelletCount;
        [InspectorName("枪口模式（0单枪／1右左交替）"), Tooltip("发射：枪口模式（0单枪／1右左交替）")] public int muzzleMode;
        [InspectorName("半自动冷却末尾点击缓存（60Hz帧）"), Tooltip("发射：半自动冷却末尾点击缓存（60Hz帧）")] public int semiBufferFrames;
        [Header("旋转枪")]
        [InspectorName("最小有效蓄力帧数"), Tooltip("旋转枪：最小有效蓄力帧数")] public int splatlingMinChargeFrames;
        [InspectorName("第一圈蓄力帧数"), Tooltip("旋转枪：第一圈蓄力帧数")] public int splatlingFirstChargeFrames;
        [InspectorName("第一圈射击窗口帧数（含第0帧首弹）"), Tooltip("旋转枪：第一圈射击窗口帧数（含第0帧首弹）")] public int splatlingFirstShootFrames;
        [InspectorName("满蓄射击窗口帧数（含末帧）"), Tooltip("旋转枪：满蓄射击窗口帧数（含末帧）")] public int splatlingFullShootFrames;
        [InspectorName("空中或缺墨蓄力耗时倍率（不叠乘）"), Tooltip("旋转枪：空中或缺墨蓄力耗时倍率（不叠乘）")] public float splatlingSlowChargeMultiplier;
        [InspectorName("蓄力移动速度（米/秒）"), Tooltip("旋转枪：蓄力移动速度（米/秒）")] public float splatlingChargeMoveSpeed;
        [InspectorName("蓄力起跳速度（米/秒）"), Tooltip("旋转枪：蓄力起跳速度（米/秒）")] public float splatlingChargeJumpSpeed;
        [InspectorName("末弹后恢复帧数"), Tooltip("旋转枪：末弹后恢复帧数")] public int splatlingPostFrames;
        [InspectorName("地面垂直散布半角（度）"), Tooltip("旋转枪：地面垂直散布半角（度）")] public float splatlingPitchSpread;
        [InspectorName("中心散布偏向（0到1）"), Tooltip("旋转枪：中心散布偏向（0到1）")] public float splatlingSpreadBias;
        [InspectorName("初速随机中心偏向（0到1）"), Tooltip("旋转枪：初速随机中心偏向（0到1）")] public float splatlingSpeedBias;
        [InspectorName("脚下落墨间隔（发）"), Tooltip("旋转枪：脚下落墨间隔（发）")] public int splatlingFootEvery;
        [InspectorName("每颗沿途最大落墨数"), Tooltip("旋转枪：每颗沿途最大落墨数")] public int splatlingTrailCount;
        [InspectorName("脚下落墨半径（米）"), Tooltip("旋转枪：脚下落墨半径（米）")] public float splatlingFootRadius;
        [InspectorName("命中玩家扫掠半径（米）"), Tooltip("旋转枪：命中玩家扫掠半径（米）")] public float splatlingPlayerRadius;
        [Header("散布")]
        [InspectorName("达到最大散布所需时长（秒，0为立即）"), Tooltip("散布：达到最大散布所需时长（秒，0为立即）")] public float spreadExpandSeconds = 1f;
        [InspectorName("从最大恢复至最小所需时长（秒，0为立即）"), Tooltip("散布：从最大恢复至最小所需时长（秒，0为立即）")] public float spreadRecoverSeconds = 0.5f;
        [InspectorName("地面基础散布半角（度，霰弹专用）"), Tooltip("散布：地面基础散布半角（度，霰弹专用）")] public float baseSpreadDegrees = 0f;
        [InspectorName("空中基础散布半角（度，霰弹专用）"), Tooltip("散布：空中基础散布半角（度，霰弹专用）")] public float baseJumpSpreadDegrees = 0f;
        public WeaponRuntimeConfig Snapshot() => new(this);
    }
}
