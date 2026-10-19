using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class PlayerMotorSimulation
    {
        static readonly Unity.Profiling.ProfilerMarker FixedMotorCpu = new("Splatoon.Player.Motor");
        static readonly Unity.Profiling.ProfilerMarker FixedMoveCpu = new("Splatoon.Fixed.CapsuleMove");
        static bool FixedGridActive => BlockTerrain.Current != null && BlockTerrain.Current.FixedGridMode;

        static bool FixedShapeFits(HeroBodyShape shape, Vector3 feet)
        {
            float radius = Mathf.Max(.05f, shape.Radius - .025f);
            return !FixedGridActive || !BlockFixedGridQueries.OverlapCapsule(BlockTerrain.Current.FixedBlocks, feet + shape.Center,
                Mathf.Max(0, shape.Height * .5f - radius), radius, Vector3.up, out _, floorContactTolerance: .001f);
        }

        static bool CastFixedMotor(BlockFixedGridWorld world, Vector3 center, float halfSegment, float radius,
            Vector3 direction, float distance, out BlockFixedGridHit hit)
        {
            bool blocked = BlockFixedGridQueries.CastCapsule(world, center, halfSegment, radius,
                direction, distance, out hit);
            if (!blocked || hit.Distance > .000001f) return blocked;
            // Only an initial pose already accepted by the unchanged endpoint rule
            // may separate from its shallow contact. Deep overlap stays blocked.
            float endpointRadius = radius - .001f;
            if (BlockFixedGridQueries.OverlapCapsule(world, center, halfSegment, endpointRadius,
                Vector3.up, out _, .00001f)) return true;
            float acceptedRadius = Mathf.Max(0, endpointRadius - .00001f);
            return BlockFixedGridQueries.CastCapsule(world, center, halfSegment, radius,
                direction, distance, out hit, radius - acceptedRadius);
        }

        CollisionFlags MoveWithFixedBlocks(Vector3 displacement)
        {
            using var timing = FixedMoveCpu.Auto();
            var terrain = BlockTerrain.Current;
            var world = terrain.FixedBlocks;
            CollisionFlags flags = CollisionFlags.None;
            Vector3 remaining = displacement;
            Vector3 lastLegal = _root.position;
            bool disableMapStep = false;
            float radius = Mathf.Max(.01f, _controller.radius - .001f);
            float halfSegment = Mathf.Max(0, _controller.height * .5f - _controller.radius);
            // Every retry validates both domains. Native map sliding cannot introduce
            // an unchecked data-block penetration, and fixed blocks never receive force.
            for (int iteration = 0; iteration < 3 && remaining.sqrMagnitude > 1e-12f; iteration++)
            {
                Vector3 before = _root.position, center = before + _controller.center;
                float distance = remaining.magnitude;
                Vector3 direction = remaining / distance;
                bool blocked = CastFixedMotor(world, center, halfSegment, radius,
                    direction, distance, out var hit);
                Vector3 allowed = blocked ? direction * Mathf.Max(0, hit.Distance - .001f) : remaining;
                float oldStep = _controller.stepOffset;
                // The native controller does not see this data floor. Re-enabling
                // its step in the projected horizontal retry can snap it down to
                // the original map, rejecting the entire stride as a crossing.
                // Keep the same setting for this complete combined movement.
                disableMapStep |= blocked;
                if (disableMapStep) _controller.stepOffset = 0;
                CollisionFlags attemptFlags;
                try { attemptFlags = _controller.Move(allowed); }
                finally { _controller.stepOffset = oldStep; }
                Vector3 actual = _root.position - before;
                bool crossed = actual.sqrMagnitude > 1e-12f &&
                    CastFixedMotor(world, center, halfSegment, radius, actual, actual.magnitude,
                        out var nativeHit) && nativeHit.Distance < actual.magnitude - .001f;
                if (crossed || terrain.FixedCapsuleOverlaps(_root.position + _controller.center, halfSegment, radius - .001f))
                {
                    bool enabled = _controller.enabled; _controller.enabled = false;
                    _root.position = lastLegal; _controller.enabled = enabled;
                    // A purely horizontal request can encounter the native step
                    // down before a data cast blocks it. Retry that exact request
                    // with native stepping disabled; do not discard valid travel.
                    if (!disableMapStep) { disableMapStep = true; continue; }
                    break;
                }
                // Rejected native attempts were rolled back. Only accepted motion
                // may publish contact flags to grounding, ceiling and wall consumers.
                flags |= attemptFlags;
                lastLegal = _root.position;
                remaining -= actual;
                if (!blocked) break;
                if (hit.Normal.y > .65f) flags |= CollisionFlags.Below;
                else if (hit.Normal.y < -.65f) flags |= CollisionFlags.Above;
                else flags |= CollisionFlags.Sides;
                Vector3 slideNormal = hit.Normal;
                if (displacement.y <= 0 && slideNormal.y > 0)
                {
                    // A capsule's rounded lower cap produces an upward contact normal
                    // at a low cube edge. Horizontal input cannot turn that normal into
                    // a climb impulse; keep its horizontal constraint and the requested
                    // vertical motion. An explicit jump/mantle retains the full normal.
                    Vector3 horizontal = Vector3.ProjectOnPlane(slideNormal, Vector3.up);
                    if (horizontal.sqrMagnitude > 1e-8f) slideNormal = horizontal.normalized;
                }
                float into = Vector3.Dot(remaining, slideNormal);
                if (into < 0) remaining -= slideNormal * into;
                else break;
            }
            return flags;
        }
    }
}
