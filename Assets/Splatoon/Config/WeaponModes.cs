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
        [InspectorName("旋转枪")] Splatling = 4
    }

    public enum WeaponMuzzleMode
    {
        [InspectorName("单枪口")] Single = 0,
        [InspectorName("右左交替")] AlternatingRightLeft = 1
    }
}
