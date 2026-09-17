using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        public readonly NetworkVariable<FixedString128Bytes> Username = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public string DisplayName => IsTestBot ? $"测试 BOT {Snapshot.Value.Slot + 1}" :
            Username.Value.IsEmpty ? $"玩家 {OwnerClientId + 1}" : Username.Value.ToString();
        Animator _nameAnimator;
        Transform _nameHead;

        public Vector3 NameHeadPosition
        {
            get
            {
                var animator = CharacterView != null ? CharacterView.Animator : null;
                if (_nameAnimator != animator)
                {
                    _nameAnimator = animator;
                    _nameHead = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                }
                return _nameHead != null ? _nameHead.position : CameraPivot;
            }
        }
        public Vector3 NameTagPosition => NameHeadPosition + Vector3.up * .25f;
    }
}
