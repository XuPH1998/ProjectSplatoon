using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>CharacterController contacts become one bounded authority push per motor step.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(CharacterController))]
    public sealed class PlayerBlockPushBridge : MonoBehaviour
    {
        public const int DynamicLayer = BlockDynamicWorld.Layer;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static event Action<ulong, BlockDynamicBody, Vector3> PushObserved;
        ulong _observedOwner;
#endif
        const float MaximumForce = BlockDynamicWorld.PushForce;
        const float MaximumPushSpeed = BlockDynamicWorld.PushSpeed;
        readonly Dictionary<ulong, PushContact> _contacts = new();
        readonly List<ulong> _contactIds = new(16);
        RaycastHit[] _hits = new RaycastHit[16];
        Collider[] _overlaps = new Collider[16];
        CharacterController _controller;
        bool _active, _authority, _moving;
        float _dt;
        Vector3 _requestedVelocity;

        struct PushContact
        {
            public BlockDynamicBody Block;
            public Vector3 Point, Velocity;
            public float Force;
        }

        void Awake() => _controller = GetComponent<CharacterController>();

        internal void BeginStep(PrototypePlayer player, float dt)
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            _active = true;
            _authority = player != null && player.IsAuthorityBlockPushStep &&
                !(BlockTerrain.Current != null && BlockTerrain.Current.FixedGridMode);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _observedOwner = player != null ? player.PlayerId : 0;
#endif
            _dt = dt;
            _contacts.Clear();
        }

        internal void DiscardContacts() => _contacts.Clear();

        internal CollisionFlags Move(Vector3 displacement, float dt)
        {
            Vector3 planar = Vector3.ProjectOnPlane(displacement, Vector3.up);
            _requestedVelocity = dt > 0 ? planar / dt : Vector3.zero;
            float clearance = float.PositiveInfinity;
            bool blockAhead = planar.sqrMagnitude > .0000001f && Probe(planar, out clearance);
            if (blockAhead && displacement.y <= 0)
            {
                // Step offset alone does not stop the capsule bottom sphere from rolling
                // up a low cube. Stop its horizontal sweep at the loose-body skin contact.
                // The contact still receives the bounded push, then the capsule follows
                // the body on later steps. Active jumps retain their full trajectory.
                float advance = Mathf.Min(planar.magnitude, Mathf.Max(0, clearance - _controller.skinWidth - .002f));
                displacement = planar.normalized * advance + Vector3.up * displacement.y;
            }
            float stepOffset = _controller.stepOffset;
            // Loose cubes must be pushed even when the authored map stair step could clear them.
            // Disable stepping only for this sweep, so it is pushed rather than
            // automatically becoming a stair. Map-only movement keeps its step.
            if (blockAhead) _controller.stepOffset = 0;
            _moving = true;
            try { return _controller.Move(displacement); }
            finally { _moving = false; _controller.stepOffset = stepOffset; }
        }

        bool Probe(Vector3 displacement, out float clearance)
        {
            Vector3 scale = transform.lossyScale;
            float radius = _controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float height = Mathf.Max(radius * 2, _controller.height * Mathf.Abs(scale.y));
            Vector3 center = transform.TransformPoint(_controller.center);
            float segment = Mathf.Max(0, height * .5f - radius);
            Vector3 bottom = center - Vector3.up * segment, top = center + Vector3.up * segment;
            Vector3 direction = displacement.normalized;
            float distance = displacement.magnitude + _controller.skinWidth;
            bool found = false;
            clearance = float.PositiveInfinity;
            int count;
            do
            {
                count = Physics.CapsuleCastNonAlloc(bottom, top, radius, direction, _hits, distance,
                    1 << DynamicLayer, QueryTriggerInteraction.Ignore);
                if (count < _hits.Length) break;
                Array.Resize(ref _hits, checked(_hits.Length * 2));
            } while (true);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (!TryBlock(hit.collider, out var block) || Vector3.Dot(hit.normal, direction) >= -.001f ||
                    hit.collider.bounds.max.y <= center.y - height * .5f + _controller.skinWidth) continue;
                found = true;
                clearance = Mathf.Min(clearance, hit.distance);
                Collect(block, hit.point);
            }
            // Casts omit initial overlaps. Include a shallow skin-width overlap
            // so contact on the previous frame still suppresses auto stepping.
            do
            {
                count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius + _controller.skinWidth, _overlaps,
                    1 << DynamicLayer, QueryTriggerInteraction.Ignore);
                if (count < _overlaps.Length) break;
                Array.Resize(ref _overlaps, checked(_overlaps.Length * 2));
            } while (true);
            for (int i = 0; i < count; i++)
            {
                var collider = _overlaps[i];
                if (!TryBlock(collider, out var block) ||
                    collider.bounds.max.y <= center.y - height * .5f + _controller.skinWidth ||
                    Vector3.Dot(collider.bounds.center - center, direction) <= 0) continue;
                found = true;
                clearance = 0;
                Collect(block, collider.ClosestPoint(center));
            }
            return found;
        }

        static bool TryBlock(Collider collider, out BlockDynamicBody block)
        {
            block = collider != null ? collider.GetComponentInParent<BlockDynamicBody>() : null;
            return block != null && block.Body != null && collider.enabled && collider.gameObject.activeInHierarchy;
        }

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!_active || !_moving || Mathf.Abs(hit.normal.y) >= .7f ||
                Vector3.Dot(hit.normal, _requestedVelocity) >= 0 || !TryBlock(hit.collider, out var block)) return;
            Collect(block, hit.point);
        }

        void Collect(BlockDynamicBody block, Vector3 point)
        {
            if (!_active || !_authority || block.World == null || !block.World.IsAuthority ||
                !block.World.Contains(block) || block.Collider == null || !block.Collider.enabled ||
                !block.Collider.gameObject.activeInHierarchy) return;
            block.World.NotifyPlayerContact(block);
            if (block.Body.isKinematic) return;
            // Initial-overlap capsule casts can report the world origin as the
            // contact point. Keep the force lever inside the actual cube so that
            // a distant block cannot acquire an artificial world-scale torque.
            point = block.Collider.ClosestPoint(point);
            _contacts[block.Id] = new PushContact { Block = block, Point = point, Velocity = _requestedVelocity };
        }

        internal void EndStep(bool committed)
        {
            try
            {
                if (!committed || !_active || !_authority || _dt <= 0 || _contacts.Count == 0) return;
                float total = 0;
                _contactIds.Clear();
                foreach (ulong id in _contacts.Keys) _contactIds.Add(id);
                foreach (ulong id in _contactIds)
                {
                    var contact = _contacts[id];
                    if (contact.Block == null || contact.Block.Id != id || contact.Block.World == null || !contact.Block.World.IsAuthority ||
                        !contact.Block.World.Contains(contact.Block) ||
                        contact.Block.Body == null || contact.Block.Body.isKinematic) continue;
                    var body = contact.Block.Body;
                    float speed = Mathf.Min(contact.Velocity.magnitude, MaximumPushSpeed);
                    Vector3 direction = contact.Velocity.normalized;
                    float missingSpeed = Mathf.Max(0, speed - Vector3.Dot(body.linearVelocity, direction));
                    contact.Force = body.mass * missingSpeed / _dt;
                    _contacts[id] = contact;
                    total += contact.Force;
                }
                float factor = total > MaximumForce ? MaximumForce / total : 1;
                foreach (ulong id in _contactIds)
                {
                    var contact = _contacts[id];
                    if (contact.Force <= 0 || contact.Block == null || contact.Block.Body == null) continue;
                    contact.Block.Body.AddForceAtPosition(contact.Velocity.normalized * (contact.Force * factor),
                        contact.Point, ForceMode.Force);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    PushObserved?.Invoke(_observedOwner, contact.Block, contact.Velocity.normalized * (contact.Force * factor));
#endif
                }
            }
            finally
            {
                _active = _authority = _moving = false;
                _contacts.Clear(); _contactIds.Clear();
            }
        }
    }
}
