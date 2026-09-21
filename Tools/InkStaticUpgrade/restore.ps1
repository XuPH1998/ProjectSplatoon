param([switch]$Apply,
      [string]$Manifest = 'Reports/InkStaticUpgrade/restore-manifest.json')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$manifestPath=Join-Path $root $Manifest
$entries=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$prefix=$root.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
foreach($entry in $entries) {
    $target=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if(!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Path outside workspace: $target" }
    if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.afterHash) {
        throw "File changed after this upgrade; restore stopped to preserve later work: $($entry.path)"
    }
    if($entry.beforePath) {
        $backup=[IO.Path]::GetFullPath((Join-Path $root $entry.beforePath))
        if(!$backup.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $backup) -or (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $entry.beforeHash) {
            throw "Missing or changed baseline: $($entry.path)"
        }
    }
}
if(!$Apply) { Write-Output "Validated $($entries.Count) files. Add -Apply to restore this upgrade only; all current hashes must match."; return }
foreach($entry in $entries) {
    $target=Join-Path $root $entry.path
    if($entry.beforePath) { Copy-Item -LiteralPath (Join-Path $root $entry.beforePath) -Destination $target }
    else { Remove-Item -LiteralPath $target }
}
Write-Output 'Restored the pre-upgrade files and removed only the recorded new assets and their .meta files.'
