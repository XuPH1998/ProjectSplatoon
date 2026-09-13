using UnityEngine;

namespace Splatoon.Combat
{
    [CreateAssetMenu(menuName = "喷墨对战/角色表现配置")]
    public sealed class CharacterPresentationProfile : ScriptableObject
    {
        [Header("转身")]
        [Min(0)] public float StationarySpeed = .1f;
        [Range(1, 89)] public float TurnThreshold = 45;
        [Min(1)] public float MovingTurnSpeed = 540;
        [Min(.05f)] public float TurnLeftDuration = 1, TurnRightDuration = 1;
        public AnimationCurve TurnLeftProgress = AnimationCurve.Linear(0, 0, 1, 1);
        public AnimationCurve TurnRightProgress = AnimationCurve.Linear(0, 0, 1, 1);
        [Header("动画")]
        [Min(.01f)] public float BlendSeconds = .1f;
        [Tooltip("前、后、左、右动画在 5 米/秒时的播放倍率")]
        public Vector4 WalkPlayback = Vector4.one;
        [Min(.1f)] public float AnimationReferenceSpeed = 5;
        public float ShootDuration = 1, DieForwardDuration = 1.8f, DieBackwardDuration = 1.8f;
        [Header("相机与逻辑枪口，角色根节点坐标")]
        public Vector3 CameraPivot = new(0, 1.5f, 0);
        public Vector3 CameraOffset = new(.65f, .15f, -3.8f);
        public Vector3 AimPivot = new(0, 1.4f, 0);
        public Vector3 MuzzlePosition = new(.25f, 1.4f, .7f);
        public float CameraCollisionRadius = .2f, CameraCollisionPadding = .08f;
        [Header("瞄准与反馈")]
        [Range(0, 1)] public float SpineAimWeight = .55f;
        public float RecoilRecovery = 18, CameraShake = .12f;

        public float TurnDuration(sbyte direction) => direction < 0 ? TurnLeftDuration : TurnRightDuration;
        public float TurnProgress(sbyte direction, float time) => Mathf.Clamp01(
            (direction < 0 ? TurnLeftProgress : TurnRightProgress).Evaluate(Mathf.Clamp01(time)));
        public Vector3 MuzzleOffset(float pitch) => AimPivot + Quaternion.Euler(pitch, 0, 0) * (MuzzlePosition - AimPivot);
    }
}
