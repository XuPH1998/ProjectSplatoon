[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$codeOutput = Join-Path $projectRoot "Assets/Splatoon/Config/Generated"
$dataOutput = Join-Path $projectRoot "Assets/GameResource/Bootstrap/Config/Luban"
$luban = Join-Path $PSScriptRoot "tools/Luban/Luban.dll"
$config = Join-Path $projectRoot "Config/Luban/source/luban.conf"
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

New-Item -ItemType Directory -Path $codeOutput -Force | Out-Null
New-Item -ItemType Directory -Path $dataOutput -Force | Out-Null

& $dotnet $luban `
    -t client `
    -c cs-simple-json `
    -d json `
    --conf $config `
    -x "outputCodeDir=$codeOutput" `
    -x "outputDataDir=$dataOutput"

if ($LASTEXITCODE -ne 0) {
    throw "Luban generation failed with exit code $LASTEXITCODE."
}

Write-Host "Luban generation completed."
