param([Parameter(Mandatory=$true)][string]$ValidationProject,
      [string]$Unity = 'C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe',
      [Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $ValidationProject).Path
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$report=(Resolve-Path -LiteralPath $ReportDirectory).Path
$results=[System.Collections.Generic.List[object]]::new()
function RunUnity([string]$name,[string]$arguments) {
    $log=Join-Path $report ($name+'.log')
    $p=Start-Process -FilePath $Unity -ArgumentList ('-batchmode -force-d3d11 -projectPath "'+$project+'" -logFile "'+$log+'" '+$arguments) -WindowStyle Hidden -PassThru
    $p.WaitForExit()
    $results.Add([pscustomobject]@{stage=$name;exitCode=$p.ExitCode;log=$log})
    if($p.ExitCode -ne 0) { Write-Warning "$name failed: $($p.ExitCode); see $log" }
    else { Write-Output "$name complete" }
}
RunUnity 'edit-tests' ('-runTests -testPlatform EditMode -testFilter "Splatoon.Tests.InkAppearanceTests;Splatoon.Tests.InkSimulationTests;Splatoon.Tests.InkEdgeTests;Splatoon.Tests.InkShapeAtlasTests;Splatoon.Tests.InkCoverageTests;Splatoon.Tests.InkSeamTests.Real;Splatoon.Tests.InkSeamTests.Ground;Splatoon.Tests.InkSeamTests.All;Splatoon.Tests.WeaponAlignmentCoverageTests;Splatoon.Tests.FrameOptimizationTests.BatchedGpuPaintMatchesOriginalBytesAndKeepsMaskIdentity" -testResults "'+(Join-Path $report 'edit-tests.xml')+'"')
RunUnity 'play-tests' ('-runTests -testPlatform EditMode -testFilter "Splatoon.Tests.InkSeamPlayTests;Splatoon.Tests.InkImpactHostPlayTests" -testResults "'+(Join-Path $report 'play-tests.xml')+'"')
RunUnity 'graphics' '-executeMethod Splatoon.Editor.InkStaticValidation.Run'
RunUnity 'windows-build' '-executeMethod Splatoon.Editor.InkStaticValidation.BuildWindows'
RunUnity 'android-shaders' '-executeMethod Splatoon.Editor.InkStaticValidation.CompileMobileVariants'
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report 'suite-summary.json') -Encoding utf8
if(@($results | Where-Object exitCode -ne 0).Count -gt 0) { exit 1 }

