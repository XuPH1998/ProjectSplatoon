#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;
using System.IO;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockNetworkProbe
    {
        [Serializable] sealed class MotorDetailsReport
        {
            public int schema = 1;
            public string session, label, role, sourceProbePath;
            public string scope = "opt-in bounded complete invocation sidecar; original motor/reconcile/raw rows unchanged; no cross-process time association";
            public double startAfterSeconds;
            public long invocationsBeforeWindow;
            public int sourceMotorConsumerErrors, sourceMotorProviderErrors;
            public bool sourceMotorComplete;
            public MotorDetailCapture.Snapshot capture;
        }
        MotorDetailCapture _motorDetailsCapture;
        MotorDetailsReport _motorDetailsReport;
        bool _motorDetailsWindowOpened, _motorDetailsFinalized, _motorDetailsSubscribed;
        public bool MotorDetailsWritten { get; private set; }
        public string MotorDetailsWriteError { get; private set; }

        void BeginMotorDetailsCollection()
        {
            if (!_motorTraceEnabled || Array.IndexOf(Environment.GetCommandLineArgs(), "-blockMotorDetails") < 0) return;
            try
            {
                _motorDetailsCapture = new MotorDetailCapture();
                double.TryParse(Arg("-blockMotorDetailsStartSeconds", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var start);
                if (!double.IsFinite(start) || start < 0) start = 0;
                _motorDetailsReport = new MotorDetailsReport { session = _report.session, label = _report.label,
                    sourceProbePath = _output, startAfterSeconds = start };
                // The original consumer was subscribed first. Read its exact appended
                // eventId; never invent a parallel ID or alter the original handler.
                _motorDetailsSubscribed = true;
                PlayerMotorSimulation.MotorBranchObserved += OnMotorDetailBaseEvent;
                PlayerMotorSimulation.MotorDetailObserved += OnMotorDetailEvent;
                PlayerMotorSimulation.MotorDetailInvocationFinished += OnMotorDetailInvocationFinished;
            }
            catch (Exception error)
            {
                _motorDetailsFinalized = true; MotorDetailsWriteError = "Initialization: " + error.GetType().Name + ": " + error.Message;
                StopMotorDetailsCollection();
            }
        }
        void OnMotorDetailBaseEvent(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorBranchObservation observation)
        {
            if (_finished || !_motorDetailsWindowOpened || _motorDetailsFinalized) return;
            try
            {
                var rows = _report.motorBranchEvents;
                long id = 0;
                if (rows.Count > 0)
                {
                    var last = rows[rows.Count - 1];
                    if (last.invocation == observation.Invocation && last.playerObjectId == observation.PlayerObjectId &&
                        last.owner == observation.Owner && last.networkObjectId == observation.NetworkObjectId &&
                        last.life == observation.Life && last.terrainRound == observation.TerrainRound && last.branch == observation.Branch.ToString())
                        id = last.eventId;
                }
                _motorDetailsCapture.OnBaseEvent(motor, observation, id);
            }
            catch (Exception) { _motorDetailsCapture.RecordConsumerError(motor, observation.Invocation); }
        }
        void OnMotorDetailEvent(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorDetailObservation observation)
        {
            if (_finished || _motorDetailsFinalized) return;
            try
            {
                if (!_motorDetailsWindowOpened)
                {
                    if (observation.Kind != PlayerMotorSimulation.MotorDetailKind.Begin) return;
                    if (Time.realtimeSinceStartupAsDouble - _boot < _motorDetailsReport.startAfterSeconds)
                    { _motorDetailsReport.invocationsBeforeWindow++; return; }
                    _motorDetailsWindowOpened = true;
                }
                _motorDetailsCapture.OnDetail(motor, observation, _report.connectionGenerations);
            }
            catch (Exception) { _motorDetailsCapture.RecordConsumerError(motor, observation.Invocation); }
        }
        void OnMotorDetailInvocationFinished(PlayerMotorSimulation motor, PlayerMotorSimulation.MotorDetailObservation observation)
        {
            if (_finished || !_motorDetailsWindowOpened || _motorDetailsFinalized) return;
            _motorDetailsCapture.OnInvocationFinished(motor, observation, _report.connectionGenerations);
        }
        void FinishMotorDetailsCollection()
        {
            if (_motorDetailsCapture == null || _motorDetailsFinalized) return;
            _motorDetailsFinalized = true;
            try
            {
                _motorDetailsReport.role = _report.role;
                _motorDetailsReport.sourceMotorConsumerErrors = _report.motorBranchConsumerErrors;
                _motorDetailsReport.sourceMotorProviderErrors = _report.motorBranchProviderErrors;
                _motorDetailsReport.sourceMotorComplete = _report.motorBranchEventsComplete;
                _motorDetailsReport.capture = _motorDetailsCapture.Finish();
                StopMotorDetailsCollection();
                string path = _output + ".motor-details.json";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".writing"; File.WriteAllText(temporary, JsonUtility.ToJson(_motorDetailsReport, true));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                MotorDetailsWritten = true;
            }
            // Includes serialization errors: none may change the original probe
            // errors/exit/raw. Missing final sidecar is unavailable evidence.
            catch (Exception error) { MotorDetailsWriteError = error.GetType().Name + ": " + error.Message; StopMotorDetailsCollection(); }
        }
        void StopMotorDetailsCollection()
        {
            if (_motorDetailsSubscribed)
            {
                PlayerMotorSimulation.MotorBranchObserved -= OnMotorDetailBaseEvent;
                PlayerMotorSimulation.MotorDetailObserved -= OnMotorDetailEvent;
                PlayerMotorSimulation.MotorDetailInvocationFinished -= OnMotorDetailInvocationFinished;
                _motorDetailsSubscribed = false;
            }
            _motorDetailsCapture?.Close();
        }
    }
}
#endif
