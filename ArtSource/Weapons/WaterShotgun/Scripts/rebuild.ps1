param(
    [string]$BlenderExecutable = 'D:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe',
    [string]$PythonExecutable = 'python',
    [switch]$ValidateUnity,
    [string]$ValidationProject = 'D:\XPHUNITY\ProjectSplatoon-WaterShotgun-Validation-20260917',
    [string]$UnityExecutable = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$waterRoot = Split-Path -Parent $PSScriptRoot
$waterSourceProject = [IO.Path]::GetFullPath((Join-Path $waterRoot '..\..\..'))
if (-not (Test-Path -LiteralPath $BlenderExecutable)) { throw "Blender not found: $BlenderExecutable" }
& $PythonExecutable (Join-Path $PSScriptRoot 'generate_texture.py')
if ($LASTEXITCODE -ne 0) { throw 'Texture generation failed; Python requires Pillow.' }
& $BlenderExecutable --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'build_water_shotgun.py') -- --render
if ($LASTEXITCODE -ne 0) { throw 'Blender build failed.' }
if ($ValidateUnity) {
    $waterValidationResolved = [IO.Path]::GetFullPath($ValidationProject)
    if ($waterValidationResolved.TrimEnd('\') -eq $waterSourceProject.TrimEnd('\')) { throw 'Use an isolated project copy, never the live project.' }
    if (-not (Test-Path -LiteralPath (Join-Path $waterValidationResolved 'ProjectSettings\ProjectVersion.txt'))) {
        throw 'Prepare an isolated Unity project copy with Assets, Packages and ProjectSettings first.'
    }
    $waterAssetFolder = Join-Path $waterValidationResolved 'Assets\WaterShotgunValidation'
    New-Item -ItemType Directory -Path $waterAssetFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WaterShotgunValidation.cs') -Destination (Join-Path $waterValidationResolved 'Assets\Splatoon\Editor\Prototype\WaterShotgunValidation.cs')
    Copy-Item -LiteralPath (Join-Path $waterRoot 'Export\WaterShotgun.fbx'),(Join-Path $waterRoot 'Export\WaterShotgun_Albedo.png') -Destination $waterAssetFolder
    $waterPreviousSource = $env:WATER_SHOTGUN_SOURCE_PROJECT
    try {
        $env:WATER_SHOTGUN_SOURCE_PROJECT = $waterSourceProject
        $waterArgs = '-batchmode -projectPath "{0}" -executeMethod Splatoon.Editor.WaterShotgunValidation.ValidateAndRender -logFile "{1}"' -f $waterValidationResolved,(Join-Path $waterRoot 'Validation\unity-validation.log')
        $waterProcess = Start-Process -FilePath $UnityExecutable -ArgumentList $waterArgs -WindowStyle Hidden -PassThru
        $waterProcess.WaitForExit()
        if ($waterProcess.ExitCode -ne 0) { throw "Unity validation failed: $($waterProcess.ExitCode)" }
        & $PythonExecutable (Join-Path $PSScriptRoot 'make_review_sheets.py')
        if ($LASTEXITCODE -ne 0) { throw 'Review sheet generation failed.' }
    } finally { $env:WATER_SHOTGUN_SOURCE_PROJECT = $waterPreviousSource }
}
Write-Output "WaterShotgun output: $waterRoot"
