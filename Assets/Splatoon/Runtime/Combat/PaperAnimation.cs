using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Explicit, replayable evaluation; Unity's render clock never advances the hit pose.</summary>
    public static class PaperAnimation
    {
        public const int FramesPerSecond = 30;
        public static void Evaluate(Animator animator, float time, Vector2 move)
        {
            animator.enabled = true; animator.fireEvents = false; animator.Rebind();
            animator.SetFloat("MoveX", move.x); animator.SetFloat("MoveY", move.y);
            animator.SetFloat("MovePlayback", 1);
            for (int i = 1; i < animator.layerCount; i++) animator.SetLayerWeight(i, 0);
            animator.Play("Base Layer.Locomotion", 0, 0); animator.Update(0);
            float duration = Mathf.Max(.001f, animator.GetCurrentAnimatorStateInfo(0).length);
            animator.Play("Base Layer.Locomotion", 0, Mathf.Repeat(time / duration, 1));
            animator.Update(0); animator.enabled = false;
        }
        public static void Step(ref PlayerSnapshot s, PlayerSnapshot before, float dt, PaperBodyProfile profile)
        {
            if (!s.ShowsSwimBody) { s.PaperAnimationTime = 0; s.PaperMove = Vector2.zero; return; }
            Vector3 local = Quaternion.Inverse(s.PaperRotation) * s.Velocity;
            var motion = new Vector2(-local.x, local.y);
            float speed = motion.magnitude;
            s.PaperMove = speed > .05f ? motion / speed : Vector2.zero;
            float playback = speed > .05f ? speed / (profile != null ? profile.AnimationReferenceSpeed : 5) : 1;
            s.PaperAnimationTime = (before.ShowsSwimBody ? before.PaperAnimationTime : 0) + dt * playback;
            // Bounded phase avoids loss of float precision in long sessions.
            s.PaperAnimationTime = Mathf.Repeat(s.PaperAnimationTime, 1024);
        }
    }
}
