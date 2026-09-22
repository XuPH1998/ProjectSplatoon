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
        public static Vector3 Point(Collider collider, Vector3 origin, float radius)
        {
            if (collider is not MeshCollider mesh || mesh.convex) return collider.ClosestPoint(origin);
            if (sphere == null)
            {
                var root = new GameObject("Floating bubble overlap query") { hideFlags = HideFlags.HideAndDontSave };
                sphere = root.AddComponent<SphereCollider>(); sphere.isTrigger = true;
            }
            sphere.radius = radius;
            return Physics.ComputePenetration(sphere, origin, Quaternion.identity, collider, collider.transform.position,
                collider.transform.rotation, out var normal, out float depth) ? origin + normal * (depth - radius) : origin;
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
            TpsAimSolver.UnembedFloatingContact(ref contact, shot.Configuration.CollisionRadius);
            var victim = contact.Collider.GetComponentInParent<PrototypePlayer>();
            var subTarget = contact.Collider.GetComponent<SubWeaponTarget>();
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
