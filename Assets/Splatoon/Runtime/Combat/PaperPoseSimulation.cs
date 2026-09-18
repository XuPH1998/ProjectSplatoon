using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public enum PaperPose : byte { None, Ground, Wall, Air, Mantle }

    /// <summary>Authoritative paper frame. Camera rotation and render correction never enter this calculation.</summary>
    public static class PaperPoseSimulation
    {
        public static Quaternion GroundRotation(Vector3 normal, float yaw)
        {
            var heading = Vector3.ProjectOnPlane(Quaternion.Euler(0, yaw, 0) * Vector3.forward, normal).normalized;
            if (heading.sqrMagnitude < .01f) heading = Vector3.ProjectOnPlane(Vector3.right, normal).normalized;
            return Quaternion.LookRotation(normal, heading);
        }
        public static void Resolve(ref PlayerSnapshot s, PlayerSnapshot before, PaperBodyProfile profile, double now)
        {
            PaperPose next = !s.ShowsSwimBody ? PaperPose.None : s.Movement == MovementMode.WallInk ? PaperPose.Wall
                : s.Movement == MovementMode.Mantle ? PaperPose.Mantle : s.Grounded ? PaperPose.Ground : PaperPose.Air;
            if (next != before.PaperPose) s.PaperChangedAt = now;
            s.PaperPose = next;
            float offset = profile != null ? profile.SurfaceOffset : .03f;
            float height = profile != null ? profile.Size.y : 1.8f;
            if (next == PaperPose.None)
            { s.PaperCenter = s.Position; s.PaperRotation = Quaternion.identity; return; }
            if (next == PaperPose.Wall || next == PaperPose.Mantle)
            {
                Vector3 normal = s.WallNormal.sqrMagnitude > .5f ? s.WallNormal.normalized : Vector3.back;
                Vector3 center = s.Position + Vector3.up * (height / 2);
                center += normal * (offset - Vector3.Dot(center - s.WallPoint, normal));
                s.PaperRotation = Quaternion.LookRotation(normal, Vector3.up); s.PaperCenter = center;
                if (next == PaperPose.Mantle)
                {
                    float t = Mathf.Clamp01((float)(now - s.MantleStartedAt) / GameplayConfig.GetHero(s.HeroId).MantleSeconds);
                    float blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01((t - .5f) * 2));
                    s.PaperRotation = Quaternion.Slerp(s.PaperRotation, GroundRotation(Vector3.up, s.Yaw), blend);
                    s.PaperCenter = Vector3.Lerp(center, s.Position + Vector3.up * offset, blend);
                }
                return;
            }
            var foam=PrototypeArena.Current?.Foam;
            if(next==PaperPose.Ground&&foam!=null&&foam.Patches.TryGetValue(s.FoamSupportRegionKey,out var patch)&&
                FoamSupport.Sample(patch,s,profile,out var support,out var supportNormal,out _))
            {s.PaperRotation=GroundRotation(supportNormal,s.Yaw);s.PaperCenter=support+supportNormal*offset;return;}
            if (next == PaperPose.Ground && Physics.Raycast(s.Position + Vector3.up * .3f, Vector3.down,
                out var ground, .65f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore))
            {
                s.PaperRotation = GroundRotation(ground.normal, s.Yaw);
                s.PaperCenter = ground.point + ground.normal * offset; return;
            }
            // Every airborne sheet is horizontal, including wall and slope takeoff.
            // Capture heading on entry, then keep it independent of camera rotation.
            bool hadPaper = before.PaperPose != PaperPose.None && before.ShowsSwimBody;
            bool newFrame = !hadPaper || before.PaperPose != PaperPose.Air || (s.Swimming && !before.Swimming) ||
                Vector3.Dot(before.PaperRotation * Vector3.forward, Vector3.up) < .999f;
            s.PaperRotation = newFrame ? GroundRotation(Vector3.up, s.Yaw) : before.PaperRotation;
            // The motor has already aligned its support point with the sheet.
            // Preserve the horizontal frame on wall takeoff, but never retain
            // a vertical gap above the collider that would snap away on landing.
            s.PaperCenter = hadPaper ? before.PaperCenter + s.Position - before.Position : s.Position;
            s.PaperCenter.y = s.Position.y + offset;
        }
    }
}
