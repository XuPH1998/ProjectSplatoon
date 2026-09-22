using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;
using Unity.Profiling;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        bool _paintReplayPending, _syncReplayPending;
        static readonly ProfilerMarker PredictionReplayMarker = new("Splatoon.Prediction.Reconcile");

        void RefreshPredictionPaint(PlayerSnapshot authority, bool initialSyncComplete, uint appliedSequence, MatchPhase phase)
        {
            if (!initialSyncComplete) { _syncReplayPending = true; return; }
            if (!_syncReplayPending && (!_paintReplayPending || appliedSequence < authority.RequiredPaintSequence)) return;
            RecordPaintReplay();
            ReconcilePrediction(authority, false, true, appliedSequence, phase);
        }

        void ReconcilePrediction(PlayerSnapshot authority, bool movementChanged, bool initialSyncComplete, uint appliedSequence, MatchPhase phase)
        {
            using var sample = PredictionReplayMarker.Auto();
            var oldPosition = _predicted.Position;
            bool lifecycle = authority.Revision != _predicted.Revision;
            RecordInputAcknowledgement(authority);
            _history.RemoveAll(f => f.Sequence <= authority.AcknowledgedInput || f.Revision != authority.Revision);
            _predictionPaused = authority.InputTimedOut;
            if (_predictionPaused || lifecycle) { _history.Clear(); DiscardInputTiming(); }
            _predicted = authority;
            // The movement capsule is needed for replay; historical paper hit meshes are not.
            _motor.Restore(authority, false);
            _syncReplayPending = !initialSyncComplete;
            _paintReplayPending = authority.Health > 0 && (authority.Swimming || authority.SwimWasHeld || authority.CompactBody) &&
                appliedSequence < authority.RequiredPaintSequence;
            int replayed = 0;
            if (lifecycle)
            { _visualOffset = Vector3.zero; _look = new Vector2(authority.Yaw, authority.Pitch); }
            else if (initialSyncComplete && !_predictionPaused && authority.CanMove)
            {
                // Already-applied authoritative ink is a prediction input. A global paint watermark
                // may be ahead because somebody else is shooting; it must not freeze this owner.
                foreach (var frame in _history)
                {
                    Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                        _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, phase);
                    replayed++;
                }
            }
            LastCorrectionDistance = lifecycle ? 0 : Vector3.Distance(oldPosition, _predicted.Position);
            if (LastCorrectionDistance > .01f) CorrectionCount++;
            bool hard = lifecycle || authority.IsDead || LastCorrectionDistance > .75f || movementChanged;
            _visualOffset = hard ? Vector3.zero : Vector3.ClampMagnitude(_visualOffset + oldPosition - _predicted.Position, .5f);
            RecordPredictionReplay(replayed, hard, initialSyncComplete && _paintReplayPending);
        }

        bool PredictInput(PlayerInputFrame frame, bool initialSyncComplete, MatchPhase phase)
        {
            if (!initialSyncComplete || _predictionPaused) return false;
            RecordPredictionStep();
            uint subAction = _predicted.SubAction;
            bool shot = Step(ref _predicted, frame, 1f / GameplayConfig.Global.SimulationRate,
                _predicted.SimulatedAt + 1.0 / GameplayConfig.Global.SimulationRate, phase);
            if (_predicted.SubAction != subAction) PrototypeMatch.Current?.SubPresentation?.PredictThrow(this, _predicted, _lastSubLaunch);
            return shot;
        }
    }
}
