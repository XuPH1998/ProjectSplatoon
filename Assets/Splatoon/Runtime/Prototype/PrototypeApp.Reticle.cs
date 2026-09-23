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
        void ReticleLine(Vector2 from, Vector2 to, Color color, float width = 1.5f)
        {
            var previous = GUI.matrix;
            try
            {
                float angle = Mathf.Atan2(to.y - from.y, to.x - from.x) * Mathf.Rad2Deg;
                GUI.matrix = previous * Matrix4x4.TRS(new Vector3(from.x, from.y, 0), Quaternion.Euler(0, 0, angle), Vector3.one);
                ReticleBar(new Rect(0, -width * .5f, Vector2.Distance(from, to), width), color);
            }
            finally { GUI.matrix = previous; }
        }
        void ReticleRing(Vector2 center, float radius, Color color, float width = 1.5f)
        {
            // Three concentric strokes keep the team colour readable on ink of either team.
            RingStroke(center, radius, new Color(0, 0, 0, .8f), width + 2);
            RingStroke(center, radius, color, width);
        }
        void RingStroke(Vector2 center, float radius, Color color, float width)
        {
            for (int i = 0; i < 64; i++)
            {
                float a = i * Mathf.PI * 2 / 64;
                var p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                Panel(new Rect(p.x - width * .5f, p.y - width * .5f, width, width), color);
            }
        }
        void DrawCombatReticle(PrototypePlayer local, PlayerSnapshot state, WeaponRuntimeConfig weapon, Vector2 center)
        {
            // HUD positions retain the 1280x720 reference; round marks use a uniform
            // vertical scale so a 4:3 or ultrawide viewport does not stretch circles.
            float aspectScale = Screen.width * 720f / (Screen.height * 1280f);
            var previous = GUI.matrix;
            // World projection is independent of the centered, letterboxed menu matrix.
            GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.height / 720f, Screen.height / 720f, 1));
            try { DrawUniformReticle(local, state, weapon, new Vector2(center.x * aspectScale, center.y), aspectScale); }
            finally { GUI.matrix = previous; }
        }
        void DrawUniformReticle(PrototypePlayer local, PlayerSnapshot state, WeaponRuntimeConfig weapon, Vector2 center, float aspectScale)
        {
            var camera = Camera.main;
            var spread = ReticleGeometry.HalfSize(state.CurrentSpread, state.CurrentVerticalSpread,
                camera != null ? camera.fieldOfView : 60, camera != null ? camera.aspect : Screen.width / (float)Screen.height);
            bool guided = weapon.AimMode == WeaponAimMode.WeaponReference;
            if (guided) spread = local.GuideSpreadHalfSize;
            spread.x *= aspectScale;
            spread = Vector2.Max(spread, Vector2.one * 16);
            bool prepaid = WeaponSimulation.IsSplatling(weapon) && state.SplatlingRemaining > 0;
            var color = ReticleGeometry.StatusColor(local.MuzzleBlocked, !prepaid && state.Ink < WeaponSimulation.InkCost(weapon));
            var team = PrototypeArena.TeamColor(state.Team);
            var impact = new Vector2(local.ImpactReticleViewport.x * 1280 * aspectScale, (1 - local.ImpactReticleViewport.y) * 720);
            bool landing = !state.ShowsSwimBody && local.ImpactReticleVisible;
            bool merge = landing && ReticleGeometry.MergeImpact(center, impact);

            ReticleRing(center, 8, color);
            if (merge) RingStroke(center, 8, team, .75f);
            else if (landing)
            {
                ReticleRing(impact, 8, Color.white, 2.5f);
                RingStroke(impact, 8, team, 1.5f);
            }
            var dot = guided ? new Vector2(local.DirectionReticleViewport.x * 1280 * aspectScale,
                (1 - local.DirectionReticleViewport.y) * 720) : center;
            ReticleBar(new Rect(dot.x - 1, dot.y - 1, 2, 2), color);
            if (!state.ShowsSwimBody)
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2)
                {
                    var corner = center + Vector2.Scale(spread, new Vector2(x, y));
                    var direction = new Vector2(x, y).normalized;
                    ReticleLine(corner - direction * 3, corner + direction * 3, color);
                }
            if (!state.ShowsSwimBody && local.TargetReticleVisible)
            {
                var target = new Vector2(local.TargetReticleViewport.x * 1280 * aspectScale, (1 - local.TargetReticleViewport.y) * 720);
                ReticleLine(target - Vector2.one * 4, target + Vector2.one * 4, team);
                ReticleLine(target + new Vector2(-4, 4), target + new Vector2(4, -4), team);
            }
            if (Time.unscaledTimeAsDouble < local.HitConfirmedUntil)
            {
                var hitColor = local.LastHitKilled ? new Color(1, .8f, .25f) : Color.white;
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2)
                {
                    var direction = new Vector2(x, y).normalized;
                    ReticleLine(center + direction * 17, center + direction * 25, hitColor, 2.5f);
                }
                if (local.LastHitKilled) GUI.Label(new Rect(center.x + 29, center.y - 13, 90, 35), "击倒", _label);
            }
        }
    }
}
