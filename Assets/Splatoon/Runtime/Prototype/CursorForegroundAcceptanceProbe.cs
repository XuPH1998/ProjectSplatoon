#if UNITY_EDITOR_WIN || (DEVELOPMENT_BUILD && UNITY_STANDALONE_WIN)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cysharp.Threading.Tasks;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Explicit foreground acceptance only; ordinary automation keeps cursor suppression enabled.</summary>
    public sealed class CursorForegroundAcceptanceProbe : MonoBehaviour
    {
        [Serializable] public sealed class Sample
        {
            public string stage, overlay, phase;
            public int frame, lockState, nativeRearms, cursorWrites;
            public double realtime;
            public bool focused, permitted, visible, nativeCursorVisible, nativeQuerySucceeded, foregroundOwned;
            public int clipLeft, clipTop, clipRight, clipBottom, clientLeft, clientTop, clientRight, clientBottom;
            public int desktopLeft, desktopTop, desktopRight, desktopBottom;
        }
        [Serializable] sealed class Result
        {
            public int schema = 1, processId;
            public string player, role, session, startedUtc, finishedUtc, error, threadDesktopName, inputDesktopName, desktopQueryError;
            public bool complete, passed, desktopQuerySucceeded, peerPreinitialized, requirePeerReady, peerReadyValidated;
            public int nativeRearms, cursorWrites;
            public List<Sample> samples = new();
            public List<string> checks = new();
        }
        [Serializable] sealed class Signal
        {
            public int schema = 1, processId, port;
            public string session, kind, utc;
        }
        [StructLayout(LayoutKind.Sequential)] struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct CursorInfo
        { public int size, flags; public IntPtr cursor; public Point screenPosition; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] static extern bool GetClipCursor(out Rect rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, uint bytes, out uint needed);
        readonly Result _result = new();
        string _output, _hostReady, _peerStop, _peerReady;
        ushort _port;
        double _deadline;
        IntPtr _window;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            // Compile the same source in a Windows Editor, but never activate a
            // system-cursor acceptance run inside the user's Editor process.
            if (Application.isEditor) return;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-cursorForegroundAcceptance") < 0) return;
            var go = new GameObject("Explicit foreground cursor acceptance");
            DontDestroyOnLoad(go); go.AddComponent<CursorForegroundAcceptanceProbe>();
        }
        static string Argument(string key, string fallback)
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        void Require(bool condition, string check)
        { if (!condition) throw new InvalidOperationException(check); _result.checks.Add(check); }
        async UniTask Await(Func<bool> condition, string check, double seconds = 15)
        {
            double deadline = Math.Min(_deadline, Time.realtimeSinceStartupAsDouble + seconds);
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
            Require(condition(), check);
        }
        static string EvidencePath(string path)
        {
            string reports = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Reports"));
            string candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(reports + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(candidate))
                throw new InvalidOperationException("Cursor evidence must be a file under the launcher project's Reports directory.");
            // A junction under Reports would evade a lexical prefix check. Do
            // not create or write evidence through an existing reparse directory.
            string directory = Path.GetDirectoryName(candidate);
            while (directory != null && directory.Length >= reports.Length)
            {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cursor evidence cannot traverse a reparse directory.");
                directory = Path.GetDirectoryName(directory);
            }
            return candidate;
        }
        static void WriteNew(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(json);
                // Publish only a complete JSON document, without replacing old
                // evidence. A peer must not observe the file half-written.
                File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        static bool DesktopName(IntPtr desktop, out string name)
        {
            name = null; if (desktop == IntPtr.Zero) return false;
            var text = new StringBuilder(256);
            if (!GetUserObjectInformationW(desktop, 2, text, 512, out _) || text.Length == 0) return false;
            name = text.ToString(); return true;
        }
        void RecordDesktopIdentity()
        {
            IntPtr input = IntPtr.Zero;
            try
            {
                // The thread desktop is borrowed; only close the handle opened
                // here. Observing names never switches or grants a desktop.
                bool threadRead = DesktopName(GetThreadDesktop(GetCurrentThreadId()), out _result.threadDesktopName);
                input = OpenInputDesktop(0, false, 0x0001);
                bool inputRead = DesktopName(input, out _result.inputDesktopName);
                _result.desktopQuerySucceeded = threadRead && inputRead;
            }
            catch (Exception error) { _result.desktopQueryError = error.ToString(); _result.desktopQuerySucceeded = false; }
            finally
            {
                if (input != IntPtr.Zero)
                {
                    try
                    {
                        if (!CloseDesktop(input))
                        { _result.desktopQuerySucceeded = false; _result.desktopQueryError = "CloseDesktop failed for the query-owned input desktop handle."; }
                    }
                    catch (Exception error) { _result.desktopQuerySucceeded = false; _result.desktopQueryError = error.ToString(); }
                }
            }
        }
        void WriteSignal(string path, string kind)
            => WriteNew(path, JsonUtility.ToJson(new Signal { processId = _result.processId, port = _port,
                session = _result.session, kind = kind, utc = DateTime.UtcNow.ToString("O") }, true));
        void ValidateSignal(string path, string kind)
        {
            var signal = JsonUtility.FromJson<Signal>(File.ReadAllText(path));
            Require(signal != null && signal.schema == 1 && signal.port == _port && signal.session == _result.session &&
                signal.kind == kind && signal.processId > 0, kind + ": current session/port signal");
        }
        void Failure(Exception error, string context)
        {
            _result.passed = false;
            _result.error = string.IsNullOrEmpty(_result.error) ? context + ": " + error : _result.error + "\n" + context + ": " + error;
            Debug.LogException(error);
        }
        bool OwnedWindow()
            => _window != IntPtr.Zero && IsWindow(_window) && GetWindowThreadProcessId(_window, out uint pid) != 0 && pid == _result.processId;
        bool OwnedForeground()
            => GetWindowThreadProcessId(GetForegroundWindow(), out uint pid) != 0 && pid == _result.processId;
        Sample Read(string stage)
        {
            var info = new CursorInfo { size = Marshal.SizeOf<CursorInfo>() };
            bool cursorRead = GetCursorInfo(ref info), clipRead = GetClipCursor(out var clip), clientRead = GetClientRect(_window, out var client);
            var origin = new Point(); bool screenRead = ClientToScreen(_window, ref origin);
            int desktopLeft = GetSystemMetrics(76), desktopTop = GetSystemMetrics(77);
            var app = PrototypeApp.Current;
            return new Sample { stage = stage, frame = Time.frameCount, realtime = Time.realtimeSinceStartupAsDouble,
                overlay = app?.Overlay.ToString(), phase = PrototypeMatch.Current?.State.Value.Phase.ToString(),
                focused = Application.isFocused, permitted = PrototypeApp.CanCaptureSystemCursor,
                lockState = (int)Cursor.lockState, visible = Cursor.visible, foregroundOwned = OwnedForeground(),
                nativeRearms = app != null ? app.CursorNativeRearmCount : 0, cursorWrites = app != null ? app.SystemCursorWriteCount : 0,
                nativeCursorVisible = (info.flags & 1) != 0, nativeQuerySucceeded = cursorRead && clipRead && clientRead && screenRead,
                clipLeft = clip.left, clipTop = clip.top, clipRight = clip.right, clipBottom = clip.bottom,
                clientLeft = client.left + origin.x, clientTop = client.top + origin.y,
                clientRight = client.right + origin.x, clientBottom = client.bottom + origin.y,
                desktopLeft = desktopLeft, desktopTop = desktopTop,
                desktopRight = desktopLeft + GetSystemMetrics(78), desktopBottom = desktopTop + GetSystemMetrics(79) };
        }
        async UniTask Observe(string stage, bool capture, bool foreground)
        {
            // Let the native backend finish focus/lock changes before checking a sustained interval.
            await UniTask.Delay(TimeSpan.FromSeconds(.25), ignoreTimeScale: true);
            double until = Time.realtimeSinceStartupAsDouble + .75;
            int count = 0;
            do
            {
                var sample = Read(stage); _result.samples.Add(sample); count++;
                Require(sample.nativeQuerySucceeded, stage + ": native cursor/window queries succeeded");
                Require(sample.foregroundOwned == foreground, stage + ": expected real foreground ownership");
                if (foreground) Require(GetForegroundWindow() == _window, stage + ": own game window is foreground");
                Require(sample.overlay == (foreground && !capture ? GameplayOverlay.RoomMenu : GameplayOverlay.Game).ToString(),
                    stage + ": normal overlay state retained");
                Require(sample.lockState == (int)(capture ? CursorLockMode.Locked : CursorLockMode.None) && sample.visible != capture,
                    stage + ": Unity cursor lock and visibility");
                if (capture)
                {
                    Require(sample.focused && sample.permitted && !sample.nativeCursorVisible, stage + ": actual foreground cursor hidden");
                    Require(CursorCaptureRearm.Healthy(sample.nativeCursorVisible,
                        new CursorCaptureBounds(sample.clipLeft, sample.clipTop, sample.clipRight, sample.clipBottom),
                        new CursorCaptureBounds(sample.clientLeft, sample.clientTop, sample.clientRight, sample.clientBottom)),
                        stage + ": actual Locked cursor uses the game-client center point");
                }
                else if (foreground)
                {
                    Require(sample.focused && sample.nativeCursorVisible, stage + ": actual foreground menu cursor visible");
                    Require(sample.clipLeft <= sample.desktopLeft && sample.clipTop <= sample.desktopTop &&
                        sample.clipRight >= sample.desktopRight && sample.clipBottom >= sample.desktopBottom,
                        stage + ": actual menu cursor released across virtual desktop");
                }
                else Require(!sample.focused && !sample.permitted, stage + ": unfocused game cannot capture system cursor");
                await UniTask.Yield();
            } while (Time.realtimeSinceStartupAsDouble < until);
            Require(count >= 3, stage + ": sustained frame samples");
        }
        async UniTaskVoid Start()
        {
            var app = PrototypeApp.Current;
            int code = 2;
            bool peer = false;
            _deadline = Time.realtimeSinceStartupAsDouble + 120;
            try
            {
                _result.processId = (int)GetCurrentProcessId(); _result.player = Application.dataPath;
                _result.startedUtc = DateTime.UtcNow.ToString("O");
                RecordDesktopIdentity();
                string role = Argument("-cursorAcceptanceRole", "host");
                Require(role is "host" or "peer", "explicit host or peer role"); peer = role == "peer"; _result.role = role;
                _result.session = Argument("-cursorAcceptanceSession", "");
                Require(Guid.TryParseExact(_result.session, "N", out _), "fresh shared acceptance session GUID");
                Require(ushort.TryParse(Argument("-cursorAcceptancePort", "52990"), out _port) && LanDiscoveryProtocol.ValidGamePort(_port), "normal LAN game port");
                // Assign write targets only after all path validation succeeds;
                // an invalid requested output must never be written in finally.
                string output = EvidencePath(Argument("-cursorAcceptanceOutput", "Reports/CursorForegroundAcceptance/result.json"));
                string hostReady = EvidencePath(Argument("-cursorAcceptanceHostReady", Path.Combine(Path.GetDirectoryName(output), "host-ready.json")));
                string peerStop = EvidencePath(Argument("-cursorAcceptancePeerStop", Path.Combine(Path.GetDirectoryName(output), "peer-stop.json")));
                string peerReady = EvidencePath(Argument("-cursorAcceptancePeerReady", Path.Combine(Path.GetDirectoryName(output), "peer-ready.json")));
                bool requirePeerReady = Array.IndexOf(Environment.GetCommandLineArgs(), "-cursorAcceptanceRequirePeerReady") >= 0;
                Require(!peer || !requirePeerReady, "only host can require preinitialized peer");
                _result.requirePeerReady = requirePeerReady;
                var paths = new[] { output, hostReady, peerStop, peerReady };
                for (int i = 0; i < paths.Length; i++) for (int j = i + 1; j < paths.Length; j++)
                    Require(!string.Equals(paths[i], paths[j], StringComparison.OrdinalIgnoreCase), "distinct result and handshake paths");
                _output = output; _hostReady = hostReady; _peerStop = peerStop; _peerReady = peerReady;
                if (File.Exists(_output)) throw new IOException("Fresh cursor evidence required.");
                if (File.Exists(_peerStop) || !peer && File.Exists(_hostReady) || peer && File.Exists(_peerReady) ||
                    !peer && !requirePeerReady && File.Exists(_peerReady)) throw new IOException("Fresh cursor handshake evidence required.");
                Require(!Application.isEditor && !Application.isBatchMode, "standalone interactive Player only");
                if (peer) Require(Environment.GetEnvironmentVariable("SPLATOON_DISABLE_SYSTEM_CURSOR") == "1" &&
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-noSystemCursor") >= 0, "peer explicitly suppresses system cursor");
                else Require(Environment.GetEnvironmentVariable("SPLATOON_DISABLE_SYSTEM_CURSOR") != "1", "Explicit foreground lane has no cursor suppression environment");
                Require(_result.desktopQuerySucceeded &&
                    string.Equals(_result.threadDesktopName, _result.inputDesktopName, StringComparison.OrdinalIgnoreCase) != peer,
                    peer ? "peer runs on a desktop isolated from real input" : "host runs on the real input desktop");
                await Await(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready && !PrototypeApp.Current.Busy, "normal app initialized", 60);
                app = PrototypeApp.Current;
                if (peer)
                {
                    // Preinitialize the native/managed app only. The peer must not connect
                    // until the host's normal practice connection has published host-ready.
                    double stableUntil = Time.realtimeSinceStartupAsDouble + 1;
                    while (Time.realtimeSinceStartupAsDouble < stableUntil)
                    {
                        Require(app.Ready && !app.Busy && !app.InRoom && app.Manager != null && !app.Manager.IsListening &&
                            app.SystemCursorWriteCount == 0, "peer preinitializes without a connection or system-cursor writes");
                        await UniTask.Yield();
                    }
                    _result.peerPreinitialized = true; WriteSignal(_peerReady, "peer-ready");
                    await Await(() => File.Exists(_hostReady) || File.Exists(_peerStop), "host ready handshake", 60);
                    Require(!File.Exists(_peerStop), "host remains available before peer join"); ValidateSignal(_hostReady, "host-ready");
                    await app.Connect(false, "127.0.0.1", _port).Timeout(TimeSpan.FromSeconds(Math.Max(.1, _deadline - Time.realtimeSinceStartupAsDouble)), DelayType.Realtime);
                    Require(app.InRoom && PrototypePlayer.Local != null && !PrototypeApp.CanCaptureSystemCursor, "suppressed peer joined using normal connection");
                    await Await(() => PrototypeMatch.Current != null && PrototypeMatch.Current.State.Value.Phase == MatchPhase.Playing || File.Exists(_peerStop), "peer observes normal Playing phase", 30);
                    Require(PrototypeMatch.Current != null && PrototypeMatch.Current.State.Value.Phase == MatchPhase.Playing, "peer observed real round start");
                    await Await(() => File.Exists(_peerStop), "host requests normal peer shutdown", 120); ValidateSignal(_peerStop, "peer-stop");
                    _result.passed = true; code = 0; return;
                }
                if (requirePeerReady)
                {
                    Require(File.Exists(_peerReady), "launcher preinitialized peer before host startup");
                    ValidateSignal(_peerReady, "peer-ready"); _result.peerReadyValidated = true;
                }
                await Await(() => Application.isFocused && OwnedForeground(), "test Player actually owns foreground", 15);
                _window = GetForegroundWindow();
                Require(OwnedWindow() && PrototypeApp.CanCaptureSystemCursor, "normal native permission accepts own foreground Player");
                await app.Connect(true, "127.0.0.1", _port).Timeout(TimeSpan.FromSeconds(Math.Max(.1, _deadline - Time.realtimeSinceStartupAsDouble)), DelayType.Realtime);
                Require(app.InRoom && PrototypePlayer.Local != null, "normal practice connection completed");
                WriteSignal(_hostReady, "host-ready");
                await Observe("practice-entry", true, true);
                app.CaptureMouse(false); Require(app.Overlay == GameplayOverlay.RoomMenu, "normal menu release changes overlay");
                await Observe("room-menu", false, true);
                app.CloseOverlay(); Require(app.Overlay == GameplayOverlay.Game, "normal close overlay returns to game");
                await Observe("resume-game", true, true);
                await Await(() => PrototypeMatch.Current != null && PrototypeMatch.Current.CanStartRound, "normal two-team room permits round start", 20);
                PrototypeMatch.Current.StartRound();
                await Await(() => PrototypeMatch.Current.State.Value.Phase == MatchPhase.Playing, "normal match started");
                await Observe("match-entry", true, true);
                Require(OwnedWindow(), "minimize target remains this Player's own game window");
                ShowWindow(_window, 6);
                await Await(() => IsIconic(_window) && !Application.isFocused && !OwnedForeground(), "real minimize loses focus");
                await Observe("focus-lost", false, false);
                Require(OwnedWindow(), "restore target remains this Player's own game window");
                ShowWindow(_window, 9); SetForegroundWindow(_window);
                await Await(() => !IsIconic(_window) && Application.isFocused && OwnedForeground(), "real restore regains focus");
                await Observe("focus-restored", true, true);
                _result.passed = true; code = 0;
            }
            catch (Exception error) { Failure(error, "acceptance"); }
            finally
            {
                if (!peer && _peerStop != null)
                {
                    try { WriteSignal(_peerStop, "peer-stop"); }
                    catch (Exception error) { Failure(error, "peer stop signal"); code = 2; }
                }
                try
                {
                    if (app != null)
                    {
                        app.CaptureMouse(false);
                        if (app.Busy) { app.CancelConnection(); await Await(() => !app.Busy, "normal connection cancellation completed", 10); }
                        if (app.InRoom)
                        {
                            await app.Leave().Timeout(TimeSpan.FromSeconds(Math.Max(.1, Math.Min(15, _deadline - Time.realtimeSinceStartupAsDouble))), DelayType.Realtime);
                            Require(!app.InRoom && !app.Busy && string.IsNullOrEmpty(app.Error) &&
                                (app.Manager == null || !app.Manager.IsListening), "normal leave shut down the session");
                        }
                    }
                }
                catch (Exception error) { Failure(error, "normal leave cleanup"); code = 2; }
                _result.nativeRearms = app != null ? app.CursorNativeRearmCount : 0;
                _result.cursorWrites = app != null ? app.SystemCursorWriteCount : 0;
                if (peer && _result.cursorWrites != 0)
                { Failure(new InvalidOperationException("Suppressed peer wrote the system cursor."), "cursor isolation"); code = 2; }
                _result.finishedUtc = DateTime.UtcNow.ToString("O"); _result.complete = true;
                try
                {
                    if (_output != null) WriteNew(_output, JsonUtility.ToJson(_result, true));
                }
                catch (Exception error) { Failure(error, "fresh evidence write"); code = 2; }
                Application.Quit(code);
            }
        }
    }
}
#endif
