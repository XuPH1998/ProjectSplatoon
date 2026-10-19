using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    // PhysX ClosestPoint does not support non-convex map meshes. A reusable
    // trigger computes their overlap contact; gameplay queries ignore this trigger.
    internal static class FloatingBubbleOverlap
    {
        static SphereCollider sphere;
#if UNITY_EDITOR
        static FloatingBubbleOverlap()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Clear;
            UnityEditor.EditorApplication.quitting += Clear;
        }
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Clear()
        {
            if (sphere != null) Object.DestroyImmediate(sphere.gameObject);
            sphere = null;
        }
        // An opt-in subweapon query recovers a sphere intersecting the back of an
        // actual concave mesh face. Bounds choose ray origins only; they never
        // become collision geometry, and points farther than the radius fail.
        public static bool TryMeshSurfaceContact(MeshCollider mesh, Vector3 origin, float radius,
            Vector3 preferredNormal, out Vector3 point, out Vector3 normal)
        {
            float reach = mesh.bounds.size.magnitude + radius + 1;
            float nearest = float.PositiveInfinity;
            Vector3 foundPoint = default, foundNormal = default;
            for (int probe = 0; probe < 7; probe++)
            {
                Vector3 direction = probe == 0 ? -preferredNormal : probe == 1 ? Vector3.right :
                    probe == 2 ? Vector3.left : probe == 3 ? Vector3.up : probe == 4 ? Vector3.down :
                    probe == 5 ? Vector3.forward : Vector3.back;
                if (direction.sqrMagnitude < .000001f) continue;
                direction.Normalize();
                if (!mesh.Raycast(new Ray(origin + direction * reach, -direction), out var hit, reach + radius)) continue;
                float squared = (origin - hit.point).sqrMagnitude;
                if (squared > radius * radius + .00000001f || squared >= nearest) continue;
                nearest = squared; foundPoint = hit.point; foundNormal = hit.normal;
            }
            point = foundPoint; normal = foundNormal;
            return nearest < float.PositiveInfinity;
        }
        public static bool TryContact(Collider collider, Vector3 origin, float radius, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (sphere == null)
            {
                var root = new GameObject("Floating bubble overlap query") { hideFlags = HideFlags.HideAndDontSave };
                // Keep a live PhysX shape, as in RescueBubbleMotor. TpsAimSolver.Valid
                // excludes this non-player trigger from its own overlap candidates.
                sphere = root.AddComponent<SphereCollider>(); sphere.isTrigger = true;
            }
            sphere.radius = Mathf.Max(.0001f, radius);
            if (!Physics.ComputePenetration(sphere, origin, Quaternion.identity, collider, collider.transform.position,
                collider.transform.rotation, out normal, out float depth)) return false;
            point = origin + normal * (depth - sphere.radius);
            // ComputePenetration tests front-faced mesh surfaces, not the bounds or
            // the solid volume of a closed concave mesh. Voxels provide that volume.
            return (point - origin).sqrMagnitude <= radius * radius + .00000001f;
        }
    }

    public sealed partial class InkProjectileService
    {
        bool TraceFloatingBubble(InkShot shot, Vector3 from, Vector3 delta, double start, double end)
        {
            float radius = shot.Configuration.CollisionRadius;
            if (_aim.Overlap(from, radius, shot.Shooter, -shot.Velocity.normalized, out var contact, null, shot.Team, floatingMesh: true))
            { ResolveFloatingBubble(shot, contact, start - shot.Born); return true; }
            float distance = delta.magnitude;
            if (!_aim.ClosestCast(from, delta, distance, radius, shot.Shooter, out contact, null, shot.Team))
            {
#if UNITY_EDITOR
                TraceObserved?.Invoke(shot, end - shot.Born, from + delta);
#endif
                return false;
            }
            double age = start - shot.Born + (end - start) * contact.Distance / Mathf.Max(.000001f, distance);
            ResolveFloatingBubble(shot, contact, age);
            return true;
        }

        void ResolveFloatingBubble(InkShot shot, TpsCollision contact, double age)
        {
            if (contact.BlockQuery.IsKnown && !contact.BlockQuery.TryResolveCurrent(out _)) return;
            TpsAimSolver.UnembedFloatingContact(ref contact, shot.Configuration.CollisionRadius);
            var victim = contact.Collider?.GetComponentInParent<PrototypePlayer>();
            contact.Collider?.GetComponent<SpecialWeaponTarget>()?.Hit(shot,WeaponSimulation.Damage(shot.Configuration,age));
            var subTarget = contact.Collider?.GetComponent<SubWeaponTarget>();
            if (subTarget != null) subTarget.Hit(shot, WeaponSimulation.Damage(shot.Configuration, age));
            float actual = 0;
            bool killed = false;
            if (victim != null)
            {
                float before = victim.Snapshot.Value.Health;
                victim.ReceiveDamage(shot.Team, WeaponSimulation.Damage(shot.Configuration, age),
                    InkBallistics.Velocity(shot, shot.Configuration, age), shot.Shooter);
                actual = before - victim.Snapshot.Value.Health;
                killed = actual > 0 && victim.Snapshot.Value.IsDead;
            }
            // Sweeps return a surface contact and the moving sphere's centre separately.
            // An embedded spawn uses the nearest visible surface for occlusion rays only.
            Vector3 visibilityOrigin = contact.Center;
            if (victim == null && (contact.Point - contact.Center).sqrMagnitude < 1e-8f)
                visibilityOrigin = contact.Point + contact.Normal * .01f;
            ResolveExplosion(shot, contact.Center, contact.Normal, true, victim != null ? victim.PlayerId : (ulong?)null,
                shot.Born + age, visibilityOrigin,subTarget!=null?subTarget.Id:(uint?)null);
            Impacts.Add(new InkImpact { Id = shot.Id, Round = shot.Round, Team = shot.Team, Time = shot.Born + age,
                Position = contact.Center, Normal = contact.Normal, Hit = true, Shooter = shot.Shooter,
                Victim = victim != null ? victim.PlayerId : 0, Damage = actual, Killed = killed,
                ActionId = shot.ActionId, Lifecycle = shot.Lifecycle, HeroRevision = shot.HeroRevision, PelletIndex = shot.PelletIndex });
        }
    }
}
