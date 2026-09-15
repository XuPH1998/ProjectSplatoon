using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public readonly struct WeaponFireResult
    {
        public readonly int HeroId;
        public readonly float Charge;
        public readonly ulong ActionId;
        public readonly byte MuzzleIndex;
        public readonly int PelletCount;
        public uint ReleaseSequence => (uint)(ActionId >> 32);
        public uint RoundIndex => (uint)ActionId;
        public WeaponFireResult(int heroId, float charge, ulong actionId, byte muzzleIndex = 0, int pelletCount = 1)
        { HeroId = heroId; Charge = charge; ActionId = actionId; MuzzleIndex = muzzleIndex; PelletCount = pelletCount; }
    }

    public static class WeaponSimulation
    {
        public const double ReferenceRate = 60;
        public static double Seconds(int frames) => frames / ReferenceRate;
        public static double FireInterval(WeaponRuntimeConfig w) => 1.0 / w.FireRate;
        public static bool IsCharge(WeaponRuntimeConfig w) => w.FireMode == WeaponFireMode.Charge;
        public static bool IsSemi(WeaponRuntimeConfig w) => w.FireMode == WeaponFireMode.SemiAutomatic;
        public static bool IsSplatling(WeaponRuntimeConfig w) => w.FireMode == WeaponFireMode.Splatling;
        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now)
            => IsSplatling(w) ? SplatlingSimulation.WantsFire(s, input, now) : !IsSemi(w) ? WantsFire(s, input) : !input.CancelFire && !s.AttackNeedsRelease &&
                (s.WeaponPhase == WeaponPhase.Starting || (s.Ink + .00001f >= w.ShotInk &&
                (input.Fire || (input.FireSequence != s.ConsumedFire && now + w.SemiBufferSeconds + 1e-8 >= s.NextShotAt))));
        public static void ResetPresentation(ref PlayerSnapshot s)
        { s.NextMuzzle = s.LastShotMuzzle = 0; s.RightShotAt = s.LeftShotAt = 0; s.RightShotAction = s.LeftShotAction = 0; }
        public static float Damage(WeaponRuntimeConfig w, double age, float charge = 1) => IsCharge(w)
            ? charge >= 1 ? w.Damage : Mathf.Lerp(w.ChargeMinDamage, w.ChargePartialMaxDamage, charge)
            : Mathf.Lerp(IsSplatling(w) && charge < 1 ? w.ChargePartialMaxDamage : w.Damage, w.DamageMin, Mathf.InverseLerp((float)w.DamageReduceStartSeconds, (float)w.DamageReduceEndSeconds, (float)age));
        public static float InkCost(WeaponRuntimeConfig w, float charge = 0) => IsCharge(w) ? Mathf.Lerp(w.ChargeMinInk, w.ShotInk, charge) : w.ShotInk;
        public static float Range(WeaponRuntimeConfig w, float charge) => IsSplatling(w) ? Mathf.Lerp(w.ChargeMinRange, w.EffectiveRange, SplatlingSimulation.RangeCharge(w, charge)) : IsCharge(w) ? Mathf.Lerp(w.ChargeMinRange, w.EffectiveRange, charge) : w.EffectiveRange;
        public static float Speed(WeaponRuntimeConfig w, float charge) => IsCharge(w) ? Mathf.Lerp(w.ChargeMinSpeed, w.SpeedMin, charge) : w.SpeedMin;
        public static float ChargeRatio(PlayerSnapshot s, WeaponRuntimeConfig w) => w.ChargeSeconds > 0 ? (float)Math.Clamp((IsSplatling(w) ? s.SplatlingChargeSeconds : s.ChargeElapsedSeconds) / w.ChargeSeconds, 0, 1) : 0;
        // Rocket intermediate charge retains its original 60 Hz quantization. The
        // authored endpoint itself may be any fractional second and is never rounded.
        public static double AffordableChargeSeconds(WeaponRuntimeConfig w, float ink)
        {
            if (ink + .00001f >= w.ShotInk) return w.ChargeSeconds;
            double fraction = (ink - w.ChargeMinInk + .00001f) / (w.ShotInk - w.ChargeMinInk);
            return Math.Clamp(Math.Floor(fraction * w.ChargeSeconds * ReferenceRate + 1e-6) / ReferenceRate, 0, w.ChargeSeconds);
        }
        static double RocketChargeSeconds(WeaponRuntimeConfig w, double elapsed)
            => elapsed + 1e-8 >= w.ChargeSeconds ? w.ChargeSeconds :
                Math.Clamp(Math.Floor(elapsed * ReferenceRate + 1e-6) / ReferenceRate, 0, w.ChargeSeconds);
        public static float Spread(WeaponRuntimeConfig w, bool airborne, float charge) => IsCharge(w)
            ? Mathf.Lerp(airborne ? w.ChargeMinJumpSpread : w.ChargeMinSpread, airborne ? w.JumpSpreadDegrees : w.SpreadDegrees, charge)
            : airborne ? w.JumpSpreadDegrees : w.SpreadDegrees;
        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input) => !input.CancelFire && !s.AttackNeedsRelease &&
            (input.Fire || input.FireSequence != s.ConsumedFire || s.WeaponPhase == WeaponPhase.Starting || s.WeaponPhase == WeaponPhase.Charging || s.BurstRemaining > 0);

        public static void Cancel(ref PlayerSnapshot s, PlayerInputFrame input, bool requireRelease = false)
        {
            SplatlingSimulation.Refund(ref s); s.SpreadFiring = false;
            s.NextShotAt = Math.Max(s.NextShotAt, s.BurstReadyAt);
            s.WeaponPhase = WeaponPhase.Idle; s.Firing = false; s.FireVisualUntil = 0; s.BurstRemaining = 0; s.ChargeElapsedSeconds = 0;
            s.ChargeReleasePending = false; s.ConsumedFire = input.FireSequence; s.ConsumedRelease = input.ReleaseSequence;
            s.AttackNeedsRelease |= requireRelease; s.SemiHoldStarted = false;
        }

        // Kept for existing deterministic simulation callers; runtime also consumes the immutable fire result.
        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now, bool emerged, bool canShoot)
            => Step(ref s, input, w, now, emerged, canShoot, out _);

        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now, bool emerged, bool canShoot, out WeaponFireResult result)
        {
            SpreadSimulation.Before(ref s, w, now);
            bool emitted = StepCore(ref s, input, w, now, emerged, canShoot, out result);
            SpreadSimulation.After(ref s, input, w, emitted, result.Charge, canShoot);
            return emitted;
        }
        static bool StepCore(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now, bool emerged, bool canShoot, out WeaponFireResult result)
        {
            result = default;
            s.Firing = s.FireVisualUntil > 0 && now <= s.FireVisualUntil + 1e-8;
            if (input.CancelFire || s.Health <= 0) { Cancel(ref s, input, true); return false; }
            if (s.AttackNeedsRelease)
            {
                s.ConsumedFire = input.FireSequence; s.ConsumedRelease = input.ReleaseSequence;
                if (!input.Fire) s.AttackNeedsRelease = false;
                return false;
            }
            bool edge = input.FireSequence != s.ConsumedFire;
            bool release = input.ReleaseSequence != s.ConsumedRelease;
            s.ConsumedRelease = input.ReleaseSequence;
            if (IsSplatling(w)) return SplatlingSimulation.Step(ref s, input, GameplayConfig.GetHero(s.HeroId), w, now, emerged, canShoot, edge, release, out result);
            if (IsSemi(w)) return StepSemi(ref s, input, w, now, emerged, canShoot, edge, out result);
            bool chargeWeapon = IsCharge(w), burst = w.FireMode == WeaponFireMode.Burst;
            // Finishing a released shot must keep progressing while submerged or
            // unable to stand. Only pending/new shots require shooting clearance.
            if (s.WeaponPhase == WeaponPhase.Ending) s.WeaponPhase = WeaponPhase.Idle;
            if (!chargeWeapon && !burst && s.WeaponPhase == WeaponPhase.Firing && !input.Fire && s.BurstRemaining == 0)
            { s.WeaponPhase = WeaponPhase.Ending; s.Firing = false; return false; }
            if (!canShoot) return false; // Preserve the press until human clearance is safe.
            if (s.WeaponPhase == WeaponPhase.BurstCooldown)
            {
                s.ConsumedFire = input.FireSequence;
                if (now + 1e-8 < s.NextShotAt) return false;
                s.WeaponPhase = WeaponPhase.Idle;
                if (input.Fire) Begin(ref s, input, w, now, 0);
            }
            if (s.WeaponPhase == WeaponPhase.Idle)
            {
                if (chargeWeapon && edge && now + 1e-8 < s.NextShotAt) { s.ConsumedFire = input.FireSequence; return false; }
                if (!(chargeWeapon ? edge : input.Fire || edge)) return false;
                s.ConsumedFire = input.FireSequence;
                if (s.Ink + .00001f < InkCost(w)) return false;
                Begin(ref s, input, w, now, emerged ? w.EmergeStartSeconds : w.StartSeconds);
            }
            if (input.Fire) s.ConsumedFire = input.FireSequence;
            if (chargeWeapon && (release || !input.Fire)) s.ChargeReleasePending = true;
            if (s.WeaponPhase == WeaponPhase.Starting)
            {
                if (now + 1e-8 < s.WeaponReadyAt) return false;
                s.WeaponPhase = chargeWeapon ? WeaponPhase.Charging : WeaponPhase.Firing;
                s.ChargeStartedAt = s.WeaponReadyAt;
            }
            if (chargeWeapon)
            {
                s.ChargeElapsedSeconds = Math.Min(AffordableChargeSeconds(w, s.Ink), RocketChargeSeconds(w, now - s.ChargeStartedAt));
                if (!s.ChargeReleasePending) return false;
                if (!Emit(ref s, w, now, ChargeRatio(s, w), out result)) return false;
                // The released shot has no pending rounds, so movement can swim during cooldown.
                s.WeaponPhase = WeaponPhase.Idle; s.ChargeReleasePending = false; s.BurstRemaining = 0; s.ChargeElapsedSeconds = 0;
                return true;
            }
            if (now + 1e-8 < s.NextShotAt) return false;
            if (!Emit(ref s, w, now, 0, out result)) return false;
            s.BurstRemaining = Math.Max(0, s.BurstRemaining - 1);
            if (burst)
            {
                s.BurstReadyAt = now + w.BurstRecoverySeconds;
                if (s.BurstRemaining == 0) { s.NextShotAt = s.BurstReadyAt; s.WeaponPhase = WeaponPhase.BurstCooldown; }
            }
            return true;
        }
        static bool StepSemi(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now,
            bool emerged, bool canShoot, bool edge, out WeaponFireResult result)
        {
            result = default;
            if (!input.Fire || edge) s.SemiHoldStarted = false;
            if (!canShoot) { Cancel(ref s, input); return false; }
            if (s.Ink + .00001f < w.ShotInk)
            {
                bool held = s.SemiHoldStarted && input.Fire;
                Cancel(ref s, input); s.SemiHoldStarted = held;
                return false;
            }
            // A real click commits one shot, including a quick tap released during startup.
            if (edge)
            {
                s.ConsumedFire = input.FireSequence;
                if (s.WeaponPhase != WeaponPhase.Starting && now + w.SemiBufferSeconds + 1e-8 >= s.NextShotAt)
                    Begin(ref s, input, w, now, emerged ? w.EmergeStartSeconds : w.StartSeconds);
            }
            // Held repetitions are created at the cooldown boundary, never buffered.
            if (s.WeaponPhase != WeaponPhase.Starting && input.Fire && now + 1e-8 >= s.NextShotAt)
            {
                if (s.SemiHoldStarted)
                {
                    // Keep the hold's action group and advance its shot index. The server
                    // can reuse one input frame between packets without reusing an action ID.
                    s.WeaponPhase = WeaponPhase.Starting; s.WeaponReadyAt = s.NextShotAt;
                }
                else Begin(ref s, input, w, now, emerged ? w.EmergeStartSeconds : w.StartSeconds);
            }
            if (s.WeaponPhase != WeaponPhase.Starting || now + 1e-8 < s.WeaponReadyAt) return false;
            bool emitted = Emit(ref s, w, now, 0, out result);
            if (emitted) s.SemiHoldStarted = input.Fire;
            s.WeaponPhase = WeaponPhase.Idle; s.BurstRemaining = 0;
            return emitted;
        }
        static void Begin(ref PlayerSnapshot s, PlayerInputFrame input, WeaponRuntimeConfig w, double now, double startupSeconds)
        {
            s.WeaponPhase = WeaponPhase.Starting; s.ConsumedFire = input.FireSequence;
            s.FireBurstSequence = input.Sequence; s.BurstShotIndex = 0;
            s.BurstRemaining = w.BurstCount; s.ChargeElapsedSeconds = 0; s.ChargeReleasePending = false;
            s.WeaponReadyAt = Math.Max(s.NextShotAt, now + startupSeconds);
        }
        internal static bool Emit(ref PlayerSnapshot s, WeaponRuntimeConfig w, double now, float charge, out WeaponFireResult result, bool prepaid = false)
        {
            result = default;
            if (!prepaid && !PrototypeRules.Spend(ref s.Ink, InkCost(w, charge)))
            { s.WeaponPhase = WeaponPhase.Idle; s.BurstRemaining = 0; s.ChargeElapsedSeconds = 0; return false; }
            if (!s.Firing || IsSemi(w) || IsCharge(w)) s.FireStartedAt = now;
            s.Firing = true; s.FireVisualUntil = now + Math.Max(Seconds(6), FireInterval(w));
            s.ShotSequence++; s.BurstShotIndex++; s.LastShotCharge = charge;
            s.LastShotMuzzle = w.MuzzleMode == WeaponMuzzleMode.AlternatingRightLeft ? s.NextMuzzle : (byte)0;
            if (s.LastShotMuzzle == 0) { s.RightShotAt = now; s.RightShotAction = s.ShotActionId; }
            else { s.LeftShotAt = now; s.LeftShotAction = s.ShotActionId; }
            if (w.MuzzleMode == WeaponMuzzleMode.AlternatingRightLeft) s.NextMuzzle = (byte)(1 - s.LastShotMuzzle);
            s.NextShotAt = now + FireInterval(w);
            s.InkRecoverAt = now + w.InkRecoverLockSeconds; s.ProtectedUntil = 0;
            result = new WeaponFireResult(s.HeroId, charge, s.ShotActionId, s.LastShotMuzzle, w.PelletCount);
            return true;
        }
    }
}
