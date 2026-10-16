using System;
using System.Diagnostics;
using Splatoon.Networking;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct TerrainTransferObservation
        {
            public readonly long Stamp;
            public readonly string Phase, Reason;
            public readonly uint Id, Round, BaseRevision, Revision, Hash;
            public readonly int Index, Length, FragmentCount, ReceivedBefore, ReceivedAfter;
            public readonly bool Full, ResultKnown, Result;
            public readonly BlockTerrain.RemoteInstallToken ActualPublication, ExpectedInstall, AssociatedPublication;
            public readonly string AssociationAvailability;
            public readonly bool OriginalManifestBranchMatched;
            internal TerrainTransferObservation(BlockTransferFragment fragment, string phase, string reason,
                bool resultKnown, bool result, int before, int after,
                BlockTerrain.RemoteInstallToken actual = default, BlockTerrain.RemoteInstallToken expected = default,
                BlockTerrain.RemoteInstallToken associated = default, string association = "NotApplicable", bool branchMatched = false)
            {
                Stamp = Stopwatch.GetTimestamp(); Phase = phase; Reason = reason;
                Id = fragment.Id; Round = fragment.Round; BaseRevision = fragment.BaseRevision;
                Revision = fragment.Revision; Hash = fragment.Hash; Index = fragment.Index;
                Length = fragment.Length; FragmentCount = fragment.Valid ? fragment.Count : 0; Full = fragment.Full;
                ResultKnown = resultKnown; Result = result; ReceivedBefore = before; ReceivedAfter = after;
                ActualPublication = actual; ExpectedInstall = expected; AssociatedPublication = associated;
                AssociationAvailability = association; OriginalManifestBranchMatched = branchMatched;
                // No Data/Bytes references or second checksum/Verify enter the observation.
            }
        }
        public static event Action<PrototypeMatch, TerrainTransferObservation> TerrainTransferObserved;
        public static int TerrainTransferProviderErrors { get; private set; }
        BlockTerrain.RemoteInstallToken _terrainIncomingAccepted, _terrainIncomingPublished, _terrainCompletedPublished;
        public readonly struct TerrainTransferScalarSnapshot
        {
            public readonly bool Available, IncomingPresent, Installing, Complete;
            public readonly BlockTransferFragment Manifest;
            public readonly int ReceivedCount, FragmentCount;
            internal TerrainTransferScalarSnapshot(BlockTransferAssembly incoming, bool installing)
            {
                Available = true; IncomingPresent = incoming != null; Installing = installing;
                Complete = incoming != null && incoming.Complete; ReceivedCount = incoming != null ? incoming.ReceivedCount : 0;
                // Rebuild scalar manifest; never retain Manifest.Data or Bytes.
                var source = incoming != null ? incoming.Manifest : default;
                FragmentCount = incoming != null ? source.Count : 0;
                Manifest = new BlockTransferFragment { Id = source.Id, Round = source.Round, BaseRevision = source.BaseRevision,
                    Revision = source.Revision, Hash = source.Hash, Length = source.Length, Full = source.Full, Index = source.Index };
            }
        }
        public TerrainTransferScalarSnapshot InspectTerrainTransferObservation() => new(_blockIncoming, _blockInstalling);
        void RecordTerrainAcceptedObservation(BlockTerrain.RemoteInstallToken token)
        { _terrainIncomingAccepted = token; _terrainIncomingPublished = default; }
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveTerrainTransfer(BlockTransferFragment fragment, string phase, string reason = "",
            bool resultKnown = false, bool result = false, int before = -1, int after = -1)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            EmitTerrainTransfer(fragment, phase, reason, resultKnown, result, before, after);
#endif
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void EmitTerrainTransfer(BlockTransferFragment fragment, string phase, string reason, bool resultKnown, bool result, int before, int after,
            BlockTerrain.RemoteInstallToken actual = default, BlockTerrain.RemoteInstallToken expected = default,
            BlockTerrain.RemoteInstallToken associated = default, string association = "NotApplicable", bool branchMatched = false)
        {
            var handlers = TerrainTransferObserved; if (handlers == null) return;
            try
            {
                var observation = new TerrainTransferObservation(fragment, phase, reason, resultKnown, result, before, after,
                    actual, expected, associated, association, branchMatched);
                foreach (Action<PrototypeMatch, TerrainTransferObservation> handler in handlers.GetInvocationList())
                    try { handler(this, observation); } catch (Exception) { TerrainTransferProviderErrors++; }
            }
            catch (Exception) { TerrainTransferProviderErrors++; }
        }
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveTerrainPublishedCallback(BlockTransferFragment manifest)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            try
            {
                var actual = Blocks != null ? Blocks.CurrentRemotePublicationToken : default;
                var expected = _terrainIncomingAccepted;
                bool exact = actual.PublishedAvailable && actual.SameAcceptedInstall(expected) && actual.Transfer.Matches(manifest, GetInstanceID());
                string association = exact ? "Exact" : actual.AcceptedAvailable && expected.AcceptedAvailable ? "Mismatch" : "Unavailable";
                if (exact) _terrainIncomingPublished = actual;
                EmitTerrainTransfer(manifest, "PublishedCommitCallback", "", false, false, -1, -1,
                    actual, expected, exact ? actual : default, association, true);
            }
            catch (Exception) { TerrainTransferProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveTerrainAppliedAck(BlockTransferFragment manifest, bool duplicate)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            try
            {
                var published = _terrainCompletedPublished;
                bool exact = published.PublishedAvailable && published.Transfer.Matches(manifest, GetInstanceID());
                EmitTerrainTransfer(manifest, duplicate ? "CompletedDuplicateAckCallReturned" : "AppliedAckCallReturned", "",
                    false, false, -1, -1, default, published, exact ? published : default,
                    exact ? "Exact" : published.AcceptedAvailable ? "Mismatch" : "Unavailable");
            }
            catch (Exception) { TerrainTransferProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void RememberTerrainAppliedObservation()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terrainCompletedPublished = _terrainIncomingPublished;
            _terrainIncomingAccepted = _terrainIncomingPublished = default;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ResetTerrainIncomingObservation()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terrainIncomingAccepted = _terrainIncomingPublished = default;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ResetTerrainTransferObservation()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terrainIncomingAccepted = _terrainIncomingPublished = _terrainCompletedPublished = default;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveCurrentTerrainTransfer(string phase, string reason)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_blockIncoming != null) ObserveTerrainTransfer(_blockIncoming.Manifest, phase, reason);
#endif
        }
    }
}
