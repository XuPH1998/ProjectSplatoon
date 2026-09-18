using UnityEngine;

namespace ChibiArt
{
    /// <summary>The bare art rig has no weapon-switching gameplay.</summary>
    public sealed class RifleGirlChibiAnimationEvents : MonoBehaviour
    {
        // The source clips include this legacy event. A gameplay view may also
        // receive it when the rig is integrated; the standalone art rig consumes it.
        public void SwitchSocket(AnimationEvent animationEvent) { }
    }
}
