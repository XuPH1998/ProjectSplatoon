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
            var rotation = Quaternion.Euler(state.Pitch, state.Yaw, 0);
            Vector3 pivot = state.Position + (player.Presentation != null ? player.Presentation.CameraPivot : Vector3.up * 1.5f);
            Vector3 camera = PrototypePlayer.CameraPosition(pivot, rotation, player.Presentation);
            Vector3 forward = rotation * Vector3.forward;
            bool hit = ClosestCast(camera, forward, ProbeDistance, 0, player.OwnerClientId, out var aimHit);
            Vector3 muzzle = state.Position + Quaternion.Euler(0, state.Yaw, 0) * player.MuzzleOffset(state.Pitch, muzzleIndex);
            var result = Geometry(camera, forward, muzzle, GameplayConfig.Global.AimCorrectionDistance,
                hit ? aimHit.Distance : float.PositiveInfinity);
            result.Pivot = pivot;
            result.AimHit = aimHit;
            float radius = GameplayConfig.GetHero(state.HeroId).CollisionRadius;
            // Detect an embedded pivot too: casts do not report an origin inside a collider.
            result.MuzzleBlocked = Overlap(pivot, radius, player.OwnerClientId, -forward, out result.MuzzleHit)
                || ClosestCast(pivot, muzzle - pivot, Vector3.Distance(pivot, muzzle), radius, player.OwnerClientId, out result.MuzzleHit)
                || Overlap(muzzle, radius, player.OwnerClientId, -result.InitialDirection, out result.MuzzleHit);
            return result;
        }

        public static TpsAimSolution Geometry(Vector3 camera, Vector3 forward, Vector3 muzzle, float correctionDistance, float hitDistance)
        {
            forward.Normalize();
            var r = new TpsAimSolution { CameraOrigin = camera, Forward = forward, Muzzle = muzzle,
                AimPoint = camera + forward * Mathf.Min(hitDistance, ProbeDistance) };
            bool near = hitDistance <= correctionDistance;
            r.CorrectionPoint = camera + forward * (near ? hitDistance : correctionDistance);
            Vector3 delta = r.CorrectionPoint - muzzle;
            if (delta.sqrMagnitude <= Epsilon * Epsilon || Vector3.Dot(delta, forward) <= Epsilon)
            {
                r.CorrectionPoint = muzzle;
                r.InitialDirection = r.ExitDirection = forward;
                return r;
            }
            r.FirstSegmentLength = delta.magnitude;
            r.InitialDirection = delta / r.FirstSegmentLength;
            r.ExitDirection = near ? r.InitialDirection : forward;
            return r;
        }

        public bool IsObstructed(TpsAimSolution aim, float radius, ulong shooter)
        {
            if (aim.MuzzleBlocked) return true;
            if (ClosestCast(aim.Muzzle, aim.InitialDirection, aim.FirstSegmentLength, radius, shooter, out var first))
                return first.Collider != aim.AimHit.Collider;
            float remaining = Mathf.Max(0, Vector3.Dot(aim.AimPoint - aim.CorrectionPoint, aim.ExitDirection));
            return ClosestCast(aim.CorrectionPoint, aim.ExitDirection, remaining, radius, shooter, out var second)
                && second.Collider != aim.AimHit.Collider;
        }

        public static Vector2 ReticleViewport(Camera camera, Vector3 aimPoint)
        {
            Vector3 p = camera.WorldToViewportPoint(aimPoint);
            return p.z > 0 && float.IsFinite(p.x) && float.IsFinite(p.y) ? new Vector2(p.x, p.y) : new Vector2(.5f, .5f);
        }

        public bool ClosestCast(Vector3 origin, Vector3 direction, float distance, float radius, ulong shooter, out TpsCollision closest)
        {
            closest = default;
            if (distance <= Epsilon || direction.sqrMagnitude <= Epsilon * Epsilon) return false;
            direction.Normalize();
            int count;
            do
            {
                count = radius > 0
                    ? Physics.SphereCastNonAlloc(origin, radius, direction, _hits, distance, ~0, QueryTriggerInteraction.Ignore)
                    : Physics.RaycastNonAlloc(origin, direction, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
                if (count < _hits.Length) break;
                Array.Resize(ref _hits, _hits.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
                if (Valid(_hits[i].collider, shooter) && _hits[i].distance < nearest)
                {
                    var h = _hits[i]; nearest = h.distance;
                    closest = new TpsCollision { Collider = h.collider, Point = h.point, Normal = h.normal, Distance = h.distance };
                }
            return nearest < float.PositiveInfinity;
        }

        public bool Overlap(Vector3 origin, float radius, ulong shooter, Vector3 fallbackNormal, out TpsCollision closest)
        {
            closest = default;
            int count;
            do
            {
                count = Physics.OverlapSphereNonAlloc(origin, radius, _overlaps, ~0, QueryTriggerInteraction.Ignore);
                if (count < _overlaps.Length) break;
                Array.Resize(ref _overlaps, _overlaps.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (!Valid(collider, shooter)) continue;
                Vector3 point = collider.ClosestPoint(origin), delta = origin - point;
                if (delta.sqrMagnitude >= nearest) continue;
                nearest = delta.sqrMagnitude;
                closest = new TpsCollision { Collider = collider, Point = point, Normal = nearest > Epsilon * Epsilon ? delta.normalized : fallbackNormal,
                    Distance = Mathf.Sqrt(nearest) };
            }
            return nearest < float.PositiveInfinity;
        }

        public static bool Valid(Collider collider, ulong shooter)
        {
            var player = collider.GetComponentInParent<PrototypePlayer>();
            return player == null || (player.OwnerClientId != shooter && player.Snapshot.Value.Health > 0);
        }
    }
}
