using UnityEngine;

namespace Splatoon.Config
{
    [CreateAssetMenu(menuName = "喷墨对战/武器配置", fileName = "WeaponConfig")]
    public sealed class WeaponConfigAsset : ScriptableObject
    {
        [Header("泡泡弹道")]
        [InspectorName("弹道运动模式"), Tooltip("弹道运动模式") ] public ProjectileMotionMode motionMode;
        [InspectorName("泡泡组周期（秒，首颗到首颗）"), Tooltip("泡泡组周期（秒，首颗到首颗）") ] public double bubbleVolleySeconds = .55;
        [InspectorName("泡泡组内间隔（秒）"), Tooltip("泡泡组内间隔（秒）") ] public double bubbleIntervalSeconds = .05;
        [InspectorName("最多地面弹跳次数"), Tooltip("最多地面弹跳次数") ] public int bubbleGroundBounces = 3;
        [InspectorName("最多总反射次数"), Tooltip("最多总反射次数") ] public int bubbleMaxBounces = 6;
        [InspectorName("地面法向速度保留比例"), Tooltip("地面法向速度保留比例") ] public float bubbleNormalRetention = .72f;
        [InspectorName("地面切向速度保留比例"), Tooltip("地面切向速度保留比例") ] public float bubbleTangentRetention = .9f;
        [InspectorName("墙面反射速度保留比例"), Tooltip("墙面反射速度保留比例") ] public float bubbleWallRetention = .9f;
        [Header("模型")]
        [InspectorName("正式武器地址"), Tooltip("模型：正式武器地址")] public string weaponPrefabAddress = "";
        [Header("弹药")]
        [InspectorName("弹药配置"), Tooltip("该武器独立的弹药、飞行墨水和爆炸配置")] public AmmoConfigAsset ammoConfig;
        [Header("发射")]
        [InspectorName("发射模式"), Tooltip("发射模式")] public WeaponFireMode fireMode;
        [InspectorName("权威射速（发/秒）"), Tooltip("发射：权威射速（发/秒）")] public float fireRate;
        [InspectorName("每次有效发射总耗墨（霰弹整组）"), Tooltip("发射：每次有效发射总耗墨（霰弹整组）")] public float shotInk;
        [InspectorName("人形起手时长（秒）"), Tooltip("人形起手时长（秒）")] public double startSeconds;
        [InspectorName("出墨起手时长（秒）"), Tooltip("出墨起手时长（秒）")] public double emergeStartSeconds;
        [InspectorName("末发后回墨锁定时长（秒）"), Tooltip("末发后回墨锁定时长（秒）")] public double inkRecoverLockSeconds;
        [InspectorName("射击或蓄力移动速度（米/秒）"), Tooltip("发射：射击或蓄力移动速度（米/秒）")] public float shootMoveSpeed;
        [InspectorName("每组发数（全自动为1）"), Tooltip("发射：每组发数（全自动为1）")] public int burstCount;
        [InspectorName("组间恢复时长（秒）"), Tooltip("组间恢复时长（秒）")] public double burstRecoverySeconds;
        [Header("弹道")]
        [InspectorName("最低初速（米/秒）"), Tooltip("弹道：最低初速（米/秒）")] public float speedMin;
        [InspectorName("最高初速（米/秒）"), Tooltip("弹道：最高初速（米/秒）")] public float speedMax;
        [InspectorName("墨弹重力（米/秒²，0为无重力）"), Tooltip("弹道：墨弹重力（米/秒²，0为无重力）")] public float projectileGravity;
        [InspectorName("有效寿命（秒）"), Tooltip("弹道：有效寿命（秒）")] public float lifetime;
        [InspectorName("扫掠半径（米）"), Tooltip("弹道：扫掠半径（米）")] public float collisionRadius;
        [InspectorName("地面最大散布半角（度）"), Tooltip("弹道：散布半角（度）")] public float spreadDegrees;
        [InspectorName("直行时长（秒）"), Tooltip("直行时长（秒）")] public double straightSeconds;
        [InspectorName("减速过渡时长（秒）"), Tooltip("减速过渡时长（秒）")] public double brakeSeconds;
        [InspectorName("减速后的速度比例"), Tooltip("弹道：减速后的速度比例")] public float brakeSpeedMultiplier;
        [InspectorName("空中最大散布半角（度）"), Tooltip("弹道：跳跃散布半角（度）")] public float jumpSpreadDegrees;
        [InspectorName("落地散布恢复时长（秒，火箭筒）"), Tooltip("落地散布恢复时长（秒，火箭筒）")] public double landingSpreadRecoverSeconds;
        [InspectorName("伤害弹最大前向射程（本项目米）"), Tooltip("弹道：伤害弹最大前向射程（本项目米）")] public float effectiveRange;
        [Header("伤害")]
        [InspectorName("每颗伤害"), Tooltip("伤害：每颗伤害")] public float damage;
        [InspectorName("衰减后伤害（生命值）"), Tooltip("伤害：衰减后伤害（生命值）")] public float damageMin;
        [InspectorName("伤害衰减开始时间（秒）"), Tooltip("伤害衰减开始时间（秒）")] public double damageReduceStartSeconds;
        [InspectorName("伤害衰减结束时间（秒）"), Tooltip("伤害衰减结束时间（秒）")] public double damageReduceEndSeconds;
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
        [InspectorName("满蓄时长（秒，非蓄力为0）"), Tooltip("满蓄时长（秒，非蓄力为0）")] public double chargeSeconds;
        [InspectorName("点射伤害（生命值）"), Tooltip("蓄力：点射伤害（生命值）")] public float chargeMinDamage;
        [InspectorName("未满蓄伤害上限（生命值）"), Tooltip("蓄力：未满蓄伤害上限（生命值）")] public float chargePartialMaxDamage;
        [InspectorName("点射耗墨（点）"), Tooltip("蓄力：点射耗墨（点）")] public float chargeMinInk;
        [InspectorName("点射伤害射程（米）"), Tooltip("蓄力：点射伤害射程（米）")] public float chargeMinRange;
        [InspectorName("点射初速（米/秒）"), Tooltip("蓄力：点射初速（米/秒）")] public float chargeMinSpeed;
        [InspectorName("点射地面散布半角（度）"), Tooltip("蓄力：点射地面散布半角（度）")] public float chargeMinSpread;
        [InspectorName("点射空中散布半角（度）"), Tooltip("蓄力：点射空中散布半角（度）")] public float chargeMinJumpSpread;
        [Header("发射")]
        [InspectorName("每次有效发射的弹丸数量"), Tooltip("发射：每次有效发射的弹丸数量")] public int pelletCount;
        [InspectorName("枪口模式"), Tooltip("枪口模式")] public WeaponMuzzleMode muzzleMode;
        [InspectorName("半自动点击缓存时长（秒）"), Tooltip("半自动点击缓存时长（秒）")] public double semiBufferSeconds;
        [Header("旋转枪")]
        [InspectorName("最短有效蓄力时长（秒）"), Tooltip("最短有效蓄力时长（秒）")] public double splatlingMinChargeSeconds;
        [InspectorName("第一圈蓄力时长（秒）"), Tooltip("第一圈蓄力时长（秒）")] public double splatlingFirstChargeSeconds;
        [InspectorName("第一圈射击窗口（秒）"), Tooltip("第一圈射击窗口（秒）；释放时立即发出首弹，窗口内按射速计算后续发数。")] public double splatlingFirstShootSeconds;
        [InspectorName("满蓄射击窗口（秒）"), Tooltip("满蓄射击窗口（秒）；包括窗口结束时刻的末弹。")] public double splatlingFullShootSeconds;
        [InspectorName("空中或缺墨蓄力耗时倍率（不叠乘）"), Tooltip("旋转枪：空中或缺墨蓄力耗时倍率（不叠乘）")] public float splatlingSlowChargeMultiplier;
        [InspectorName("蓄力移动速度（米/秒）"), Tooltip("旋转枪：蓄力移动速度（米/秒）")] public float splatlingChargeMoveSpeed;
        [InspectorName("蓄力起跳速度（米/秒）"), Tooltip("旋转枪：蓄力起跳速度（米/秒）")] public float splatlingChargeJumpSpeed;
        [InspectorName("末弹后恢复时长（秒）"), Tooltip("末弹后恢复时长（秒）")] public double splatlingPostSeconds;
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
        [Header("爆破枪")]
        [InspectorName("爆破枪发射间隔（秒）"), Tooltip("爆破枪发射间隔（秒）；爆破枪弹道参考步长为 1/60 秒。") ] public double blasterRepeatSeconds;
        [InspectorName("爆破枪射后动作限制（秒）"), Tooltip("爆破枪射后动作限制（秒）；爆破枪弹道参考步长为 1/60 秒。") ] public double blasterPostSeconds;
        [InspectorName("爆破枪玩家扫掠半径（米）"), Tooltip("爆破枪玩家扫掠半径（米）；爆破枪弹道参考步长为 1/60 秒。") ] public float blasterPlayerRadius;
        [InspectorName("爆破枪直进末端限速（米/秒）"), Tooltip("爆破枪直进末端限速（米/秒）；爆破枪弹道参考步长为 1/60 秒。") ] public float blasterBrakeEndSpeed;
        [InspectorName("爆破枪参考步长内制动阻力（0 到 1）"), Tooltip("爆破枪参考步长内制动阻力（0 到 1）；爆破枪弹道参考步长为 1/60 秒。") ] public float blasterBrakeDrag;
        [InspectorName("爆破枪制动重力（米/秒平方）"), Tooltip("爆破枪制动重力（米/秒平方）；爆破枪弹道参考步长为 1/60 秒。") ] public float blasterBrakeGravity;
        [InspectorName("爆破枪每颗沿途最大落墨数"), Tooltip("爆破枪每颗沿途最大落墨数；爆破枪弹道参考步长为 1/60 秒。") ] public int blasterTrailCount;
        [Header("普通双枪（原作参数的项目近似）")]
        [InspectorName("双枪直进末端限速（米/秒）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesBrakeEndSpeed = 0f;
        [InspectorName("双枪参考步长内制动阻力"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesBrakeDrag = 0f;
        [InspectorName("双枪制动重力（米/秒平方）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesBrakeGravity = 0f;
        [InspectorName("双枪玩家扫掠半径（米）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesPlayerRadius = 0f;
        [InspectorName("双枪初始散布偏置"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesSpreadMinBias = 0.03f;
        [InspectorName("双枪连续射击最大偏置（近似）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesSpreadMaxBias = 0.25f;
        [InspectorName("双枪每发偏置增加量"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesSpreadPerShot = 0.03f;
        [InspectorName("双枪每秒偏置恢复量"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesSpreadRecoverPerSecond = 0.3f;
        [InspectorName("双枪跳跃初始偏置"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesJumpBias = 0.4f;
        [InspectorName("双枪空中偏置恢复开始（秒）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public double dualiesJumpRecoverStartSeconds = 0.4166666666666667;
        [InspectorName("双枪空中偏置恢复结束（秒）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public double dualiesJumpRecoverEndSeconds = 1.1666666666666667;
        [InspectorName("双枪首次沿途落墨距离（米）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesTrailStartDistance = 0f;
        [InspectorName("双枪每颗沿途最大落墨数"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public int dualiesTrailCount = 2;
        [InspectorName("双枪脚下落墨间隔（发，首发触发）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public int dualiesFootEvery = 5;
        [InspectorName("双枪脚下落墨半径（米）"), Tooltip("仅用于普通双枪；参考步长为 1/60 秒，距离已换算为项目米制。") ] public float dualiesFootRadius = 0f;
        public WeaponRuntimeConfig Snapshot() => new(this);
    }
}
