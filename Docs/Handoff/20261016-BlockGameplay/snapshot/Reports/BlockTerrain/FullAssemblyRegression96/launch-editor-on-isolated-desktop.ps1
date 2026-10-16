param(
    [switch]$Execute,
    [string]$DesktopName = ('CodexBlocks-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
)
$ErrorActionPreference = 'Stop'
$editorPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
$projectPath = 'D:\XPHUNITY\ProjectSplatoon'
if ($DesktopName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') { throw 'Invalid desktop name.' }
if ($DesktopName -ieq 'Default' -or $DesktopName -ieq 'Winlogon') { throw 'Reserved user/system desktops are forbidden for test launches.' }
$runDirectory = Join-Path $projectPath ('Reports\BlockTerrain\FullAssemblyRegression96\IsolatedDesktop\' + $DesktopName)
$logPath = Join-Path $runDirectory 'editor.log'
$receiptPath = Join-Path $runDirectory 'launch.json'
$arguments = '"' + $editorPath + '" -projectPath "' + $projectPath + '" -automated -acceptSoftwareTermsForThisRunOnly -logFile "' + $logPath + '"'
if (-not $Execute) {
    [pscustomobject]@{ preparedOnly = $true; desktopName = $DesktopName; editorPath = $editorPath; projectPath = $projectPath; logPath = $logPath; commandLine = $arguments } | ConvertTo-Json
    return
}
if (-not (Test-Path -LiteralPath $editorPath -PathType Leaf)) { throw 'Verified Unity 6000.3.9f1 executable is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt') -PathType Leaf)) { throw 'The expected project is missing.' }
if (Test-Path -LiteralPath $runDirectory) { throw 'Preserve the existing desktop launch receipt; use a fresh desktop name.' }
[void][IO.Directory]::CreateDirectory($runDirectory)

# Ordinary graphical Editor on an additional desktop of the current window station.
# Never switch the user's desktop. No batchmode/nographics flag and no focus promise.
# The launcher must remain alive until the Editor exits to retain the desktop handle.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public sealed class IsolatedBlockEditor : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, string device, IntPtr devmode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, uint bytes, out uint needed);
    // HDESK must be closed with CloseDesktop; CloseHandle is for the process/thread handles.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    private IntPtr desktopHandle, processHandle;
    public uint ProcessId { get; private set; }
    public string DesktopName { get; private set; }
    public string InputDesktopNameBeforeLaunch { get; private set; }
    private static string ReadInputDesktopName()
    {
        var input = OpenInputDesktop(0, false, 0x0001);
        if (input == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenInputDesktop failed; refusing launch");
        try
        {
            var text = new StringBuilder(256);
            uint needed;
            if (!GetUserObjectInformationW(input, 2, text, 512, out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Input desktop name unavailable; refusing launch");
            return text.ToString();
        }
        finally { CloseDesktop(input); }
    }
    public static IsolatedBlockEditor Start(string editor, string project, string commandLine, string desktopName)
    {
        var launch = new IsolatedBlockEditor();
        try
        {
            if (string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reserved user/system desktops are forbidden for test launches.");
            // READOBJECTS/CREATEWINDOW/CREATEMENU/HOOKCONTROL/ENUMERATE/WRITEOBJECTS.
            // Deliberately omit the desktop-switch access right.
            launch.desktopHandle = CreateDesktopW(desktopName, null, IntPtr.Zero, 0, 0x00CF, IntPtr.Zero);
            if (launch.desktopHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktopW failed");
            var startup = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = "winsta0\\" + desktopName };
            PROCESS_INFORMATION info;
            launch.InputDesktopNameBeforeLaunch = ReadInputDesktopName();
            if (string.Equals(desktopName, launch.InputDesktopNameBeforeLaunch, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Requested desktop is the active input desktop; refusing launch.");
            if (!CreateProcessW(editor, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0,
                IntPtr.Zero, project, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed");
            launch.processHandle = info.hProcess;
            launch.ProcessId = info.dwProcessId;
            launch.DesktopName = desktopName;
            CloseHandle(info.hThread);
            return launch;
        }
        catch { launch.Dispose(); throw; }
    }
    public int? WaitForExit()
    {
        // The native process handle stays open, so its PID cannot be reused during this wait.
        try
        {
            using (var child = Process.GetProcessById((int)ProcessId))
            { child.WaitForExit(); return child.ExitCode; }
        }
        catch (ArgumentException) { return null; } // Child already exited before the managed lookup.
    }
    public void Dispose()
    {
        if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
        if (desktopHandle != IntPtr.Zero) { CloseDesktop(desktopHandle); desktopHandle = IntPtr.Zero; }
    }
}
'@

$editorLaunch = $null
try {
    $editorLaunch = [IsolatedBlockEditor]::Start($editorPath, $projectPath, $arguments, $DesktopName)
    $receipt = [pscustomobject]@{
        state = 'started'
        startedUtc = [DateTime]::UtcNow.ToString('o')
        pid = $editorLaunch.ProcessId
        desktopName = $editorLaunch.DesktopName
        inputDesktopNameBeforeLaunch = $editorLaunch.InputDesktopNameBeforeLaunch
        logPath = $logPath
        editorPath = $editorPath
        projectPath = $projectPath
        launcherPid = $PID
        commandLine = $arguments
    }
    $receiptJson = $receipt | ConvertTo-Json
    [IO.File]::WriteAllText($receiptPath, $receiptJson, [Text.UTF8Encoding]::new($false))
    Write-Output $receiptJson
    $editorExitCode = $editorLaunch.WaitForExit()
    $exitReceipt = [pscustomobject]@{ state = 'exited'; finishedUtc = [DateTime]::UtcNow.ToString('o'); pid = $editorLaunch.ProcessId; desktopName = $DesktopName; exitCode = $editorExitCode }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'exit.json'), ($exitReceipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
} finally {
    if ($null -ne $editorLaunch) { $editorLaunch.Dispose() }
}
