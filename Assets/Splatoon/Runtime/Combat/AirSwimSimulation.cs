using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Stateless fixed-tick glide shared by authority and prediction replay.</summary>
    public static class AirSwimSimulation
    {
        public static float VerticalSpeed(float speed, bool gliding, cfg.HeroConfig hero, float dt)
        {
            // Preserve the jump arc. Toggling paper never adds upward velocity.
            if (!gliding || speed > 0) return speed - hero.CharacterGravity * dt;
            float terminal = -hero.AirSwimFallSpeed;
            return speed < terminal ? Mathf.MoveTowards(speed, terminal, hero.AirSwimBraking * dt)
                : Mathf.Max(terminal, speed - hero.AirSwimGravity * dt);
        }
    }
}
