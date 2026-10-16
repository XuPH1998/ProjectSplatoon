using System;
using System.Diagnostics;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        bool RemoteTransferObservationEnabled
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return RemoteInstallObserved != null;
#else
                return false;
#endif
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly struct RemoteTransferContext
        {
            public readonly uint Id, Round, BaseRevision, Revision, Hash;
            public readonly bool Full, Available;
            public readonly int MatchObjectId, Length, FragmentCount;
            internal RemoteTransferContext(BlockTransferFragment fragment, int match)
            {
                Id = fragment.Id; Round = fragment.Round; BaseRevision = fragment.BaseRevision;
                Revision = fragment.Revision; Hash = fragment.Hash; Full = fragment.Full;
                MatchObjectId = match; Length = fragment.Length; FragmentCount = fragment.Valid ? fragment.Count : 0; Available = true;
            }
            public bool Matches(BlockTransferFragment fragment, int match)
                => Available && MatchObjectId == match && Id == fragment.Id && Round == fragment.Round &&
                    BaseRevision == fragment.BaseRevision && Revision == fragment.Revision && Hash == fragment.Hash &&
                    Full == fragment.Full && Length == fragment.Length && fragment.Valid && FragmentCount == fragment.Count;
        }
        public readonly struct RemoteInstallToken
        {
            public readonly int TerrainObjectId;
            public readonly long InstallId, PendingId, TerminalStamp;
            public readonly bool AcceptedAvailable, PublishedAvailable;
            public readonly RemoteTransferContext Transfer;
            internal RemoteInstallToken(int terrain, long install, long pending, long terminal, RemoteTransferContext transfer)
            {
                TerrainObjectId = terrain; InstallId = install; PendingId = pending; TerminalStamp = terminal; Transfer = transfer;
                AcceptedAvailable = terrain != 0 && install > 0 && transfer.Available;
                PublishedAvailable = AcceptedAvailable && pending > 0 && terminal > 0;
            }
            public bool SameAcceptedInstall(RemoteInstallToken other)
                => AcceptedAvailable && other.AcceptedAvailable && TerrainObjectId == other.TerrainObjectId && InstallId == other.InstallId &&
                    Transfer.MatchObjectId == other.Transfer.MatchObjectId && Transfer.Id == other.Transfer.Id &&
                    Transfer.Round == other.Transfer.Round && Transfer.BaseRevision == other.Transfer.BaseRevision &&
                    Transfer.Revision == other.Transfer.Revision && Transfer.Hash == other.Transfer.Hash && Transfer.Full == other.Transfer.Full &&
                    Transfer.Length == other.Transfer.Length && Transfer.FragmentCount == other.Transfer.FragmentCount;
        }
        public readonly struct RemoteInstallObservation
        {
            public readonly long Stamp, InstallId, PendingId, PendingCreatedStamp;
            public readonly uint Round, Revision;
            public readonly string Phase;
            public readonly RemoteTransferContext Transfer;
            public readonly RemoteInstallToken Token;
            public readonly bool TimingAvailable;
            public readonly PendingWallDiagnostic Timing;
            internal RemoteInstallObservation(string phase, long stamp, RemoteInstallToken token, long created,
                uint round, uint revision,
                bool timingAvailable = false, PendingWallDiagnostic timing = default)
            {
                Phase = phase; Stamp = stamp; Token = token; InstallId = token.InstallId; Transfer = token.Transfer;
                PendingId = token.PendingId; PendingCreatedStamp = created; Round = round; Revision = revision;
                TimingAvailable = timingAvailable; Timing = timing;
            }
        }
        internal sealed class RemoteTransferCapture { public RemoteInstallToken Accepted; }
        public readonly struct RemoteTransferObservationScope : IDisposable
        {
            readonly BlockTerrain _terrain;
            readonly RemoteTransferContext _prior;
            readonly RemoteTransferCapture _capture, _priorCapture;
            internal RemoteTransferObservationScope(BlockTerrain terrain, RemoteTransferContext prior, RemoteTransferCapture capture, RemoteTransferCapture priorCapture)
            { _terrain = terrain; _prior = prior; _capture = capture; _priorCapture = priorCapture; }
            public RemoteInstallToken Accepted => _capture != null ? _capture.Accepted : default;
            public void Dispose()
            { if (_terrain != null) { _terrain._remoteTraceContext = _prior; _terrain._remoteTraceCapture = _priorCapture; } }
        }
        public readonly struct RemotePublicationObservationScope : IDisposable
        {
            readonly BlockTerrain _terrain;
            readonly RemoteInstallToken _prior;
            internal RemotePublicationObservationScope(BlockTerrain terrain, RemoteInstallToken prior)
            { _terrain = terrain; _prior = prior; }
            public void Dispose() { if (_terrain != null) _terrain._remoteTracePublication = _prior; }
        }
        public static event Action<BlockTerrain, RemoteInstallObservation> RemoteInstallObserved;
        public static int RemoteInstallProviderErrors { get; private set; }
        RemoteTransferContext _remoteTraceContext;
        RemoteTransferCapture _remoteTraceCapture;
        RemoteInstallToken _remoteTracePublication;
        long _remoteTraceInstallId;
        public RemoteInstallToken CurrentRemotePublicationToken => _remoteTracePublication;
        public RemoteTransferObservationScope ObserveRemoteTransferScope(BlockTransferFragment fragment, int matchObjectId)
        {
            var prior = _remoteTraceContext; var priorCapture = _remoteTraceCapture;
            RemoteTransferCapture capture = null;
            try
            {
                if (RemoteInstallObserved != null)
                { capture = new RemoteTransferCapture(); _remoteTraceContext = new RemoteTransferContext(fragment, matchObjectId); _remoteTraceCapture = capture; }
            }
            catch (Exception) { RemoteInstallProviderErrors++; capture = null; _remoteTraceContext = prior; _remoteTraceCapture = priorCapture; }
            return new RemoteTransferObservationScope(this, prior, capture, priorCapture);
        }
        RemotePublicationObservationScope ObserveRemotePublicationScope(Pending pending)
        {
            var prior = _remoteTracePublication;
            _remoteTracePublication = pending != null && pending.Remote ? pending.TracePublishedToken : default;
            return new RemotePublicationObservationScope(this, prior);
        }
        void EmitRemoteInstall(RemoteInstallObservation observation)
        {
            var handlers = RemoteInstallObserved; if (handlers == null) return;
            foreach (Action<BlockTerrain, RemoteInstallObservation> handler in handlers.GetInvocationList())
                try { handler(this, observation); } catch (Exception) { RemoteInstallProviderErrors++; }
        }
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveRemoteInstallQueued(RemoteInstall install)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (RemoteInstallObserved == null || install == null) return;
            try
            {
                install.TraceInstallId = ++_remoteTraceInstallId; install.TraceContext = _remoteTraceContext;
                var token = new RemoteInstallToken(GetInstanceID(), install.TraceInstallId, 0, 0, install.TraceContext);
                if (_remoteTraceCapture != null) _remoteTraceCapture.Accepted = token;
                EmitRemoteInstall(new RemoteInstallObservation("Queued", Stopwatch.GetTimestamp(), token, 0, install.Next.Round, install.Next.Revision));
            }
            catch (Exception) { RemoteInstallProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveRemoteInstallStaged(RemoteInstall install)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (RemoteInstallObserved == null || install == null || _pending == null) return;
            try
            {
                _pending.TraceInstallId = install.TraceInstallId; _pending.TraceContext = install.TraceContext;
                var token = new RemoteInstallToken(GetInstanceID(), install.TraceInstallId, _pending.WallId, 0, install.TraceContext);
                EmitRemoteInstall(new RemoteInstallObservation("Staged", Stopwatch.GetTimestamp(), token, _pending.CreatedWallStamp, install.Next.Round, install.Next.Revision));
            }
            catch (Exception) { RemoteInstallProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveRemoteInstallTerminal(Pending pending, long endpoint, PendingWallDiagnostic timing)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (RemoteInstallObserved == null || !pending.Remote) return;
            try
            {
                var token = new RemoteInstallToken(GetInstanceID(), pending.TraceInstallId, pending.WallId,
                    timing.Outcome == PendingWallOutcome.Published ? endpoint : 0, pending.TraceContext);
                if (timing.Outcome == PendingWallOutcome.Published) pending.TracePublishedToken = token;
                EmitRemoteInstall(new RemoteInstallObservation(timing.Outcome.ToString(), endpoint, token,
                    pending.CreatedWallStamp, pending.Next.Round, pending.Next.Revision, true, timing));
            }
            catch (Exception) { RemoteInstallProviderErrors++; }
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ObserveRemoteInstallDiscarded(RemoteInstall install, string phase)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (RemoteInstallObserved == null || install == null) return;
            try
            {
                var token = new RemoteInstallToken(GetInstanceID(), install.TraceInstallId, 0, 0, install.TraceContext);
                EmitRemoteInstall(new RemoteInstallObservation(phase, Stopwatch.GetTimestamp(), token, 0, install.Next.Round, install.Next.Revision));
            }
            catch (Exception) { RemoteInstallProviderErrors++; }
#endif
        }
    }
}
