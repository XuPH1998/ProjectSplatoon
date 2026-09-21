using UnityEngine;

namespace Splatoon.Combat
{
    public static class ReticleGeometry
    {
        public static Vector2 GuideHalfSize(Camera camera, TpsAimSolution aim, Splatoon.Config.WeaponRuntimeConfig w,
            Splatoon.Prototype.PlayerSnapshot state, double age)
        {
            // Surface contact can be offset by the sweep radius or the embedded-muzzle guard.
            // Measure only angular displacement around the centre trajectory, not that offset.
            var representative = WeaponLaunch.Representative(aim, w, WeaponLaunch.PreviewCharge(state, w), state.PlanarVelocity, state.Yaw);
            var center = camera.WorldToViewportPoint(InkBallistics.Position(representative, w, age));
            var basis = WeaponLaunch.Basis(aim.InitialDirection);
            var size = Vector2.zero;
            for (int i = 0; i < 4; i++)
            {
                float x = i < 2 ? (i == 0 ? -1 : 1) * Mathf.Tan(state.CurrentSpread * Mathf.Deg2Rad) : 0;
                float y = i >= 2 ? (i == 2 ? -1 : 1) * Mathf.Tan(state.CurrentVerticalSpread * Mathf.Deg2Rad) : 0;
                var boundary = aim;
                boundary.InitialDirection = basis * new Vector3(x, y, 1).normalized;
                var shot = WeaponLaunch.Representative(boundary, w, WeaponLaunch.PreviewCharge(state, w), state.PlanarVelocity, state.Yaw);
                var projected = camera.WorldToViewportPoint(InkBallistics.Position(shot, w, age));
                if (projected.z <= 0 || !float.IsFinite(projected.x) || !float.IsFinite(projected.y)) continue;
                size = Vector2.Max(size, new Vector2(Mathf.Abs(projected.x - center.x) * 1280, Mathf.Abs(projected.y - center.y) * 720));
            }
            return Vector2.Max(size, Vector2.one * 6);
        }
        public static bool MergeImpact(Vector2 aim, Vector2 impact) => (aim - impact).sqrMagnitude < 16;
        public static Color StatusColor(bool blocked, bool lowInk) => blocked ? Color.red : lowInk ? Color.yellow : Color.white;

        // Return reference-canvas distances, correcting X for non-16:9 Game views.
        public static Vector2 HalfSize(float horizontalDegrees, float verticalDegrees, float verticalFov, float aspect)
        {
            float focalY = 360 / Mathf.Tan(Mathf.Clamp(verticalFov, 1, 179) * Mathf.Deg2Rad * .5f);
            float focalX = focalY * (1280f / 720) / Mathf.Max(.01f, aspect);
            return new Vector2(Mathf.Max(6, Mathf.Tan(horizontalDegrees * Mathf.Deg2Rad) * focalX),
                Mathf.Max(6, Mathf.Tan(verticalDegrees * Mathf.Deg2Rad) * focalY));
        }
    }
}
