using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Splatoon.Config
{
    public enum SubWeaponType : byte
    {
        [InspectorName("斯普拉炸弹")] SplatBomb,
        [InspectorName("吸盘炸弹")] SuctionBomb,
        [InspectorName("快速炸弹")] BurstBomb,
        [InspectorName("冰壶炸弹")] CurlingBomb,
        [InspectorName("机器人炸弹")] Autobomb,
        [InspectorName("碳酸炸弹")] FizzyBomb,
        [InspectorName("鱼雷")] Torpedo,
        [InspectorName("墨汁陷阱")] InkMine,
        [InspectorName("定点侦测器")] PointSensor,
        [InspectorName("毒雾")] ToxicMist,
        [InspectorName("标线器")] AngleShooter,
        [InspectorName("洒墨器")] Sprinkler,
        [InspectorName("斯普拉防护墙")] SplashWall
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SubTypesAttribute : Attribute
    {
        public readonly SubWeaponType[] Types;
        public SubTypesAttribute(params SubWeaponType[] types) => Types = types;
    }

    [Serializable] public struct SubCommon
    {
        [InspectorName("配置编号")] public int id;
        [InspectorName("中文名称")] public string displayName;
        [InspectorName("图标")] public Sprite icon;
        [InspectorName("手持模型")] public GameObject heldPrefab;
        [InspectorName("投出／部署模型")] public GameObject entityPrefab;
        [InspectorName("使用音效")] public AudioClip useAudio;
        [InspectorName("爆炸／生效音效")] public AudioClip effectAudio;
        [InspectorName("轨迹与效果材质")] public Material effectMaterial;
        [InspectorName("耗墨（点）"), Tooltip("英雄最大墨量默认为100；成功投出或放置时扣除。没有次数充能。")]
        public float inkCost;
        [InspectorName("释放前摇（秒）"), Tooltip("松开E到实际投出。当前默认0.1秒为项目调校值。")]
        public double startup;
        [InspectorName("动作恢复（秒）"), Tooltip("投出后限制主武器与潜墨的时长；取消操作不能绕过。")]
        public double recovery;
        [InspectorName("再次使用间隔（秒）"), Tooltip("从投出起计算，独立于动作恢复与回墨锁定。")]
        public double reuse;
        [InspectorName("回墨锁定（秒）"), Tooltip("仅停止墨水恢复，剩余墨水仍可使用。")]
        public double inkLock;
    }
    [Serializable] public struct SubFlight
    {
        [InspectorName("初速度（米／秒）"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.PointSensor, SubWeaponType.ToxicMist, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public float speed;
        [InspectorName("向上附加速度（米／秒）"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.PointSensor, SubWeaponType.ToxicMist, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public float lift;
        [InspectorName("重力（米／秒²）")] public float gravity;
        [InspectorName("空气阻力（每秒衰减率）"), Tooltip("速度按 exp(-阻力×时间) 衰减；原始每帧阻力换算到秒。")]
        public float drag;
        [InspectorName("碰撞半径（米）")] public float radius;
        [InspectorName("空中最大寿命（秒）")] public double lifetime;
        [InspectorName("反弹保速率"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.FizzyBomb, SubWeaponType.Autobomb, SubWeaponType.Torpedo)] public float bounce;
    }
    [Serializable] public struct SubBlast
    {
        [InspectorName("近距离伤害")] public float innerDamage;
        [InspectorName("近距离半径（米）")] public float innerRadius;
        [InspectorName("远距离伤害")] public float outerDamage;
        [InspectorName("远距离半径（米）")] public float outerRadius;
        [InspectorName("直击总伤害"), SubTypes(SubWeaponType.BurstBomb, SubWeaponType.Torpedo)] public float directDamage;
        [InspectorName("中圈伤害"), SubTypes(SubWeaponType.BurstBomb)] public float middleDamage;
        [InspectorName("中圈半径（米）"), SubTypes(SubWeaponType.BurstBomb)] public float middleRadius;
    }
    [Serializable] public struct SubPaint
    {
        [InspectorName("生效涂墨半径（米）")] public float radius;
        [InspectorName("墨迹硬度")] public float hardness;
        [InspectorName("墨迹强度")] public float strength;
        [InspectorName("沿途涂墨间距（米）"), SubTypes(SubWeaponType.CurlingBomb, SubWeaponType.FizzyBomb)] public float trailSpacing;
        [InspectorName("沿途涂墨半径（米）"), SubTypes(SubWeaponType.CurlingBomb, SubWeaponType.FizzyBomb)] public float trailRadius;
    }
    [Serializable] public struct SubTracking
    {
        [InspectorName("索敌半径（米）")] public float radius;
        [InspectorName("追踪速度（米／秒）")] public float speed;
        [InspectorName("最长追踪（秒）")] public double duration;
        [InspectorName("接近触发距离（米）"), SubTypes(SubWeaponType.Autobomb)] public float triggerRadius;
    }
    [Serializable] public struct SubMark
    {
        [InspectorName("标记时间（秒）")] public double duration;
        [InspectorName("侦测半径（米）"), SubTypes(SubWeaponType.PointSensor, SubWeaponType.InkMine)] public float radius;
    }
    [Serializable] public struct SubDurability { [InspectorName("耐久")] public float health; }
    [Serializable] public struct SubDeployment { [InspectorName("每名玩家部署上限")] public int maxCount; }
    [Serializable] public struct SplatBombSettings { [InspectorName("累计触地引信（秒）")] public double groundFuse; }
    [Serializable] public struct SuctionBombSettings { [InspectorName("吸附后引信（秒）")] public double fuse; }
    [Serializable] public struct CurlingBombSettings
    {
        [InspectorName("最大蓄力（秒）")] public double charge;
        [InspectorName("未蓄力／满蓄力引信（秒）")] public Vector2 fuse;
        [InspectorName("未蓄力／满蓄力速度（米／秒）")] public Vector2 speed;
        [InspectorName("满蓄力近圈半径（米）")] public float innerRadius;
        [InspectorName("满蓄力远圈半径（米）")] public float outerRadius;
        [InspectorName("满蓄力涂墨半径（米）")] public float paintRadius;
        [InspectorName("满蓄力回墨锁定（秒）")] public double fullInkLock;
        [InspectorName("接触伤害")] public float contactDamage;
        [InspectorName("同目标接触间隔（秒）")] public double contactInterval;
    }
    [Serializable] public struct AutobombSettings
    {
        [InspectorName("开始搜索延迟（秒）")] public double searchDelay;
        [InspectorName("停止后引信（秒）")] public double fuse;
    }
    [Serializable] public struct FizzyBombSettings
    {
        [InspectorName("二次爆炸蓄力阈值（秒）")] public double secondCharge;
        [InspectorName("三次爆炸蓄力阈值（秒）")] public double thirdCharge;
        [InspectorName("首次触地引信（秒）")] public double fuse;
        [InspectorName("后续爆炸间隔（秒）")] public double interval;
        [InspectorName("爆炸弹跳速度（米／秒）")] public float hopSpeed;
        [InspectorName("后续爆炸前移速度（米／秒）")] public float hopForward;
        [InspectorName("二／三次近圈半径（米）")] public Vector2 innerRadii;
        [InspectorName("二／三次远圈半径（米）")] public Vector2 outerRadii;
    }
    [Serializable] public struct TorpedoSettings
    {
        [InspectorName("变形时间（秒）")] public double transform;
        [InspectorName("落地滚动引信（秒）")] public double rollFuse;
        [InspectorName("每玩家同时存在上限")] public int maxActive;
        [InspectorName("子弹数量")] public int droplets;
        [InspectorName("子弹伤害")] public float dropletDamage;
        [InspectorName("子弹散落半径（米）")] public float dropletRadius;
        [InspectorName("子弹爆炸半径（米）")] public float dropletBlastRadius;
        [InspectorName("子弹涂墨半径（米）")] public float dropletPaintRadius;
        [InspectorName("子弹落地引信（秒）")] public double dropletFuse;
    }
    [Serializable] public struct MineSettings
    {
        [InspectorName("布置后激活时间（秒）")] public double arm;
        [InspectorName("触发后引信（秒）")] public double fuse;
        [InspectorName("触发距离（米）")] public float triggerRadius;
    }
    [Serializable] public struct SensorSettings { [InspectorName("侦测区域持续（秒）")] public double duration; }
    [Serializable] public struct MistSettings
    {
        [InspectorName("雾区域半径（米）")] public float radius;
        [InspectorName("雾区域持续（秒）")] public double duration;
        [InspectorName("最低移动倍率")] public float moveRate;
        [InspectorName("达到最低速度（秒）")] public double slowRamp;
        [InspectorName("耗墨阶段阈值（秒）")] public Vector2 drainThresholds;
        [InspectorName("三阶段耗墨（点／秒）")] public Vector3 drainRates;
    }
    [Serializable] public struct AngleShooterSettings
    {
        [InspectorName("飞行速度（米／秒）")] public float speed;
        [InspectorName("总行进距离（米）")] public float range;
        [InspectorName("最大反射次数")] public int reflections;
        [InspectorName("直击伤害")] public float damage;
        [InspectorName("标线持续（秒）")] public double trailDuration;
        [InspectorName("标线触发半径（米）")] public float trailRadius;
    }
    [Serializable] public struct SprinklerSettings
    {
        [InspectorName("强／中阶段时间（秒）")] public Vector2 phases;
        [InspectorName("强／中／弱喷射间隔（秒）")] public Vector3 intervals;
        [InspectorName("喷射距离（米）")] public float range;
        [InspectorName("单颗伤害")] public float damage;
        [InspectorName("单颗涂墨半径（米）")] public float paintRadius;
        [InspectorName("旋转速度（度／秒）")] public float rotationSpeed;
        [InspectorName("墨滴初速（米／秒）")] public float dropletSpeed;
        [InspectorName("墨滴重力（米／秒²）")] public float dropletGravity;
        [InspectorName("墨滴寿命（秒）")] public double dropletLifetime;
    }
    [Serializable] public struct WallSettings
    {
        [InspectorName("展开时间（秒）")] public double expand;
        [InspectorName("自然耗尽时间（秒）")] public double lifetime;
        [InspectorName("宽高厚（米）")] public Vector3 size;
        [InspectorName("接触伤害")] public float damage;
        [InspectorName("接触伤害间隔（秒）")] public double interval;
    }

    [CreateAssetMenu(menuName = "喷墨对战/副武器配置", fileName = "SubWeaponConfig")]
    public sealed class SubWeaponConfigAsset : ScriptableObject
    {
        [InspectorName("副武器类型")] public SubWeaponType type;
        [InspectorName("通用参数")] public SubCommon common;
        [InspectorName("通用表现")] public SubVisualCommon visuals = SubVisualCommon.Defaults;
        [InspectorName("专用表现")] public SubVisualSpecific typeVisuals = SubVisualSpecific.Defaults;
        [InspectorName("投掷运动"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.CurlingBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.PointSensor, SubWeaponType.ToxicMist, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public SubFlight flight;
        [InspectorName("爆炸伤害"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.CurlingBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.InkMine)] public SubBlast blast;
        [InspectorName("涂墨"), SubTypes(SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.CurlingBomb, SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.InkMine, SubWeaponType.AngleShooter, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public SubPaint paint;
        [InspectorName("索敌与追踪"), SubTypes(SubWeaponType.Autobomb, SubWeaponType.Torpedo)] public SubTracking tracking;
        [InspectorName("敌人标记"), SubTypes(SubWeaponType.InkMine, SubWeaponType.PointSensor, SubWeaponType.AngleShooter)] public SubMark mark;
        [InspectorName("可破坏对象"), SubTypes(SubWeaponType.Torpedo, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public SubDurability durability;
        [InspectorName("部署数量"), SubTypes(SubWeaponType.InkMine, SubWeaponType.Sprinkler, SubWeaponType.SplashWall)] public SubDeployment deployment;
        [InspectorName("斯普拉炸弹"), SubTypes(SubWeaponType.SplatBomb)] public SplatBombSettings splat;
        [InspectorName("吸盘炸弹"), SubTypes(SubWeaponType.SuctionBomb)] public SuctionBombSettings suction;
        [InspectorName("冰壶蓄力与滑行"), SubTypes(SubWeaponType.CurlingBomb)] public CurlingBombSettings curling;
        [InspectorName("机器人行动"), SubTypes(SubWeaponType.Autobomb)] public AutobombSettings autobomb;
        [InspectorName("碳酸蓄力与连爆"), SubTypes(SubWeaponType.FizzyBomb)] public FizzyBombSettings fizzy;
        [InspectorName("鱼雷变形与散弹"), SubTypes(SubWeaponType.Torpedo)] public TorpedoSettings torpedo;
        [InspectorName("陷阱触发"), SubTypes(SubWeaponType.InkMine)] public MineSettings mine;
        [InspectorName("侦测区域"), SubTypes(SubWeaponType.PointSensor)] public SensorSettings sensor;
        [InspectorName("毒雾减速与耗墨"), SubTypes(SubWeaponType.ToxicMist)] public MistSettings mist;
        [InspectorName("标线反射"), SubTypes(SubWeaponType.AngleShooter)] public AngleShooterSettings angle;
        [InspectorName("洒墨三个阶段"), SubTypes(SubWeaponType.Sprinkler)] public SprinklerSettings sprinkler;
        [InspectorName("防护墙展开与衰减"), SubTypes(SubWeaponType.SplashWall)] public WallSettings wall;
        public SubWeaponRuntimeConfig Snapshot() => new(this);
    }

    public static class SubWeaponFields
    {
        public static bool Visible(FieldInfo field, SubWeaponType type) => field.GetCustomAttribute<SubTypesAttribute>() is not { } a || Array.IndexOf(a.Types, type) >= 0;
        public static IEnumerable<FieldInfo> Fields(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken);
        // Struct copies isolate running entities and eliminate irrelevant fields from validation and signatures.
        public static T Active<T>(T value, SubWeaponType type) where T : struct
        { object box = value; foreach (var f in Fields(typeof(T))) if (!Visible(f, type)) f.SetValue(box, f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null); return (T)box; }
        public static string Name(FieldInfo field) => field.GetCustomAttribute<InspectorNameAttribute>()?.displayName ?? field.Name;
        public static string TypeName(SubWeaponType type) => Name(typeof(SubWeaponType).GetField(type.ToString()));
    }

    public sealed class SubWeaponRuntimeConfig
    {
        public readonly SubWeaponType Type;
        public readonly SubCommon Common;
        public readonly SubVisualCommon Visuals;
        public readonly SubVisualSpecific TypeVisuals;
        public readonly SubFlight Flight;
        public readonly SubBlast Blast;
        public readonly SubPaint Paint;
        public readonly SubTracking Tracking;
        public readonly SubMark Mark;
        public readonly SubDurability Durability;
        public readonly SubDeployment Deployment;
        public readonly SplatBombSettings Splat;
        public readonly SuctionBombSettings Suction;
        public readonly CurlingBombSettings Curling;
        public readonly AutobombSettings Autobomb;
        public readonly FizzyBombSettings Fizzy;
        public readonly TorpedoSettings Torpedo;
        public readonly MineSettings Mine;
        public readonly SensorSettings Sensor;
        public readonly MistSettings Mist;
        public readonly AngleShooterSettings Angle;
        public readonly SprinklerSettings Sprinkler;
        public readonly WallSettings Wall;
        public readonly string ContentHash;
        public SubWeaponRuntimeConfig(SubWeaponConfigAsset a)
        {
            Type = a.type;
            Common = Copy(a.common, nameof(a.common));
            Visuals = a.visuals; TypeVisuals = SubWeaponFields.Active(a.typeVisuals, Type);
            Flight = Copy(a.flight, nameof(a.flight));
            Blast = Copy(a.blast, nameof(a.blast));
            Paint = Copy(a.paint, nameof(a.paint));
            Tracking = Copy(a.tracking, nameof(a.tracking));
            Mark = Copy(a.mark, nameof(a.mark));
            Durability = Copy(a.durability, nameof(a.durability));
            Deployment = Copy(a.deployment, nameof(a.deployment));
            Splat = Copy(a.splat, nameof(a.splat));
            Suction = Copy(a.suction, nameof(a.suction));
            Curling = Copy(a.curling, nameof(a.curling));
            Autobomb = Copy(a.autobomb, nameof(a.autobomb));
            Fizzy = Copy(a.fizzy, nameof(a.fizzy));
            Torpedo = Copy(a.torpedo, nameof(a.torpedo));
            Mine = Copy(a.mine, nameof(a.mine));
            Sensor = Copy(a.sensor, nameof(a.sensor));
            Mist = Copy(a.mist, nameof(a.mist));
            Angle = Copy(a.angle, nameof(a.angle));
            Sprinkler = Copy(a.sprinkler, nameof(a.sprinkler));
            Wall = Copy(a.wall, nameof(a.wall));
            T Copy<T>(T value, string name) where T : struct => SubWeaponFields.Visible(typeof(SubWeaponConfigAsset).GetField(name), Type) ? SubWeaponFields.Active(value, Type) : default;
            var text = new StringBuilder(); Append(text, this);
            using var sha = SHA256.Create(); ContentHash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        static void Append(StringBuilder b, object value)
        {
            if (value == null || value is UnityEngine.Object) return;
            var t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string) { b.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|'); return; }
            foreach (var f in SubWeaponFields.Fields(t)) if (f.Name != nameof(ContentHash) && f.Name != nameof(Visuals) && f.Name != nameof(TypeVisuals)) { b.Append(f.Name).Append(':'); Append(b, f.GetValue(value)); }
        }
        public float Charge(double seconds) => Type == SubWeaponType.CurlingBomb ? Mathf.Clamp01((float)(seconds / Math.Max(.00001, Curling.charge)))
            : Type == SubWeaponType.FizzyBomb ? (seconds + 1e-8 >= Fizzy.thirdCharge ? 1 : seconds + 1e-8 >= Fizzy.secondCharge ? .5f : 0) : 0;
        public double InkLock(float charge) => Type == SubWeaponType.CurlingBomb ? Common.inkLock + (Curling.fullInkLock - Common.inkLock) * charge : Common.inkLock;
        public float Damage(float distance, float charge = 0, int explosion = 0)
        {
            float inner = Blast.innerRadius, outer = Blast.outerRadius;
            if (Type == SubWeaponType.CurlingBomb) { inner = Mathf.Lerp(inner, Curling.innerRadius, charge); outer = Mathf.Lerp(outer, Curling.outerRadius, charge); }
            if (Type == SubWeaponType.FizzyBomb && explosion > 0) { inner = explosion == 1 ? Fizzy.innerRadii.x : Fizzy.innerRadii.y; outer = explosion == 1 ? Fizzy.outerRadii.x : Fizzy.outerRadii.y; }
            return distance <= inner ? Blast.innerDamage : Type == SubWeaponType.BurstBomb && distance <= Blast.middleRadius ? Blast.middleDamage : distance <= outer ? Blast.outerDamage : 0;
        }
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(SubWeaponType), Type) || Common.id <= 0 || string.IsNullOrWhiteSpace(Common.displayName)) throw new InvalidOperationException("副武器类型、编号或名称无效");
            Check(this, "副武器");
            if (Common.inkCost > 100 || Flight.bounce > 1 || Paint.hardness > 1 || Mist.moveRate > 1) throw new InvalidOperationException("比例或耗墨超出范围");
            if (Blast.outerRadius < Blast.innerRadius || (Type == SubWeaponType.BurstBomb && (Blast.middleRadius < Blast.innerRadius || Blast.middleRadius > Blast.outerRadius))) throw new InvalidOperationException("爆炸半径必须由内向外递增");
            if (Type == SubWeaponType.CurlingBomb && (Curling.charge <= 0 || Curling.fuse.x < Curling.fuse.y || Curling.outerRadius < Curling.innerRadius)) throw new InvalidOperationException("冰壶蓄力端点无效");
            if (Type == SubWeaponType.FizzyBomb && (Fizzy.secondCharge <= 0 || Fizzy.thirdCharge <= Fizzy.secondCharge || Fizzy.interval <= 0)) throw new InvalidOperationException("碳酸蓄力阈值或连爆间隔无效");
            if (Type == SubWeaponType.Torpedo && (Torpedo.maxActive < 1 || Torpedo.droplets < 1 || Torpedo.droplets > 32)) throw new InvalidOperationException("鱼雷数量无效");
            if ((Type == SubWeaponType.InkMine || Type == SubWeaponType.Sprinkler || Type == SubWeaponType.SplashWall) && (Deployment.maxCount < 1 || Deployment.maxCount > 8)) throw new InvalidOperationException("部署数量必须在1至8之间");
            if (Type == SubWeaponType.Sprinkler && (Mathf.Min(Sprinkler.intervals.x, Sprinkler.intervals.y, Sprinkler.intervals.z) <= 0 || Sprinkler.dropletSpeed<=0 || Sprinkler.dropletLifetime<=0)) throw new InvalidOperationException("洒墨间隔、墨滴初速和寿命必须大于零");
            if(Type==SubWeaponType.AngleShooter&&(Angle.speed<=0||Angle.range<=0||Angle.reflections<0))throw new InvalidOperationException("标线器速度和射程必须为正，反射次数不可为负");
            if (Type == SubWeaponType.SplashWall && (Wall.lifetime <= 0 || Wall.interval <= 0 || Wall.size.x <= 0 || Wall.size.y <= 0 || Wall.size.z <= 0)) throw new InvalidOperationException("防护墙时间或尺寸无效");
            if (Type == SubWeaponType.ToxicMist && Mist.drainThresholds.y < Mist.drainThresholds.x) throw new InvalidOperationException("毒雾阶段时间顺序无效");
        }
        static void Check(object v, string path)
        {
            if (v == null || v is UnityEngine.Object || v is string || v.GetType().IsEnum) return;
            if (v is float f) { if (!float.IsFinite(f) || f < 0) throw new InvalidOperationException(path + "必须为有限非负数"); return; }
            if (v is double d) { if (!double.IsFinite(d) || d < 0) throw new InvalidOperationException(path + "必须为有限非负数"); return; }
            foreach (var field in SubWeaponFields.Fields(v.GetType())) Check(field.GetValue(v), path + "." + field.Name);
        }
    }
}
