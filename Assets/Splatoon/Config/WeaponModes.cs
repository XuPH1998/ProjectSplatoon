using UnityEngine;

namespace Splatoon.Config
{
    // Explicit values preserve existing assets and content signatures.
    public enum WeaponFireMode
    {
        [InspectorName("全自动")] Automatic = 0,
        [InspectorName("三连发")] Burst = 1,
        [InspectorName("蓄力松开发射")] Charge = 2,
        [InspectorName("半自动（支持长按）")] SemiAutomatic = 3,
        [InspectorName("旋转枪")] Splatling = 4,
        [InspectorName("泡泡连发（整组耗墨）")] BubbleVolley = 5,
        [InspectorName("爆破枪（点击或长按）")] Blaster = 6
    }

    public enum WeaponMuzzleMode
    {
        [InspectorName("单枪口")] Single = 0,
        [InspectorName("右左交替")] AlternatingRightLeft = 1
    }

    public enum ProjectileMotionMode
    {
        [InspectorName("普通墨弹")] Ballistic = 0,
        [InspectorName("弹跳泡泡")] BouncingBubble = 1,
        [InspectorName("定时爆破墨弹")] TimedBlaster = 2,
        [InspectorName("普通双枪（三段参考弹道）")] DualiesNormal = 3
    }
}
