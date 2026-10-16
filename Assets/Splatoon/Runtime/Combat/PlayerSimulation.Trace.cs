using System;
using System.Diagnostics;
using Splatoon.Blocks;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed partial class PlayerMotorSimulation
    {
        public enum MotorTraceBranch : byte
        { EntryGround, DestinationMove, WallMove, GroundEvaluation, GroundResolved, AirEntry, AirExit, StepComplete,
            WallAttachEvaluation, WallAttachResolved }

        bool MotorTracing
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return MotorBranchObserved != null;
#else
                return false;
#endif
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Value copies of the exact inputs, queries and branch states used by one motor invocation.</summary>
        public readonly struct MotorBranchObservation
        {
            public readonly PlayerSnapshot Before, After;
            public readonly PlayerInputFrame Input;
            public readonly MotorTraceBranch Branch;
            public readonly TraversalContact Contact;
            public readonly bool ProbeEvaluated, ProbeSucceeded, ControllerGrounded, StandingEvaluated, StandingFits, IdentityAvailable;
            public readonly bool WantsFire, ShootingMovement;
            public readonly CollisionFlags Collisions;
            public readonly Vector3 RequestedMove, ControllerPosition;
            public readonly float RebaseHeight;
            public readonly uint TerrainRound, TerrainRevision, SimulationTick, Life;
            public readonly ulong Owner, NetworkObjectId, Invocation;
            public readonly int PlayerObjectId;
            public readonly double SimulatedAt, Realtime, TerrainTime;

            internal MotorBranchObservation(PlayerMotorSimulation motor, MotorTraceBranch branch,
                PlayerSnapshot before, PlayerSnapshot after, bool probeEvaluated, bool probeSucceeded,
                TraversalContact contact, CollisionFlags collisions, Vector3 requestedMove, float rebaseHeight,
                bool standingEvaluated, bool standingFits)
            {
                Before = before; After = after; Input = motor._motorTraceInput; Branch = branch; Contact = contact;
                ProbeEvaluated = probeEvaluated; ProbeSucceeded = probeSucceeded;
                ControllerGrounded = motor._controller.isGrounded; ControllerPosition = motor._root.position;
                StandingEvaluated = standingEvaluated; StandingFits = standingFits;
                WantsFire = motor._motorTraceWantsFire; ShootingMovement = motor._motorTraceShootingMovement;
                Collisions = collisions; RequestedMove = requestedMove; RebaseHeight = rebaseHeight;
                SimulationTick = after.SimulationTick; Life = after.Revision; SimulatedAt = after.SimulatedAt;
                Invocation = motor._motorTraceInvocation; Realtime = Time.realtimeSinceStartupAsDouble;
                var terrain = BlockTerrain.Current;
                bool hasTerrain = terrain != null && terrain.Initialized;
                TerrainRound = hasTerrain ? terrain.Round : 0; TerrainRevision = hasTerrain ? terrain.Revision : 0;
                TerrainTime = hasTerrain ? terrain.SimulationTime : -1;
                var player = motor._root.GetComponent<PrototypePlayer>();
                PlayerObjectId = player != null ? player.GetInstanceID() : 0;
                IdentityAvailable = player != null && player.IsSpawned;
                Owner = IdentityAvailable ? player.PlayerId : 0; NetworkObjectId = IdentityAvailable ? player.NetworkObjectId : 0;
            }
        }

        public static event Action<PlayerMotorSimulation, MotorBranchObservation> MotorBranchObserved;
        public static int MotorBranchObserverErrors { get; private set; }
        public static string LastMotorBranchObserverError { get; private set; }
        PlayerInputFrame _motorTraceInput;
        ulong _motorTraceInvocation;
        bool _motorTraceWantsFire, _motorTraceShootingMovement;
#endif

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void BeginMotorTrace(PlayerInputFrame input, bool wantsFire, bool shootingMovement)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (MotorBranchObserved == null) return;
            _motorTraceInput = input; _motorTraceWantsFire = wantsFire; _motorTraceShootingMovement = shootingMovement;
            _motorTraceInvocation++;
#endif
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void TraceMotor(MotorTraceBranch branch, PlayerSnapshot before, PlayerSnapshot after,
            bool probeEvaluated = false, bool probeSucceeded = false, TraversalContact contact = default,
            CollisionFlags collisions = CollisionFlags.None, Vector3 requestedMove = default, float rebaseHeight = 0,
            bool standingEvaluated = false, bool standingFits = false)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var observers = MotorBranchObserved;
            if (observers == null) return;
            var observation = new MotorBranchObservation(this, branch, before, after, probeEvaluated, probeSucceeded,
                contact, collisions, requestedMove, rebaseHeight, standingEvaluated, standingFits);
            foreach (Action<PlayerMotorSimulation, MotorBranchObservation> observer in observers.GetInvocationList())
            {
                try { observer(this, observation); }
                catch (Exception exception) { MotorBranchObserverErrors++; LastMotorBranchObserverError = exception.ToString(); }
            }
#endif
        }
    }
}
