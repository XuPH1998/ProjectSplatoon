using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public enum WeaponFireMode { Automatic, Burst, Charge, SemiAutomatic }
    public readonly struct WeaponFireResult
    {
        public readonly int HeroId;
        public readonly float Charge;
        public readonly ulong ActionId;
        public readonly byte MuzzleIndex;
        public readonly int PelletCount;
        public WeaponFireResult(int heroId, float charge, ulong actionId, byte muzzleIndex = 0, int pelletCount = 1)
        { HeroId = heroId; Charge = charge; ActionId = actionId; MuzzleIndex = muzzleIndex; PelletCount = pelletCount; }
    }

    public static class WeaponSimulation
    {
        public const double ReferenceRate = 60;
        public static double Seconds(int frames) => frames / ReferenceRate;
        public static double FireInterval(cfg.HeroConfig w) => 1.0 / w.FireRate;
        public static bool IsCharge(cfg.HeroConfig w) => w.FireMode == (int)WeaponFireMode.Charge;
        public static bool IsSemi(cfg.HeroConfig w) => w.FireMode == (int)WeaponFireMode.SemiAutomatic;
        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now)
            => !IsSemi(w) ? WantsFire(s, input) : !input.CancelFire && !s.AttackNeedsRelease &&
                (s.WeaponPhase == WeaponPhase.Starting || (s.Ink + .00001f >= w.ShotInk &&
                (input.Fire || (input.FireSequence != s.ConsumedFire && now + Seconds(w.SemiBufferFrames) + 1e-8 >= s.NextShotAt))));
        public static void ResetPresentation(ref PlayerSnapshot s)
        { s.NextMuzzle = s.LastShotMuzzle = 0; s.RightShotAt = s.LeftShotAt = 0; s.RightShotAction = s.LeftShotAction = 0; }
        public static float Damage(cfg.HeroConfig w, double age, float charge = 1) => IsCharge(w)
            ? charge >= 1 ? w.Damage : Mathf.Lerp(w.ChargeMinDamage, w.ChargePartialMaxDamage, charge)
            : Mathf.Lerp(w.Damage, w.DamageMin, Mathf.InverseLerp((float)Seconds(w.DamageReduceStartFrames), (float)Seconds(w.DamageReduceEndFrames), (float)age));
        public static float InkCost(cfg.HeroConfig w, float charge = 0) => IsCharge(w) ? Mathf.Lerp(w.ChargeMinInk, w.ShotInk, charge) : w.ShotInk;
        public static float Range(cfg.HeroConfig w, float charge) => IsCharge(w) ? Mathf.Lerp(w.ChargeMinRange, w.EffectiveRange, charge) : w.EffectiveRange;
        public static float Speed(cfg.HeroConfig w, float charge) => IsCharge(w) ? Mathf.Lerp(w.ChargeMinSpeed, w.SpeedMin, charge) : w.SpeedMin;
        public static float ChargeRatio(PlayerSnapshot s, cfg.HeroConfig w) => w.ChargeFrames > 0 ? Mathf.Clamp01((float)s.ChargeTicks / w.ChargeFrames) : 0;
        public static float Spread(cfg.HeroConfig w, bool airborne, float charge) => IsCharge(w)
            ? Mathf.Lerp(airborne ? w.ChargeMinJumpSpread : w.ChargeMinSpread, airborne ? w.JumpSpreadDegrees : w.SpreadDegrees, charge)
            : airborne ? w.JumpSpreadDegrees : w.SpreadDegrees;
        public static bool WantsFire(PlayerSnapshot s, PlayerInputFrame input) => !input.CancelFire && !s.AttackNeedsRelease &&
            (input.Fire || input.FireSequence != s.ConsumedFire || s.WeaponPhase == WeaponPhase.Starting || s.WeaponPhase == WeaponPhase.Charging || s.BurstRemaining > 0);

        public static void Cancel(ref PlayerSnapshot s, PlayerInputFrame input, bool requireRelease = false)
        {
            s.NextShotAt = Math.Max(s.NextShotAt, s.BurstReadyAt);
            s.WeaponPhase = WeaponPhase.Idle; s.Firing = false; s.FireVisualUntil = 0; s.BurstRemaining = s.ChargeTicks = 0;
            s.ChargeReleasePending = false; s.ConsumedFire = input.FireSequence; s.ConsumedRelease = input.ReleaseSequence;
            s.AttackNeedsRelease |= requireRelease; s.SemiHoldStarted = false;
        }

        // Kept for existing deterministic simulation callers; runtime also consumes the immutable fire result.
        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now, bool emerged, bool canShoot)
            => Step(ref s, input, w, now, emerged, canShoot, out _);

        public static bool Step(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now, bool emerged, bool canShoot, out WeaponFireResult result)
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
            if (IsSemi(w)) return StepSemi(ref s, input, w, now, emerged, canShoot, edge, out result);
            if (!canShoot) return false; // Preserve the press until human clearance is safe.
            bool chargeWeapon = IsCharge(w), burst = w.FireMode == (int)WeaponFireMode.Burst;
            if (s.WeaponPhase == WeaponPhase.Ending) s.WeaponPhase = WeaponPhase.Idle;
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
                Begin(ref s, input, w, now, emerged ? w.EmergeStartFrames : w.StartFrames);
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
                int affordable = Mathf.Clamp(Mathf.FloorToInt((s.Ink - w.ChargeMinInk + .00001f) / (w.ShotInk - w.ChargeMinInk) * w.ChargeFrames), 0, w.ChargeFrames);
                s.ChargeTicks = Math.Min(affordable, Math.Max(0, (int)Math.Floor((now - s.ChargeStartedAt) * ReferenceRate + 1e-6)));
                if (!s.ChargeReleasePending) return false;
                if (!Emit(ref s, w, now, ChargeRatio(s, w), out result)) return false;
                // The released shot has no pending rounds, so movement can swim during cooldown.
                s.WeaponPhase = WeaponPhase.Idle; s.ChargeReleasePending = false; s.BurstRemaining = s.ChargeTicks = 0;
                return true;
            }
            if (!burst && s.WeaponPhase == WeaponPhase.Firing && !input.Fire && s.BurstRemaining == 0)
            { s.WeaponPhase = WeaponPhase.Ending; s.Firing = false; return false; }
            if (now + 1e-8 < s.NextShotAt) return false;
            if (!Emit(ref s, w, now, 0, out result)) return false;
            s.BurstRemaining = Math.Max(0, s.BurstRemaining - 1);
            if (burst)
            {
                s.BurstReadyAt = now + Seconds(w.BurstRecoveryFrames);
                if (s.BurstRemaining == 0) { s.NextShotAt = s.BurstReadyAt; s.WeaponPhase = WeaponPhase.BurstCooldown; }
            }
            return true;
        }
        static bool StepSemi(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now,
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
                if (s.WeaponPhase != WeaponPhase.Starting && now + Seconds(w.SemiBufferFrames) + 1e-8 >= s.NextShotAt)
                    Begin(ref s, input, w, now, emerged ? w.EmergeStartFrames : w.StartFrames);
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
                else Begin(ref s, input, w, now, emerged ? w.EmergeStartFrames : w.StartFrames);
            }
            if (s.WeaponPhase != WeaponPhase.Starting || now + 1e-8 < s.WeaponReadyAt) return false;
            bool emitted = Emit(ref s, w, now, 0, out result);
            if (emitted) s.SemiHoldStarted = input.Fire;
            s.WeaponPhase = WeaponPhase.Idle; s.BurstRemaining = 0;
            return emitted;
        }
        static void Begin(ref PlayerSnapshot s, PlayerInputFrame input, cfg.HeroConfig w, double now, int startup)
        {
            s.WeaponPhase = WeaponPhase.Starting; s.ConsumedFire = input.FireSequence;
            s.FireBurstSequence = input.Sequence; s.BurstShotIndex = 0;
            s.BurstRemaining = w.BurstCount; s.ChargeTicks = 0; s.ChargeReleasePending = false;
            s.WeaponReadyAt = Math.Max(s.NextShotAt, now + Seconds(startup));
        }
        static bool Emit(ref PlayerSnapshot s, cfg.HeroConfig w, double now, float charge, out WeaponFireResult result)
        {
            result = default;
            if (!PrototypeRules.Spend(ref s.Ink, InkCost(w, charge)))
            { s.WeaponPhase = WeaponPhase.Idle; s.BurstRemaining = s.ChargeTicks = 0; return false; }
            if (!s.Firing || IsSemi(w) || IsCharge(w)) s.FireStartedAt = now;
            s.Firing = true; s.FireVisualUntil = now + Math.Max(Seconds(6), FireInterval(w));
            s.ShotSequence++; s.BurstShotIndex++; s.LastShotCharge = charge;
            s.LastShotMuzzle = w.MuzzleMode == 1 ? s.NextMuzzle : (byte)0;
            if (s.LastShotMuzzle == 0) { s.RightShotAt = now; s.RightShotAction = s.ShotActionId; }
            else { s.LeftShotAt = now; s.LeftShotAction = s.ShotActionId; }
            if (w.MuzzleMode == 1) s.NextMuzzle = (byte)(1 - s.LastShotMuzzle);
            s.NextShotAt = now + FireInterval(w);
            s.InkRecoverAt = now + Seconds(w.InkRecoverLockFrames); s.ProtectedUntil = 0;
            result = new WeaponFireResult(w.Id, charge, s.ShotActionId, s.LastShotMuzzle, w.PelletCount);
            return true;
        }
    }
}
