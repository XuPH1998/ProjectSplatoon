#if UNITY_EDITOR
using UnityEngine;
using Splatoon.Prototype;
using Splatoon.Combat;
using Splatoon.Config;

namespace Splatoon.Tests
{
    public sealed class CameraReticleBaselineHud : MonoBehaviour
    {
        public PrototypePlayer Player;
        static void Panel(Rect rect, Color color)
        { var old = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old; }
        void OnGUI()
        {
            if (Player == null) return;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.width / 1280f, Screen.height / 720f, 1));
            var s = Player.PresentedState;
            DrawSpreadReticle(Player, s, GameplayConfig.GetWeapon(s.HeroId), new Vector2(Player.ReticleViewport.x * 1280, (1 - Player.ReticleViewport.y) * 720));
            if (Player.ImpactReticleVisible) DrawImpactReticle(new Vector2(Player.ImpactReticleViewport.x * 1280, (1 - Player.ImpactReticleViewport.y) * 720));
            GUI.matrix = old;
        }
        void ReticleBar(Rect rect, Color color)
        {
            Panel(new Rect(rect.x - 1, rect.y - 1, rect.width + 2, rect.height + 2), new Color(0, 0, 0, .8f));
            Panel(rect, color);
        }
        void DrawImpactReticle(Vector2 center)
        {
            // A small hollow ring marks the predicted impact, separate from the aiming crosshair.
            var color = new Color(.5f, .9f, 1, .8f);
            for (int i = 0; i < 64; i++)
            {
                if (i % 16 < 3) continue;
                float angle = i * Mathf.PI * 2 / 64;
                var point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 10;
                Panel(new Rect(point.x - 1.5f, point.y - 1.5f, 3, 3), new Color(0, 0, 0, .6f));
            }
            for (int i = 0; i < 64; i++)
            {
                if (i % 16 < 3) continue;
                float angle = i * Mathf.PI * 2 / 64;
                var point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 10;
                Panel(new Rect(point.x - .75f, point.y - .75f, 1.5f, 1.5f), color);
            }
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

#endif
