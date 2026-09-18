using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Foot-aligned collision dimensions, shared by prediction, authority and remote proxies.</summary>
    public readonly struct HeroBodyShape
    {
        public readonly float Height, Radius, CompactHeight, StepOffset, SkinWidth;
        public Vector3 Center => Vector3.up * (Height * .5f);
        public Vector3 CompactCenter => Vector3.up * (CompactHeight * .5f);
        public HeroBodyShape(cfg.HeroConfig hero)
        { Height = hero.StandingHeight; Radius = hero.BodyRadius; CompactHeight = hero.CompactHeight; StepOffset = hero.ControllerStepOffset; SkinWidth = hero.ControllerSkinWidth; }
        public HeroBodyShape(CharacterController controller)
        { Height = controller.height; Radius = controller.radius; CompactHeight = .7f; StepOffset = controller.stepOffset; SkinWidth = controller.skinWidth; }
        public static HeroBodyShape For(int heroId) => new(GameplayConfig.GetHero(heroId));
        public void Apply(CharacterController controller, bool compact)
        {
            float height = compact ? CompactHeight : Height;
            Vector3 center = compact ? CompactCenter : Center;
            if (controller.height == height && controller.radius == Radius && controller.center == center &&
                controller.stepOffset == StepOffset && controller.skinWidth == SkinWidth) return;
            controller.stepOffset = Mathf.Min(StepOffset, height);
            controller.radius = Radius; controller.height = height; controller.center = center; controller.skinWidth = SkinWidth;
        }
        public bool Fits(Vector3 feet, Collider[] buffer, int mask)
        {
            float radius = Mathf.Max(.05f, Radius - .025f);
            var center = feet + Center;
            return Physics.OverlapCapsuleNonAlloc(center + Vector3.up * (Height * .5f - radius),
                center - Vector3.up * (Height * .5f - radius), radius, buffer, mask, QueryTriggerInteraction.Ignore) == 0;
        }
    }
}
