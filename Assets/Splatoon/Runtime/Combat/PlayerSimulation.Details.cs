using System;
using System.Diagnostics;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class PlayerMotorSimulation
    {
        public enum MotorDetailKind : byte { Begin, Move, GroundQuery, WallDetach, Complete }
        public enum GroundWinner : byte { None, Center, Right, Left, Forward, Back, SphereCast, PublishedOverlap }
        [Flags] public enum GroundEvaluated : ushort
        { None = 0, Center = 1, Right = 2, Left = 4, Forward = 8, Back = 16, SphereCast = 32, OverlapCheck = 64, PublishedOverlap = 128 }
        public enum WallDetachReason : byte
        { None, CurrentMissingInBlockMode, CurrentNotClimbable, SwimReleased, WantsFire, JumpRequested, NextNotClimbable, NextNormalMismatch, NextMissingAfterRecovery, FinalPaperObstructed }

        [Serializable] public struct GroundQueryDetails
        {
            public bool Evaluated, BlockTraversal;
            public Vector3 Feet;
            public float SlopeLimit, Footprint;
            public GroundEvaluated EvaluatedPaths;
            public GroundWinner Winner;
        }
        [Serializable] public struct MoveCapsuleDetails
        {
            public bool MoveExecuted;
            public float Height, Radius, StepOffset, SkinWidth, SlopeLimit, MinMoveDistance;
            public Vector3 Center;
        }
        [Serializable] public struct DetailContact
        {
            public byte Owner;
            public Vector3 Point, Normal;
            public Vector3Int Cell;
            public uint Revision;
            public int SurfaceId, RegionId;
            public bool IsBlock, Stable, Climbable;
            public DetailContact(TraversalContact c)
            { Owner = c.Owner; Point = c.Point; Normal = c.Normal; Cell = c.Cell; Revision = c.Revision;
                SurfaceId = c.SurfaceId; RegionId = c.RegionId; IsBlock = c.IsBlock; Stable = c.Stable; Climbable = c.Climbable; }
        }
        [Serializable] public struct WallDetachDetails
        {
            public bool Evaluated, BlockTraversal, JumpRequested;
            public bool CurrentEvaluated, CurrentFound, CurrentClimbEvaluated, CurrentCanClimb;
            public bool NextEvaluated, InitialNextFound, NextFound, NextClimbEvaluated, NextCanClimb, NeutralRetryEvaluated;
            public bool NeutralConditionClimbEvaluated, NeutralConditionCanClimb;
            public bool NormalEvaluated, NormalMatch, MantleEvaluated, MantleSucceeded;
            public bool GraceEvaluated, GraceAvailable, SeamEvaluated, SeamSucceeded;
            public float NormalDot;
            public DetailContact Current, InitialNext, Next;
            public SwimSurface SeamSource;
            public MovementMode BeforeMovement, AfterMovement;
            public WallDetachReason Reason;
        }
        // These wrappers record only a result already evaluated by the original
        // expression. They return that result and never perform a query.
        static bool ObserveWallBool(bool result, ref bool evaluated, ref bool value)
        { evaluated = true; value = result; return result; }
        static bool ObserveWallNormal(float dot, ref WallDetachDetails details)
        { details.NormalEvaluated = true; details.NormalDot = dot; return details.NormalMatch = dot > .99f; }
        static MoveCapsuleDetails CaptureMoveCapsule(CharacterController c) => new()
        { MoveExecuted = true, Height = c.height, Center = c.center, Radius = c.radius, StepOffset = c.stepOffset,
            SkinWidth = c.skinWidth, SlopeLimit = c.slopeLimit, MinMoveDistance = c.minMoveDistance };

        bool MotorDetailTracing
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _motorDetailActive;
#else
                return false;
#endif
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct MotorDetailObservation
        {
            public readonly MotorDetailKind Kind;
            public readonly MotorTraceBranch SourceBranch;
            public readonly ulong Invocation;
            public readonly MoveCapsuleDetails Capsule;
            public readonly GroundQueryDetails Ground;
            public readonly WallDetachDetails Wall;
            public readonly PlayerInputFrame Input;
            public readonly ulong Owner, NetworkObjectId;
            public readonly int PlayerObjectId;
            public readonly uint Life, SimulationTick, TerrainRound;
            public readonly bool IdentityAvailable;
            public readonly int ProviderErrorsAtEmission;
            public readonly double Realtime;
            internal MotorDetailObservation(PlayerMotorSimulation motor, MotorDetailKind kind,
                MotorTraceBranch branch, MoveCapsuleDetails capsule, GroundQueryDetails ground, WallDetachDetails wall)
            {
                Kind = kind; SourceBranch = branch; Invocation = motor._motorTraceInvocation;
                Capsule = capsule; Ground = ground; Wall = wall; Input = motor._motorTraceInput;
                Owner = motor._motorDetailOwner; NetworkObjectId = motor._motorDetailNetworkObjectId;
                PlayerObjectId = motor._motorDetailPlayerObjectId; IdentityAvailable = motor._motorDetailIdentityAvailable;
                Life = motor._motorDetailLife; SimulationTick = motor._motorDetailSimulationTick; TerrainRound = motor._motorDetailRound;
                Realtime = Time.realtimeSinceStartupAsDouble;
                ProviderErrorsAtEmission = MotorDetailObserverErrors;
            }
        }
        // Separate event and errors: the legacy motor schema, row population,
        // enum values and provider error statistics remain unchanged.
        public static event Action<PlayerMotorSimulation, MotorDetailObservation> MotorDetailObserved;
        public static event Action<PlayerMotorSimulation, MotorDetailObservation> MotorDetailInvocationFinished;
        public static int MotorDetailObserverErrors { get; private set; }
        bool _motorDetailActive, _motorDetailIdentityAvailable;
        ulong _motorDetailOwner, _motorDetailNetworkObjectId;
        int _motorDetailPlayerObjectId;
        uint _motorDetailLife, _motorDetailSimulationTick, _motorDetailRound;
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void BeginMotorDetails(PlayerSnapshot state)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Never enable the legacy motor trace as a side effect of details.
            _motorDetailActive = MotorTracing && MotorDetailObserved != null;
            if (!_motorDetailActive) return;
            var player = _root.GetComponent<PrototypePlayer>();
            _motorDetailPlayerObjectId = player != null ? player.GetInstanceID() : 0;
            _motorDetailIdentityAvailable = player != null && player.IsSpawned;
            _motorDetailOwner = _motorDetailIdentityAvailable ? player.PlayerId : 0;
            _motorDetailNetworkObjectId = _motorDetailIdentityAvailable ? player.NetworkObjectId : 0;
            _motorDetailLife = state.Revision; _motorDetailSimulationTick = state.SimulationTick;
            var terrain = Splatoon.Blocks.BlockTerrain.Current;
            _motorDetailRound = terrain != null && terrain.Initialized ? terrain.Round : 0;
            EmitMotorDetails(MotorDetailKind.Begin);
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void EndMotorDetails()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            EmitMotorDetails(MotorDetailKind.Complete, MotorTraceBranch.StepComplete);
            // Finalize only after every data observer has run and its errors are known.
            var finished = MotorDetailInvocationFinished;
            if (_motorDetailActive && finished != null)
            {
                var observation = new MotorDetailObservation(this, MotorDetailKind.Complete, MotorTraceBranch.StepComplete, default, default, default);
                foreach (Action<PlayerMotorSimulation, MotorDetailObservation> observer in finished.GetInvocationList())
                { try { observer(this, observation); } catch (Exception) { MotorDetailObserverErrors++; } }
            }
            _motorDetailActive = false;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void EmitMotorDetails(MotorDetailKind kind, MotorTraceBranch branch = default,
            MoveCapsuleDetails capsule = default, GroundQueryDetails ground = default, WallDetachDetails wall = default)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_motorDetailActive) return;
            var observers = MotorDetailObserved; if (observers == null) return;
            var observation = new MotorDetailObservation(this, kind, branch, capsule, ground, wall);
            foreach (Action<PlayerMotorSimulation, MotorDetailObservation> observer in observers.GetInvocationList())
            { try { observer(this, observation); } catch (Exception) { MotorDetailObserverErrors++; } }
#endif
        }
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Reused by the opt-in probe and the bounded PhysX fixtures. No sampling loop.
    public sealed class MotorDetailCapture
    {
        public const int DefaultInvocationCap = 2048, DefaultRowCap = 16384, DefaultSlotCap = 32, DefaultRowsPerInvocation = 32;
        [Serializable] public struct Row
        {
            public long sourceMotorEventId, anchorMotorEventId;
            public string kind, sourceBranch;
            public double realtime;
            public PlayerMotorSimulation.MoveCapsuleDetails capsule;
            public PlayerMotorSimulation.GroundQueryDetails ground;
            public PlayerMotorSimulation.WallDetachDetails wall;
        }
        [Serializable] public sealed class InvocationSample
        {
            public ulong invocation, owner, networkObjectId;
            public int playerObjectId, connectionGeneration;
            public uint life, simulationTick, terrainRound;
            public bool identityAvailable;
            public long firstMotorEventId, terminalMotorEventId;
            public Row[] rows;
        }
        [Serializable] public sealed class Snapshot
        {
            public int schema = 1, invocationCap, rowCap, slotCap, rowsPerInvocation;
            public long startedInvocations, retainedInvocations, retainedRows, droppedInvocations, droppedRows, incompleteInvocations;
            public long consumerErrors, providerErrors;
            public bool closed, complete;
            public InvocationSample[] invocations;
        }
        sealed class Slot
        {
            public PlayerMotorSimulation Motor;
            public PlayerMotorSimulation.MotorDetailObservation Begin;
            public readonly Row[] Rows;
            public int Count, SeenRows, TerminalCount, ProviderErrorsAtStart, ConnectionGeneration;
            public long FirstEventId, LastEventId;
            public PlayerMotorSimulation.MotorTraceBranch LastBranch;
            public bool Invalid, CompleteObserved, FinalizationObserved;
            public Slot(int capacity) => Rows = new Row[capacity];
        }
        readonly Slot[] _slots;
        readonly InvocationSample[] _retained;
        readonly Snapshot _counts;
        readonly int _rowCap, _rowsPerInvocation, _providerBaseline;
        int _retainedCount, _retainedRows;
        bool _closed;
        public MotorDetailCapture(int invocationCap = DefaultInvocationCap, int rowCap = DefaultRowCap,
            int slotCap = DefaultSlotCap, int rowsPerInvocation = DefaultRowsPerInvocation)
        {
            if (invocationCap < 1 || invocationCap > DefaultInvocationCap || rowCap < 1 || rowCap > DefaultRowCap ||
                slotCap < 1 || slotCap > DefaultSlotCap || rowsPerInvocation < 1 || rowsPerInvocation > DefaultRowsPerInvocation)
                throw new ArgumentOutOfRangeException(nameof(invocationCap));
            _slots = new Slot[slotCap]; for (int i = 0; i < slotCap; i++) _slots[i] = new Slot(rowsPerInvocation);
            _retained = new InvocationSample[invocationCap]; _rowCap = rowCap; _rowsPerInvocation = rowsPerInvocation;
            _providerBaseline = PlayerMotorSimulation.MotorDetailObserverErrors;
            _counts = new Snapshot { invocationCap = invocationCap, rowCap = rowCap, slotCap = slotCap, rowsPerInvocation = rowsPerInvocation };
        }
        Slot Find(PlayerMotorSimulation motor, ulong invocation)
        {
            foreach (var slot in _slots)
                if (ReferenceEquals(slot.Motor, motor) && slot.Begin.Invocation == invocation) return slot;
            return null;
        }
        static bool SameIdentity(PlayerMotorSimulation.MotorBranchObservation source, PlayerMotorSimulation.MotorDetailObservation begin)
            => source.Invocation == begin.Invocation && source.Owner == begin.Owner && source.NetworkObjectId == begin.NetworkObjectId &&
               source.PlayerObjectId == begin.PlayerObjectId && source.Life == begin.Life && source.SimulationTick == begin.SimulationTick &&
               source.TerrainRound == begin.TerrainRound && source.IdentityAvailable == begin.IdentityAvailable && source.Input.Equals(begin.Input);
        // Call after the untouched legacy consumer appends its complete source row.
        public void OnBaseEvent(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation source, long eventId)
        {
            if (_closed) return;
            try
            {
                var slot = Find(motor, source.Invocation); if (slot == null) return;
                if (eventId <= slot.LastEventId || !SameIdentity(source, slot.Begin)) { slot.Invalid = true; return; }
                if (slot.FirstEventId == 0) slot.FirstEventId = eventId;
                slot.LastEventId = eventId; slot.LastBranch = source.Branch;
                if (source.Branch == PlayerMotorSimulation.MotorTraceBranch.StepComplete) slot.TerminalCount++;
            }
            catch (Exception) { RecordConsumerError(motor, source.Invocation); }
        }
        public void OnDetail(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorDetailObservation sample, int connectionGeneration = 0)
        {
            if (_closed) return;
            try
            {
                if (sample.Kind == PlayerMotorSimulation.MotorDetailKind.Begin)
                {
                    _counts.startedInvocations++;
                    foreach (var old in _slots) if (ReferenceEquals(old.Motor, motor))
                    { if (old.FinalizationObserved) Commit(old); else Drop(old, true); }
                    if (_retainedCount >= _retained.Length) { _counts.droppedInvocations++; return; }
                    Slot available = null; foreach (var candidate in _slots) if (candidate.Motor == null) { available = candidate; break; }
                    if (available == null) { _counts.droppedInvocations++; return; }
                    available.Motor = motor; available.Begin = sample; available.Count = available.SeenRows = available.TerminalCount = 0;
                    available.FirstEventId = available.LastEventId = 0;
                    available.Invalid = available.CompleteObserved = available.FinalizationObserved = false;
                    available.ConnectionGeneration = connectionGeneration;
                    available.ProviderErrorsAtStart = sample.ProviderErrorsAtEmission;
                    return;
                }
                var slot = Find(motor, sample.Invocation);
                if (slot == null)
                { if (sample.Kind != PlayerMotorSimulation.MotorDetailKind.Complete) _counts.droppedRows++; return; }
                if (sample.Kind == PlayerMotorSimulation.MotorDetailKind.Complete)
                {
                    if (slot.CompleteObserved || slot.LastBranch != PlayerMotorSimulation.MotorTraceBranch.StepComplete) slot.Invalid = true;
                    slot.CompleteObserved = true; return;
                }
                slot.SeenRows++;
                if (slot.Count >= _rowsPerInvocation) { slot.Invalid = true; return; }
                bool exact = sample.Kind != PlayerMotorSimulation.MotorDetailKind.WallDetach;
                if (slot.LastEventId <= 0 || exact && slot.LastBranch != sample.SourceBranch) slot.Invalid = true;
                slot.Rows[slot.Count++] = new Row { sourceMotorEventId = exact ? slot.LastEventId : 0, anchorMotorEventId = slot.LastEventId,
                    kind = sample.Kind.ToString(), sourceBranch = exact ? sample.SourceBranch.ToString() : "",
                    realtime = sample.Realtime, capsule = sample.Capsule, ground = sample.Ground, wall = sample.Wall };
            }
            catch (Exception) { _counts.consumerErrors++; var slot = Find(motor, sample.Invocation); if (slot != null) slot.Invalid = true; }
        }
        public void OnInvocationFinished(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorDetailObservation sample, int connectionGeneration = 0)
        {
            if (_closed) return;
            try
            {
                var slot = Find(motor, sample.Invocation); if (slot == null) return;
                if (slot.FinalizationObserved || slot.ConnectionGeneration != connectionGeneration) slot.Invalid = true;
                // A later finalization observer can still throw. Keep the fixed
                // slot until the next Begin on this motor, or explicit Finish,
                // when the entire finalizer dispatch has returned.
                slot.FinalizationObserved = true;
            }
            catch (Exception) { RecordConsumerError(motor, sample.Invocation); }
        }
        void Commit(Slot slot)
        {
            var motor = slot.Motor; ulong invocation = slot.Begin.Invocation;
            try
            {
                bool full = !slot.Invalid && slot.CompleteObserved && slot.TerminalCount == 1 && slot.LastBranch == PlayerMotorSimulation.MotorTraceBranch.StepComplete &&
                    slot.FinalizationObserved &&
                    slot.ProviderErrorsAtStart == PlayerMotorSimulation.MotorDetailObserverErrors;
                if (!full || _retainedCount >= _retained.Length || _retainedRows + slot.Count > _rowCap) { Drop(slot, !full); return; }
                var rows = new Row[slot.Count]; Array.Copy(slot.Rows, rows, slot.Count);
                var begin = slot.Begin;
                _retained[_retainedCount] = new InvocationSample { invocation = begin.Invocation, owner = begin.Owner,
                    networkObjectId = begin.NetworkObjectId, playerObjectId = begin.PlayerObjectId, connectionGeneration = slot.ConnectionGeneration,
                    life = begin.Life, simulationTick = begin.SimulationTick, terrainRound = begin.TerrainRound,
                    identityAvailable = begin.IdentityAvailable, firstMotorEventId = slot.FirstEventId, terminalMotorEventId = slot.LastEventId, rows = rows };
                _retainedCount++; _retainedRows += rows.Length; Release(slot);
            }
            catch (Exception) { RecordConsumerError(motor, invocation); Drop(slot, true); }
        }
        public void RecordConsumerError(PlayerMotorSimulation motor, ulong invocation)
        { _counts.consumerErrors++; var slot = Find(motor, invocation); if (slot != null) slot.Invalid = true; }
        void Drop(Slot slot, bool incomplete)
        {
            _counts.droppedInvocations++; _counts.droppedRows += slot.SeenRows;
            if (incomplete) _counts.incompleteInvocations++;
            Release(slot);
        }
        static void Release(Slot slot)
        { Array.Clear(slot.Rows, 0, slot.Count); slot.Motor = null; slot.Count = slot.SeenRows = slot.TerminalCount = 0; }
        public void Close()
        {
            if (_closed) return; _closed = true;
            foreach (var slot in _slots) if (slot.Motor != null)
            { if (slot.FinalizationObserved) Commit(slot); else Drop(slot, true); }
        }
        // Called only at explicit finalization, never once per frame.
        public Snapshot Finish()
        {
            Close(); _counts.retainedInvocations = _retainedCount; _counts.retainedRows = _retainedRows;
            _counts.providerErrors = Math.Max(0, PlayerMotorSimulation.MotorDetailObserverErrors - _providerBaseline);
            _counts.complete = _counts.droppedInvocations == 0 && _counts.incompleteInvocations == 0 && _counts.consumerErrors == 0 && _counts.providerErrors == 0;
            _counts.closed = true; var groups = new InvocationSample[_retainedCount]; Array.Copy(_retained, groups, _retainedCount);
            _counts.invocations = groups; return _counts;
        }
    }
#endif
}
