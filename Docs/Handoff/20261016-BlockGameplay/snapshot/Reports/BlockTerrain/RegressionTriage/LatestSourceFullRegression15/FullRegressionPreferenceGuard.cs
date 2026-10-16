// Prepared Reports-only guard. Compile/execute only when the owner starts the reviewed wrapper.
// This file is outside Assets and is invoked with run_script in ephemeral mode.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Splatoon.ReportGuards
{
    public static class FullRegressionPreferenceGuard
    {
        const string Company = "DefaultCompany";
        const string Product = "喷墨对战";
        const string Hive = @"Software\Unity\UnityEditor\DefaultCompany\喷墨对战";
        const string RunnerType = "Unity.Pipeline.Editor.Testing.PipelineTestRunner";
        const string FrameworkType = "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi";

        public sealed class ApiPreference
        {
            public string key;
            public string type;
            public bool had;
            public string stringValue;
            public int intValue;
        }

        public sealed class RegistryPreference
        {
            public string logicalKey;
            public string name;
            public string kind;
            public string binaryBase64;
            public string stringValue;
            public string[] stringsValue;
            public int dwordValue;
            public long qwordValue;
        }

        public sealed class SnapshotData
        {
            public int schema = 1;
            public string utc;
            public string project;
            public string hive;
            public string user;
            public string userSid;
            public int editorPid;
            public ApiPreference[] api;
            public RegistryPreference[] registry;
        }

        static string[] Keys()
        {
            var keys = new List<string> { "Splatoon.PlayerName" };
            for (int hero = 1; hero <= 9; hero++)
            {
                keys.Add("Loadout.Sub." + hero);
                keys.Add("Loadout.Special." + hero);
            }
            return keys.ToArray();
        }

        static bool IsPhysicalName(string name, string logical)
        {
            if (string.Equals(name, logical, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = logical + "_h";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) return false;
            return name.Substring(prefix.Length).All(c => c >= '0' && c <= '9');
        }

        static string Project(string expected)
        {
            string actual = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)).TrimEnd(Path.DirectorySeparatorChar);
            string wanted = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Connected Editor project mismatch.");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                PlayerSettings.companyName != Company || PlayerSettings.productName != Product)
                throw new InvalidOperationException("Windows Editor preference hive identity mismatch.");
            return actual;
        }

        static Type LoadedType(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
            if (type == null) throw new InvalidOperationException("Cannot verify test-runner idle state: " + name);
            return type;
        }

        static void RequireIdle(string root)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPaused)
                throw new InvalidOperationException("Editor must be ready and stopped.");
            if (File.Exists(Path.Combine(root, "Temp/pipeline_test_request.json")))
                throw new InvalidOperationException("An existing Pipeline test request is pending.");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var field = LoadedType(RunnerType).GetField("m_ActiveCollector", flags);
            var active = LoadedType(FrameworkType).GetMethod("IsRunActive", flags);
            if (field == null || active == null)
                throw new InvalidOperationException("Installed runner idle schema has changed.");
            if (field.GetValue(null) != null || (bool)active.Invoke(null, null))
                throw new InvalidOperationException("An existing Unity test runner is active.");
        }

        static string ReportPath(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string reports = Path.GetFullPath(Path.Combine(root,
                "Reports/BlockTerrain/RegressionTriage/LatestSourceFullRegression15/Runs")) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(reports, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Guard artifacts must be in the prepared fresh Runs directory.");
            return full;
        }

        static RegistryPreference ReadValue(RegistryKey registry, string name, string key)
        {
            RegistryValueKind kind = registry.GetValueKind(name);
            object value = registry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var row = new RegistryPreference { logicalKey = key, name = name, kind = kind.ToString() };
            switch (kind)
            {
                case RegistryValueKind.Binary:
                case RegistryValueKind.None: row.binaryBase64 = Convert.ToBase64String((byte[])value); break;
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: row.stringValue = (string)value; break;
                case RegistryValueKind.MultiString: row.stringsValue = (string[])value; break;
                case RegistryValueKind.DWord: row.dwordValue = (int)value; break;
                case RegistryValueKind.QWord: row.qwordValue = (long)value; break;
                default: throw new InvalidOperationException("Unsupported target registry value kind: " + kind);
            }
            return row;
        }

        static object RawValue(RegistryPreference row)
        {
            switch ((RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), row.kind))
            {
                case RegistryValueKind.Binary:
                case RegistryValueKind.None: return Convert.FromBase64String(row.binaryBase64);
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: return row.stringValue;
                case RegistryValueKind.MultiString: return row.stringsValue;
                case RegistryValueKind.DWord: return row.dwordValue;
                case RegistryValueKind.QWord: return row.qwordValue;
                default: throw new InvalidOperationException("Unsupported baseline registry kind.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeTokenUser
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool OpenThreadToken(IntPtr thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetTokenInformation(IntPtr token, int informationClass,
            IntPtr information, uint informationLength, out uint returnLength);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsValidSid(IntPtr sid);

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        static string GetEffectiveUserSid()
        {
            const uint TokenQuery = 0x0008;
            const int TokenUser = 1, ErrorNoToken = 1008, ErrorInsufficientBuffer = 122;
            IntPtr token = IntPtr.Zero, userBuffer = IntPtr.Zero, stringSid = IntPtr.Zero;
            try
            {
                // OpenAsSelf affects the access check, not which thread token is queried.
                if (!OpenThreadToken(GetCurrentThread(), TokenQuery, true, out token))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoToken)
                        throw new Win32Exception(error, "Cannot query the current thread token SID.");
                    if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the current process token SID.");
                }

                uint requiredLength;
                if (GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out requiredLength))
                    throw new InvalidOperationException("Unexpected empty TokenUser query result.");
                int sizeError = Marshal.GetLastWin32Error();
                if (sizeError != ErrorInsufficientBuffer)
                    throw new Win32Exception(sizeError, "Cannot size the current token SID buffer.");
                uint headerLength = (uint)Marshal.SizeOf(typeof(NativeTokenUser));
                if (requiredLength < headerLength || requiredLength > int.MaxValue)
                    throw new InvalidOperationException("Invalid TokenUser buffer size.");

                userBuffer = Marshal.AllocHGlobal((int)requiredLength);
                uint returnedLength;
                if (!GetTokenInformation(token, TokenUser, userBuffer, requiredLength, out returnedLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the current token SID.");
                if (returnedLength < headerLength || returnedLength > requiredLength)
                    throw new InvalidOperationException("Invalid TokenUser result size.");
                IntPtr sid = Marshal.ReadIntPtr(userBuffer); // TOKEN_USER begins with SID_AND_ATTRIBUTES.Sid.
                if (sid == IntPtr.Zero || !IsValidSid(sid))
                    throw new InvalidOperationException("The current token contains an invalid user SID.");
                if (!ConvertSidToStringSidW(sid, out stringSid))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot convert the current token SID.");
                string value = Marshal.PtrToStringUni(stringSid);
                if (string.IsNullOrEmpty(value) || !value.StartsWith("S-", StringComparison.Ordinal))
                    throw new InvalidOperationException("The current token SID string is invalid.");
                return value;
            }
            finally
            {
                // Only the opened token is owned; GetCurrentThread/Process return pseudo handles.
                if (stringSid != IntPtr.Zero) LocalFree(stringSid);
                if (userBuffer != IntPtr.Zero) Marshal.FreeHGlobal(userBuffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        static SnapshotData Capture(string root)
        {
            var data = new SnapshotData {
                utc = DateTime.UtcNow.ToString("O"), project = root, hive = Hive,
                user = Environment.UserName, userSid = GetEffectiveUserSid(),
                editorPid = System.Diagnostics.Process.GetCurrentProcess().Id
            };
            data.api = Keys().Select(key => new ApiPreference {
                key = key, type = key == "Splatoon.PlayerName" ? "string" : "int",
                had = PlayerPrefs.HasKey(key),
                stringValue = key == "Splatoon.PlayerName" && PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null,
                intValue = key != "Splatoon.PlayerName" && PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) : 0
            }).ToArray();
            using (var registry = Registry.CurrentUser.OpenSubKey(Hive, false))
            {
                // A sandbox or wrong user can make HKCU appear absent. Never infer 19 absent keys from null.
                if (registry == null) throw new InvalidOperationException("Actual Editor preference hive is unavailable; snapshot rejected.");
                var rows = new List<RegistryPreference>();
                foreach (string name in registry.GetValueNames())
                {
                    string key = Keys().FirstOrDefault(k => IsPhysicalName(name, k));
                    if (key != null) rows.Add(ReadValue(registry, name, key));
                }
                data.registry = rows.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            return data;
        }

        static void ValidateBaseline(SnapshotData data, string root)
        {
            if (data == null || data.schema != 1 || data.hive != Hive ||
                !string.Equals(data.project, root, StringComparison.OrdinalIgnoreCase) ||
                data.userSid != GetEffectiveUserSid() || data.api == null || data.registry == null)
                throw new InvalidOperationException("Invalid baseline identity or schema.");
            var keys = Keys();
            if (data.api.Length != 19 || !data.api.Select(p => p.key).SequenceEqual(keys))
                throw new InvalidOperationException("Baseline must contain exactly the 19 reviewed logical preferences.");
            foreach (var api in data.api)
            {
                if (api.type != (api.key == "Splatoon.PlayerName" ? "string" : "int"))
                    throw new InvalidOperationException("Baseline API value type mismatch.");
                var raw = data.registry.Where(r => r.logicalKey == api.key).ToArray();
                if (api.had != (raw.Length != 0))
                    throw new InvalidOperationException("API and physical key presence disagree: " + api.key);
                if (api.had && !raw.Any(r => api.type == "int"
                    ? r.kind == "DWord" && r.dwordValue == api.intValue
                    : (r.kind == "String" && r.stringValue == api.stringValue) ||
                      (r.kind == "Binary" && DecodeString(r.binaryBase64) == api.stringValue)))
                    throw new InvalidOperationException("API and persisted value disagree: " + api.key);
            }
            if (data.registry.Select(r => r.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.registry.Length)
                throw new InvalidOperationException("Duplicate physical baseline names.");
            foreach (var raw in data.registry)
            {
                if (!keys.Contains(raw.logicalKey) || !IsPhysicalName(raw.name, raw.logicalKey))
                    throw new InvalidOperationException("Baseline includes an unrelated registry value.");
                RawValue(raw); // Validate every typed value before changing any preference.
            }
        }

        static string DecodeString(string base64)
        {
            byte[] value = Convert.FromBase64String(base64);
            int count = value.Length != 0 && value[value.Length - 1] == 0 ? value.Length - 1 : value.Length;
            return new UTF8Encoding(false, true).GetString(value, 0, count);
        }

        static void WriteNew(string path, object value)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        public static object Snapshot(string expectedRoot, string outputPath)
        {
            string root = Project(expectedRoot);
            RequireIdle(root);
            string output = ReportPath(root, outputPath);
            var before = Capture(root);
            ValidateBaseline(before, root);
            WriteNew(output, before);
            return new { saved = true, preferences = before.api.Length, physicalValues = before.registry.Length,
                snapshotPath = output, before.editorPid, before.userSid };
        }

        public static object Restore(string expectedRoot, string beforePath, string outputDirectory)
        {
            string root = Project(expectedRoot);
            RequireIdle(root); // No preference writes if a test is still active, even after a misleading stale status.
            string baselinePath = ReportPath(root, beforePath);
            string output = ReportPath(root, outputDirectory);
            if (Directory.Exists(output)) throw new InvalidOperationException("Use a fresh restoration evidence directory.");
            var before = JsonConvert.DeserializeObject<SnapshotData>(File.ReadAllText(baselinePath));
            ValidateBaseline(before, root);
            Directory.CreateDirectory(output);
            WriteNew(Path.Combine(output, "after-test.json"), Capture(root));
            foreach (var preference in before.api)
            {
                if (!preference.had) PlayerPrefs.DeleteKey(preference.key);
                else if (preference.type == "string") PlayerPrefs.SetString(preference.key, preference.stringValue);
                else PlayerPrefs.SetInt(preference.key, preference.intValue);
            }
            PlayerPrefs.Save(); // Restore Editor cache, then preserve exact persisted target types and bytes.
            using (var registry = Registry.CurrentUser.OpenSubKey(Hive, true))
            {
                if (registry == null) throw new InvalidOperationException("Actual Editor preference hive is unavailable for restoration.");
                foreach (string name in registry.GetValueNames())
                    if (Keys().Any(key => IsPhysicalName(name, key))) registry.DeleteValue(name, false);
                foreach (var raw in before.registry)
                    registry.SetValue(raw.name, RawValue(raw), (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), raw.kind));
                registry.Flush();
            }
            var after = Capture(root);
            WriteNew(Path.Combine(output, "after-restored.json"), after);
            bool apiEqual = JsonConvert.SerializeObject(before.api) == JsonConvert.SerializeObject(after.api);
            bool rawEqual = JsonConvert.SerializeObject(before.registry) == JsonConvert.SerializeObject(after.registry);
            var result = new { restored = apiEqual && rawEqual, apiEqual, rawEqual,
                preferences = before.api.Length, physicalValues = before.registry.Length,
                before.editorPid, restoredEditorPid = after.editorPid };
            WriteNew(Path.Combine(output, "verification.json"), result);
            if (!result.restored) throw new InvalidOperationException("Exact preference verification failed; preserve all restoration evidence.");
            return result;
        }
    }
}
