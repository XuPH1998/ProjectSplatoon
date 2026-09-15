using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        void ReticleBar(Rect rect, Color color)
        {
            Panel(new Rect(rect.x - 1, rect.y - 1, rect.width + 2, rect.height + 2), new Color(0, 0, 0, .8f));
            Panel(rect, color);
        }
        void DrawSpreadReticle(PrototypePlayer local, PlayerSnapshot state, WeaponRuntimeConfig weapon, Vector2 center)
        {
            var camera = Camera.main;
            var size = ReticleGeometry.HalfSize(state.CurrentSpread, state.CurrentVerticalSpread,
                camera != null ? camera.fieldOfView : 60, camera != null ? camera.aspect : Screen.width / (float)Screen.height);
            bool prepaid = WeaponSimulation.IsSplatling(weapon) && state.SplatlingRemaining > 0;
            Color color = local.MuzzleBlocked ? Color.red : !prepaid && state.Ink < WeaponSimulation.InkCost(weapon) ? Color.yellow : Color.white;
            ReticleBar(new Rect(center.x - 1.5f, center.y - 1.5f, 3, 3), color);
            ReticleBar(new Rect(center.x - 1.5f, center.y - size.y - 12, 3, 12), color);
            ReticleBar(new Rect(center.x - 1.5f, center.y + size.y, 3, 12), color);
            ReticleBar(new Rect(center.x - size.x - 12, center.y - 1.5f, 12, 3), color);
            ReticleBar(new Rect(center.x + size.x, center.y - 1.5f, 12, 3), color);
            if (WeaponSimulation.IsSplatling(weapon))
            {
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2)
                {
                    var corner = center + Vector2.Scale(size, new Vector2(x, y));
                    float width = Mathf.Min(8, size.x * .5f), height = Mathf.Min(8, size.y * .5f);
                    ReticleBar(new Rect(corner.x - (x > 0 ? width : 0), corner.y - 1, width, 2), color);
                    ReticleBar(new Rect(corner.x - 1, corner.y - (y > 0 ? height : 0), 2, height), color);
                }
            }
            else
            {
                int segments = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(size.x, size.y) * 5), 48, 256);
                Color outline = new(color.r, color.g, color.b, .55f);
                for (int i = 0; i < segments; i++)
                {
                    float angle = i * Mathf.PI * 2 / segments;
                    Panel(new Rect(center.x + Mathf.Cos(angle) * size.x - .75f, center.y + Mathf.Sin(angle) * size.y - .75f, 1.5f, 1.5f), outline);
                }
            }
        }
    }
}
