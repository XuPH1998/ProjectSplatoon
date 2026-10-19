using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;
using Splatoon.Blocks;

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
        /// <summary>Raise a fitted sheet only far enough to clear real voxel top faces under its projected footprint.</summary>
        public static Vector3 BlockGroundCenter(BlockWorld world, Vector3 anchor, Vector3 normal, Quaternion rotation, Vector2 size, float offset)
        {
            Vector3 center = anchor + normal * offset;
            if (world == null || normal.y < .65f) return center;
            Vector3 r = rotation * Vector3.right, f = rotation * Vector3.up;
            float determinant = r.x * f.z - r.z * f.x;
            Vector3 right = r * (size.x * .5f), along = f * (size.y * .5f);
            Vector3 a = center - right - along, b = center + right - along, c = center + right + along, d = center - right + along;
            float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)), maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
            float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z)), maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
            float verticalRange = Mathf.Abs(right.y) + Mathf.Abs(along.y) + BlockWorld.CellSize;
            int minY = Mathf.FloorToInt((anchor.y - verticalRange) / BlockWorld.CellSize);
            int maxY = Mathf.FloorToInt((anchor.y + verticalRange) / BlockWorld.CellSize);
            float safeY = center.y;
            for (int z = Mathf.FloorToInt(minZ / BlockWorld.CellSize); z <= Mathf.FloorToInt(maxZ / BlockWorld.CellSize); z++)
            for (int x = Mathf.FloorToInt(minX / BlockWorld.CellSize); x <= Mathf.FloorToInt(maxX / BlockWorld.CellSize); x++)
            for (int y = minY; y <= maxY; y++)
            {
                var cell = new Vector3Int(x, y, z);
                if (!world.TryGet(cell, out _) || world.TryGet(cell + Vector3Int.up, out _)) continue;
                float left = x * BlockWorld.CellSize, bottom = z * BlockWorld.CellSize;
                float top = (y + 1) * BlockWorld.CellSize;
                // The extrema of a plane over a clipped rectangle occur at a voxel corner,
                // a sheet corner, or an intersection of their edges. No holes become support.
                Check(left, bottom); Check(left + BlockWorld.CellSize, bottom);
                Check(left, bottom + BlockWorld.CellSize); Check(left + BlockWorld.CellSize, bottom + BlockWorld.CellSize);
                Corner(a); Corner(b); Corner(c); Corner(d);
                Edge(a, b); Edge(b, c); Edge(c, d); Edge(d, a);
                void Check(float px, float pz)
                {
                    float dx = px - center.x, dz = pz - center.z;
                    float u = (dx * f.z - dz * f.x) / determinant, v = (r.x * dz - r.z * dx) / determinant;
                    if (Mathf.Abs(u) > size.x * .5f + .0001f || Mathf.Abs(v) > size.y * .5f + .0001f) return;
                    safeY = Mathf.Max(safeY, top + (normal.x * dx + normal.z * dz + offset) / normal.y);
                }
                void Corner(Vector3 p)
                { if (p.x >= left && p.x <= left + BlockWorld.CellSize && p.z >= bottom && p.z <= bottom + BlockWorld.CellSize) Check(p.x, p.z); }
                void Edge(Vector3 start, Vector3 end)
                {
                    if (Mathf.Abs(end.x - start.x) > .00001f)
                    { IntersectX(left); IntersectX(left + BlockWorld.CellSize); }
                    if (Mathf.Abs(end.z - start.z) > .00001f)
                    { IntersectZ(bottom); IntersectZ(bottom + BlockWorld.CellSize); }
                    void IntersectX(float px)
                    {
                        float t = (px - start.x) / (end.x - start.x), pz = Mathf.LerpUnclamped(start.z, end.z, t);
                        if (t >= 0 && t <= 1 && pz >= bottom && pz <= bottom + BlockWorld.CellSize) Check(px, pz);
                    }
                    void IntersectZ(float pz)
                    {
                        float t = (pz - start.z) / (end.z - start.z), px = Mathf.LerpUnclamped(start.x, end.x, t);
                        if (t >= 0 && t <= 1 && px >= left && px <= left + BlockWorld.CellSize) Check(px, pz);
                    }
                }
            }
            center.y = safeY; return center;
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
            if (next == PaperPose.Ground && PlayerMotorSimulation.BlockTraversalEnabled &&
                PlayerMotorSimulation.TryGetGround(s.Position, out var contact) && contact.IsBlock && contact.Stable && contact.Owner == s.Team)
            {
                Vector3 normal = s.GroundNormal.y >= .65f ? s.GroundNormal.normalized : contact.Normal;
                s.PaperRotation = GroundRotation(normal, s.Yaw);
                var anchor = new Vector3(s.Position.x, contact.Point.y, s.Position.z);
                // Surface deposits carry ownership on the authored map plane. They are
                // ignored by the character, so their voxel top cannot lift the paper pose.
                bool surfaceAttached = contact.Kind == BlockContactKind.SurfacePaint || contact.Kind == BlockContactKind.SurfaceAttached;
                s.PaperCenter = surfaceAttached ? anchor + normal * offset
                    : BlockGroundCenter(BlockTerrain.Current.World, anchor, normal, s.PaperRotation,
                        profile != null ? profile.Size : new Vector2(.9f, 1.8f), offset);
                return;
            }
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
