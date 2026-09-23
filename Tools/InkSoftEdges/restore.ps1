[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$baseline=Join-Path $root 'Reports/InkSoftEdges/Baseline'
$manifest=Get-Content -LiteralPath (Join-Path $root 'Reports/InkSoftEdges/implementation-manifest.json') -Raw | ConvertFrom-Json
# Validate every path and hash before restoring anything. Never overwrite later edits.
foreach($entry in $manifest){
    $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if(!$path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest escaped project root'}
    if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "File changed after delivery: $($entry.path). Restore manually."}
}
foreach($entry in $manifest){
    $path=Join-Path $root $entry.path
    if($PSCmdlet.ShouldProcess($path,'Restore pre-experiment code')){
        if($entry.existed){Copy-Item -LiteralPath (Join-Path $baseline $entry.path) -Destination $path -Force}
        else{Remove-Item -LiteralPath $path -Force}
    }
}
if($WhatIfPreference){Write-Output 'Dry run validated; no files changed.'}
else{Write-Output 'Only ink experiment source files were restored. Reports and unrelated work remain.'}
