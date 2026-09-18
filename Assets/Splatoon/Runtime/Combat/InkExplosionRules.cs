using Splatoon.Config;
using Splatoon.Prototype;
using Splatoon.Painting;
using UnityEngine;

namespace Splatoon.Combat
{
    public static class InkExplosionRules
    {
        public static float Radius(AmmoRuntimeConfig ammo, bool collision)
            => ammo.ExplosionRadius * (collision ? ammo.CollisionExplosionRadiusRate : 1);
        public static float Damage(AmmoRuntimeConfig ammo, bool collision, float distance)
        {
            float radius = Radius(ammo, collision);
            if (!ammo.HasExplosion || radius <= 0 || distance > radius + .00001f) return 0;
            return ammo.ExplosionDamage * (collision ? ammo.CollisionExplosionDamageRate : 1) *
                (ammo.ExplosionConstantDamage ? 1 : Mathf.Clamp01(1 - distance / radius));
        }

        public static bool ClosestPoint(PrototypePlayer player, Vector3 origin, out Vector3 point)
        {
            point = default;
            var body = player.SwimBody;
            if (body != null && body.FlatHitActive) { point = body.ClosestHitPoint(origin); return true; }
            Collider collider = body != null && body.UsesHitProxy ? body.CapsuleHitVolume : player.GetComponent<CharacterController>();
            if (collider == null || !collider.enabled) return false;
            point = collider.ClosestPoint(origin);
            return true;
        }
    }

    public sealed partial class InkProjectileService
    {
        readonly System.Collections.Generic.HashSet<int> _paintedExplosionSurfaces = new();
        readonly System.Collections.Generic.List<(int surface, Vector3 point)> _explosionPaintSites = new();
        readonly System.Collections.Generic.List<(int surface, Vector3 normal, float plane)> _explosherPaintPlanes = new();

        void ResolveExplosion(InkShot shot, Vector3 position, Vector3 normal, bool collision = false, ulong? directVictim = null, double? at = null)
        {
            var ammo = shot.Configuration?.Ammo;
            if (ammo == null || !ammo.HasExplosion || !_exploded.Add((shot.Round, shot.Id))) return;
            float radius = InkExplosionRules.Radius(ammo, collision);
            bool explosher = WeaponSimulation.IsExplosher(shot.Configuration);
            Vector3 origin = position + (collision ? normal.normalized * (explosher ? shot.Configuration.ExplosherBlastOffset : .01f) : Vector3.zero);
            var match = PrototypeMatch.Current;
            if (match != null)
            {
                // The match roster contains each authoritative player exactly once. Use the
                // current hit shape, not the locomotion capsule or a fixed standing offset.
                foreach (var player in match.Players)
                {
                    if (player == null || player.PlayerId == shot.Shooter || player.Snapshot.Value.Team == shot.Team || player.Snapshot.Value.Health <= 0 ||
                        (ammo.ExcludeDirectHitFromExplosion && directVictim == player.PlayerId) ||
                        !InkExplosionRules.ClosestPoint(player, explosher ? origin : position, out var target)) continue;
                    float damage = InkExplosionRules.Damage(ammo, collision, Vector3.Distance(explosher ? origin : position, target));
                    Vector3 delta = target - origin;
                    if (damage <= 0 || Occluded(origin, delta, shot.Shooter)) continue;
                    float before = player.Snapshot.Value.Health;
                    player.ReceiveDamage(shot.Team, damage, delta.normalized, shot.Shooter);
                    float actual = before - player.Snapshot.Value.Health;
                    if (actual <= 0) continue;
                    Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Position = position, Normal = normal,
                        Hit = true, ContinuesProjectile = explosher, Time = at ?? shot.Born + shot.Configuration.Lifetime,
                        Shooter = shot.Shooter, Victim = player.PlayerId, Damage = actual, Killed = player.Snapshot.Value.Health <= 0,
                        ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex });
                }
            }
            if (ammo.ExplosionPaint && radius > 0)
            {
                BeginFoamGroup();
                try
                {
                _paintedExplosionSurfaces.Clear(); _explosionPaintSites.Clear(); _explosherPaintPlanes.Clear();
                // First-hit rays produce an actual surface normal and never stamp a wall's
                // far side. Downward ray guarantees floor coverage, even between fan samples.
                float searchRadius = explosher ? ExplosherSimulation.PaintRadius(shot, position) : radius;
                if (explosher) PaintExplosionRay(shot, origin, -normal, searchRadius, collision);
                PaintExplosionRay(shot, origin, Vector3.down, searchRadius, collision);
                PaintExplosionRay(shot, origin, Vector3.up, searchRadius, collision);
                for (int i = 0; i < 40; i++)
                {
                    float y = 1 - 2 * (i + .5f) / 40, r = Mathf.Sqrt(1 - y * y), angle = i * 2.39996323f;
                    PaintExplosionRay(shot, origin, new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r), searchRadius, collision);
                }
                }
                finally { EndFoamGroup(shot); }
            }
            Explosions.Add(new InkExplosionEvent { Round = shot.Round, ShotId = shot.Id, ActionId = shot.ActionId, Shooter = shot.Shooter,
                Team = shot.Team, HeroId = shot.HeroId, ConfigurationRevision = shot.ConfigurationRevision, Seed = shot.Seed,
                Position = position, Normal = normal, Collision = collision, Time = at ?? shot.Born + shot.Configuration.Lifetime });
        }

        bool Occluded(Vector3 origin, Vector3 delta, ulong shooter)
            => delta.sqrMagnitude > .000001f && _aim.ClosestCast(origin, delta, Mathf.Max(0, delta.magnitude - .002f), 0, shooter, out _, false);

        void PaintExplosionRay(InkShot shot, Vector3 origin, Vector3 direction, float distance, bool collision)
        {
            if (!_aim.ClosestCast(origin, direction, distance, 0, shot.Shooter, out var hit, false)) return;
            var surface = hit.Collider.GetComponentInParent<Splatoon.Painting.PaintSurface>();
            if (surface == null) return;
            if (WeaponSimulation.IsExplosher(shot.Configuration))
            {
                // One footprint per receiving plane (a surface can contain floor and walls). Project the explosion centre onto
                // that plane instead of adding a full circle at every fan ray endpoint.
                float plane = Vector3.Dot(hit.Point, hit.Normal);
                foreach (var previous in _explosherPaintPlanes)
                    if (previous.surface == surface.SurfaceId && Vector3.Dot(previous.normal, hit.Normal) > .999f && Mathf.Abs(previous.plane - plane) < .025f) return;
                _explosherPaintPlanes.Add((surface.SurfaceId, hit.Normal, plane));
                float planeDistance = Mathf.Abs(Vector3.Dot(origin - hit.Point, hit.Normal));
                Vector3 point = origin - hit.Normal * Vector3.Dot(origin - hit.Point, hit.Normal);
                if (!_aim.ClosestCast(point + hit.Normal * .025f, -hit.Normal, .075f, 0, shot.Shooter, out var projected, false) ||
                    projected.Collider.GetComponentInParent<PaintSurface>() != surface) point = hit.Point;
                float radius = Mathf.Sqrt(Mathf.Max(0, distance * distance - planeDistance * planeDistance));
                if (radius <= .001f) return;
                uint ordinal = InkShapeAtlas.Hash(shot.Seed ^ (uint)surface.SurfaceId);
                ApplyPaint(surface, shot, point, hit.Normal, radius, shot.Configuration, ordinal, true, origin);
                return;
            }
            if (!shot.Configuration.ReferenceRules && !_paintedExplosionSurfaces.Add(surface.SurfaceId)) return;
            var ammo = shot.Configuration.Ammo;
            uint seed = InkShapeAtlas.Hash(shot.Seed ^ (shot.Configuration.ReferenceRules ? (uint)_explosionPaintSites.Count : shot.Id) ^ (uint)surface.SurfaceId * 0x9e3779b9u);
            float r = Mathf.Lerp(ammo.ExplosionPaintRadiusMin, ammo.ExplosionPaintRadiusMax, (seed & 0xffffff) / 16777216f);
            if (collision) r = shot.Configuration.ReferenceRules ? shot.Configuration.CollisionExplosionPaintRadius : r * ammo.CollisionExplosionRadiusRate;
            if (shot.Configuration.ReferenceRules)
            {
                foreach (var site in _explosionPaintSites)
                    if (site.surface == surface.SurfaceId && Vector3.Distance(site.point, hit.Point) < r * .75f) return;
                _explosionPaintSites.Add((surface.SurfaceId, hit.Point));
            }
            // Keep the entire stamp visible. A wide stamp on the floor could otherwise
            // reach behind a nearby wall even though its centre passed the visibility ray.
            ApplyPaint(surface, shot, hit.Point, hit.Normal, r, shot.Configuration, seed, true, origin);
        }

        void PopulatePaintClip(ref PaintStamp stamp, InkShot shot, PaintSurface surface, Vector3 origin, Vector3 point, float extent)
        {
            stamp.ClipEnabled = true;
            InkShapeAtlas.StampBasis(stamp, out var tangent, out var bitangent);
            var start = point + stamp.Normal * .025f;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * (Mathf.PI / 4), radius = extent;
                var direction = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
                if (_aim.ClosestCast(start, direction, radius, .01f, shot.Shooter, out var edge, false) && edge.Collider.GetComponentInParent<PaintSurface>() != surface)
                    radius = Mathf.Max(0, edge.Distance - .025f);
                if (Occluded(origin, start + direction * radius - origin, shot.Shooter))
                {
                    float lo = 0, hi = radius;
                    for (int k = 0; k < 10; k++) { float mid = (lo + hi) * .5f; if (Occluded(origin, start + direction * mid - origin, shot.Shooter)) hi = mid; else lo = mid; }
                    radius = Mathf.Max(0, lo - .025f);
                }
                if (i < 4) stamp.Clip0[i] = radius; else stamp.Clip1[i - 4] = radius;
            }
        }

        float VisiblePaintRadius(InkShot shot, PaintSurface surface, Vector3 origin, Vector3 point, Vector3 normal, float radius)
        {
            var tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var bitangent = Vector3.Cross(normal, tangent);
            // Radial sweeps constrain the footprint at wall boundaries; visibility rays
            // also catch overhangs between the detonation and the proposed outer edge.
            const int samples = 24;
            float result = radius;
            for (int i = 0; i < samples; i++)
            {
                float angle = i * (Mathf.PI * 2 / samples);
                var direction = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
                var start = point + normal * .025f;
                if (_aim.ClosestCast(start, direction, result, .01f, shot.Shooter, out var edge, false) &&
                    edge.Collider.GetComponentInParent<PaintSurface>() != surface)
                    result = Mathf.Min(result, Mathf.Max(0, edge.Distance - .025f));
                var outer = start + direction * result;
                if (!Occluded(origin, outer - origin, shot.Shooter)) continue;
                float low = 0, high = result;
                for (int k = 0; k < 8; k++)
                {
                    float mid = (low + high) * .5f;
                    if (Occluded(origin, start + direction * mid - origin, shot.Shooter)) high = mid;
                    else low = mid;
                }
                result = Mathf.Max(0, low - .025f);
            }
            return result;
        }

        bool TraceBlasterCollision(ref Active active, WeaponRuntimeConfig w, Vector3 from, Vector3 delta, double start, double end)
        {
            var shot = active.Shot;
            float distance = delta.magnitude;
            bool worldOverlap = _aim.Overlap(from, w.CollisionRadius, shot.Shooter, -delta.normalized, out var world, false);
            bool playerOverlap = _aim.Overlap(from, w.BlasterPlayerRadius, shot.Shooter, -delta.normalized, out var player, true, shot.Team);
            if (worldOverlap || playerOverlap)
            {
                var contact = worldOverlap ? world : player;
                if (w.ReferenceRules) PaintReferenceTrail(ref active, from, from, start, start);
                Resolve(shot, contact.Collider, contact.Point, contact.Normal, start - shot.Born, ref active.PaintOrdinal);
                return true;
            }
            bool worldHit = _aim.ClosestCast(from, delta, distance, w.CollisionRadius, shot.Shooter, out world, false);
            bool playerHit = _aim.ClosestCast(from, delta, distance, w.BlasterPlayerRadius, shot.Shooter, out player, true, shot.Team);
            if (!worldHit && !playerHit) return false;
            var hit = worldHit && (!playerHit || world.Distance <= player.Distance) ? world : player;
            if (w.ReferenceRules) PaintReferenceTrail(ref active, from, from + delta * (hit.Distance / Mathf.Max(.0001f, distance)), start, start + (end-start)*hit.Distance/Mathf.Max(.0001f,distance));
            Resolve(shot, hit.Collider, hit.Point, hit.Normal, start - shot.Born + (end - start) * hit.Distance / Mathf.Max(.0001f, distance), ref active.PaintOrdinal);
            return true;
        }
    }
}
