// Reports-only probe: no asset changes and no Play Mode/test execution.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Splatoon.ReportGuards
{
    public static class IsolatedDesktopProbe
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, uint bytes, out uint needed);

        static string DesktopName(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var text = new StringBuilder(256);
            uint needed;
            if (!GetUserObjectInformationW(handle, 2, text, 512, out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return text.ToString();
        }

        static string InputDesktopName()
        {
            var handle = OpenInputDesktop(0, false, 0x0001);
            try { return DesktopName(handle); }
            finally { if (handle != IntPtr.Zero) CloseDesktop(handle); }
        }

        public static Dictionary<string, object> Verify(string expectedDesktopName)
        {
            if (string.IsNullOrEmpty(expectedDesktopName) ||
                !System.Text.RegularExpressions.Regex.IsMatch(expectedDesktopName, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$") ||
                string.Equals(expectedDesktopName, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expectedDesktopName, "Winlogon", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing testing on a reserved or invalid desktop.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("Probe requires an idle Editor.");
            var actualDesktop = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            if (!string.Equals(actualDesktop, expectedDesktopName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actualDesktop, InputDesktopName(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing testing outside the expected isolated desktop.");
            var result = Read();
            if ((bool)result["frameworkActive"] || (bool)result["collectorActive"])
                throw new InvalidOperationException("An existing test run is active.");
            return result;
        }

        public static Dictionary<string, object> Run(string expectedDesktopName)
        {
            Verify(expectedDesktopName);
            var gameViewType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.GameView", false)).FirstOrDefault(t => t != null);
            if (gameViewType == null) throw new InvalidOperationException("GameView type unavailable.");
            // Exercise the same Show/Focus calls that previously raised the Editor on Default.
            // The owner first verifies the process is on the isolated Windows desktop.
            var gameView = EditorWindow.GetWindow(gameViewType, false, "Game", false);
            gameView.Show();
            gameView.Focus();
            gameView.Repaint();
            return Read();
        }

        public static Dictionary<string, object> Read()
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var framework = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi)
                .GetMethod("IsRunActive", flags);
            var runner = AppDomain.CurrentDomain.GetAssemblies().Select(a =>
                a.GetType("Unity.Pipeline.Editor.Testing.PipelineTestRunner", false))
                .FirstOrDefault(t => t != null);
            var collector = runner?.GetField("m_ActiveCollector", flags);
            if (framework == null || collector == null)
                throw new InvalidOperationException("Test-runner idle schema unavailable.");
            return new Dictionary<string, object>
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["mainThreadDesktopName"] = DesktopName(GetThreadDesktop(GetCurrentThreadId())),
                ["inputDesktopName"] = InputDesktopName(),
                ["projectPath"] = System.IO.Path.GetDirectoryName(Application.dataPath),
                ["graphicsDeviceType"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDeviceName"] = SystemInfo.graphicsDeviceName,
                ["batchMode"] = Application.isBatchMode,
                ["applicationFocused"] = Application.isFocused,
                ["gameViewFocused"] = EditorWindow.focusedWindow != null &&
                    EditorWindow.focusedWindow.GetType().FullName == "UnityEditor.GameView",
                ["runInBackground"] = Application.runInBackground,
                ["playing"] = EditorApplication.isPlayingOrWillChangePlaymode,
                ["compiling"] = EditorApplication.isCompiling,
                ["updating"] = EditorApplication.isUpdating,
                ["building"] = BuildPipeline.isBuildingPlayer,
                ["frameworkActive"] = (bool)framework.Invoke(null, null),
                ["collectorActive"] = collector.GetValue(null) != null,
                ["runtimeMvid"] = typeof(Splatoon.Blocks.BlockTerrain).Assembly.ManifestModule.ModuleVersionId.ToString(),
                ["configMvid"] = typeof(Splatoon.Config.BlockGameplaySettings).Assembly.ManifestModule.ModuleVersionId.ToString(),
                ["testsMvid"] = AppDomain.CurrentDomain.GetAssemblies().Single(a =>
                    a.GetName().Name == "Splatoon.Tests").ManifestModule.ModuleVersionId.ToString(),
                ["utc"] = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
