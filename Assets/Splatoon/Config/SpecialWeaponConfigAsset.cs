using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
namespace Splatoon.Config
{
    public enum SpecialWeaponType : byte { Trizooka=1, TripleInkstrike, WaveBreaker, InkStorm, Reefslider }
    [Serializable]
    public struct SpecialParameters
    {
        [Tooltip("使用窗口/启动/攻击前摇/攻击间隔/恢复（秒）")]
        public float duration, startup, shotDelay, repeat, recovery, chargeLock;
        [Tooltip("声呐搭配地雷时额外投掷前摇；社区测量值，非原生继承默认值")]
        public float mineThrowExtraDelay;
        public int shots;
        public float moveSpeed, firingMoveSpeed, equipDelay, throwSpeed, throwLift, throwGravity, throwDrag, throwRadius;
        public float directDamage, innerDamage, outerDamage, innerRadius, outerRadius, paintRadius;
        public float projectileSpeed, straightSeconds, brakeSeconds, brakeMaxSpeed, lobeDelay, brakeDrag, brakeGravity, freeDrag, freeGravity;
        public float fieldRadiusStart, fieldRadiusEnd, fieldRadiusTime, playerRadiusStart, playerRadiusEnd, playerRadiusTime, orbitalRadius, orbitalTime;
        public float effectDelay, effectDuration, expandSeconds, damagePerSecond, damageHeight, damageHeightDown, paintInterval;
        public float health, waveFirst, waveInterval, waveLifetime, waveRadius, waveHeightUp, waveHeightDown, markSeconds;
        public float cloudSpeed, cloudHeight;
        public int rainDrops;
        public float rainGravity, rainDrag, rainFieldRadius;
        public float rideStartup, rideDuration, rideBrakeAllowed, rideSpeed, rideAirSpeed, rideAcceleration, rideBrake, rideBurstDelay, rideInvincibleStart, rideInvincibleAfter, rideRecovery;
        public float ridePlayerRadius, rideDetectRadius, rideDetectOffsetY, rideDetectOffsetZ, rideGravityMultiplier, rideRutRadius;
        public int reefSplashCount;
        public float reefSplashOffsetY, reefSplashPitchMax, reefSplashSpeedMin, reefSplashSpeedMax, reefSplashPaintRadius;
        [Tooltip("外围飞沫重力：按实测约2.5条线最远涂墨范围拟合，非原生默认值")]
        public float reefSplashGravity;
        [Tooltip("击退单位转换/衰减解释为项目实现；原始字段来源见参数表")]
        public float knockbackAcceleration,knockbackRetention,knockbackDistance,contactKnockbackAcceleration;
        public float recoilSpeed,recoilAirRetention,recoilBackInputRate;
    }
    [CreateAssetMenu(menuName="喷墨对战/大招配置")]
    public sealed class SpecialWeaponConfigAsset : ScriptableObject
    {
        public SpecialWeaponType type;
        public string displayName;
        public SpecialParameters parameters;
        public GameObject heldPrefab, entityPrefab;
        public Material effectMaterial;
        public Sprite icon;
        public AudioClip useAudio, effectAudio;
        public SpecialWeaponRuntimeConfig Snapshot()=>new(this);
    }
    public sealed class SpecialWeaponRuntimeConfig
    {
        public readonly SpecialWeaponType Type;
        public readonly SpecialParameters P;
        public readonly string Name, ContentHash;
        public readonly GameObject HeldPrefab, EntityPrefab;
        public readonly Material Material;
        public readonly Sprite Icon;
        public readonly AudioClip UseAudio, EffectAudio;
        public SpecialWeaponRuntimeConfig(SpecialWeaponConfigAsset a)
        {
            Type=a.type;P=a.parameters;Name=a.displayName;HeldPrefab=a.heldPrefab;EntityPrefab=a.entityPrefab;Material=a.effectMaterial;Icon=a.icon;UseAudio=a.useAudio;EffectAudio=a.effectAudio;
            using var sha=SHA256.Create();ContentHash=Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(((int)Type)+":"+JsonUtility.ToJson(P))));
        }
        public void Validate()
        {
            if(!Enum.IsDefined(typeof(SpecialWeaponType),Type)||string.IsNullOrWhiteSpace(Name)||P.duration<=0||P.shots<1||P.repeat<=0)throw new InvalidOperationException("大招类型/名称/时序无效");
            if(Type==SpecialWeaponType.InkStorm&&(P.rainDrops<1||P.rainDrops>256||P.rainGravity<=0||P.rainDrag>=1||P.rainFieldRadius<=0))throw new InvalidOperationException("墨雨玩法雨滴参数无效");
            if(Type==SpecialWeaponType.Reefslider&&(P.reefSplashCount<1||P.reefSplashCount>128||P.reefSplashGravity<=0||P.reefSplashSpeedMin>P.reefSplashSpeedMax||P.reefSplashPitchMax>90))throw new InvalidOperationException("鲨鱼外围涂墨参数无效");
            foreach(var f in typeof(SpecialParameters).GetFields())if(f.FieldType==typeof(float)){float v=(float)f.GetValue(P);if(!float.IsFinite(v)||v<0)throw new InvalidOperationException("大招参数必须有限非负："+f.Name);}
        }
    }
    public static class SpecialWeaponDefaults
    {
        public const float Scale=SubWeaponDefaults.Scale;
        public const string Root="Assets/GameResource/SpecialWeapons/";
        public static string Path(SpecialWeaponType t)=>Root+t+"/"+t+".asset";
        // Explicit reference overrides; inherited values pending verification are listed in the parameter ledger.
        public static void Apply(SpecialWeaponConfigAsset a,SpecialWeaponType t)
        {
            float s=Scale;a.type=t;a.displayName=new[]{"终极发射","三重龙卷风","弹跳声呐","墨雨","鲨鱼坐骑"}[(int)t-1];
            var p=new SpecialParameters{duration=6,startup=.1f,repeat=.5f,recovery=.2f,shots=1,chargeLock=1,
                moveSpeed=.07f*60*s,throwSpeed=1.12f*60*s,throwLift=.24f*60*s,throwGravity=.016f*3600*s,throwDrag=-60*Mathf.Log(1-.05866f),throwRadius=.15f*s,
                effectDelay=1,effectDuration=2,expandSeconds=50f/60,paintInterval=2f/60};
            switch(t)
            {
                case SpecialWeaponType.Trizooka:
                    p.knockbackAcceleration=470*s;p.knockbackRetention=.8f;p.knockbackDistance=8*s;
                    p.recoilSpeed=.04f*60*s;p.recoilAirRetention=.8f;p.recoilBackInputRate=5;
                    // Equip animation is the measured 18-frame base; StartDelayFrame adds 5 in 11.3.0.
                    p.duration=330f/60;p.startup=5f/60;p.equipDelay=18f/60;p.shotDelay=15f/60;p.repeat=55f/60;p.shots=3;p.recovery=40f/60;p.chargeLock=p.duration;p.firingMoveSpeed=.04f*60*s;
                    p.directDamage=220;p.innerDamage=53;p.outerDamage=35;p.innerRadius=2.5f*s;p.outerRadius=4*s;p.paintRadius=3.2f*s;
                    p.projectileSpeed=1.125f*60*s;p.straightSeconds=16f/60;p.brakeSeconds=10f/60;p.brakeMaxSpeed=60*s;p.lobeDelay=4f/60;p.brakeDrag=.09f;p.brakeGravity=.09f*3600*s;p.freeDrag=.01985f;p.freeGravity=.0190565f*3600*s;
                    p.fieldRadiusStart=.01f*s;p.fieldRadiusEnd=.3f*s;p.fieldRadiusTime=20f/60;p.playerRadiusStart=.01f*s;p.playerRadiusEnd=.75f*s;p.playerRadiusTime=10f/60;p.orbitalRadius=s;p.orbitalTime=10f/60;break;
                case SpecialWeaponType.TripleInkstrike:
                    p.knockbackAcceleration=93.333f*s;p.knockbackRetention=.8f;p.knockbackDistance=8*s;
                    p.duration=6;p.startup=20f/60;p.shotDelay=14f/60;p.repeat=19f/60;p.recovery=19f/60;p.effectDelay=82f/60;p.effectDuration=51f/60;p.shots=3;p.chargeLock=0;p.throwSpeed=2*60*s;p.throwLift=.276f*60*s;p.innerRadius=7.7f*s;p.paintRadius=7*s;p.damagePerSecond=7.5f*60;p.damageHeight=20*s;
                    // Community measurement: approximately two training lines below the locator.
                    // This is not an extracted native default; provenance is in behavior-evidence.json.
                    p.damageHeightDown=10*s;p.moveSpeed=.1f*60*s;break;
                case SpecialWeaponType.WaveBreaker:
                    p.contactKnockbackAcceleration=400*s;p.knockbackRetention=.8f;p.knockbackDistance=8*s;
                    p.duration=float.MaxValue;p.shotDelay=5f/60;p.mineThrowExtraDelay=11f/60;p.chargeLock=6.5f;p.throwSpeed=.3f*60*s;p.throwLift=.07f*60*s;p.throwGravity=.009f*3600*s;p.throwDrag=-60*Mathf.Log(1-.001f);
                    p.health=480;p.directDamage=30;p.innerDamage=45;p.waveFirst=90f/60;p.waveInterval=150f/60;p.waveLifetime=160f/60;p.waveRadius=20*s;p.waveHeightUp=2.8f*s;p.waveHeightDown=5.2f*s;p.markSeconds=8;p.effectDuration=390f/60+160f/60;break;
                case SpecialWeaponType.InkStorm:
                    p.duration=float.MaxValue;p.shotDelay=13f/60;p.chargeLock=8;p.effectDelay=.5f;p.effectDuration=8;p.innerRadius=10*s;p.damagePerSecond=24;p.cloudSpeed=35*s/8;p.cloudHeight=8*s;p.paintRadius=.5f*s;p.rainDrops=72;p.rainGravity=.02f*3600*s;p.rainDrag=.07f;p.rainFieldRadius=.25f*s;break;
                case SpecialWeaponType.Reefslider:
                    p.knockbackAcceleration=700*s;p.knockbackRetention=.8f;p.knockbackDistance=12*s;
                    p.reefSplashCount=15;p.reefSplashOffsetY=.3f*s;p.reefSplashPitchMax=30;p.reefSplashSpeedMin=.6f*60*s;p.reefSplashSpeedMax=.7f*60*s;p.reefSplashPaintRadius=s;
                    // At maximum speed/elevation: centre reaches 11.5 raw units, plus
                    // radius 1 reaches the measured ~12.5. Explicit project approximation.
                    p.reefSplashGravity=2*(.3f+11.5f*Mathf.Tan(30*Mathf.Deg2Rad))*.7f*.7f*.75f/(11.5f*11.5f)*3600*s;
                    p.duration=5;p.chargeLock=5;p.rideStartup=38f/60;p.rideDuration=54f/60;p.rideBrakeAllowed=15f/60;p.rideSpeed=.405f*60*s;p.rideAirSpeed=.22f*60*s;p.rideAcceleration=.031f*3600*s;p.rideBrake=.031f*3600*s;
                    p.rideBurstDelay=38f/60;p.rideInvincibleStart=25f/60;p.rideInvincibleAfter=28f/60;p.rideRecovery=28f/60;
                    p.ridePlayerRadius=.8f*s;p.rideDetectRadius=s;p.rideDetectOffsetY=.8f*s;p.rideDetectOffsetZ=s;p.rideGravityMultiplier=1.2f;p.rideRutRadius=1.2f*s;
                    p.directDamage=220;p.innerDamage=220;p.outerDamage=70;p.innerRadius=9*s;p.outerRadius=14.9f*s;p.paintRadius=7.51f*s;break;
            }
            a.parameters=p;
        }
    }
}
