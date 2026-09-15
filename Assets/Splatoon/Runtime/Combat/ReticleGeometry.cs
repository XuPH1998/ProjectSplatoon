using UnityEngine;

namespace Splatoon.Combat
{
    public static class ReticleGeometry
    {
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
