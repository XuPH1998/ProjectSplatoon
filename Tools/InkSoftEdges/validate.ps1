param(
    [Parameter(Mandatory=$true)][string]$ValidationProject,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [ValidateSet('graphics','regression','windows-build','android-shaders')]
    [string[]]$Stages=@('graphics','regression','windows-build','android-shaders')
)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $ValidationProject).Path
if($project -eq (Resolve-Path "$PSScriptRoot/../..").Path){throw 'Use an isolated validation copy.'}
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$report=(Resolve-Path -LiteralPath $ReportDirectory).Path
$results=[System.Collections.Generic.List[object]]::new()
$summary=Join-Path $report 'suite-summary.json'
if(Test-Path -LiteralPath $summary){
    foreach($entry in @(Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json)){
        if($entry.stage -notin $Stages){$results.Add($entry)}
    }
}
foreach($stage in $Stages){
    $method=switch($stage){'graphics'{'Run'} 'windows-build'{'BuildWindows'} 'android-shaders'{'CompileMobileVariants'}}
    $extra=if($stage -eq 'regression'){
        '-runTests -testPlatform EditMode -testFilter "Splatoon.Tests.InkSoftEdgeTests;Splatoon.Tests.InkAppearanceTests;Splatoon.Tests.InkSimulationTests;Splatoon.Tests.InkEdgeTests;Splatoon.Tests.InkShapeAtlasTests;Splatoon.Tests.InkCoverageTests;Splatoon.Tests.InkSeamTests.Real;Splatoon.Tests.InkSeamTests.Ground;Splatoon.Tests.InkSeamTests.All;Splatoon.Tests.WeaponAlignmentCoverageTests;Splatoon.Tests.FrameOptimizationTests.BatchedGpuPaintMatchesOriginalBytesAndKeepsMaskIdentity;Splatoon.Tests.InkSeamPlayTests" -testResults "'+$report+'/regression.xml"'
    }else{'-executeMethod Splatoon.Editor.InkSoftValidation.'+$method}
    $args='-batchmode -force-d3d11 -projectPath "'+$project+'" -logFile "'+$report+'/'+$stage+'.log" -inkValidationOutput "'+$report+'" '+$extra
    $p=Start-Process 'C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe' -ArgumentList $args -WindowStyle Hidden -PassThru
    $p.WaitForExit()
    $results.Add([pscustomobject]@{stage=$stage;exitCode=$p.ExitCode;time=[DateTime]::UtcNow.ToString('o')})
    $results | ConvertTo-Json | Set-Content -LiteralPath ($report+'/suite-summary.json') -Encoding utf8
    if($p.ExitCode -ne 0){throw "$stage failed; inspect its log"}
    Write-Output "$stage passed"
}
