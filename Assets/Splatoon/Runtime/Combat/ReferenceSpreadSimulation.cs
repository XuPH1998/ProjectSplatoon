using System;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    // Shares the existing bias snapshot slots: these modes and normal dualies are exclusive.
    public static class ReferenceSpreadSimulation
    {
        public static bool Enabled(WeaponRuntimeConfig w) => w.ReferenceRules && w.ReferenceSpreadEnabled;
        public static void Reset(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        { s.DualiesGroundBias = w.ReferenceBiasMin; s.DualiesJumpAge = 0; s.DualiesWasGrounded = s.Grounded; s.LastShotSpreadBias = 0; Refresh(ref s, w); }
        public static void Before(ref PlayerSnapshot s, WeaponRuntimeConfig w, double now)
        {
            double dt = s.SpreadInitialized ? Math.Max(0, now - s.SpreadUpdatedAt) : 0;
            if (!s.SpreadInitialized) Reset(ref s, w);
            s.SpreadInitialized = true; s.SpreadUpdatedAt = now;
            s.DualiesJumpAge = s.Grounded || s.DualiesWasGrounded ? 0 : s.DualiesJumpAge + dt;
            s.DualiesWasGrounded = s.Grounded;
            if (!s.SpreadFiring) s.DualiesGroundBias -= (float)Math.Max(0, now - Math.Max(now - dt, s.NextShotAt)) * w.ReferenceBiasRecovery;
            s.DualiesGroundBias = Mathf.Clamp(s.DualiesGroundBias, w.ReferenceBiasMin, w.ReferenceBiasMax);
            Refresh(ref s, w);
        }
        public static float Bias(PlayerSnapshot s, WeaponRuntimeConfig w) => s.Grounded ? s.DualiesGroundBias : Mathf.Lerp(w.ReferenceJumpBias, s.DualiesGroundBias,
            Mathf.InverseLerp((float)w.ReferenceJumpStart, (float)w.ReferenceJumpEnd, (float)s.DualiesJumpAge));
        public static void After(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, bool emitted, bool canShoot)
        {
            if (emitted)
            { s.LastShotSpread = s.CurrentSpread; s.LastShotVerticalSpread = s.CurrentVerticalSpread; s.LastShotSpreadBias = Bias(s, w); s.DualiesGroundBias = Mathf.Min(w.ReferenceBiasMax, s.DualiesGroundBias + w.ReferenceBiasPerShot); }
            s.SpreadFiring = canShoot && s.Health > 0 && !input.CancelFire && !s.AttackNeedsRelease &&
                (WeaponSimulation.IsSplatling(w) ? s.SplatlingRemaining > 0 && s.WeaponPhase == WeaponPhase.Firing : input.Fire && s.Ink + PrototypeRules.InkTolerance >= w.ShotInk && (emitted || s.SpreadFiring));
            Refresh(ref s, w);
        }
        public static void Refresh(ref PlayerSnapshot s, WeaponRuntimeConfig w)
        {
            s.CurrentSpread = s.Grounded ? w.SpreadDegrees : w.JumpSpreadDegrees;
            s.CurrentVerticalSpread = s.Grounded && WeaponSimulation.IsSplatling(w) ? w.SplatlingPitchSpread : s.CurrentSpread;
            s.SpreadProgress = Mathf.InverseLerp(w.ReferenceBiasMin, w.ReferenceBiasMax, s.DualiesGroundBias);
        }
    }
}

