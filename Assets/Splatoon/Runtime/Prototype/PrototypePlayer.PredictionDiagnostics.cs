using System;
using System.Diagnostics;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public InputAcknowledgementTiming InputTiming { get; } = new();
        public uint PredictionSteps { get; private set; }
        public uint PaintReplayCount { get; private set; }
        public uint SpeculativePaintReconciles { get; private set; }
        public uint HardCorrectionCount { get; private set; }
        public long TotalReplaySteps { get; private set; }
        public int LastReplaySteps { get; private set; }
        public int MaxReplaySteps { get; private set; }
        public double CorrectionDistanceTotal { get; private set; }
        public double InitialSyncPauseSeconds { get; private set; }
        public double InputTimeoutPauseSeconds { get; private set; }
        public bool PaintReplayPending => _paintReplayPending;
        double _diagnosticTime = -1;
        bool _diagnosticSyncPause, _diagnosticTimeoutPause;
#endif
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void UpdatePredictionDiagnostics()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            double now = Time.realtimeSinceStartupAsDouble;
            if (_diagnosticTime >= 0)
            {
                double delta = Math.Max(0, now - _diagnosticTime);
                if (_diagnosticSyncPause) InitialSyncPauseSeconds += delta;
                else if (_diagnosticTimeoutPause) InputTimeoutPauseSeconds += delta;
            }
            _diagnosticTime = now;
            _diagnosticSyncPause = !IsServer && (PrototypeMatch.Current == null || !PrototypeMatch.Current.InitialSyncComplete);
            _diagnosticTimeoutPause = !IsServer && _predictionPaused;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void TrackInputTiming(PlayerInputFrame frame)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            InputTiming.Track(frame.Sequence, frame.Revision, Time.realtimeSinceStartupAsDouble);
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void RecordInputAcknowledgement(PlayerSnapshot authority)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            InputTiming.Acknowledge(authority.AcknowledgedInput, authority.Revision, Time.realtimeSinceStartupAsDouble);
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void DiscardInputTiming()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            InputTiming.DiscardPending();
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void RecordPaintReplay()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            PaintReplayCount++;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void RecordPredictionStep()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            PredictionSteps++;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void RecordPredictionReplay(int steps, bool hard, bool speculativePaint)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LastReplaySteps = steps; TotalReplaySteps += steps; MaxReplaySteps = Math.Max(MaxReplaySteps, steps);
            CorrectionDistanceTotal += LastCorrectionDistance;
            if (hard) HardCorrectionCount++;
            if (speculativePaint) SpeculativePaintReconciles++;
#endif
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        void ResetPredictionDiagnostics()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            InputTiming.Reset(); PredictionSteps = PaintReplayCount = SpeculativePaintReconciles = HardCorrectionCount = 0;
            TotalReplaySteps = 0; LastReplaySteps = MaxReplaySteps = 0;
            CorrectionDistanceTotal = InitialSyncPauseSeconds = InputTimeoutPauseSeconds = 0;
            _diagnosticTime = -1; _diagnosticSyncPause = _diagnosticTimeoutPause = false;
#endif
        }
    }
}
