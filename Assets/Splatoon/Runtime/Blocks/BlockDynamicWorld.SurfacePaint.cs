using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockDynamicWorld
    {
        // Native free blocks use a .002 m contact offset; the authored map uses
        // Unity's .01 m margin. Include that small separation without reaching
        // the next layer of a pile or waking an entire terrain page.
        const float SurfacePaintContactTolerance = .0121f;
        Collider[] _surfacePaintCandidates = new Collider[16];
        readonly HashSet<BlockSurfaceKey> _surfacePaintWakeCells = new();
        readonly HashSet<ulong> _surfacePaintWakeIds = new();
        readonly List<(BlockDynamicBody block, ulong id, uint round, byte owner)> _surfacePaintWakes = new();

        /// <summary>Color changes wake existing touching sleepers; only a later native contact can convert them.</summary>
        public void WakeSurfacePaintContacts(IReadOnlyList<BlockSurfaceKey> changed)
        {
            if (_disposed || !IsAuthority || Frozen || changed == null || changed.Count == 0 || _terrain == null ||
                !_terrain.Initialized || !_terrain.SurfaceOnly || _terrain.Dynamic != this ||
                _terrain.Round != Round || _terrain.SurfacePaints == null || _bodies.Count == 0) return;
            _surfacePaintWakeCells.Clear(); _surfacePaintWakeIds.Clear(); _surfacePaintWakes.Clear();
            try
            {
                uint round = Round;
                for (int i = 0; i < changed.Count; i++)
                {
                    var key = changed[i];
                    if (!_surfacePaintWakeCells.Add(key) || !_terrain.SurfacePaints.TryGet(key, out var cell) ||
                        !_terrain.SurfacePaints.TryGetFace(key.FaceId, out var face)) continue;
                    var center = face.Center(key);
                    var half = new Vector3(BlockSurfaceWorld.CellSize * .5f + .0001f,
                        BlockSurfaceWorld.CellSize * .5f + .0001f, SurfacePaintContactTolerance);
                    var rotation = Quaternion.LookRotation(face.Normal, face.V);
                    int count = QuerySurfacePaintCandidates(center, half, rotation);
                    for (int j = 0; j < count; j++)
                    {
                        var collider = _surfacePaintCandidates[j];
                        var native = collider == null ? null : collider.attachedRigidbody;
                        var block = native == null ? null : native.GetComponent<BlockDynamicBody>();
                        if (block == null || collider != block.Collider || block.Owner == cell.Owner ||
                            !CanConvertSurfaceBody(block, block.Id, round, block.Owner) || native.isKinematic ||
                            !native.IsSleeping() || Vector3.Dot(native.position - center, face.Normal) <= 0 ||
                            !_surfacePaintWakeIds.Add(block.Id)) continue;
                        _surfacePaintWakes.Add((block, block.Id, round, block.Owner));
                    }
                    Array.Clear(_surfacePaintCandidates, 0, count);
                }
                // Finish every query before waking. No paint, delete or external
                // notification occurs here, so observers cannot nest conversion.
                foreach (var pending in _surfacePaintWakes)
                    if (CanConvertSurfaceBody(pending.block, pending.id, pending.round, pending.owner))
                        WakeBody(pending.block, WakeReason.Support);
            }
            finally
            {
                _surfacePaintWakes.Clear(); _surfacePaintWakeIds.Clear(); _surfacePaintWakeCells.Clear();
            }
        }

        int QuerySurfacePaintCandidates(Vector3 center, Vector3 half, Quaternion rotation)
        {
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(center, half, _surfacePaintCandidates, rotation,
                    1 << Layer, QueryTriggerInteraction.Ignore);
                if (count < _surfacePaintCandidates.Length) return count;
                // A full nonalloc result is not a complete result, even if the
                // true count happened to equal capacity. Retry after growth.
                Array.Resize(ref _surfacePaintCandidates, Math.Max(1, checked(_surfacePaintCandidates.Length * 2)));
            }
        }

        struct SurfaceContactSelection
        {
            public bool Found;
            public float Distance;
            public Collider Map;
            public Vector3 Point, Normal;
            public BlockSurfaceKey Key;
        }

        void ConsiderSurfaceContact(BlockDynamicBody block, Collider map, Vector3 point, Vector3 normal,
            byte owner, Vector3 center, ref SurfaceContactSelection selected)
        {
            if (!TryResolveSurfaceContact(block, map, point, normal, out var cell, out var outward) ||
                cell.Owner == owner) return;
            float distance = (point - center).sqrMagnitude;
            if (selected.Found && (distance > selected.Distance || distance.Equals(selected.Distance) &&
                CompareSurfaceKey(cell.Key, selected.Key) >= 0)) return;
            selected = new SurfaceContactSelection { Found = true, Distance = distance, Map = map,
                Point = point, Normal = outward, Key = cell.Key };
        }

        /// <summary>Consumes at most one current body for one native callback. Clients never convert.</summary>
        public bool TryConvertSurfaceCollision(BlockDynamicBody block, Collision collision)
        {
            if (block == null || collision == null || !CanConvertSurfaceBody(block, block.Id, Round, block.Owner) ||
                block.SurfacePaintConsumedFixedTime.Equals(Time.fixedTimeAsDouble)) return false;
            // A callback may publish events which immediately reuse this component from the pool.
            // All selection and the commit are bound to the identity captured before enumeration.
            ulong id = block.Id; uint round = Round; byte owner = block.Owner;
            Vector3 center = block.Body.position;
            SurfaceContactSelection selected = default;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                Collider map = contact.thisCollider == block.Collider ? contact.otherCollider :
                    contact.otherCollider == block.Collider ? contact.thisCollider : null;
                ConsiderSurfaceContact(block, map, contact.point, contact.normal, owner, center, ref selected);
            }
            return selected.Found && TryConvertSurfaceContact(block, selected.Map, selected.Point, selected.Normal, id, round, owner);
        }

        /// <summary>Batched contact seam sharing native candidate selection and the authoritative conversion commit.</summary>
        public bool TryConvertSurfaceContacts(BlockDynamicBody block,
            IReadOnlyList<(Collider map, Vector3 point, Vector3 normal)> contacts)
        {
            if (block == null || contacts == null || !CanConvertSurfaceBody(block, block.Id, Round, block.Owner) ||
                block.SurfacePaintConsumedFixedTime.Equals(Time.fixedTimeAsDouble)) return false;
            ulong id = block.Id; uint round = Round; byte owner = block.Owner;
            Vector3 center = block.Body.position;
            SurfaceContactSelection selected = default;
            for (int i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                ConsiderSurfaceContact(block, contact.map, contact.point, contact.normal, owner, center, ref selected);
            }
            return selected.Found && TryConvertSurfaceContact(block, selected.Map, selected.Point, selected.Normal, id, round, owner);
        }

        /// <summary>Direct authoritative contact seam, also usable by isolated contract fixtures.</summary>
        public bool TryConvertSurfaceContact(BlockDynamicBody block, Collider map, Vector3 point, Vector3 normal)
            => block != null && TryConvertSurfaceContact(block, map, point, normal, block.Id, Round, block.Owner);

        public bool TryConvertSurfaceContact(BlockDynamicBody block, Collider map, Vector3 point, Vector3 normal,
            ulong expectedId, uint expectedRound, byte expectedOwner)
        {
            if (!CanConvertSurfaceBody(block, expectedId, expectedRound, expectedOwner) ||
                !TryResolveSurfaceContact(block, map, point, normal, out var cell, out _) || cell.Owner == expectedOwner) return false;

            // Complete the potentially growing support query before either representation changes.
            // Release(false) then performs the existing pool/count lifecycle without another query.
            NotifySupportGeometryChanging(block.Collider.bounds);
            if (!CanConvertSurfaceBody(block, expectedId, expectedRound, expectedOwner)) return false;
            if (!_terrain.PaintSurfaceCell(cell.Key, expectedOwner)) return false;
            block.SurfacePaintConsumedFixedTime = Time.fixedTimeAsDouble;
            Release(block, false); RemovedCount++; LifecycleRevision++;
            // Both representations and their revisions are complete before either external event.
            // This is a representation conversion, never a second attack/payment/charge event.
            Changed?.Invoke();
            _terrain.NotifySurfacePaintChanged();
            return true;
        }

        bool CanConvertSurfaceBody(BlockDynamicBody block, ulong id, uint round, byte owner) =>
            !_disposed && IsAuthority && !Frozen && _terrain != null && _terrain.Initialized && _terrain.SurfaceOnly &&
            _terrain.Dynamic == this && _terrain.Round == Round && round == Round && id != 0 && (owner is 1 or 2) &&
            block != null && block.Id == id && block.Owner == owner && Contains(block) && block.Body != null &&
            block.Collider != null && block.Collider.enabled && block.gameObject.activeInHierarchy;

        bool TryResolveSurfaceContact(BlockDynamicBody block, Collider map, Vector3 point, Vector3 normal,
            out BlockSurfaceCell cell, out Vector3 outward)
        {
            cell = default; outward = default;
            if (map == null || !_terrain.IsAuthoredMapCollider(map) || !map.enabled || map.isTrigger ||
                !map.gameObject.activeInHierarchy || !Finite(point) || !Finite(normal) || normal.sqrMagnitude < .000001f) return false;
            outward = normal.normalized;
            // Unity contact orientation depends on which collider owns the callback. Resolve the
            // map-facing normal from the authoritative cube center before projecting to a face.
            if (Vector3.Dot(outward, block.Body.position - point) < 0) outward = -outward;
            return _terrain.TryGetSurfacePaint(map, point, outward, out cell);
        }

        static int CompareSurfaceKey(BlockSurfaceKey a, BlockSurfaceKey b)
        {
            int order = a.FaceId.CompareTo(b.FaceId); if (order != 0) return order;
            order = a.U.CompareTo(b.U); return order != 0 ? order : a.V.CompareTo(b.V);
        }
    }
}
