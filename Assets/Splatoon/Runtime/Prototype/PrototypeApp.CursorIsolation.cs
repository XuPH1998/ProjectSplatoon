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

        static void ReleaseSystemCursorIfDisallowed()
        {
            if (UnityEngine.Cursor.lockState == UnityEngine.CursorLockMode.None || CanCaptureSystemCursor) return;
            UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
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
