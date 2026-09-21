using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Painting;

namespace Splatoon.Combat
{
    public struct InkShot : INetworkSerializable
    {
        public uint Id, Round, Seed, ShotSequence;
        public ulong Shooter;
        public ulong ActionId;
        public uint ReleaseSequence => (uint)(ActionId >> 32);
        public uint RoundIndex => (uint)ActionId;
        public uint Lifecycle, HeroRevision;
        public byte MuzzleIndex, PelletIndex;
        public int HeroId;
        public float Charge;
        public float SpreadHorizontal, SpreadVertical;
        public uint ConfigurationRevision;
        [NonSerialized] public WeaponRuntimeConfig Configuration;
        public byte Team;
        public double Born;
        public byte VolleyIndex;
        public Vector3 Origin, Velocity;
        public float FirstSegmentLength, GravityStartAge;
        public Vector3 PostCorrectionVelocity;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Seed); s.SerializeValue(ref Shooter);
            s.SerializeValue(ref HeroId); s.SerializeValue(ref Team); s.SerializeValue(ref Born); s.SerializeValue(ref Origin); s.SerializeValue(ref Velocity);
            s.SerializeValue(ref Charge); s.SerializeValue(ref SpreadHorizontal); s.SerializeValue(ref SpreadVertical); s.SerializeValue(ref ConfigurationRevision);
            s.SerializeValue(ref ShotSequence);
            s.SerializeValue(ref ActionId);
            s.SerializeValue(ref Lifecycle); s.SerializeValue(ref HeroRevision);
            s.SerializeValue(ref MuzzleIndex); s.SerializeValue(ref PelletIndex);
            s.SerializeValue(ref VolleyIndex);
            s.SerializeValue(ref FirstSegmentLength); s.SerializeValue(ref PostCorrectionVelocity); s.SerializeValue(ref GravityStartAge);
        }
    }
    public struct InkImpact : INetworkSerializable
    {
        public uint Id, Round;
        public double Time;
        public byte Team;
        public Vector3 Position, Normal;
        public bool Hit;
        // Damage-only events must not retire a piercing projectile on clients.
        public bool ContinuesProjectile;
        public ulong Shooter, Victim;
        public float Damage;
        public bool Killed;
        public ulong ActionId;
        public uint Lifecycle, HeroRevision;
        public byte PelletIndex;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Id); s.SerializeValue(ref Round); s.SerializeValue(ref Time); s.SerializeValue(ref Team); s.SerializeValue(ref Position); s.SerializeValue(ref Normal); s.SerializeValue(ref Hit); s.SerializeValue(ref ContinuesProjectile);
          s.SerializeValue(ref Shooter); s.SerializeValue(ref Victim); s.SerializeValue(ref Damage); s.SerializeValue(ref Killed);
          s.SerializeValue(ref ActionId); s.SerializeValue(ref Lifecycle); s.SerializeValue(ref HeroRevision); s.SerializeValue(ref PelletIndex); }
    }
    public struct InkExplosionEvent : INetworkSerializable
    {
        public uint Round, ShotId, ConfigurationRevision, Seed;
        public double Time;
        public bool Collision;
        public ulong ActionId, Shooter;
        public byte Team;
        public int HeroId;
        public Vector3 Position, Normal;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Time); s.SerializeValue(ref Collision); s.SerializeValue(ref Round); s.SerializeValue(ref ShotId); s.SerializeValue(ref ActionId); s.SerializeValue(ref Shooter); s.SerializeValue(ref Team); s.SerializeValue(ref HeroId); s.SerializeValue(ref ConfigurationRevision); s.SerializeValue(ref Seed); s.SerializeValue(ref Position); s.SerializeValue(ref Normal); }
    }
    public static class InkBallistics
    {
        public static Vector3 Position(Vector3 origin, Vector3 velocity, float gravity, double age)
        { float t = (float)age; return origin + velocity * t + Vector3.down * (.5f * gravity * t * t); }
        public static float TravelTime(WeaponRuntimeConfig w, double age)
        {
            if (ReferenceBallistics.Enabled(w)) return ReferenceBallistics.Position(Vector3.zero, Vector3.forward * w.SpeedMin, w, age).z / w.SpeedMin;
            if (DualiesNormalSimulation.Enabled(w)) return DualiesBallistics.Distance(w, w.SpeedMin, age) / w.SpeedMin;
            float t = Mathf.Max(0, (float)age), straight = (float)w.StraightSeconds;
            if (t <= straight) return t;
            float brake = Mathf.Max(.0001f, (float)w.BrakeSeconds);
            float b = Mathf.Min(t - straight, brake);
            return straight + b - .5f * (1 - w.BrakeSpeedMultiplier) * b * b / brake + Mathf.Max(0, t - straight - brake) * w.BrakeSpeedMultiplier;
        }
        public static Vector3 Position(Vector3 origin, Vector3 velocity, WeaponRuntimeConfig w, double age)
        {
            if (ReferenceBallistics.Enabled(w)) return ReferenceBallistics.Position(origin, velocity, w, age);
            if (DualiesNormalSimulation.Enabled(w)) return DualiesBallistics.Position(origin, velocity, w, age);
            if (w.MotionMode == ProjectileMotionMode.TimedBlaster) return BlasterBallistics.Position(origin, velocity, w, age);
            float fall = Mathf.Max(0, (float)(age - w.StraightSeconds));
            return origin + velocity * TravelTime(w, age) + Vector3.down * (.5f * w.ProjectileGravity * fall * fall);
        }
        public static double AgeAtDistance(WeaponRuntimeConfig w, float speed, float distance)
        {
            if (ReferenceBallistics.Enabled(w)) return ReferenceBallistics.AgeAtDistance(w, speed, distance);
            if (DualiesNormalSimulation.Enabled(w)) return DualiesBallistics.AgeAtDistance(w, speed, distance);
            double travel = Math.Max(0, distance) / Math.Max(.0001, speed);
            double straight = w.StraightSeconds;
            if (travel <= straight) return travel;
            double brake = Math.Max(.0001, w.BrakeSeconds);
            double multiplier = w.BrakeSpeedMultiplier, extra = travel - straight;
            double brakingTravel = brake * (1 + multiplier) * .5;
            if (extra >= brakingTravel) return straight + brake + (extra - brakingTravel) / multiplier;
            // Stable inverse of b - (1 - multiplier) * b^2 / (2 * brake).
            return straight + 2 * extra / (1 + Math.Sqrt(Math.Max(0, 1 - 2 * (1 - multiplier) * extra / brake)));
        }
        public static double CorrectionAge(InkShot shot, WeaponRuntimeConfig w) =>
            AgeAtDistance(w, shot.Velocity.magnitude, shot.FirstSegmentLength);
        public static void ApplyCorrection(ref InkShot shot, TpsAimSolution aim, WeaponRuntimeConfig w)
        {
            // Preserve the sampled muzzle-to-target direction and spread for the entire flight.
            // Keep the serialized trajectory layout; passing the target never rotates a projectile.
            shot.PostCorrectionVelocity = shot.Velocity;
            shot.FirstSegmentLength = aim.FirstSegmentLength;
            // Convergence distance does not change the weapon's gravity clock.
            shot.GravityStartAge = (float)w.StraightSeconds;
        }
        public static Vector3 Position(InkShot shot, WeaponRuntimeConfig w, double age)
        {
            if (ReferenceBallistics.Enabled(w)) return ReferenceBallistics.Position(shot.Origin, shot.Velocity, w, age);
            if (DualiesNormalSimulation.Enabled(w)) return DualiesBallistics.Position(shot.Origin, shot.Velocity, w, age);
            if (w.MotionMode == ProjectileMotionMode.TimedBlaster) return BlasterBallistics.Position(shot.Origin, shot.Velocity, w, age);
            if (shot.PostCorrectionVelocity.sqrMagnitude == 0) return Position(shot.Origin, shot.Velocity, w, age);
            float distance = shot.Velocity.magnitude * TravelTime(w, age);
            float first = Mathf.Min(distance, shot.FirstSegmentLength);
            float fall = Mathf.Max(0, (float)age - shot.GravityStartAge);
            return shot.Origin + shot.Velocity.normalized * first
                + shot.PostCorrectionVelocity.normalized * (distance - first)
                + Vector3.down * (.5f * w.ProjectileGravity * fall * fall);
        }
        public static Vector3 Velocity(InkShot shot, WeaponRuntimeConfig w, double age)
        {
            if (ReferenceBallistics.Enabled(w)) return ReferenceBallistics.Velocity(shot.Velocity, w, age);
            if (DualiesNormalSimulation.Enabled(w)) return DualiesBallistics.Velocity(shot.Velocity, w, age);
            if (w.MotionMode == ProjectileMotionMode.TimedBlaster) return BlasterBallistics.Velocity(shot.Velocity, w, age);
            float straight = (float)w.StraightSeconds;
            float brake = Mathf.Max(.0001f, (float)w.BrakeSeconds);
            float multiplier = Mathf.Lerp(1, w.BrakeSpeedMultiplier, Mathf.Clamp01(((float)age - straight) / brake));
            bool corrected = shot.PostCorrectionVelocity.sqrMagnitude > 0;
            Vector3 velocity = corrected && age >= CorrectionAge(shot, w) ? shot.PostCorrectionVelocity : shot.Velocity;
            float fall = Mathf.Max(0, (float)age - (corrected ? shot.GravityStartAge : straight));
            return velocity * multiplier + Vector3.down * (w.ProjectileGravity * fall);
        }
        public static float Random01(ref uint seed)
        { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / 16777216f; }
        public static Vector3 LaunchVelocity(Vector3 direction, WeaponRuntimeConfig w, ref uint seed, float spread = -1)
        {
            float radius = Mathf.Sqrt(Random01(ref seed)) * Mathf.Tan((spread < 0 ? w.SpreadDegrees : spread) * Mathf.Deg2Rad);
            float angle = Random01(ref seed) * Mathf.PI * 2;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized;
            return Quaternion.LookRotation(direction) * local * Mathf.Lerp(w.SpeedMin, w.SpeedMax, Random01(ref seed));
        }
        public static Vector3 SplatlingVelocity(Vector3 direction, WeaponRuntimeConfig w, float charge, float spread, float vertical, ref uint seed, float shotBias = -1)
        {
            float bias = Mathf.Log(shotBias > 0 ? shotBias : w.SplatlingSpreadBias) / Mathf.Log(.5f);
            float x = Centered(ref seed, bias), y = Centered(ref seed, w.ReferenceRules ? Mathf.Log(w.ReferencePitchBias) / Mathf.Log(.5f) : bias);
            Vector3 local = new(Mathf.Tan(x * spread * Mathf.Deg2Rad), Mathf.Tan(y * vertical * Mathf.Deg2Rad), 1);
            float center = Mathf.Lerp(w.ChargeMinSpeed, (w.SpeedMin+w.SpeedMax)*.5f, SplatlingSimulation.RangeCharge(w, charge));
            float jitter = Centered(ref seed, Mathf.Log(w.SplatlingSpeedBias)/Mathf.Log(.5f)) * (w.SpeedMax-w.SpeedMin)*.5f;
            return Quaternion.LookRotation(direction) * local.normalized * Mathf.Max(.01f,center+jitter);
        }
        static float Centered(ref uint seed, float gamma)
        { float x=Random01(ref seed)*2-1; return Mathf.Sign(x)*Mathf.Pow(Mathf.Abs(x),gamma); }
        public static Vector3 PelletVelocity(Vector3 direction, WeaponRuntimeConfig w, float spread, int index, uint groupSeed)
        {
            if (WeaponSimulation.IsFloatingBubble(w))
            {
                float yaw = w.PelletCount > 1 ? Mathf.Lerp(-spread, spread, index / (float)(w.PelletCount - 1)) : 0;
                uint pitchSeed = InkShapeAtlas.Hash(groupSeed ^ ((uint)index + 1) * 277803737u);
                float pitch = (Random01(ref pitchSeed) * 2 - 1) * w.FloatingPitchSpreadDegrees;
                return Quaternion.LookRotation(direction) * Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * w.SpeedMin;
            }
            // Equal-area disk samples; rotate the complete pattern, never cluster eight independent random samples.
            float angle = index * 2.39996323f + Random01(ref groupSeed) * Mathf.PI * 2;
            float radius = Mathf.Sqrt((index + .5f) / w.PelletCount) * Mathf.Tan(spread * Mathf.Deg2Rad);
            return Quaternion.LookRotation(direction) * new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 1).normalized * w.SpeedMin;
        }
    }
    /// <summary>Server-only continuous collision simulation. Presentation never reports hits.</summary>
    public sealed partial class InkProjectileService
    {
        private struct Active { public InkShot Shot; public double SimulatedUntil, CorrectionAt; public Vector3 LastTrail; public uint TrailSeed, PaintOrdinal; public int TrailCount, TrailBudget; public float TrailTravelled, NextTrailDistance; public HashSet<(ulong player, uint life)> Pierced; public InkBounce Bubble; public float BubbleTrailDistance; }
        private readonly List<Active> _active = new(256);
        private readonly TpsAimSolver _aim = new();
        private readonly InkShapeSelector _shapes = new();
        struct LegacyFoot { public PaintSurface Surface; public InkShot Shot; public Vector3 Point, Normal, Origin; public float Radius; public uint Ordinal; }
        readonly List<LegacyFoot> _legacyFeet = new();
        bool _clockReady;
        double _clockOrigin;
        long _clockFrame;
        private uint _id;
        public readonly List<InkShot> Spawned = new(16);
        public readonly List<InkImpact> Impacts = new(32);
        public readonly List<InkExplosionEvent> Explosions = new(16);
        readonly HashSet<(uint round, uint shot)> _exploded = new();
        public int ActiveCount => _active.Count;
        public int PendingCount => _active.Count + _paintDrops.Count + _wallDrops.Count + _legacyFeet.Count;
#if UNITY_EDITOR
        // Opt-in observations of the real simulation; never sent over the network.
        public Action<InkShot, double, Vector3> TraceObserved;
        public Action<PaintStamp> PaintObserved;
        // Diagnostic submission time on the internal 60 Hz clock, not the caller's render frame.
        public double ObservedPaintTime { get; private set; }
        public void SpawnForMeasurement(InkShot shot)
        {
            if (GameplayConfig.GetWeapon(shot.HeroId) == null || shot.Velocity.sqrMagnitude <= 0)
                throw new ArgumentException("测量墨弹必须指定有效武器与初速");
            shot.Configuration ??= GameplayConfig.GetWeapon(shot.HeroId);
            Spawned.Add(shot);
            BeginFlight(shot, shot.Origin, shot.Velocity.normalized);
        }
#endif
        public void Spawn(PrototypePlayer player, PlayerSnapshot state, double born, uint round)
        {
#if UNITY_EDITOR
            ObservedPaintTime = born;
#endif
            var w = GameplayConfig.GetWeapon(state.HeroId);
            var aim = _aim.Resolve(player, state, state.LastShotMuzzle, state.LastShotCharge);
            uint groupSeed = unchecked((uint)state.ShotActionId ^ (uint)(state.ShotActionId >> 32) * 747796405u ^ round * 2891336453u ^ (uint)player.PlayerId ^ state.HeroRevision);
            groupSeed ^= (uint)(player.PlayerId >> 32) ^ (state.Revision > 1 ? InkShapeAtlas.Hash(state.Revision - 1) : 0);
            if (groupSeed == 0) groupSeed = 1;
            for (byte pellet = 0; pellet < w.PelletCount; pellet++)
            {
            ++_id;
            uint localId = state.ShotSequence > 0 ? (state.ShotSequence - 1) * (uint)w.PelletCount + pellet + 1 : _id;
            uint seed = unchecked(localId * 747796405u + round * 2891336453u + (uint)player.PlayerId + 1u);
            seed ^= (uint)(player.PlayerId >> 32) ^ (state.Revision > 1 ? InkShapeAtlas.Hash(state.Revision - 1) : 0);
            if (seed == 0) seed = 1;
            var shot = new InkShot { Id = _id, Round = round, Seed = seed, ShotSequence = state.ShotSequence, Shooter = player.PlayerId, HeroId = state.HeroId, Team = state.Team, Born = born, Origin = aim.MuzzleBlocked ? aim.Pivot : aim.Muzzle };
            shot.VolleyIndex = (byte)Mathf.Max(0, state.BurstShotIndex - 1);
            if (w.ReferenceRules) { seed = InkShapeAtlas.Hash(groupSeed ^ state.ShotSequence * 747796405u ^ (uint)pellet * 277803737u ^ state.Revision); if (seed == 0) seed = 1; shot.Seed = seed; }
            shot.ActionId = state.ShotActionId;
            shot.Lifecycle = state.Revision; shot.HeroRevision = state.HeroRevision;
            shot.MuzzleIndex = state.LastShotMuzzle; shot.PelletIndex = pellet;
            shot.Charge = state.LastShotCharge;
            shot.Configuration = w; shot.ConfigurationRevision = WeaponConfigService.Current.Revision(state.HeroId); shot.SpreadHorizontal = state.LastShotSpread; shot.SpreadVertical = state.LastShotVerticalSpread;
            float spread = shot.SpreadHorizontal;
            if (w.AngularSpread)
                shot.Velocity = WeaponLaunch.Velocity(aim, w, shot.Charge, state.PlanarVelocity, state.Yaw,
                    spread, shot.SpreadVertical, state.LastShotSpreadBias, shot.Seed, true);
            else
            {
            shot.Velocity = w.PelletCount > 1 || WeaponSimulation.IsFloatingBubble(w) ? InkBallistics.PelletVelocity(aim.InitialDirection, w, spread, pellet, groupSeed)
                : InkBallistics.LaunchVelocity(aim.InitialDirection, w, ref seed, spread);
            if (WeaponSimulation.IsCharge(w)) shot.Velocity = shot.Velocity.normalized * WeaponSimulation.Speed(w, shot.Charge);
            if (WeaponSimulation.IsSplatling(w)) shot.Velocity = InkBallistics.SplatlingVelocity(aim.InitialDirection,w,shot.Charge,spread,shot.SpreadVertical,ref seed, w.ReferenceRules ? state.LastShotSpreadBias : -1);
            if (DualiesNormalSimulation.Enabled(w))
            {
                uint dualiesSeed = groupSeed;
                shot.Seed = dualiesSeed;
                shot.Velocity = DualiesNormalSimulation.LaunchVelocity(aim.InitialDirection, w, spread, state.LastShotSpreadBias, ref dualiesSeed);
            }
            if (ReferenceSpreadSimulation.Enabled(w) && !WeaponSimulation.IsSplatling(w))
                shot.Velocity = DualiesNormalSimulation.LaunchVelocity(aim.InitialDirection, w, spread, state.LastShotSpreadBias > 0 ? state.LastShotSpreadBias : w.ReferenceBiasMin, ref seed);
            if (w.ReferenceRules && WeaponSimulation.IsBubble(w)) shot.Velocity = ReferenceBallistics.BubbleLaunch(aim.InitialDirection, w, shot.VolleyIndex, state.Grounded);
            if (WeaponSimulation.IsExplosher(w)) shot.Velocity = ExplosherSimulation.Launch(aim.InitialDirection, w, state.Grounded, state.PlanarVelocity + Vector3.up * state.VerticalSpeed, state.Yaw);
            if (w.InheritsMovement) shot.Velocity = ShooterDetailSimulation.InheritMovement(shot.Velocity, state.PlanarVelocity, state.Yaw, w);
            }
            if (WeaponSimulation.IsFloatingBubble(w) && aim.MuzzleBlocked) shot.Origin = aim.MuzzleHit.Center;
            InkBallistics.ApplyCorrection(ref shot, aim, w);
            Spawned.Add(shot);
            if (w.DetailedPaint && pellet == 0 && ShooterDetailSimulation.Foot(shot.RoundIndex, shot.ShotSequence, w))
                QueuePaintDrop(shot, state.Position + Vector3.up * .1f, born, w.ReferenceFootRadius,
                    WeaponLaunch.FootOrdinal, aim.InitialDirection, w.ReferenceFootDepth);
            if (w.MotionMode == ProjectileMotionMode.BouncingBubble) BeginFlight(shot, shot.Origin, aim.InitialDirection);
            else if (aim.MuzzleBlocked && WeaponSimulation.IsFloatingBubble(w)) ResolveFloatingBubble(shot, aim.MuzzleHit, 0);
            else if (aim.MuzzleBlocked) { uint ordinal = 0; Resolve(shot, aim.MuzzleHit.Collider, aim.MuzzleHit.Point, aim.MuzzleHit.Normal, 0, ref ordinal); }
            else BeginFlight(shot, aim.Muzzle, aim.InitialDirection, (WeaponSimulation.IsSplatling(w) || DualiesNormalSimulation.Enabled(w)) ? state.Position + Vector3.up * .1f : (Vector3?)null);
            }
        }
        private void BeginFlight(InkShot shot, Vector3 muzzle, Vector3 forward, Vector3? foot = null)
        {
            // A newly accepted shot can predate the most recent service call (late input
            // or several emissions submitted separately). Older active entries keep their
            // own cursors, so revisiting driver ticks only catches up the new work.
            if (_clockReady) _clockFrame = Math.Min(_clockFrame, (long)Math.Floor((shot.Born - _clockOrigin) * 60 + 1e-8));
            uint trailSeed = shot.Seed ^ 0x9E3779B9u, ordinal = 0;
            if (trailSeed == 0) trailSeed = 1;
            var config = shot.Configuration;
            if (config.DetailedPaint) { /* Foot paint is scheduled at accepted emission, including an immediately blocked shot. */ }
            else if (config.ShooterDetails)
            {
                ShooterDetailSimulation.Schedule(shot.RoundIndex, config, out _, out _, out bool feet);
                if (feet) QueuePaintDrop(shot, foot ?? muzzle - forward * .6f, shot.Born, config.ReferenceFootRadius, ++ordinal, forward, config.ReferenceFootDepth);
            }
            else if (config.ReferenceRules)
            {
                if (shot.PelletIndex == 0 && (!WeaponSimulation.IsBubble(config) || shot.VolleyIndex == 0) && config.ReferenceFootRadius > 0 && (shot.ShotSequence - 1) % config.ReferenceFootEvery == 0)
                    QueuePaintDrop(shot, foot ?? muzzle - forward * .6f, shot.Born, config.ReferenceFootRadius, ++ordinal, forward, WeaponSimulation.IsExplosher(config) ? config.ExplosherFootDepth : config.ReferenceFootDepth);
            }
            else if (DualiesNormalSimulation.Enabled(config))
            {
                if ((shot.ShotSequence - 1) % config.DualiesFootEvery == 0)
                    PaintTrail(foot ?? muzzle - forward * .6f, shot, config, ref trailSeed, ref ordinal, config.DualiesFootRadius, true);
            }
            else if (!WeaponSimulation.IsFloatingBubble(config) && !WeaponSimulation.IsBlaster(config) && shot.PelletIndex == 0 && (!WeaponSimulation.IsSplatling(config) || (shot.ShotSequence-1)%config.SplatlingFootEvery==0))
                PaintTrail(foot ?? (muzzle - forward * .6f), shot, config, ref trailSeed, ref ordinal, WeaponSimulation.IsSplatling(config) ? config.SplatlingFootRadius : -1, true);
            float budget = config.ReferenceRules && (!WeaponSimulation.IsBubble(config) || shot.VolleyIndex == 0) ? config.ReferenceTrailBudget : 0;
            uint scheduleSeed = InkShapeAtlas.Hash(shot.Seed ^ 0xA511E9B3u);
            int count = (int)budget + (InkBallistics.Random01(ref scheduleSeed) < budget - (int)budget ? 1 : 0);
            float first = config.ReferenceTrailStart + (config.ReferenceTrailRandomPhase ? InkBallistics.Random01(ref scheduleSeed) * config.TrailSpacing : 0);
            if (config.UsesDetailedPaint) ShooterDetailSimulation.Schedule(shot.RoundIndex, config, out count, out first, out _);
            if (WeaponSimulation.IsExplosher(config)) first = Mathf.Lerp(config.ReferenceTrailStart, config.ExplosherTrailPhaseMax * config.TrailSpacing, InkBallistics.Random01(ref scheduleSeed));
            _active.Add(new Active { Pierced = WeaponSimulation.IsExplosher(config) ? new HashSet<(ulong, uint)>() : null, TrailBudget = count, NextTrailDistance = first, Shot = shot, SimulatedUntil = shot.Born, LastTrail = muzzle, TrailSeed = trailSeed, PaintOrdinal = ordinal,
                CorrectionAt = config.ReferenceRules || DualiesNormalSimulation.Enabled(config) || WeaponSimulation.IsBlaster(config) ? double.PositiveInfinity : shot.Born + InkBallistics.CorrectionAge(shot, config),
                Bubble = InkBounce.Initial(shot) });
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, 0, muzzle);
#endif
        }
        public void Simulate(double until)
        {
            if (!_clockReady)
            {
                if (_active.Count == 0 && _paintDrops.Count == 0 && _wallDrops.Count == 0) return;
                _clockOrigin = until;
                foreach (var a in _active) _clockOrigin = Math.Min(_clockOrigin, a.Shot.Born);
                foreach (var d in _paintDrops) _clockOrigin = Math.Min(_clockOrigin, d.Born);
                _clockFrame = 0; _clockReady = true;
            }
            while (_clockOrigin + _clockFrame / 60.0 <= until + 1e-8)
            {
                double time = _clockOrigin + _clockFrame++ / 60.0;
#if UNITY_EDITOR
                ObservedPaintTime = time;
#endif
                // Preserve the original 60 Hz order: feet first, then active shots.
                for (int i=0;i<_legacyFeet.Count;)
                {
                    var foot=_legacyFeet[i];
                    if (foot.Shot.Born>time+1e-8) { i++; continue; }
                    if (foot.Surface!=null) ApplyPaint(foot.Surface,foot.Shot,foot.Point,foot.Normal,foot.Radius,foot.Shot.Configuration,foot.Ordinal,false,foot.Origin);
                    _legacyFeet.RemoveAt(i);
                }
                SimulateStep(time);
                if (PendingCount==0) { _clockReady=false; break; }
            }
        }
        void SimulateStep(double until)
        {
            double defaultStep = 1.0 / GameplayConfig.Global.ProjectileStepRate;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i]; var w = a.Shot.Configuration;
                double step = w.ReferenceRules || WeaponSimulation.IsBlaster(w) || DualiesNormalSimulation.Enabled(w) ? 1.0 / 60 : defaultStep;
                if (w.MotionMode == ProjectileMotionMode.BouncingBubble)
                {
                    if (SimulateBubble(ref a, until, step)) _active.RemoveAt(i);
                    else _active[i] = a;
                    continue;
                }
                double end = Math.Min(until, a.Shot.Born + w.Lifetime); bool hit = false;
                while (a.SimulatedUntil < end - 1e-8)
                {
                    // External callers may run at 30/60/144 Hz. Only integrate complete
                    // configured projectile steps, so trail positions and random draws agree.
                    double next = Math.Min(a.SimulatedUntil + step, a.Shot.Born + w.Lifetime);
                    if (next > end + 1e-8) break;
                    double from = a.SimulatedUntil;
                    // Keep the fixed grid intact; split only the collision path at the bend.
                    if (a.CorrectionAt > from && a.CorrectionAt < next)
                    {
                        hit = TraceSegment(ref a, w, from, a.CorrectionAt);
                        from = a.CorrectionAt;
                    }
                    if (!hit) hit = TraceSegment(ref a, w, from, next);
                    if (hit) break;
                    a.SimulatedUntil = next;
                }
                if (hit || end >= a.Shot.Born + w.Lifetime - 1e-8)
                {
                    if (!hit)
                    {
                        var position = InkBallistics.Position(a.Shot, w, w.Lifetime);
                        if (!WeaponSimulation.IsExplosher(w)) ResolveExplosion(a.Shot, position, Vector3.up);
                        Impacts.Add(new InkImpact { Id = a.Shot.Id, Round = a.Shot.Round, Team = a.Shot.Team, Position = position, Hit = false,
                            Time = a.Shot.Born + w.Lifetime,
                            ActionId = a.Shot.ActionId, Lifecycle = a.Shot.Lifecycle, HeroRevision = a.Shot.HeroRevision, PelletIndex = a.Shot.PelletIndex, Shooter = a.Shot.Shooter });
                    }
                    _active.RemoveAt(i);
                }
                else _active[i] = a;
            }
            SimulatePaintDrops(until);
        }
        private bool TraceSegment(ref Active active, WeaponRuntimeConfig weapon, double start, double end)
        {
            var shot = active.Shot;
            Vector3 from = InkBallistics.Position(shot, weapon, start - shot.Born);
            Vector3 to = InkBallistics.Position(shot, weapon, end - shot.Born);
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (WeaponSimulation.IsFloatingBubble(weapon)) return TraceFloatingBubble(shot, from, delta, start, end);
            if (WeaponSimulation.IsExplosher(weapon)) return TraceExplosher(ref active, from, to, start, end);
            bool blaster = WeaponSimulation.IsBlaster(weapon);
            if (blaster && TraceBlasterCollision(ref active, weapon, from, delta, start, end)) return true;
            bool separateRadius = !blaster && (weapon.ReferenceRules || WeaponSimulation.IsSplatling(weapon) || DualiesNormalSimulation.Enabled(weapon));
            float playerRadius = weapon.ReferenceRules ? weapon.ReferencePlayerRadius : DualiesNormalSimulation.Enabled(weapon) ? weapon.DualiesPlayerRadius : weapon.SplatlingPlayerRadius;
            if (separateRadius)
            {
                bool worldOverlap = _aim.Overlap(from,weapon.CollisionRadius,shot.Shooter,-delta.normalized,out var world,false);
                bool playerOverlap = _aim.Overlap(from,playerRadius,shot.Shooter,-delta.normalized,out var player,true);
                if (worldOverlap || playerOverlap)
                {
                    var contact=worldOverlap?world:player;
                    if (weapon.ReferenceRules) PaintReferenceTrail(ref active, from, from, start, start);
                    else PaintDualiesTrail(ref active, weapon, start - shot.Born);
                    Resolve(shot,contact.Collider,contact.Point,contact.Normal,start-shot.Born,ref active.PaintOrdinal); return true;
                }
                bool worldHit=_aim.ClosestCast(from,delta,distance,weapon.CollisionRadius,shot.Shooter,out world,false);
                bool playerHit=_aim.ClosestCast(from,delta,distance,playerRadius,shot.Shooter,out player,true);
                if (worldHit || playerHit)
                {
                    var contact=worldHit && (!playerHit || world.Distance <= player.Distance) ? world : player;
                    if (weapon.ReferenceRules) PaintReferenceTrail(ref active, from, from + delta * (contact.Distance / Mathf.Max(.0001f, distance)), start, start + (end-start)*contact.Distance/Mathf.Max(.0001f,distance));
                    else PaintDualiesTrail(ref active, weapon, start-shot.Born+(end-start)*contact.Distance/Mathf.Max(.0001f,distance));
                    Resolve(shot,contact.Collider,contact.Point,contact.Normal,start-shot.Born+(end-start)*contact.Distance/Mathf.Max(.0001f,distance),ref active.PaintOrdinal); return true;
                }
            }
            else if (!blaster && _aim.Overlap(from, weapon.CollisionRadius, shot.Shooter, -InkBallistics.Velocity(shot, weapon, start - shot.Born).normalized, out var overlap))
            {
                Resolve(shot, overlap.Collider, overlap.Point, overlap.Normal, start - shot.Born, ref active.PaintOrdinal);
                return true;
            }
            if (!blaster && !separateRadius && _aim.ClosestCast(from, delta, distance, weapon.CollisionRadius, shot.Shooter, out var hit))
            {
                Resolve(shot, hit.Collider, hit.Point, hit.Normal, start - shot.Born + (end - start) * hit.Distance / Mathf.Max(.0001f, distance), ref active.PaintOrdinal);
                return true;
            }
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, end - shot.Born, to);
#endif
            if (weapon.ReferenceRules) { PaintReferenceTrail(ref active, from, to, start, end); return false; }
            if (blaster)
            {
                while (active.TrailCount < weapon.BlasterTrailCount && Vector3.Distance(active.LastTrail, to) + .00001f >= weapon.TrailSpacing)
                {
                    active.LastTrail = Vector3.MoveTowards(active.LastTrail, to, weapon.TrailSpacing);
                    PaintTrail(active.LastTrail, shot, weapon, ref active.TrailSeed, ref active.PaintOrdinal);
                    active.TrailCount++;
                }
                return false;
            }
            if (DualiesNormalSimulation.Enabled(weapon))
                PaintDualiesTrail(ref active, weapon, end - shot.Born);
            else if (Vector3.Distance(active.LastTrail, to) >= weapon.TrailSpacing && (!WeaponSimulation.IsSplatling(weapon) || active.TrailCount < weapon.SplatlingTrailCount))
            { PaintTrail(to, shot, weapon, ref active.TrailSeed, ref active.PaintOrdinal); active.LastTrail = to; active.TrailCount++; }
            return false;
        }
        private void PaintDualiesTrail(ref Active active, WeaponRuntimeConfig weapon, double untilAge)
        {
            if (!DualiesNormalSimulation.Enabled(weapon)) return;
            var shot = active.Shot;
            float reached = DualiesBallistics.Distance(weapon, shot.Velocity.magnitude, untilAge);
            while (active.TrailCount < weapon.DualiesTrailCount)
            {
                float nextDrop = weapon.DualiesTrailStartDistance + active.TrailCount * weapon.TrailSpacing;
                if (reached + .00001f < nextDrop) break;
                double age = DualiesBallistics.AgeAtDistance(weapon, shot.Velocity.magnitude, nextDrop);
                PaintTrail(InkBallistics.Position(shot, weapon, age), shot, weapon, ref active.TrailSeed, ref active.PaintOrdinal);
                active.TrailCount++;
            }
        }
        private void PaintTrail(Vector3 position, InkShot shot, WeaponRuntimeConfig w, ref uint seed, ref uint ordinal, float radius = -1, bool foot = false)
        {
            bool enabled = PrototypeMatch.Current != null;
#if UNITY_EDITOR
            enabled |= PaintObserved != null;
#endif
            if (!enabled || !Physics.Raycast(position, Vector3.down, out var h, w.TrailMaxDrop, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore)) return;
            var surface = h.collider.GetComponentInParent<PaintSurface>();
            if (surface != null)
            {
                float resolved = radius > 0 ? radius : Mathf.Lerp(w.TrailRadiusMin, w.TrailRadiusMax, InkBallistics.Random01(ref seed));
                ordinal++;
                if (foot) _legacyFeet.Add(new LegacyFoot { Surface=surface,Shot=shot,Point=h.point,Normal=h.normal,Origin=position,Radius=resolved,Ordinal=ordinal });
                else ApplyPaint(surface, shot, h.point, h.normal, resolved, w, ordinal, false, position);
            }
        }
        private void ApplyPaint(PaintSurface surface, InkShot shot, Vector3 point, Vector3 normal, float radius, WeaponRuntimeConfig w, uint ordinal, bool impact, Vector3? sightFrom = null, Vector3? paintDirection = null, float depthScale = 1)
        {
            if (WeaponSimulation.IsBlaster(w) && !w.ReferenceRules)
            {
                radius = VisiblePaintRadius(shot, surface, sightFrom ?? point + normal * .025f, point, normal, radius);
                if (radius <= .01f) return;
            }
            // A sustained magazine can have several bullets in flight. Give each
            // stamp an immutable shape so batching their arrivals cannot alter paint.
            uint entropy = InkShapeAtlas.Hash(shot.Seed ^ InkShapeAtlas.Hash(shot.Id) ^ InkShapeAtlas.Hash(ordinal) ^ (impact ? 0xb5297a4du : 0x68e31da4u));
            if (w.ReferenceRules || DualiesNormalSimulation.Enabled(w))
                entropy = InkShapeAtlas.Hash(shot.Seed ^ InkShapeAtlas.Hash(ordinal) ^ (impact ? 0xb5297a4du : 0x68e31da4u));
            uint shapeSeed = (w.ReferenceRules || WeaponSimulation.IsSplatling(w) || WeaponSimulation.IsBlaster(w) || DualiesNormalSimulation.Enabled(w)) ? InkShapeAtlas.Pack((int)(entropy % InkShapeAtlas.Count), entropy)
                : _shapes.Select(shot.Shooter, shot.Round, shot.Seed, shot.ShotSequence > 0 ? (shot.ShotSequence - 1) * (uint)w.PelletCount + shot.PelletIndex + 1 : shot.Id, shot.PelletIndex, ordinal, impact);
            var clip = new PaintStamp { Normal = normal, Direction = paintDirection ?? Vector3.zero };
            if (WeaponSimulation.IsFloatingBubble(w) || WeaponSimulation.IsExplosher(w) || (w.ReferenceRules && WeaponSimulation.IsBlaster(w) && sightFrom.HasValue))
                PopulatePaintClip(ref clip, shot, surface, sightFrom ?? point + normal * .025f, point,
                    WeaponSimulation.IsFloatingBubble(w) ? radius : radius * Mathf.Max(1, depthScale) * 1.414214f);
#if UNITY_EDITOR
            PaintObserved?.Invoke(new PaintStamp { Round = shot.Round, SurfaceId = surface.SurfaceId, Team = shot.Team,
                Position = point, Normal = normal, Radius = radius, Hardness = w.PaintHardness, Strength = w.PaintStrength, ShapeSeed = shapeSeed, Direction = paintDirection ?? Vector3.zero, DepthScale = depthScale, ClipEnabled = clip.ClipEnabled, Clip0 = clip.Clip0, Clip1 = clip.Clip1 });
#endif
            if (PrototypeMatch.Current != null)
                PrototypeMatch.Current.Paint(surface, point, normal, radius, shot.Team, w.PaintHardness, w.PaintStrength, shapeSeed, paintDirection, depthScale, clip.ClipEnabled, clip.Clip0, clip.Clip1);
        }
        private void Resolve(InkShot shot, Collider collider, Vector3 point, Vector3 normal, double age, ref uint ordinal, Vector3? incomingVelocity = null)
        {
#if UNITY_EDITOR
            TraceObserved?.Invoke(shot, age, point);
#endif
            var w = shot.Configuration ?? GameplayConfig.GetWeapon(shot.HeroId);
            var victim = collider.GetComponentInParent<PrototypePlayer>();
            var subTarget = collider.GetComponent<SubWeaponTarget>();
            if (subTarget != null) subTarget.Hit(shot, WeaponSimulation.Damage(w, age, shot.Charge));
            float actualDamage = 0; bool killed = false;
            if (victim != null)
            {
                float before = victim.Snapshot.Value.Health;
                if (w.ReferenceRules || DualiesNormalSimulation.Enabled(w) || w.MotionMode == ProjectileMotionMode.BouncingBubble || WeaponSimulation.IsBlaster(w) || shot.Velocity.magnitude * InkBallistics.TravelTime(w, age) <= WeaponSimulation.Range(w, shot.Charge))
                    victim.ReceiveDamage(shot.Team, WeaponSimulation.Damage(w, age, shot.Charge), incomingVelocity ?? InkBallistics.Velocity(shot, w, age), shot.Shooter);
                actualDamage = before - victim.Snapshot.Value.Health; killed = actualDamage > 0 && victim.Snapshot.Value.Health <= 0;
            }
            else
            {
                var surface = collider.GetComponentInParent<PaintSurface>();
                if (surface != null)
                {
                    uint seed = shot.Seed;
                    if (w.ReferenceRules) PaintReferenceImpact(surface, shot, point, normal, incomingVelocity ?? InkBallistics.Velocity(shot, w, age), age, ref ordinal);
                    else ApplyPaint(surface, shot, point, normal, Mathf.Lerp(w.PaintRadiusMin, w.PaintRadiusMax, InkBallistics.Random01(ref seed)), w, ++ordinal, true);
                }
            }
            ResolveExplosion(shot, point, normal, true, victim != null ? victim.PlayerId : (ulong?)null, shot.Born + age, directObject:subTarget!=null?subTarget.Id:(uint?)null);
            Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Position = point, Normal = normal, Hit = true, Time = shot.Born + age,
                ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex,
                Shooter = shot.Shooter, Victim = victim != null ? victim.PlayerId : 0, Damage = actualDamage, Killed = killed });
        }
        public InkShot[] LiveShots() => _active.ConvertAll(a => a.Shot).ToArray();
        public void Clear() { _active.Clear(); Spawned.Clear(); Bounces.Clear(); Impacts.Clear(); Explosions.Clear(); _exploded.Clear(); _shapes.Clear(); _paintDrops.Clear(); _wallDrops.Clear(); _legacyFeet.Clear(); _clockReady=false; }
    }
}
