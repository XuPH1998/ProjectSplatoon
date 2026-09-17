using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public static class SpreadSimulation
    {
        public static Vector2 Angles(WeaponRuntimeConfig w, bool airborne, float progress)
        {
            if (ReferenceSpreadSimulation.Enabled(w)) return new Vector2(airborne ? w.JumpSpreadDegrees : w.SpreadDegrees, airborne ? w.JumpSpreadDegrees : WeaponSimulation.IsSplatling(w) ? w.SplatlingPitchSpread : w.SpreadDegrees);
            if (WeaponSimulation.IsBlaster(w) || DualiesNormalSimulation.Enabled(w)) return Vector2.one * (airborne ? w.JumpSpreadDegrees : w.SpreadDegrees);
            float p = Mathf.Clamp01(progress);
            float min = airborne ? w.BaseJumpSpreadDegrees : w.BaseSpreadDegrees;
            float max = airborne ? w.JumpSpreadDegrees : w.SpreadDegrees;
            float horizontal = Mathf.Lerp(min, max, p);
            float vertical = WeaponSimulation.IsSplatling(w) ? (airborne ? w.JumpSpreadDegrees : w.SplatlingPitchSpread) * p : horizontal;
            return new Vector2(horizontal, vertical);
        }
        public static void Reset(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            s.SpreadProgress = 0; s.SpreadFiring = false; s.SpreadInitialized = false; s.SpreadUpdatedAt = 0;
            if (ReferenceSpreadSimulation.Enabled(w)) { ReferenceSpreadSimulation.Reset(ref s, w); return; }
            if (DualiesNormalSimulation.Enabled(w)) { DualiesNormalSimulation.Reset(ref s, w); return; }
            var angles = WeaponSimulation.IsCharge(w) ? Vector2.one * WeaponSimulation.Spread(w, !s.Grounded, 0) : Angles(w, !s.Grounded, 0);
            s.CurrentSpread = angles.x; s.CurrentVerticalSpread = angles.y;
            s.LastShotSpread = s.LastShotVerticalSpread = 0;
        }
        public static void Before(ref PlayerSnapshot s, WeaponRuntimeConfig w, double now)
        {
            if (ReferenceSpreadSimulation.Enabled(w)) { ReferenceSpreadSimulation.Before(ref s, w, now); return; }
            if (WeaponSimulation.IsBlaster(w))
            { s.SpreadProgress = 0; s.SpreadFiring = false; s.SpreadUpdatedAt = now; Refresh(ref s, w); return; }
            float dt = s.SpreadInitialized ? (float)Math.Max(0, now - s.SpreadUpdatedAt) : 0;
            s.SpreadInitialized = true; s.SpreadUpdatedAt = now;
            if (DualiesNormalSimulation.Enabled(w)) { DualiesNormalSimulation.Before(ref s, w, now, dt); return; }
            if (WeaponSimulation.IsCharge(w))
            {
                float charge = WeaponSimulation.ChargeRatio(s, w);
                float ground = WeaponSimulation.Spread(w, false, charge), air = WeaponSimulation.Spread(w, true, charge);
                s.CurrentSpread = !s.Grounded ? air : Mathf.MoveTowards(s.CurrentSpread, ground,
                    Mathf.Abs(air - ground) * dt / Mathf.Max(.001f, (float)w.LandingSpreadRecoverSeconds));
                s.CurrentVerticalSpread = s.CurrentSpread;
                return;
            }
            float duration = s.SpreadFiring ? w.SpreadExpandSeconds : w.SpreadRecoverSeconds;
            s.SpreadProgress = duration <= 0 ? (s.SpreadFiring ? 1 : 0) : Mathf.Clamp01(s.SpreadProgress + (s.SpreadFiring ? dt : -dt) / duration);
            Refresh(ref s, w);
        }
        public static void After(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, bool emitted, float charge, bool canShoot)
        {
            if (ReferenceSpreadSimulation.Enabled(w)) { ReferenceSpreadSimulation.After(ref s, input, w, emitted, canShoot); return; }
            if (DualiesNormalSimulation.Enabled(w)) { DualiesNormalSimulation.After(ref s, input, w, emitted, canShoot); return; }
            if (WeaponSimulation.IsBlaster(w))
            {
                s.SpreadFiring = false; Refresh(ref s, w);
                if (emitted) { s.LastShotSpread = s.CurrentSpread; s.LastShotVerticalSpread = s.CurrentVerticalSpread; }
                return;
            }
            if (WeaponSimulation.IsCharge(w))
            {
                if (emitted) s.CurrentSpread = WeaponSimulation.Spread(w, !s.Grounded, charge);
                s.CurrentVerticalSpread = s.CurrentSpread;
            }
            else
            {
                bool active = canShoot && s.Health > 0 && !input.CancelFire && !s.AttackNeedsRelease;
                active &= WeaponSimulation.IsSplatling(w) ? s.SplatlingRemaining > 0 && s.WeaponPhase == WeaponPhase.Firing :
                    s.BurstRemaining > 0 && w.FireMode == WeaponFireMode.Burst ||
                    input.Fire && (emitted || s.SpreadFiring) && s.Ink + PrototypeRules.InkTolerance >= w.ShotInk &&
                    (s.WeaponPhase == WeaponPhase.Firing || s.WeaponPhase == WeaponPhase.BurstCooldown || WeaponSimulation.IsSemi(w));
                // Zero expansion takes effect on the first emitted shot, including a one-round magazine.
                if (w.SpreadExpandSeconds <= 0 && emitted) { s.SpreadProgress = 1; Refresh(ref s, w); }
                s.SpreadFiring = active;
            }
            if (emitted) { s.LastShotSpread = s.CurrentSpread; s.LastShotVerticalSpread = s.CurrentVerticalSpread; }
            // Record the final round before an instantaneous recovery affects the HUD.
            if (!WeaponSimulation.IsCharge(w) && !s.SpreadFiring && w.SpreadRecoverSeconds <= 0)
            { s.SpreadProgress = 0; Refresh(ref s, w); }
        }
        public static void Refresh(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            if (ReferenceSpreadSimulation.Enabled(w)) { ReferenceSpreadSimulation.Refresh(ref s, w); return; }
            if (DualiesNormalSimulation.Enabled(w)) { DualiesNormalSimulation.Refresh(ref s, w); return; }
            if (WeaponSimulation.IsCharge(w)) return;
            var angles = Angles(w, !s.Grounded, s.SpreadProgress);
            s.CurrentSpread = angles.x; s.CurrentVerticalSpread = angles.y;
        }
    }
}
