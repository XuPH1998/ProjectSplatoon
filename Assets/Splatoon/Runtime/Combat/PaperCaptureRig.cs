using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Authored bindings only; no automatic update or gameplay callbacks on the capture clone.</summary>
    public sealed class PaperCaptureRig : MonoBehaviour
    {
        public Animator Animator;
        public Transform Nozzle, AimReference, LeftGrip;
        public float SpineAimWeight = .55f;
        public void Evaluate(float time, Vector2 move)
        {
            PaperAnimation.Evaluate(Animator, time, move);
            // Match the standing view's forward weapon alignment and support-hand
            // IK, without camera recoil, shot layers or frame-clock animation.
            var spine = Animator.GetBoneTransform(HumanBodyBones.Spine);
            var chest = Animator.GetBoneTransform(HumanBodyBones.Chest);
            var reference = AimReference != null ? AimReference : Nozzle;
            if (spine != null && chest != null && reference != null)
            {
                Vector3 direction = transform.forward;
                Quaternion correction = Quaternion.FromToRotation(reference.forward, direction);
                spine.rotation = Quaternion.Slerp(Quaternion.identity, correction, SpineAimWeight) * spine.rotation;
                chest.rotation = Quaternion.FromToRotation(reference.forward, direction) * chest.rotation;
            }
            if (LeftGrip != null)
                InkCharacterView.SolveArm(Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
                    Animator.GetBoneTransform(HumanBodyBones.LeftLowerArm), Animator.GetBoneTransform(HumanBodyBones.LeftHand),
                    LeftGrip.position, LeftGrip.rotation);
        }
    }
}
