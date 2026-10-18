using System;
using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Prototype;
using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockDynamicWorld
    {
        public const int RangeActivationVersion = 5;
        public const float ActivationRadius = 2f, DeactivationRadius = 2f;
        const int RequiredSleepSamples = 3;
        const int MaximumSupportProofDepth = 64, MaximumSupportComponentNodes = 256;
        const int MaximumSupportQueriesPerPrepare = 4096, MaximumSupportOffsetScanObjects = 65536;
        const float SupportEnvelopeEpsilon = .0001f;
        static readonly ProfilerMarker ActivationCpu = new("Splatoon.Blocks.Activation");
        static float ContactSlack => BlockWorld.CellSize * .08f;
        static float HalfDiagonal => BlockWorld.CellSize * .866025404f;
        static float BucketSize => BlockWorld.CellSize * 2;

        [Serializable] public struct ActivationDiagnostics
        {
            public int total, active, sleeping, parked;
            public bool authority;
            // Horizontal XZ distance from each block centre to the union of all
            // spawned players; the whole vertical column is active within 2 m.
            public string distanceMetric;
            public long prePhysicsCalls, earlyRangePasses, supportChecks, parks;
            public long rangeWakes, motionWakes, supportWakes, playerContactWakes, noPlayerWakes;
            public long nativeWakeClears, unsupportedWakes, pendingMotionWakes;
            public long supportGraphBuilds, restingNodesVisited, replicaPoseWrites, skippedReplicaPoseWrites;
            public long supportProvenAdmissions, supportUnknownAdmissions, supportAbsentComponents;
            public long supportClosurePasses, supportClosureQueries, supportClosureNodes;
            public long supportExternalDeferrals, supportOverflowDeferrals, supportBudgetDeferrals, supportInvalidDeferrals;
            public long supportOffsetScans, supportOffsetColliderReads;
            public int maximumSupportComponentNodes, maximumSupportOffsetScanObjects;
            public double supportOffsetScanCpuMilliseconds, maximumSupportOffsetScanCpuMilliseconds;
            // Retained as zero-valued report fields for older acceptance readers.
            public long secondaryGuardCalls, secondaryPairChecks, secondaryFallbackSteps, secondaryFallbackWakes;
            public long maximumSecondaryPairChecks;
            public int maximumSecondaryAwakeBodies;
            public double secondaryGuardCpuMilliseconds, maximumSecondaryGuardCpuMilliseconds;
            public double secondaryFallbackCpuMilliseconds, maximumSecondaryFallbackCpuMilliseconds;
            public double cpuMilliseconds, maximumCpuMilliseconds;
        }
        ActivationDiagnostics _activation;
        int _parkedCount;
        readonly List<Vector3> _activationPlayers = new();
        readonly List<RestNode> _restNodes = new();
        readonly Dictionary<Vector3Int, List<int>> _restBuckets = new();
        readonly Stack<List<int>> _restBucketPool = new();
        readonly HashSet<ulong> _restTouched = new();
        readonly Dictionary<ulong, SupportEvidence> _supportProof = new();
        readonly Dictionary<ulong, SupportEvidence> _supportAdmissions = new();
        readonly HashSet<ulong> _supportVisiting = new();
        readonly List<BlockDynamicBody> _supportComponent = new();
        readonly HashSet<ulong> _supportComponentIds = new();
        readonly List<int> _restQueue = new();
        readonly RaycastHit[] _supportHits = new RaycastHit[64];
        readonly Collider[] _supportCandidates = new Collider[128];
        bool _supportOffsetScanAttempted, _supportOffsetBoundValid;
        float _supportMaximumContactOffset;
        int _supportQueriesThisPrepare;

        struct RestNode { public BlockDynamicBody Block; public Bounds Bounds; }
        enum SupportEvidence { Unknown, Proven, Absent }
        enum WakeReason { Range, Support, PlayerContact, NoPlayers, Unsupported }

        // Stack-local to a single body in one early/late traversal. A support
        // check can wake other bodies, and Match can change forces/geometry
        // between traversals, so no native state survives those boundaries.
        struct ActivationBodyRead
        {
            readonly Rigidbody _body;
            byte _read;
            bool _kinematic, _sleeping, _zeroMotion;
            Vector3 _position;
            Quaternion _rotation;

            public ActivationBodyRead(Rigidbody body)
            {
                _body = body; _read = 0;
                _kinematic = _sleeping = _zeroMotion = false;
                _position = default; _rotation = default;
            }

            public bool IsKinematic
            {
                get { if ((_read & 1) == 0) { _kinematic = _body.isKinematic; _read |= 1; } return _kinematic; }
            }
            public bool IsSleeping
            {
                get { if ((_read & 2) == 0) { _sleeping = _body.IsSleeping(); _read |= 2; } return _sleeping; }
            }
            public bool HasZeroMotion
            {
                get
                {
                    if ((_read & 4) == 0)
                    {
                        _zeroMotion = BlockDynamicBody.HasZeroMotion(_body.linearVelocity, _body.angularVelocity);
                        _read |= 4;
                    }
                    return _zeroMotion;
                }
            }
            public bool IsAtRest => IsSleeping && HasZeroMotion;
            public Vector3 Position
            {
                get { if ((_read & 8) == 0) { _position = _body.position; _read |= 8; } return _position; }
            }
            public Quaternion Rotation
            {
                get { if ((_read & 16) == 0) { _rotation = _body.rotation; _read |= 16; } return _rotation; }
            }
            public void WakeUp()
            {
                _body.WakeUp(); _sleeping = false; _read |= 2;
            }
        }

        public ActivationDiagnostics InspectActivation()
        {
            var result = _activation;
            result.total = _bodies.Count; result.active = result.sleeping = result.parked = 0;
            result.authority = IsAuthority;
            result.distanceMetric = "horizontal XZ; union of all spawned players; <=2m wake managed native sleepers, >2m may manage native sleep; airborne or moving bodies retain native dynamics";
            foreach (var block in _bodies.Values)
            {
                bool sleeping = Frozen || (IsAuthority ? block.IsAtRest : block.RemoteSleeping);
                if (!sleeping) result.active++;
                else if (IsAuthority && !Frozen && block.Parked) result.parked++;
                else result.sleeping++;
            }
            return result;
        }

        // Run before the ordinary Match.FixedUpdate so CharacterController pushes
        // encounter a real dynamic mass. Match calls PrepareForPhysics again at
        // the end of its step, including births and forces generated in that step.
        void FixedUpdate()
        {
            var match = PrototypeMatch.Current;
            if (!IsAuthority || Frozen || match == null || !match.IsServer || match.Blocks?.Dynamic != this) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            BlockBodyStateDiagnostic55.WorldEntry(match, this);
#endif
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            CollectPlayers(match.Players); _activation.earlyRangePasses++;
            WakeNearby(); RecordActivationTime(started);
        }

        void CollectPlayers(IReadOnlyList<PrototypePlayer> players)
        {
            _activationPlayers.Clear();
            if (players == null) return;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || !player.IsSpawned) continue;
                Vector3 position = PlayerMotorSimulation.HumanPosition(player.Snapshot.Value);
                if (Finite(position)) _activationPlayers.Add(position);
            }
        }

        public void PrepareForPhysics(IReadOnlyList<PrototypePlayer> players, float dt)
        {
            if (!IsAuthority || Frozen) return;
            CollectPlayers(players); PrepareForPhysicsCore(dt);
        }

        // Explicit observer positions also support an isolated physics world;
        // an empty observer set clears managed tags once; native sleep remains
        // available without repeatedly waking every settled mass.
        public void PrepareForPhysics(IReadOnlyList<Vector3> playerPositions, float dt)
        {
            if (!IsAuthority || Frozen) return;
            _activationPlayers.Clear();
            if (playerPositions != null)
                for (int i = 0; i < playerPositions.Count; i++)
                    if (Finite(playerPositions[i])) _activationPlayers.Add(playerPositions[i]);
            PrepareForPhysicsCore(dt);
        }

        void PrepareForPhysicsCore(float dt)
        {
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _activation.prePhysicsCalls++;
                ClearSupportAdmissionCache();
                bool canAdmit = _activationPlayers.Count != 0 && float.IsFinite(dt) && dt > 0;
                foreach (var block in _bodies.Values)
                {
                    // Reuse the existing body traversal. Resolve pending native
                    // CCD momentum before any observer/range/admission decision,
                    // including worlds with no spawned players.
                    var native = new ActivationBodyRead(block.Body);
                    WakePendingMotion(block, ref native);
                    MaintainManagement(block, ref native);
                    if (!canAdmit) { block.StableSleepSamples = 0; continue; }
                    if (block.Parked) continue;
                    if (native.IsKinematic || !native.IsAtRest ||
                        NearestPlayerSquared(native.Position) <= DeactivationRadius * DeactivationRadius)
                    { block.StableSleepSamples = 0; continue; }
                    var position = native.Position; var rotation = native.Rotation;
                    if (!position.Equals(block.SleepSamplePosition) || !rotation.Equals(block.SleepSampleRotation)) block.StableSleepSamples = 0;
                    block.SleepSamplePosition = position; block.SleepSampleRotation = rotation;
                    if (++block.StableSleepSamples < RequiredSleepSamples) continue;
                    // A missed support proof is not evidence of absent contact.
                    // Only a complete, conservatively bounded contact component
                    // with no external candidate may resume unsupported gravity.
                    var support = AssessSettledSupport(block);
                    if (support == SupportEvidence.Absent) continue;
                    if (support == SupportEvidence.Proven) _activation.supportProvenAdmissions++;
                    else _activation.supportUnknownAdmissions++;
                    // Management is a tag only. PhysX retains this dynamic
                    // mass and can wake it in this very solve on contact/force.
                    block.Parked = true; _parkedCount++; _activation.parks++;
                }
            }
            finally { ClearSupportAdmissionCache(); RecordActivationTime(started); }
        }

        void RecordActivationTime(long started)
        {
            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _activation.cpuMilliseconds += elapsed;
            _activation.maximumCpuMilliseconds = Math.Max(_activation.maximumCpuMilliseconds, elapsed);
        }

        float NearestPlayerSquared(Vector3 position)
        {
            float nearest = float.PositiveInfinity;
            foreach (var player in _activationPlayers)
            {
                float x = position.x - player.x, z = position.z - player.z;
                nearest = Mathf.Min(nearest, x * x + z * z);
            }
            return nearest;
        }

        void WakeNearby()
        {
            foreach (var block in _bodies.Values)
            {
                var native = new ActivationBodyRead(block.Body);
                WakePendingMotion(block, ref native);
                MaintainManagement(block, ref native);
            }
        }

        void WakePendingMotion(BlockDynamicBody block, ref ActivationBodyRead native)
        {
            if (native.IsKinematic || !native.IsSleeping || native.HasZeroMotion) return;
            // Unity can report sleep after CCD has already delivered nonzero
            // momentum. Without this local wake, the following solve can erase
            // that impulse. WakeUp preserves both velocities; never rewrite them.
            native.WakeUp(); _activation.pendingMotionWakes++;
        }

        void MaintainManagement(BlockDynamicBody block, ref ActivationBodyRead native)
        {
            if (_activationPlayers.Count == 0)
            {
                // An observer gap also breaks a pending three-sample admission;
                // only previously managed masses receive a one-time wake.
                if (block.Parked) WakeBody(block, WakeReason.NoPlayers, ref native);
                else block.StableSleepSamples = 0;
                return;
            }
            if (_parkedCount == 0 || !block.Parked) return;
            // Near-range wake must remain eligible even when a stale sleep flag
            // and nonzero momentum have just invalidated the management tag.
            if (NearestPlayerSquared(native.Position) <= ActivationRadius * ActivationRadius)
                WakeBody(block, WakeReason.Range, ref native);
            else if (!native.IsAtRest) { DropManagement(block); _activation.nativeWakeClears++; }
        }

        // Terrain invokes this immediately before a physical collider swap, and
        // Release invokes it before a loose supporting collider is disabled.
        // No event or deferred work may intervene before the removal.
        public void NotifySupportGeometryChanging()
        {
            if (!IsAuthority || Frozen || _bodies.Count == 0) return;
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var block in _bodies.Values) WakeBody(block, WakeReason.Support);
            RecordActivationTime(started);
        }

        public void NotifySupportGeometryChanging(Bounds changedBounds)
        {
            if (!IsAuthority || Frozen || _bodies.Count == 0) return;
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildRestIndex();
            changedBounds.Expand(ContactSlack * 2); TouchResting(changedBounds);
            RestoreRestingQueue(WakeReason.Support); RecordActivationTime(started);
        }

        // Batch all collider replacements in one publication. Rebuild the rest
        // graph only once, including untagged native sleepers. Remote
        // disconnected piles retain their actual sleeping state.
        public void NotifySupportGeometryChanging(IReadOnlyList<Bounds> changedBounds)
        {
            if (!IsAuthority || Frozen || _bodies.Count == 0 || changedBounds == null || changedBounds.Count == 0) return;
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            BuildRestIndex();
            for (int i = 0; i < changedBounds.Count; i++)
            {
                var bounds = changedBounds[i]; bounds.Expand(ContactSlack * 2); TouchResting(bounds);
            }
            RestoreRestingQueue(WakeReason.Support); RecordActivationTime(started);
        }

        // Covers a player teleport/respawn after the early range pass. Ordinary
        // nearby movement already sees dynamic bodies, making this an O(1) check.
        public void NotifyPlayerContact(BlockDynamicBody block)
        {
            if (block == null || !IsAuthority || Frozen || !Contains(block) || block.Body.isKinematic) return;
            using var marker = ActivationCpu.Auto();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            WakeBody(block, WakeReason.PlayerContact); RecordActivationTime(started);
        }

        void DropManagement(BlockDynamicBody block)
        {
            block.StableSleepSamples = 0;
            if (!block.Parked) return;
            block.Parked = false; _parkedCount--;
        }

        void WakeBody(BlockDynamicBody block, WakeReason reason)
        {
            var native = new ActivationBodyRead(block.Body);
            WakeBody(block, reason, ref native);
        }

        void WakeBody(BlockDynamicBody block, WakeReason reason, ref ActivationBodyRead native)
        {
            DropManagement(block);
            // A contact/force may have already woken the mass. Never rewrite
            // its velocity or disturb any disconnected body's native sleep.
            if (native.IsKinematic || !native.IsSleeping) return;
            native.WakeUp();
            switch (reason)
            {
                case WakeReason.Range: _activation.rangeWakes++; break;
                case WakeReason.Support: _activation.supportWakes++; break;
                case WakeReason.PlayerContact: _activation.playerContactWakes++; break;
                case WakeReason.NoPlayers: _activation.noPlayerWakes++; break;
                case WakeReason.Unsupported: _activation.unsupportedWakes++; break;
            }
        }

        SupportEvidence AssessSettledSupport(BlockDynamicBody block)
        {
            if (_supportAdmissions.TryGetValue(block.Id, out var cached)) return cached;
            if (HasSettledSupport(block) == SupportEvidence.Proven)
                return _supportAdmissions[block.Id] = SupportEvidence.Proven;

            // Most real sleepers encounter an external candidate in this small
            // local pass. They need no scene collection or stronger proof.
            var evidence = FindUnanchoredSupportComponent(block, false);
            if (evidence == SupportEvidence.Absent)
            {
                // The first envelope is only a cheap filter. Before declaring
                // absence, bound every actual collider's contact offset, then
                // rebuild the entire component with that verified envelope.
                if (TryBoundSupportContactOffsets()) evidence = FindUnanchoredSupportComponent(block, true);
                else evidence = SupportEvidence.Unknown;
            }
            foreach (var member in _supportComponent) _supportAdmissions[member.Id] = evidence;
            if (evidence != SupportEvidence.Absent) return evidence;
            _activation.supportAbsentComponents++;
            foreach (var member in _supportComponent)
                if (Contains(member)) WakeBody(member, WakeReason.Unsupported);
            return evidence;
        }

        SupportEvidence HasSettledSupport(BlockDynamicBody block)
        {
            if (_supportProof.TryGetValue(block.Id, out var proven)) return proven;
            if (_supportVisiting.Count >= MaximumSupportProofDepth)
            { _activation.supportBudgetDeferrals++; return SupportEvidence.Unknown; }
            if (!_supportVisiting.Add(block.Id)) return SupportEvidence.Unknown;
            var supported = FindSettledSupport(block);
            _supportVisiting.Remove(block.Id); _supportProof[block.Id] = supported;
            return supported;
        }

        SupportEvidence FindSettledSupport(BlockDynamicBody block)
        {
            _activation.supportChecks++;
            var bounds = block.Collider.bounds;
            float slack = BlockWorld.CellSize * .04f + block.Collider.contactOffset;
            if (!Finite(bounds.min) || !Finite(bounds.max) || !float.IsFinite(slack) || slack <= 0)
            { _activation.supportInvalidDeferrals++; return SupportEvidence.Unknown; }
            int count = Physics.RaycastNonAlloc(bounds.center, Vector3.down, _supportHits, bounds.extents.y + slack,
                ~((1 << 8) | (1 << 10) | (1 << SubWeaponTarget.Layer)), QueryTriggerInteraction.Ignore);
            if (count == _supportHits.Length) return SupportEvidence.Unknown;
            for (int i = 0; i < count; i++)
            {
                var hit = _supportHits[i];
                if (hit.collider == null || hit.collider == block.Collider || hit.normal.y < .7f ||
                    Mathf.Abs(hit.point.y - bounds.min.y) > slack) continue;
                var support = hit.rigidbody;
                if (support == null) return SupportEvidence.Proven;
                var other = support.GetComponent<BlockDynamicBody>();
                if (other != null && other != block && Contains(other))
                    // This shared-buffer shortcut can only establish positive
                    // evidence. Its misses and cycles remain unknown.
                    return !support.isKinematic && other.IsAtRest ? HasSettledSupport(other) : SupportEvidence.Unknown;
            }
            return SupportEvidence.Unknown;
        }

        SupportEvidence FindUnanchoredSupportComponent(BlockDynamicBody root, bool verifiedOffsets)
        {
            _activation.supportClosurePasses++;
            _supportComponent.Clear(); _supportComponentIds.Clear();
            _supportComponent.Add(root); _supportComponentIds.Add(root.Id);
            for (int index = 0; index < _supportComponent.Count; index++)
            {
                var block = _supportComponent[index];
                _activation.supportClosureNodes++;
                _activation.maximumSupportComponentNodes = Math.Max(_activation.maximumSupportComponentNodes, _supportComponent.Count);
                if (!Contains(block) || block.Body.isKinematic || !block.IsAtRest ||
                    !block.Collider.enabled || !block.gameObject.activeInHierarchy || block.Collider.isTrigger)
                { _activation.supportExternalDeferrals++; return SupportEvidence.Unknown; }
                var bounds = block.Collider.bounds;
                float offset = block.Collider.contactOffset;
                float margin = verifiedOffsets ? Mathf.Max(ContactSlack, offset + _supportMaximumContactOffset + SupportEnvelopeEpsilon) : ContactSlack;
                var extents = bounds.extents + Vector3.one * margin;
                if (!Finite(bounds.center) || !Finite(bounds.min) || !Finite(bounds.max) || !Finite(extents) ||
                    !Finite(bounds.center - extents) || !Finite(bounds.center + extents) ||
                    bounds.extents.x < 0 || bounds.extents.y < 0 || bounds.extents.z < 0 ||
                    extents.x <= 0 || extents.y <= 0 || extents.z <= 0 || !float.IsFinite(offset) || offset <= 0 ||
                    !float.IsFinite(margin) || margin <= 0)
                { _activation.supportInvalidDeferrals++; return SupportEvidence.Unknown; }
                if (_supportQueriesThisPrepare >= MaximumSupportQueriesPerPrepare)
                { _activation.supportBudgetDeferrals++; return SupportEvidence.Unknown; }
                _supportQueriesThisPrepare++; _activation.supportClosureQueries++;
                // Include every layer and every side of the body's AABB. Extra
                // candidates are harmless; rejecting a side or layer is not an
                // admissible proof that real physical support is absent.
                int count = Physics.OverlapBoxNonAlloc(bounds.center, extents, _supportCandidates,
                    Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                if (count == _supportCandidates.Length)
                { _activation.supportOverflowDeferrals++; return SupportEvidence.Unknown; }
                for (int i = 0; i < count; i++)
                {
                    var candidate = _supportCandidates[i];
                    if (candidate == null || candidate == block.Collider || !candidate.enabled ||
                        !candidate.gameObject.activeInHierarchy || candidate.isTrigger) continue;
                    var body = candidate.attachedRigidbody;
                    var other = body == null ? null : body.GetComponent<BlockDynamicBody>();
                    if (other == null || !Contains(other) || candidate != other.Collider || body.isKinematic || !other.IsAtRest)
                    { _activation.supportExternalDeferrals++; return SupportEvidence.Unknown; }
                    if (_supportComponentIds.Contains(other.Id)) continue;
                    if (_supportComponent.Count >= MaximumSupportComponentNodes)
                    { _activation.supportBudgetDeferrals++; return SupportEvidence.Unknown; }
                    _supportComponentIds.Add(other.Id); _supportComponent.Add(other);
                }
            }
            return SupportEvidence.Absent;
        }

        bool TryBoundSupportContactOffsets()
        {
            if (_supportOffsetScanAttempted) return _supportOffsetBoundValid;
            _supportOffsetScanAttempted = true; _activation.supportOffsetScans++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // FindObjectsByType omits DontSave objects. The all-loaded
                // collection also covers hidden live physics shapes; assets and
                // inactive/disabled/trigger colliders cannot provide support.
                var colliders = Resources.FindObjectsOfTypeAll<Collider>();
                _activation.maximumSupportOffsetScanObjects = Math.Max(_activation.maximumSupportOffsetScanObjects, colliders.Length);
                if (colliders.Length > MaximumSupportOffsetScanObjects)
                { _activation.supportBudgetDeferrals++; return false; }
                foreach (var collider in colliders)
                {
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                        !collider.gameObject.scene.IsValid() || !collider.gameObject.scene.isLoaded) continue;
                    _activation.supportOffsetColliderReads++;
                    float offset = collider.contactOffset;
                    if (!float.IsFinite(offset) || offset <= 0)
                    { _activation.supportInvalidDeferrals++; return false; }
                    _supportMaximumContactOffset = Mathf.Max(_supportMaximumContactOffset, offset);
                }
                if (_supportMaximumContactOffset <= 0)
                { _activation.supportInvalidDeferrals++; return false; }
                _supportOffsetBoundValid = true;
                return true;
            }
            finally
            {
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _activation.supportOffsetScanCpuMilliseconds += elapsed;
                _activation.maximumSupportOffsetScanCpuMilliseconds = Math.Max(_activation.maximumSupportOffsetScanCpuMilliseconds, elapsed);
            }
        }

        void ClearSupportAdmissionCache()
        {
            _supportProof.Clear(); _supportAdmissions.Clear(); _supportVisiting.Clear();
            _supportComponent.Clear(); _supportComponentIds.Clear();
            Array.Clear(_supportCandidates, 0, _supportCandidates.Length);
            _supportOffsetScanAttempted = _supportOffsetBoundValid = false;
            _supportMaximumContactOffset = 0; _supportQueriesThisPrepare = 0;
        }

        static Vector3Int RestBucket(Vector3 point) => new(Mathf.FloorToInt(point.x / BucketSize),
            Mathf.FloorToInt(point.y / BucketSize), Mathf.FloorToInt(point.z / BucketSize));

        void ClearRestIndex()
        {
            foreach (var bucket in _restBuckets.Values) { bucket.Clear(); _restBucketPool.Push(bucket); }
            _restBuckets.Clear(); _restNodes.Clear(); _restTouched.Clear(); _restQueue.Clear();
        }

        void BuildRestIndex()
        {
            _activation.supportGraphBuilds++;
            ClearRestIndex();
            foreach (var block in _bodies.Values)
            {
                if (block.Body.isKinematic || !block.Body.IsSleeping()) continue;
                int index = _restNodes.Count;
                var bounds = block.Collider.bounds;
                _restNodes.Add(new RestNode { Block = block, Bounds = bounds });
                var key = RestBucket(bounds.center);
                if (!_restBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = _restBucketPool.Count != 0 ? _restBucketPool.Pop() : new List<int>();
                    _restBuckets.Add(key, bucket);
                }
                bucket.Add(index);
            }
        }

        void TouchResting(Bounds contact)
        {
            if (_restNodes.Count == 0) return;
            var search = contact; search.Expand(HalfDiagonal * 2);
            var low = RestBucket(search.min); var high = RestBucket(search.max);
            long columns = (long)high.x - low.x + 1, rows = (long)high.y - low.y + 1, depth = (long)high.z - low.z + 1;
            // An unusually large support publication must not enumerate an
            // enormous empty grid. A complete node scan remains bounded.
            if (columns > _restNodes.Count || rows > _restNodes.Count || depth > _restNodes.Count ||
                columns * rows * depth > _restNodes.Count)
            {
                for (int i = 0; i < _restNodes.Count; i++) TouchNode(i, contact);
                return;
            }
            for (int x = low.x; x <= high.x; x++) for (int y = low.y; y <= high.y; y++) for (int z = low.z; z <= high.z; z++)
                if (_restBuckets.TryGetValue(new Vector3Int(x, y, z), out var bucket))
                    foreach (int index in bucket) TouchNode(index, contact);
        }

        void TouchNode(int index, Bounds contact)
        {
            var node = _restNodes[index];
            if (!node.Bounds.Intersects(contact) || !Contains(node.Block) || !_restTouched.Add(node.Block.Id)) return;
            _activation.restingNodesVisited++; _restQueue.Add(index);
        }

        void RestoreRestingQueue(WakeReason reason)
        {
            for (int i = 0; i < _restQueue.Count; i++)
            {
                var node = _restNodes[_restQueue[i]];
                if (!Contains(node.Block)) continue;
                WakeBody(node.Block, reason);
                var contact = node.Bounds; contact.Expand(ContactSlack * 2);
                TouchResting(contact);
            }
        }

        void ClearActivationState(bool counters)
        {
            foreach (var block in _bodies.Values) { block.Parked = false; block.StableSleepSamples = 0; }
            _parkedCount = 0; _activationPlayers.Clear(); ClearRestIndex();
            ClearSupportAdmissionCache();
            if (counters) _activation = default;
        }
    }
}
