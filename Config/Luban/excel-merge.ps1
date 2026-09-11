param(
    [string]$File,
    [switch]$AllXlsx,
    [switch]$Help
)

$ErrorActionPreference = "Stop"

function Show-Usage {
    Write-Host "Usage:"
    Write-Host "  .\excel-merge.ps1"
    Write-Host "  .\excel-merge.ps1 -File TbWeapon.xlsx"
    Write-Host "  .\excel-merge.ps1 -File Config\Luban\source\TbWeapon.xlsx"
    Write-Host "  .\excel-merge.ps1 -AllXlsx"
}

if ($Help) {
    Show-Usage
    exit 0
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = git -C $scriptDir rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Error "This script must be inside a Git repository."
    exit 1
}
Set-Location $repoRoot

$lubanDir = "Config/Luban/source"
$toolDir = Join-Path $repoRoot "Tools\ExcelMerge"
$env:PYTHONPATH = Join-Path $toolDir "src"

function Find-Python {
    foreach ($cmd in @("py", "python", "python3")) {
        $found = Get-Command $cmd -ErrorAction SilentlyContinue
        if ($found) {
            if ($cmd -eq "py") { return @("py", "-3") }
            return @($cmd)
        }
    }

    $candidates = @(
        "$env:USERPROFILE\miniconda3\python.exe",
        "$env:USERPROFILE\anaconda3\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python310\python.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return @($candidate) }
    }
    return $null
}

$python = Find-Python
if (-not $python) {
    Write-Error "Python 3.10 or newer was not found. Download: https://www.python.org/downloads/"
    exit 1
}

function Invoke-Python {
    $extra = @()
    if ($python.Count -gt 1) {
        $extra = $python[1..($python.Count - 1)]
    }
    & $python[0] @extra @args
}

Invoke-Python -c "import openpyxl, flask, click; import excelcompare.git.export_stage" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "First run: installing Python dependencies..."
    Invoke-Python -m pip install openpyxl flask click --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Dependency installation failed. Please run: $($python -join ' ') -m pip install openpyxl flask click"
        exit 1
    }

    Invoke-Python -c "import excelcompare.git.export_stage" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Excel merge helper module was not found under $toolDir\src."
        exit 1
    }
}

function Export-GitStage {
    param(
        [Parameter(Mandatory)][string]$StagePath,
        [Parameter(Mandatory)][string]$OutputPath
    )

    Invoke-Python -m excelcompare.git.export_stage $StagePath $OutputPath
    if ($LASTEXITCODE -eq 0) { return $true }
    if ($LASTEXITCODE -eq 1) { return $false }

    throw "Failed to export $StagePath. If this file uses Git LFS, run git lfs pull and try again."
}

function New-EmptyWorkbook {
    param([Parameter(Mandatory)][string]$OutputPath)

    Invoke-Python -m excelcompare.git.export_stage --empty $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create an empty workbook placeholder."
    }
}

function Invoke-ExcelMerge {
    param([Parameter(Mandatory)][string]$Path)

    $gitPath = $Path.Replace("\", "/")
    $tmpDir = Join-Path $env:TEMP ("excelmerge_{0}_{1}" -f (Get-Random), (Get-Random))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null

    try {
        if (-not (Export-GitStage -StagePath ":1:$gitPath" -OutputPath (Join-Path $tmpDir "base.xlsx"))) {
            Write-Warning "Base version was not found; using an empty workbook placeholder."
            New-EmptyWorkbook -OutputPath (Join-Path $tmpDir "base.xlsx")
        }
        if (-not (Export-GitStage -StagePath ":2:$gitPath" -OutputPath (Join-Path $tmpDir "ours.xlsx"))) {
            throw "Ours version was not found for $Path."
        }
        if (-not (Export-GitStage -StagePath ":3:$gitPath" -OutputPath (Join-Path $tmpDir "theirs.xlsx"))) {
            throw "Theirs version was not found for $Path."
        }

        Invoke-Python -m excelcompare merge `
            (Join-Path $tmpDir "base.xlsx") `
            (Join-Path $tmpDir "ours.xlsx") `
            (Join-Path $tmpDir "theirs.xlsx") `
            -o $Path --visual
        return $LASTEXITCODE
    }
    finally {
        if (Test-Path -LiteralPath $tmpDir) {
            Remove-Item -LiteralPath $tmpDir -Recurse -Force
        }
    }
}

if ($File) {
    $File = $File.Replace("\", "/")
    if ($File -notlike "*/*" -and (Test-Path -LiteralPath (Join-Path $repoRoot "$lubanDir/$File"))) {
        $File = "$lubanDir/$File"
    }
    exit (Invoke-ExcelMerge -Path $File)
}

if ($AllXlsx) {
    Write-Host "Scanning conflicted xlsx files in the whole repository..."
    $files = git diff --name-only --diff-filter=U -- "*.xlsx"
} else {
    Write-Host "Scanning conflicted Luban xlsx files under $lubanDir..."
    $files = git diff --name-only --diff-filter=U -- "$lubanDir/*.xlsx" "$lubanDir/**/*.xlsx"
}

if (-not $files) {
    Write-Host "No conflicted xlsx files found."
    Show-Usage
    exit 0
}

foreach ($path in $files) {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "Merging conflicted file: $path"
    Write-Host "========================================"
    $code = Invoke-ExcelMerge -Path $path
    if ($code -eq 0) {
        git add -- "$path"
        Write-Host "Merge completed and marked resolved: $path"
    } else {
        Write-Warning "Merge failed or was cancelled: $path"
    }
}
