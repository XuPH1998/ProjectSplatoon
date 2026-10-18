using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        // Actual candidate default, configured before initialization or between
        // stages. Acceptance still requires the next rendered Player measurement.
        BlockPublicationPolicy _publicationPolicy = BlockPublicationPolicy.CandidateDefault();
        readonly BlockPublicationFrameBudget _publicationFrameBudget = new();
        BlockPublicationWindow _stagePublicationWindow, _gravityRetryPublicationWindow;
        bool _stagePublicationReplayOnly;
        const int SurfaceBatchOperationLookahead = 8;
        bool _publicationTimingOpen;
        int _authorityPublicationWorkFrame = -1;
        public BlockPublicationPolicy PublicationPolicy
        {
            get => _publicationPolicy;
            set => _publicationPolicy = value ?? throw new ArgumentNullException(nameof(value));
        }
        public int PendingPublicationPolicyVersion => _pending?.Window?.Policy.Version ?? 0;
        public double TerrainSynchronousMilliseconds => _publicationFrameBudget.ObservedSynchronousMilliseconds;
        public double PeakTerrainSynchronousMilliseconds => _publicationFrameBudget.PeakObservedSynchronousMilliseconds;
        public long PublicationOverrunAdmissions => _publicationFrameBudget.ObservedOverrunAdmissions;
        public long PublicationCpuDeferrals { get; private set; }
        public int LocalPublishedKeysThisFrame => _publicationFrameBudget.ChangedKeys;

        void EnsurePublicationFrame()
        {
            _publicationFrameBudget.BeginFrame(Time.frameCount);
            if (_budgetFrame == Time.frameCount) return;
            _budgetFrame = Time.frameCount; _publishedThisFrame = 0;
        }
        IDisposable MeasurePublicationWork()
        {
            EnsurePublicationFrame();
            // Synchronous callbacks may legitimately invoke Clear or another
            // public terrain method. Its work is included by the outer scope.
            if (_publicationTimingOpen) return NestedTimingScope.Instance;
            _publicationTimingOpen = true;
            try { return new PublicationTimingScope(this, _publicationFrameBudget.MeasureSynchronousWork(Time.frameCount)); }
            catch { _publicationTimingOpen = false; throw; }
        }
        sealed class NestedTimingScope : IDisposable
        {
            public static readonly NestedTimingScope Instance = new();
            public void Dispose() { }
        }
        sealed class PublicationTimingScope : IDisposable
        {
            BlockTerrain _owner;
            IDisposable _inner;
            public PublicationTimingScope(BlockTerrain owner, IDisposable inner) { _owner = owner; _inner = inner; }
            public void Dispose()
            {
                var owner = _owner; var inner = _inner; _owner = null; _inner = null;
                if (owner == null) return;
                try { inner.Dispose(); }
                finally { owner._publicationTimingOpen = false; }
            }
        }
        static BlockPublicationOperation PublicationOperations(Command command)
            => command.Spread ? BlockPublicationOperation.Gravity : command.Build ? BlockPublicationOperation.OrdinaryAttack
                : command.OrdinaryMainAttack ? BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Destruction
                : BlockPublicationOperation.Destruction;
        BlockPublicationWindow CaptureNewPublicationWindow()
        {
            var operations = _commands.Count != 0 ? PublicationOperations(_commands.Peek()) : BlockPublicationOperation.None;
            if (SurfaceOnly)
            {
                // Capture a bounded, continuous fresh prefix. This is an operation
                // lookahead, never a command count or target truncation limit.
                // Mixed operations retain their policy's complete minimum key cap.
                int inspected = 0;
                foreach (var command in _commands)
                {
                    if (inspected++ == SurfaceBatchOperationLookahead || command.Spread || command.RetryWindow != null) break;
                    operations |= PublicationOperations(command);
                }
            }
            if (_gravityColumns.Count != 0 || FallingReady()) operations |= BlockPublicationOperation.Gravity;
            // Capture potential local gravity/index capability at the start.
            // Retries retain this exact lease; ordinary still supplies its
            // smaller cap, and custom mixed profiles use their full minimum.
            return _publicationPolicy.Capture((operations == BlockPublicationOperation.None ? BlockPublicationOperation.Metadata : operations)
                | BlockPublicationOperation.Gravity);
        }
        BlockPublicationWindow SelectPublicationWindow()
        {
            // All preparation and retry owners preserve their immutable lease.
            return _dependentEditPreparation?.Context.Window
                ?? _buildOccupancyPreparation?.Window
                ?? _historicalRecoveryPreparation?.Request.Window ?? _historicalRecoveryRequest?.Window
                ?? _headCopyWindow
                ?? _activePrivateGravityRequest?.Window ?? _privateGravityRequest?.Window
                ?? (FairBackgroundStage ? FairPublicationWindow() : null)
                ?? (_commands.Count != 0 ? _commands.Peek().RetryWindow : null)
                ?? _gravityRetryPublicationWindow ?? PreparedHeadPublicationWindow()
                ?? FairPublicationWindow() ?? CaptureNewPublicationWindow();
        }
        BlockPublicationWindow CaptureLegacyPublicationWindow(List<(Command command, BlockMutationResult result)> results)
        {
            if (_stagePublicationWindow != null) return _stagePublicationWindow;
            if (_gravityJournal?.Window != null) return _gravityJournal.Window;
            // Compatibility reflection entry point only. Production continuations
            // call BeginWindowTransaction with their explicit stored window.
            var operations = BlockPublicationOperation.None;
            if (results != null) foreach (var pair in results) operations |= PublicationOperations(pair.command);
            return _publicationPolicy.Capture(operations == BlockPublicationOperation.None ? BlockPublicationOperation.Metadata : operations);
        }
        bool CanRunPublicationCommand(Command command)
        {
            if (!FairAllowsCommand(command)) return false;
            if (!_stagePublicationWindow.Includes(PublicationOperations(command))) return false;
            if (command.RetryWindow != null && !ReferenceEquals(command.RetryWindow, _stagePublicationWindow)) return false;
            // A completed surface retry head may share its still-immutable
            // window with fresh FIFO work only while that exact policy remains
            // current. Policy replacement still separates old/new admissions.
            return !_stagePublicationReplayOnly || command.RetryWindow != null
                || SurfaceOnly && ReferenceEquals(_stagePublicationWindow.Policy, _publicationPolicy);
        }
    }
}
