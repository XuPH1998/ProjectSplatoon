using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public struct SubLaunchSolution
    {
        public Vector3 Position, Velocity, Normal;
        public Collider Surface;
        public SubWeaponFailure Failure;
        public bool Valid => Failure == SubWeaponFailure.None;
    }

    public enum SubPreviewEnd : byte { Lifetime, Impact, Deployment, Explosion, Tracking, InvalidGround }
    public struct SubPreviewResult
    {
        public Vector3 Position, Normal;
        public Quaternion Rotation;
        public SubPreviewEnd End;
        public bool Valid, Uncertain;
        public double Time;
    }

    public enum SubMotionContact : byte { None, Bounce, Deploy, Activate, Explode, Droplet }

    /// <summary>Shared fixed-step geometry. This layer never paints, spends ink or deals damage.</summary>
    public static class SubWeaponMotion
    {
        public const float SurfaceOffset = .04f;
        public static Quaternion Rotation(SubEntityState s) => s.Type == SubWeaponType.SuctionBomb ||
            s.Type == SubWeaponType.Sprinkler || s.Type == SubWeaponType.InkMine
            ? Quaternion.FromToRotation(Vector3.up, s.Normal.sqrMagnitude > .001f ? s.Normal : Vector3.up)
            : Quaternion.Euler(0, s.Yaw, 0);

        public static void Stick(SubWeaponService.Entity e, Collider surface, Vector3 point, Vector3 normal, double now)
        {
            e.State.Position = point + normal * SurfaceOffset; e.State.Normal = normal; e.State.Velocity = Vector3.zero;
            e.State.Phase = SubEntityPhase.Active; e.State.Changed = now;
            e.Attachment = surface != null ? surface.transform : null; e.Attached = e.Attachment != null;
            if (e.Attached) { e.LocalPosition = e.Attachment.InverseTransformPoint(e.State.Position); e.LocalNormal = e.Attachment.InverseTransformDirection(normal); }
        }

        public static SubMotionContact Advance(SubWeaponService.Entity e, TpsAimSolver aim, double now, float dt, out TpsCollision hit)
        {
            var c = e.Config;
            var delta = SubWeaponService.FlightStep(ref e.State.Velocity, c.Flight, dt,
                c.Type == SubWeaponType.CurlingBomb && e.State.Phase == SubEntityPhase.Grounded || e.State.Phase == SubEntityPhase.Seeking);
            if (!SubWeaponService.FlightCollision(aim, e.State.Position, delta, Mathf.Max(.03f, c.Flight.radius), e.State.Owner, e.State.Team, out hit))
            {
                e.State.Position += delta;
                if (e.State.Phase == SubEntityPhase.Grounded && !Physics.Raycast(e.State.Position, Vector3.down, c.Flight.radius + .08f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
                { e.State.Phase = SubEntityPhase.Flying; e.State.Changed = now; }
                return SubMotionContact.None;
            }
            e.State.Position = hit.Point + hit.Normal * (c.Flight.radius + .005f); e.State.Normal = hit.Normal;
            var player = hit.Collider.GetComponentInParent<PrototypePlayer>();
            var target = hit.Collider.GetComponent<SubWeaponTarget>();
            bool ground = hit.Normal.y >= .65f && player == null && target == null;
            if (target != null && target.Type == SubWeaponType.SplashWall) return SubMotionContact.Explode;
            if (e.State.Phase == SubEntityPhase.Droplet)
            { e.State.Velocity = Vector3.zero; e.Fuse = Math.Min(e.Fuse, now + c.Torpedo.dropletFuse); return SubMotionContact.Droplet; }
            if (c.Type == SubWeaponType.BurstBomb || c.Type == SubWeaponType.Torpedo && e.State.Phase == SubEntityPhase.Seeking) return SubMotionContact.Explode;
            if (c.Type == SubWeaponType.PointSensor || c.Type == SubWeaponType.ToxicMist)
            {
                e.State.Phase = SubEntityPhase.Active; e.State.Velocity = Vector3.zero; e.State.Changed = now;
                e.State.Expires = now + (c.Type == SubWeaponType.PointSensor ? c.Sensor.duration : c.Mist.duration);
                return SubMotionContact.Activate;
            }
            if (player == null && target == null && (c.Type == SubWeaponType.SuctionBomb || c.Type == SubWeaponType.Sprinkler || c.Type == SubWeaponType.SplashWall && ground))
            {
                Stick(e, hit.Collider, hit.Point, hit.Normal, now);
                if (c.Type == SubWeaponType.SuctionBomb) { e.Fuse = now + c.Suction.fuse; e.State.Expires = e.Fuse + 1; }
                if (c.Type == SubWeaponType.Sprinkler) { e.State.Expires = double.MaxValue; e.NextAction = now; }
                if (c.Type == SubWeaponType.SplashWall) { e.State.Expires = now + c.Wall.expand + c.Wall.lifetime; e.NextAction = now + c.Wall.expand; }
                return SubMotionContact.Deploy;
            }
            e.State.Velocity = c.Type == SubWeaponType.CurlingBomb
                ? ground ? Vector3.ProjectOnPlane(e.State.Velocity, hit.Normal) : Vector3.Reflect(e.State.Velocity, hit.Normal)
                : Vector3.Reflect(e.State.Velocity, hit.Normal) * c.Flight.bounce;
            if (ground)
            {
                if (e.State.Phase != SubEntityPhase.Grounded) e.State.Changed = now;
                e.State.Phase = SubEntityPhase.Grounded;
                if (c.Type == SubWeaponType.FizzyBomb && e.Fuse == 0) e.Fuse = now + c.Fizzy.fuse;
                if (c.Type == SubWeaponType.Torpedo && e.Fuse == 0) e.Fuse = now + c.Torpedo.rollFuse;
                if (c.Type == SubWeaponType.Autobomb && e.NextAction == 0) e.NextAction = now + c.Autobomb.searchDelay;
            }
            return SubMotionContact.Bounce;
        }

        public static bool FuseDue(SubWeaponService.Entity e, double now, float dt)
        {
            if (e.State.Type == SubWeaponType.SplatBomb && e.State.Phase == SubEntityPhase.Grounded) e.GroundAge += dt;
            return e.State.Type == SubWeaponType.SplatBomb && e.GroundAge + 1e-8 >= e.Config.Splat.groundFuse || e.Fuse > 0 && now + 1e-8 >= e.Fuse;
        }
        public static bool NextFizzyBurst(SubWeaponService.Entity e, double now)
        {
            if (e.Config.Type != SubWeaponType.FizzyBomb || ++e.State.Explosions >= 1 + Mathf.RoundToInt(e.State.Charge * 2)) return false;
            e.Fuse = now + e.Config.Fizzy.interval; e.Attached = false; e.State.Phase = SubEntityPhase.Flying; e.State.Changed = now;
            e.State.Velocity = Quaternion.Euler(0, e.State.Yaw, 0) * Vector3.forward * e.Config.Fizzy.hopForward + Vector3.up * e.Config.Fizzy.hopSpeed;
            e.State.Position += Vector3.up * .05f; return true;
        }

        public static SubWeaponService.Entity Create(SubLaunchSolution launch, SubWeaponRuntimeConfig c, SubEntityState identity, double now)
        {
            identity.Position = launch.Position; identity.Velocity = launch.Velocity; identity.Normal = launch.Normal;
            identity.Type = c.Type; identity.Born = identity.Changed = identity.SampledAt = now; identity.Expires = now + Math.Max(1, c.Flight.lifetime);
            var e = new SubWeaponService.Entity(identity, c);
            if (c.Type == SubWeaponType.CurlingBomb) e.Fuse = now + Mathf.Lerp(c.Curling.fuse.x, c.Curling.fuse.y, identity.Charge);
            if (c.Type == SubWeaponType.AngleShooter) e.State.Expires = now + c.Angle.range / c.Angle.speed + .1;
            if (c.Type == SubWeaponType.InkMine && launch.Valid)
            { Stick(e, launch.Surface, launch.Position, launch.Normal, now); e.State.Expires = double.MaxValue; e.NextAction = now + c.Mine.arm; }
            return e;
        }

        // A target-dependent preview deliberately stops at the reference landing, not a promised future target.
        public static SubPreviewResult Preview(SubLaunchSolution launch, SubWeaponRuntimeConfig c, SubEntityState identity, List<Vector3> points, TpsAimSolver aim)
        {
            points.Clear(); var e = Create(launch, c, identity, 0); points.Add(e.State.Position);
            var end = !launch.Valid ? SubPreviewEnd.InvalidGround : c.Type == SubWeaponType.InkMine ? SubPreviewEnd.Deployment : SubPreviewEnd.Lifetime;
            double now = 0; float dt = 1f / GameplayConfig.Global.SimulationRate;
            if (launch.Valid && c.Type == SubWeaponType.AngleShooter)
            {
                while (e.Travelled < c.Angle.range && e.State.Explosions <= c.Angle.reflections)
                {
                    float remaining = c.Angle.range - e.Travelled;
                    bool hit = LineStep(e, aim, remaining, out var contact); points.Add(e.State.Position);
                    if (!hit) break;
                    if (contact.Collider.GetComponentInParent<PrototypePlayer>() != null || contact.Collider.GetComponent<SubWeaponTarget>() != null) { end = SubPreviewEnd.Impact; break; }
                    ReflectLine(e, contact);
                }
            }
            else if (launch.Valid && c.Type != SubWeaponType.InkMine)
            {
                double until = Math.Max(1, c.Flight.lifetime);
                for (int step = 0; step < Math.Ceiling(until / dt); step++)
                {
                    now += dt;
                    var contact = Advance(e, aim, now, dt, out _); points.Add(e.State.Position);
                    if (contact == SubMotionContact.Deploy || contact == SubMotionContact.Activate) { end = SubPreviewEnd.Deployment; break; }
                    if (contact == SubMotionContact.Explode) { end = SubPreviewEnd.Impact; break; }
                    if (c.Type == SubWeaponType.Autobomb && e.State.Phase == SubEntityPhase.Grounded) { end = SubPreviewEnd.Tracking; break; }
                    if (FuseDue(e, now, dt))
                    { if (NextFizzyBurst(e, now)) continue; end = SubPreviewEnd.Explosion; break; }
                }
            }
            return new SubPreviewResult { Position = e.State.Position, Normal = e.State.Normal, Rotation = Rotation(e.State), End = end,
                Valid = launch.Valid, Uncertain = c.Type == SubWeaponType.Autobomb || c.Type == SubWeaponType.Torpedo, Time = now };
        }
        public static bool LineStep(SubWeaponService.Entity e, TpsAimSolver aim, float remaining, out TpsCollision hit)
        {
            var direction = e.State.Velocity.normalized;
            bool collided = SubWeaponService.FlightCollision(aim, e.State.Position, direction * remaining, .04f, e.State.Owner, e.State.Team, out hit);
            float distance = collided ? Mathf.Min(remaining, hit.Distance) : remaining;
            e.State.Position += direction * distance; e.Travelled += distance; return collided;
        }
        public static void ReflectLine(SubWeaponService.Entity e, TpsCollision h)
        { e.State.Explosions++; e.State.Velocity = Vector3.Reflect(e.State.Velocity, h.Normal); e.State.Position = h.Point + h.Normal * .05f; }
    }
}
