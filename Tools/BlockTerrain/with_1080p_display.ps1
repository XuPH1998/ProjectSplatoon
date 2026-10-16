param(
    [Parameter(Mandatory = $true)][string]$PythonPath,
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ScriptArguments
)

$ErrorActionPreference = 'Stop'
# Use enumerated DEVMODEW records and dynamic flags=0; do not persist registry settings.
# https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsw
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BlockAcceptanceDisplay {
    private const int ModeBytes = 220;
    public static string DeviceName;
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int index, IntPtr mode);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string device, IntPtr mode, IntPtr window, uint flags, IntPtr parameter);
    public static byte[] Read(int index) {
        IntPtr memory = Marshal.AllocHGlobal(ModeBytes);
        try {
            byte[] record = new byte[ModeBytes];
            Marshal.Copy(record, 0, memory, ModeBytes);
            Marshal.WriteInt16(memory, 68, ModeBytes);
            if (!EnumDisplaySettingsW(DeviceName, index, memory)) return null;
            Marshal.Copy(memory, record, 0, ModeBytes);
            return record;
        } finally { Marshal.FreeHGlobal(memory); }
    }
    public static int Apply(byte[] record, uint flags) {
        if (record == null || record.Length != ModeBytes) throw new ArgumentException("Invalid display record");
        IntPtr memory = Marshal.AllocHGlobal(ModeBytes);
        try {
            Marshal.Copy(record, 0, memory, ModeBytes);
            return ChangeDisplaySettingsExW(DeviceName, memory, IntPtr.Zero, flags, IntPtr.Zero);
        } finally { Marshal.FreeHGlobal(memory); }
    }
    public static int Width(byte[] mode) { return BitConverter.ToInt32(mode, 172); }
    public static int Height(byte[] mode) { return BitConverter.ToInt32(mode, 176); }
    public static int Frequency(byte[] mode) { return BitConverter.ToInt32(mode, 184); }
    public static bool Same(byte[] a, byte[] b) {
        return a != null && b != null && Width(a)==Width(b) && Height(a)==Height(b)
            && Frequency(a)==Frequency(b) && BitConverter.ToInt32(a,168)==BitConverter.ToInt32(b,168)
            && BitConverter.ToInt32(a,180)==BitConverter.ToInt32(b,180)
            && BitConverter.ToInt32(a,76)==BitConverter.ToInt32(b,76)
            && BitConverter.ToInt32(a,80)==BitConverter.ToInt32(b,80)
            && BitConverter.ToInt32(a,84)==BitConverter.ToInt32(b,84)
            && BitConverter.ToInt32(a,88)==BitConverter.ToInt32(b,88);
    }
    public static bool Is1080p(byte[] mode) {
        return mode != null && Width(mode)==1920 && Height(mode)==1080;
    }
}
'@

Add-Type -AssemblyName System.Windows.Forms
[BlockAcceptanceDisplay]::DeviceName = [System.Windows.Forms.Screen]::PrimaryScreen.DeviceName

$originalMode = [BlockAcceptanceDisplay]::Read(-1)
if ($null -eq $originalMode) { throw 'Cannot read the current display mode.' }
$acceptanceMode = $null
for ($modeIndex = 0; $modeIndex -lt 2048; $modeIndex++) {
    $mode = [BlockAcceptanceDisplay]::Read($modeIndex)
    if ($null -eq $mode) { break }
    if ([BlockAcceptanceDisplay]::Width($mode) -eq 1920 -and [BlockAcceptanceDisplay]::Height($mode) -eq 1080) {
        $acceptanceMode = $mode
        if ([BlockAcceptanceDisplay]::Frequency($mode) -eq [BlockAcceptanceDisplay]::Frequency($originalMode)) { break }
    }
}
if ($null -eq $acceptanceMode) { throw 'The current display does not advertise 1920x1080.' }
$modeChanged = $false
$appliedMode = $null
$childExitCode = 1
try {
    if (-not [BlockAcceptanceDisplay]::Same($originalMode, $acceptanceMode)) {
        $testResult = [BlockAcceptanceDisplay]::Apply($acceptanceMode, 2)
        if ($testResult -ne 0) { throw "1080p display mode test failed: $testResult" }
        $applyResult = [BlockAcceptanceDisplay]::Apply($acceptanceMode, 0)
        if ($applyResult -ne 0) { throw "Temporary 1080p display mode change failed: $applyResult" }
        $modeChanged = $true
    }
    # Drivers may report 59Hz after accepting an enumerated 60Hz mode, and apply asynchronously.
    $modeDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $activeMode = [BlockAcceptanceDisplay]::Read(-1)
        if ([BlockAcceptanceDisplay]::Is1080p($activeMode)) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $modeDeadline)
    if (-not [BlockAcceptanceDisplay]::Is1080p($activeMode)) { throw 'Display did not enter the requested 1080p mode.' }
    $appliedMode = $activeMode
    Write-Output ('Acceptance display: 1920x1080@' + [BlockAcceptanceDisplay]::Frequency($activeMode) + 'Hz; temporary=' + $modeChanged)
    & $PythonPath $ScriptPath @ScriptArguments
    $childExitCode = $LASTEXITCODE
} finally {
    if ($modeChanged) {
        # Preserve a later user/remote-session mode change instead of overriding it.
        $endingMode = [BlockAcceptanceDisplay]::Read(-1)
        if (($null -ne $appliedMode -and [BlockAcceptanceDisplay]::Same($endingMode, $appliedMode)) -or
            ($null -eq $appliedMode -and [BlockAcceptanceDisplay]::Is1080p($endingMode))) {
            $restoreResult = [BlockAcceptanceDisplay]::Apply($originalMode, 0)
            if ($restoreResult -ne 0) { throw "Display restoration failed: $restoreResult" }
            $restoreDeadline = [DateTime]::UtcNow.AddSeconds(3)
            do {
                $restoredMode = [BlockAcceptanceDisplay]::Read(-1)
                if ([BlockAcceptanceDisplay]::Same($restoredMode, $originalMode)) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $restoreDeadline)
            if (-not [BlockAcceptanceDisplay]::Same($restoredMode, $originalMode)) { throw 'Display restoration was accepted but the original mode was not observed.' }
            Write-Output ('Restored display verified: ' + [BlockAcceptanceDisplay]::Width($restoredMode) + 'x' + [BlockAcceptanceDisplay]::Height($restoredMode))
        } else {
            Write-Output 'Display changed during the run; preserved its newer mode.'
        }
    }
}
exit $childExitCode
