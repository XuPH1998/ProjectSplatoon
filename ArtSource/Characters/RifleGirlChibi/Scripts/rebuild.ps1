param(
    [string]$BlenderExecutable='D:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe',
    [string]$UnityExecutable='C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe',
    [string]$ValidationProject='D:\XPHUNITY\ProjectSplatoon-RifleGirlChibi-Validation-20260917',
    [switch]$RefreshSource,
    [switch]$ValidateUnity
)
$ErrorActionPreference='Stop'
$chibiArt=Split-Path -Parent $PSScriptRoot
$chibiProject=[IO.Path]::GetFullPath((Join-Path $chibiArt '..\..\..'))
$chibiRelative='Assets\GameResource\Characters\RifleGirlChibi'
function Invoke-ChibiUnity([string]$Method,[string]$Log) {
    $chibiArguments='-batchmode -projectPath "{0}" -executeMethod {1} -logFile "{2}"' -f $ValidationProject,$Method,(Join-Path $chibiArt ('Validation\'+$Log))
    $chibiProcess=Start-Process -FilePath $UnityExecutable -ArgumentList $chibiArguments -WindowStyle Hidden -PassThru
    $chibiProcess.WaitForExit()
    if($chibiProcess.ExitCode -ne 0){throw "Unity failed: $Method. See $Log"}
}
if($RefreshSource -or $ValidateUnity) {
    if([IO.Path]::GetFullPath($ValidationProject).TrimEnd('\') -eq $chibiProject.TrimEnd('\')){throw 'Use an isolated project copy.'}
    if(-not(Test-Path (Join-Path $ValidationProject 'ProjectSettings\ProjectVersion.txt'))){throw 'Prepare a full isolated copy of Assets, Packages and ProjectSettings.'}
    $chibiEditor=Join-Path $ValidationProject 'Assets\ChibiArt\Editor'
    New-Item -ItemType Directory -Path $chibiEditor -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RifleGirlChibiPipeline.cs') -Destination $chibiEditor
    $chibiPreview=Join-Path $ValidationProject ($chibiRelative+'\Preview')
    New-Item -ItemType Directory -Path $chibiPreview -Force | Out-Null
    Copy-Item -Path (Join-Path $chibiProject ($chibiRelative+'\Preview\*.cs*')) -Destination $chibiPreview -Force
    Copy-Item -LiteralPath (Join-Path $chibiProject 'Assets\Splatoon\Runtime\Core\SplatoonBootstrap.cs') -Destination (Join-Path $ValidationProject 'Assets\Splatoon\Runtime\Core\SplatoonBootstrap.cs') -Force
}
if($RefreshSource){Invoke-ChibiUnity 'ChibiArt.RifleGirlChibiPipeline.ExportSource' 'unity-export.log'}
& $BlenderExecutable --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'build_chibi.py') -- --render
if($LASTEXITCODE -ne 0){throw 'Blender model build failed.'}
& $BlenderExecutable --background --factory-startup --disable-autoexec --python-exit-code 1 --python (Join-Path $PSScriptRoot 'verify_blend.py')
if($LASTEXITCODE -ne 0){throw 'Delivered Blender model verification failed.'}
if($ValidateUnity) {
    $chibiModels=Join-Path $ValidationProject ($chibiRelative+'\Models')
    New-Item -ItemType Directory -Path $chibiModels -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $chibiProject ($chibiRelative+'\Models\RifleGirlChibi.fbx')) -Destination $chibiModels
    Invoke-ChibiUnity 'ChibiArt.RifleGirlChibiPipeline.BuildAndValidate' 'unity-validation.log'
    Invoke-ChibiUnity 'ChibiArt.ChibiPreviewPlayValidation.Run' 'unity-play-mode.log'
    # Copy only this newly authored character's assets and stable GUIDs.
    Copy-Item -Path (Join-Path $ValidationProject ($chibiRelative+'\*')) -Destination (Join-Path $chibiProject $chibiRelative) -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $ValidationProject ($chibiRelative+'.meta')) -Destination (Join-Path $chibiProject ($chibiRelative+'.meta')) -Force
}
Write-Output "Chibi assets: $(Join-Path $chibiProject $chibiRelative)"
