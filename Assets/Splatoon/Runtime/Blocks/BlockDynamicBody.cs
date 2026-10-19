using UnityEngine;

namespace Splatoon.Blocks
{
    /// <summary>Stable combat identity; movement never changes a block's identity.</summary>
    public sealed class BlockDynamicBody : MonoBehaviour
    {
        public ulong Id { get; internal set; }
        public byte Owner { get; internal set; }
        public BlockDynamicWorld World { get; internal set; }
        public Rigidbody Body { get; internal set; }
        public BoxCollider Collider { get; internal set; }
        // A CCD impulse can be present before Unity's native sleep flag clears.
        // Exact zero is required: even a very small pending motion is real.
        public bool IsAtRest => Body != null && Body.IsSleeping() && HasZeroMotion(Body.linearVelocity, Body.angularVelocity);
        internal static bool HasZeroMotion(Vector3 velocity, Vector3 angularVelocity)
            => velocity.Equals(Vector3.zero) && angularVelocity.Equals(Vector3.zero);
        // A managed far native sleeper keeps its real dynamic Rigidbody,
        // collider, ID and combat kind. PhysX contact/force can wake it directly;
        // this tag is cleared on the following range pass.
        public bool Parked { get; internal set; }
        internal int StableSleepSamples;
        internal Vector3 SleepSamplePosition;
        internal Quaternion SleepSampleRotation;
        internal ulong MotionSequence;
        internal bool RemoteSleeping;
        internal Vector3 RemoteVelocity, RemoteAngularVelocity;
        // Exact authoritative wire values survive native quaternion normalization.
        internal BlockDynamicState FrozenState;
        internal bool HasFrozenState;
        internal bool HasReplicaPose;
        internal Vector3 ReplicaWirePosition;
        internal Quaternion ReplicaWireRotation;
        // Kept across pooling: callbacks already queued for a consumed native collider
        // cannot convert a replacement born from an observer in the same physics step.
        internal double SurfacePaintConsumedFixedTime = double.NaN;

        void OnCollisionEnter(Collision collision) => World?.TryConvertSurfaceCollision(this, collision);
        void OnCollisionStay(Collision collision) => World?.TryConvertSurfaceCollision(this, collision);

        public bool TryContact(Vector3 point, Vector3 normal, out BlockContact contact)
        {
            contact = default;
            if (World == null || !World.Contains(this) || !Collider.enabled) return false;
            contact = new BlockContact { Owner = Owner, Point = point, Normal = normal, OrientationNormal = normal,
                Kind = BlockContactKind.Dynamic, DynamicId = Id, Stable = false, Climbable = false,
                Revision = World.LifecycleRevision };
            return true;
        }
    }
}
