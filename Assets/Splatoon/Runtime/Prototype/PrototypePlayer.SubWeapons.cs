using Splatoon.Config;
using Splatoon.Combat;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        SubLaunchSolution _lastSubLaunch;
        SubWeaponFailure SubPredictionPlacement(PlayerSnapshot s, SubWeaponRuntimeConfig c)
        {
            if (c.Type == SubWeaponType.InkMine && (!s.Grounded || !SubWeaponService.TryMineGround(s, out _))) return SubWeaponFailure.InvalidGround;
            if (c.Type == SubWeaponType.Torpedo && PrototypeMatch.Current?.SubPresentation != null && PrototypeMatch.Current.SubPresentation.Count(PlayerId, c.Type) >= c.Torpedo.maxActive) return SubWeaponFailure.ActiveLimit;
            return SubWeaponFailure.None;
        }
        public void CancelForSubWeaponReload()
        {
            if (!IsServer) return;
            var s = Snapshot.Value; SubWeaponSimulation.Cancel(ref s, _lastInput); Snapshot.Value = s;
        }
    }
}
