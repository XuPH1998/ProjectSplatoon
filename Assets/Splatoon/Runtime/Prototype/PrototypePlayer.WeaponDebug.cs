#if UNITY_EDITOR
using Splatoon.Combat;
using Splatoon.Config;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        public void CancelForWeaponReload()
        {
            if (!IsServer) return;
            var s = Snapshot.Value;
            WeaponSimulation.Cancel(ref s, _lastInput, true);
            s.WeaponReadyAt = s.NextShotAt = s.BurstReadyAt = s.SimulatedAt;
            s.HeroRevision++;
            _serverInputs.Clear(); _history.Clear();
            _lastInput.HeroRevision = s.HeroRevision;
            Snapshot.Value = s;
        }
        public void RefreshWeaponConfiguration()
        {
            if (!IsServer) return;
            var s = Snapshot.Value;
            SpreadSimulation.Refresh(ref s, GameplayConfig.GetWeapon(s.HeroId));
            Snapshot.Value = s;
            EnsureHeroPresentation(s.HeroId);
        }
    }
}
#endif
