#if UNITY_EDITOR
using System.Collections.Generic;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Combat
{
    [ExecuteAlways]
    public sealed class MotorGroundContactRecorder : MonoBehaviour
    {
        public readonly struct ContactObservation
        {
            public readonly uint InputSequence;
            public readonly Vector3 Point, Normal, MoveDirection;
            public readonly float MoveLength;
            public readonly int ColliderId, Layer;
            public readonly Bounds Bounds;
            public readonly bool BlockCollider;
            internal ContactObservation(uint inputSequence, ControllerColliderHit hit)
            {
                InputSequence = inputSequence; Point = hit.point; Normal = hit.normal;
                MoveDirection = hit.moveDirection; MoveLength = hit.moveLength;
                ColliderId = hit.collider.GetInstanceID(); Layer = hit.collider.gameObject.layer;
                Bounds = hit.collider.bounds; BlockCollider = hit.collider.GetComponentInParent<BlockTerrain>() != null;
            }
        }
        public readonly List<ContactObservation> Contacts = new();
        uint _inputSequence;
        public void Begin(uint inputSequence) { _inputSequence = inputSequence; Contacts.Clear(); }
        void OnControllerColliderHit(ControllerColliderHit hit) => Contacts.Add(new ContactObservation(_inputSequence, hit));
    }
}
#endif
