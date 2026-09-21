param(
    [Parameter(Mandatory=$true)][string]$ValidationProject,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [string]$Unity='C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe',
    [ValidateSet('graphics','regression','windows-build','android-shaders')]
    [string[]]$Stages=@('graphics','regression','windows-build','android-shaders')
)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $ValidationProject).Path
if ($project -eq (Resolve-Path "$PSScriptRoot/../..").Path) { throw 'Use an isolated validation copy, not the active project.' }
if (!(Test-Path -LiteralPath (Join-Path $project 'Tools/CombatGirls/hero-packs.json'))) { throw 'The validation copy must include the Tools directory required by the formal build.' }
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$report=(Resolve-Path -LiteralPath $ReportDirectory).Path
$results=[System.Collections.Generic.List[object]]::new()
$summary=Join-Path $report 'suite-summary.json'
if(Test-Path -LiteralPath $summary) {
    foreach($entry in @(Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json)) {
        if($entry.stage -notin $Stages){$results.Add($entry)}
    }
}
function RunUnity([string]$stage,[string]$arguments) {
    $log=Join-Path $report ($stage+'.log')
    $process=Start-Process -FilePath $Unity -ArgumentList ('-batchmode -force-d3d11 -projectPath "'+$project+'" -logFile "'+$log+'" -inkValidationOutput "'+$report+'" '+$arguments) -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    $results.Add([pscustomobject]@{stage=$stage;exitCode=$process.ExitCode;log=$log})
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report 'suite-summary.json') -Encoding utf8
    if($process.ExitCode -ne 0){throw "$stage failed: $($process.ExitCode); see $log"}
    Write-Output "$stage passed"
}
if('graphics' -in $Stages){RunUnity 'graphics' '-executeMethod Splatoon.Editor.InkStaticValidation.RunRounded'}
$filter='Splatoon.Tests.InkAppearanceTests;Splatoon.Tests.InkSimulationTests;Splatoon.Tests.InkEdgeTests;Splatoon.Tests.InkShapeAtlasTests;Splatoon.Tests.InkCoverageTests;Splatoon.Tests.InkSeamTests.Real;Splatoon.Tests.InkSeamTests.Ground;Splatoon.Tests.InkSeamTests.All;Splatoon.Tests.WeaponAlignmentCoverageTests;Splatoon.Tests.FrameOptimizationTests.BatchedGpuPaintMatchesOriginalBytesAndKeepsMaskIdentity;Splatoon.Tests.InkSeamPlayTests'
if('regression' -in $Stages){RunUnity 'regression' ('-runTests -testPlatform EditMode -testFilter "'+$filter+'" -testResults "'+(Join-Path $report 'regression.xml')+'"')}
if('windows-build' -in $Stages){RunUnity 'windows-build' '-executeMethod Splatoon.Editor.InkStaticValidation.BuildWindows'}
if('android-shaders' -in $Stages){RunUnity 'android-shaders' '-executeMethod Splatoon.Editor.InkStaticValidation.CompileMobileVariants'}
