using System;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        static readonly bool AutomatedCursorCaptureSuppressed = IsAutomatedCursorCaptureSuppressed();
        readonly CursorCaptureRearm _cursorNativeRearm = new();
        public int CursorNativeRearmCount { get; private set; }
        public int SystemCursorWriteCount { get; private set; }
        static bool SystemCursorWritesSuppressed => AutomatedCursorCaptureSuppressed || UnityEngine.Application.isBatchMode;

        void WriteCursorLock(UnityEngine.CursorLockMode mode)
        { UnityEngine.Cursor.lockState = mode; SystemCursorWriteCount++; }
        void WriteCursorVisible(bool visible)
        { UnityEngine.Cursor.visible = visible; SystemCursorWriteCount++; }
        void ReleaseSystemCursorIfPermitted()
        {
            if (SystemCursorWritesSuppressed || !CanCaptureSystemCursor) return;
            if (UnityEngine.Cursor.lockState != UnityEngine.CursorLockMode.None) WriteCursorLock(UnityEngine.CursorLockMode.None);
            if (!UnityEngine.Cursor.visible) WriteCursorVisible(true);
        }

        static bool IsAutomatedCursorCaptureSuppressed()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("SPLATOON_DISABLE_SYSTEM_CURSOR"), "1", StringComparison.Ordinal))
                return true;
            foreach (var argument in Environment.GetCommandLineArgs())
                if (string.Equals(argument, "-noSystemCursor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-automated", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-blockNetworkProbe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-blockBenchmark", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-frameProbe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-lanSmokeHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-lanSmokeClient", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Logical gameplay capture remains independent of permission to lock the system cursor.
        public static bool CanCaptureSystemCursor
        {
            get
            {
                if (AutomatedCursorCaptureSuppressed || UnityEngine.Application.isBatchMode ||
                    !UnityEngine.Application.isFocused) return false;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                IntPtr inputDesktop = IntPtr.Zero;
                bool allowed = false;
                try
                {
                    var foreground = GetForegroundWindow();
                    if (foreground == IntPtr.Zero ||
                        GetWindowThreadProcessId(foreground, out var foregroundProcess) == 0 ||
                        foregroundProcess != GetCurrentProcessId()) return false;
                    // The thread desktop handle is borrowed and must never be closed here.
                    var threadDesktop = GetThreadDesktop(GetCurrentThreadId());
                    inputDesktop = OpenInputDesktop(0, false, 0x0001);
                    allowed = TryCursorDesktopName(threadDesktop, out var threadName) &&
                        TryCursorDesktopName(inputDesktop, out var inputName) &&
                        string.Equals(threadName, inputName, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    allowed = false;
                }
                finally
                {
                    if (inputDesktop != IntPtr.Zero)
                    {
                        try { if (!CloseDesktop(inputDesktop)) allowed = false; }
                        catch (Exception) { allowed = false; }
                    }
                }
                return allowed;
#else
                return true;
#endif
            }
        }

        // Gameplay can request capture before the window gains native focus.
        // Retry that intent without resetting input or dismissing a menu.
        void SynchronizeSystemCursor()
        {
            // Suppressed automation and batch Players never write the shared system cursor,
            // including release writes. Logical intent/HasControl remain available to probes.
            if (SystemCursorWritesSuppressed) return;
            bool captureSystemCursor = _cursorCapture.AllowsCapture && CanCaptureSystemCursor;
            var desiredLock = captureSystemCursor ? UnityEngine.CursorLockMode.Locked : UnityEngine.CursorLockMode.None;
            bool desiredVisible = !captureSystemCursor;
            if (UnityEngine.Cursor.lockState != desiredLock)
            {
                WriteCursorLock(desiredLock);
                _cursorNativeRearm.NativeStateChanged(UnityEngine.Time.frameCount);
            }
            if (UnityEngine.Cursor.visible != desiredVisible) WriteCursorVisible(desiredVisible);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Unity's logical Locked value can survive an external native unclipping.
            // Native observations are read-only; only the existing strict permission can rearm.
            if (captureSystemCursor && TryReadNativeCursorHealth(out var healthWindow, out bool healthy, out bool nativeVisible) &&
                !healthy && CanCaptureSystemCursor && GetForegroundWindow() == healthWindow &&
                _cursorNativeRearm.TryConsume(_cursorCapture.AllowsCapture, true,
                    true, healthy, UnityEngine.Time.frameCount, UnityEngine.Time.realtimeSinceStartupAsDouble))
            {
                // A same-value Locked assignment may be a native no-op. Rebuild through Unity,
                // without changing gameplay intent, overlays or fire-input gating.
                WriteCursorLock(UnityEngine.CursorLockMode.None);
                WriteCursorLock(UnityEngine.CursorLockMode.Locked);
                if (nativeVisible) { WriteCursorVisible(true); WriteCursorVisible(false); }
                CursorNativeRearmCount++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                UnityEngine.Debug.Log($"[CURSOR] native rearm={CursorNativeRearmCount} frame={UnityEngine.Time.frameCount}");
#endif
            }
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var status = (_cursorCapture.Requested, _cursorCapture.Focused, _cursorCapture.Paused,
                captureSystemCursor, UnityEngine.Cursor.lockState, UnityEngine.Cursor.visible);
            if (!_cursorStatusRecorded || status != _lastCursorStatus)
            {
                _cursorStatusRecorded = true; _lastCursorStatus = status;
                UnityEngine.Debug.Log($"[CURSOR] requested={status.Item1} focused={status.Item2} paused={status.Item3} permitted={status.Item4} lock={status.Item5} visible={status.Item6} automated={AutomatedCursorCaptureSuppressed}");
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        bool _cursorStatusRecorded;
        (bool, bool, bool, bool, UnityEngine.CursorLockMode, bool) _lastCursorStatus;
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential)] struct CursorNativePoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct CursorNativeRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct CursorNativeInfo
        { public int size, flags; public IntPtr cursor; public CursorNativePoint screenPosition; }
        static bool TryReadNativeCursorHealth(out IntPtr window, out bool healthy, out bool nativeVisible)
        {
            window = IntPtr.Zero; healthy = false; nativeVisible = false;
            try
            {
                var observed = GetForegroundWindow();
                if (observed == IntPtr.Zero || GetWindowThreadProcessId(observed, out uint pid) == 0 || pid != GetCurrentProcessId()) return false;
                var info = new CursorNativeInfo { size = Marshal.SizeOf<CursorNativeInfo>() };
                var origin = new CursorNativePoint();
                if (!GetCursorInfo(ref info) || !GetClipCursor(out var clip) || !GetClientRect(observed, out var client) ||
                    !ClientToScreen(observed, ref origin) || GetForegroundWindow() != observed) return false;
                window = observed;
                nativeVisible = (info.flags & 1) != 0;
                healthy = CursorCaptureRearm.Healthy(nativeVisible,
                    new CursorCaptureBounds(clip.left, clip.top, clip.right, clip.bottom),
                    new CursorCaptureBounds(client.left + origin.x, client.top + origin.y, client.right + origin.x, client.bottom + origin.y));
                return true;
            }
            catch (Exception) { return false; }
        }
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorInfo(ref CursorNativeInfo info);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetClipCursor(out CursorNativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(IntPtr window, out CursorNativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(IntPtr window, ref CursorNativePoint point);
#endif

        static bool TryCursorDesktopName(IntPtr desktop, out string name)
        {
            name = null;
            if (desktop == IntPtr.Zero) return false;
            var text = new StringBuilder(256);
            if (!GetUserObjectInformationW(desktop, 2, text, 512, out _) || text.Length == 0) return false;
            name = text.ToString();
            return true;
        }

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, uint bytes, out uint needed);
#endif
    }
}
