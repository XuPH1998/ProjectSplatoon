param(
    [string]$PlayerPath,
    [int]$Port = 18518,
    [string]$OutputDirectory,
    [ValidateSet(6, 8)][int]$HeroId = 8,
    [switch]$CameraAcceptance
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PlayerPath) { $PlayerPath = Join-Path $projectRoot 'Builds/SplooshGirl/InkLan.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'Reports/SplooshGirl/Network' }
$PlayerPath = (Resolve-Path -LiteralPath $PlayerPath).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$launched = @()
try {
    foreach ($role in @('host', 'client')) {
        $report = Join-Path $OutputDirectory "$role.txt"
        if (Test-Path -LiteralPath $report) { throw "Use a new output directory; existing evidence: $report" }
        $log = Join-Path $OutputDirectory "$role.log"
        $arguments = "-batchmode -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -splooshRole $role -splooshHero $HeroId -splooshPort $Port -splooshOutput `"$report`" -logFile `"$log`""
        if ($CameraAcceptance) { $arguments += ' -cameraAcceptance' }
        $process = Start-Process -FilePath $PlayerPath -WorkingDirectory (Split-Path $PlayerPath -Parent) -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $launched += @{ Role = $role; Process = $process }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(210)
    while (@($launched | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Independent-player acceptance timed out.' }
        Start-Sleep -Seconds 2
    }
    $reports = @{}
    foreach ($run in $launched) {
        $run.Process.Refresh()
        if ($run.Process.ExitCode -ne 0) { throw "$($run.Role) exited with $($run.Process.ExitCode). Inspect its log." }
        $reports[$run.Role] = ConvertFrom-StringData (Get-Content -LiteralPath (Join-Path $OutputDirectory "$($run.Role).txt") -Raw)
        if ($reports[$run.Role]['passed'] -ne 'True') { throw "$($run.Role) acceptance failed." }
    }
    foreach ($key in @('finalPaintHash', 'finalPaintSequence', 'contentSignature')) {
        if ($reports.host[$key] -ne $reports.client[$key]) { throw "Host and Client disagree: $key" }
    }
    $states = @{}
    foreach ($role in @('host', 'client')) {
        $states[$role] = @{}
        foreach ($line in Get-Content -LiteralPath (Join-Path $OutputDirectory "$role.txt.states.tsv")) {
            $parts = $line -split "`t", 2
            $states[$role][$parts[0]] = $parts[1]
        }
    }
    $matched = @{}
    foreach ($key in $states.client.Keys) {
        if (-not $states.host.ContainsKey($key)) { continue }
        if ($states.host[$key] -ne $states.client[$key]) { throw "Replicated combat snapshot differs: $key" }
        $owner = ($key -split '/')[0]
        $matched[$owner] = 1 + [int]$matched[$owner]
    }
    if ($matched.Count -lt 2 -or @($matched.Values | Where-Object { $_ -lt 20 }).Count -gt 0) {
        throw 'Insufficient overlapping combat snapshots for both players.'
    }
    $result = @{ passed = $true; player = $PlayerPath; host = $reports.host; client = $reports.client;
        matchingCombatSnapshotsByOwner = $matched;
        environment = 'Two independent Windows processes on one PC; not physical two-machine or target-device acceptance.' }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'comparison.json') -Encoding utf8
    Write-Output 'PASS: independent Host + Client; matching settled ownership, paint sequence and content signature.'
}
finally {
    foreach ($run in $launched) {
        if (-not $run.Process.HasExited) { Stop-Process -Id $run.Process.Id }
        $run.Process.Dispose()
    }
}
