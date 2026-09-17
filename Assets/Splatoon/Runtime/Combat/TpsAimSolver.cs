using System;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public struct TpsCollision
    {
        public Collider Collider;
        public Vector3 Point, Normal;
        public float Distance;
    }

    public struct TpsAimSolution
    {
        public Vector3 Pivot, CameraOrigin, Forward, Muzzle, AimPoint, CorrectionPoint;
        public Vector3 InitialDirection, ExitDirection;
        public float FirstSegmentLength;
        public TpsCollision AimHit, MuzzleHit;
        public bool MuzzleBlocked;
    }

    /// <summary>Shared logical aim and collision queries. Never reads animated weapon bones or camera shake.</summary>
    public sealed class TpsAimSolver
    {
        public const float ProbeDistance = 100;
        const float Epsilon = .0001f;
        RaycastHit[] _hits = new RaycastHit[64];
        Collider[] _overlaps = new Collider[64];

        public TpsAimSolution Resolve(PrototypePlayer player, PlayerSnapshot state, byte muzzleIndex)
        {
            byte? ignoreTeam = (GameplayConfig.GetWeapon(state.HeroId).MotionMode == ProjectileMotionMode.BouncingBubble || WeaponSimulation.IsBlaster(GameplayConfig.GetWeapon(state.HeroId))) ? state.Team : (byte?)null;
            var rotation = Quaternion.Euler(state.Pitch, state.Yaw, 0);
            Vector3 pivot = state.Position + PrototypePlayer.CameraPivotOffset(state, player.Presentation);
            Vector3 camera = PrototypePlayer.CameraPosition(pivot, rotation, player.Presentation);
            Vector3 forward = rotation * Vector3.forward;
            bool hit = ClosestCast(camera, forward, ProbeDistance, 0, player.PlayerId, out var aimHit, null, ignoreTeam);
            Vector3 muzzle = state.Position + Quaternion.Euler(0, state.Yaw, 0) * player.MuzzleOffset(state.Pitch, muzzleIndex);
            var result = Geometry(camera, forward, muzzle, GameplayConfig.Global.AimCorrectionDistance,
                GameplayConfig.Global.AimFarCorrectionDistance, hit ? aimHit.Distance : float.PositiveInfinity);
            result.Pivot = pivot;
            result.AimHit = aimHit;
            float radius = GameplayConfig.GetWeapon(state.HeroId).CollisionRadius;
            // Detect an embedded pivot too: casts do not report an origin inside a collider.
            result.MuzzleBlocked = Overlap(pivot, radius, player.PlayerId, -forward, out result.MuzzleHit, null, ignoreTeam)
                || ClosestCast(pivot, muzzle - pivot, Vector3.Distance(pivot, muzzle), radius, player.PlayerId, out result.MuzzleHit, null, ignoreTeam)
                || Overlap(muzzle, radius, player.PlayerId, -result.InitialDirection, out result.MuzzleHit, null, ignoreTeam);
            return result;
        }

        public static TpsAimSolution Geometry(Vector3 camera, Vector3 forward, Vector3 muzzle,
            float correctionDistance, float farCorrectionDistance, float hitDistance)
        {
            forward.Normalize();
            bool hit = float.IsFinite(hitDistance) && hitDistance <= ProbeDistance;
            var r = new TpsAimSolution { CameraOrigin = camera, Forward = forward, Muzzle = muzzle,
                AimPoint = camera + forward * (hit ? hitDistance : farCorrectionDistance) };
            r.CorrectionPoint = camera + forward * (hit ? Mathf.Max(hitDistance, correctionDistance) : farCorrectionDistance);
            Vector3 delta = r.CorrectionPoint - muzzle;
            if (delta.sqrMagnitude <= Epsilon * Epsilon || Vector3.Dot(delta, forward) <= Epsilon)
            {
                r.CorrectionPoint = muzzle;
                r.InitialDirection = r.ExitDirection = forward;
                return r;
            }
            r.FirstSegmentLength = delta.magnitude;
            r.InitialDirection = delta / r.FirstSegmentLength;
            r.ExitDirection = r.InitialDirection;
            return r;
        }

        public bool IsObstructed(TpsAimSolution aim, float radius, ulong shooter)
        {
            if (aim.MuzzleBlocked) return true;
            // End at the camera aim depth, not at the near convergence point beyond a close target.
            float forward = Vector3.Dot(aim.InitialDirection, aim.Forward);
            if (forward <= Epsilon) return false;
            float distance = Mathf.Max(0, Vector3.Dot(aim.AimPoint - aim.Muzzle, aim.Forward) / forward);
            return ClosestCast(aim.Muzzle, aim.InitialDirection, distance, radius, shooter, out var hit)
                && hit.Collider != aim.AimHit.Collider;
        }

        public static Vector2 ReticleViewport(Camera camera, Vector3 aimPoint)
        {
            Vector3 p = camera.WorldToViewportPoint(aimPoint);
            return p.z > 0 && float.IsFinite(p.x) && float.IsFinite(p.y) ? new Vector2(p.x, p.y) : new Vector2(.5f, .5f);
        }

        public bool ClosestCast(Vector3 origin, Vector3 direction, float distance, float radius, ulong shooter, out TpsCollision closest, bool? playersOnly = null, byte? ignoreTeam = null)
        {
            closest = default;
            if (distance <= Epsilon || direction.sqrMagnitude <= Epsilon * Epsilon) return false;
            direction.Normalize();
            int count;
            do
            {
                count = radius > 0
                    ? Physics.SphereCastNonAlloc(origin, radius, direction, _hits, distance, ~0, QueryTriggerInteraction.Collide)
                    : Physics.RaycastNonAlloc(origin, direction, _hits, distance, ~0, QueryTriggerInteraction.Collide);
                if (count < _hits.Length) break;
                Array.Resize(ref _hits, _hits.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
                if (Valid(_hits[i].collider, shooter, ignoreTeam) && Matches(_hits[i].collider,playersOnly) && _hits[i].distance < nearest)
                {
                    var h = _hits[i]; nearest = h.distance;
                    closest = new TpsCollision { Collider = h.collider, Point = h.point, Normal = h.normal, Distance = h.distance };
                }
            return nearest < float.PositiveInfinity;
        }

        public bool Overlap(Vector3 origin, float radius, ulong shooter, Vector3 fallbackNormal, out TpsCollision closest, bool? playersOnly = null, byte? ignoreTeam = null)
        {
            closest = default;
            int count;
            do
            {
                count = Physics.OverlapSphereNonAlloc(origin, radius, _overlaps, ~0, QueryTriggerInteraction.Collide);
                if (count < _overlaps.Length) break;
                Array.Resize(ref _overlaps, _overlaps.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (!Valid(collider, shooter, ignoreTeam) || !Matches(collider,playersOnly)) continue;
                var paper = collider.GetComponentInParent<SwimBody>();
                Vector3 point = paper != null && collider == paper.HitVolume && !paper.HitVolume.convex
                    ? paper.ClosestHitPoint(origin) : collider.ClosestPoint(origin);
                Vector3 delta = origin - point;
                if (delta.sqrMagnitude >= nearest) continue;
                nearest = delta.sqrMagnitude;
                closest = new TpsCollision { Collider = collider, Point = point, Normal = nearest > Epsilon * Epsilon ? delta.normalized : fallbackNormal,
                    Distance = Mathf.Sqrt(nearest) };
            }
            // PhysX may omit a sphere fully contained inside a non-convex mesh.
            // Test the same thin prism union analytically for embedded projectiles.
            foreach (var paper in SwimBody.ActiveBodies)
            {
                if (!paper.FlatHitActive || !Valid(paper.HitVolume, shooter, ignoreTeam) || !Matches(paper.HitVolume,playersOnly)) continue;
                Vector3 point = paper.ClosestHitPoint(origin), delta = origin - point;
                float squared = delta.sqrMagnitude;
                if (squared > radius * radius || squared >= nearest) continue;
                nearest = squared;
                closest = new TpsCollision { Collider = paper.HitVolume, Point = point,
                    Normal = squared > Epsilon * Epsilon ? delta.normalized : fallbackNormal, Distance = Mathf.Sqrt(squared) };
            }
            return nearest < float.PositiveInfinity;
        }

        static bool Matches(Collider collider,bool? playersOnly) => !playersOnly.HasValue || (collider.GetComponentInParent<PrototypePlayer>() != null) == playersOnly.Value;
        public static bool Valid(Collider collider, ulong shooter, byte? ignoreTeam = null)
        {
            if (collider == null || !collider.enabled) return false;
            if (collider.isTrigger)
            {
                var volume = collider.GetComponentInParent<SwimBody>();
                if (volume == null || !volume.IsHitVolume(collider)) return false;
            }
            var player = collider.GetComponentInParent<PrototypePlayer>();
            if (player != null && collider is CharacterController && player.SwimBody != null && player.SwimBody.UsesHitProxy) return false;
            return player == null || (player.PlayerId != shooter && player.Snapshot.Value.Health > 0 && (!ignoreTeam.HasValue || player.Snapshot.Value.Team != ignoreTeam.Value));
        }
    }
}
