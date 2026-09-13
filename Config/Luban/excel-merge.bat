@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

rem ProjectSplatoon Excel three-way merge tool.
rem Usage:
rem   excel-merge.bat
rem   excel-merge.bat TbHero.xlsx
rem   excel-merge.bat --all-xlsx

set "SCRIPT_DIR=%~dp0"
for /f "delims=" %%R in ('git -C "%SCRIPT_DIR%." rev-parse --show-toplevel 2^>nul') do set "REPO_ROOT=%%R"
if "%REPO_ROOT%"=="" (
    echo Error: this script must be inside a Git repository.
    pause
    exit /b 1
)
cd /d "%REPO_ROOT%"

set "LUBAN_DIR=Config/Luban/source"
set "TOOL_DIR=%REPO_ROOT%\Tools\ExcelMerge"
set "PYTHONPATH=%TOOL_DIR%\src"
set "SCAN_ALL=0"
set "TARGET_FILE="

if /i "%~1"=="--help" goto :usage
if /i "%~1"=="-h" goto :usage
if /i "%~1"=="--all-xlsx" (
    set "SCAN_ALL=1"
) else if not "%~1"=="" (
    set "TARGET_FILE=%~1"
)

if not "%TARGET_FILE%"=="" (
    set "TARGET_FILE=%TARGET_FILE:\=/%"
    echo "%TARGET_FILE%" | findstr /c:"/" >nul
    if errorlevel 1 (
        if exist "%REPO_ROOT%\%LUBAN_DIR%\%TARGET_FILE%" set "TARGET_FILE=%LUBAN_DIR%/%TARGET_FILE%"
    )
)

set "PYTHON="
where py >nul 2>&1
if not errorlevel 1 (
    set "PYTHON=py -3"
    goto :python_found
)
where python >nul 2>&1
if not errorlevel 1 (
    set "PYTHON=python"
    goto :python_found
)
where python3 >nul 2>&1
if not errorlevel 1 (
    set "PYTHON=python3"
    goto :python_found
)
if exist "%USERPROFILE%\miniconda3\python.exe" (
    set "PYTHON=%USERPROFILE%\miniconda3\python.exe"
    goto :python_found
)
if exist "%USERPROFILE%\anaconda3\python.exe" (
    set "PYTHON=%USERPROFILE%\anaconda3\python.exe"
    goto :python_found
)
if exist "%LOCALAPPDATA%\Programs\Python\Python313\python.exe" (
    set "PYTHON=%LOCALAPPDATA%\Programs\Python\Python313\python.exe"
    goto :python_found
)
if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" (
    set "PYTHON=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
    goto :python_found
)
if exist "%LOCALAPPDATA%\Programs\Python\Python311\python.exe" (
    set "PYTHON=%LOCALAPPDATA%\Programs\Python\Python311\python.exe"
    goto :python_found
)
if exist "%LOCALAPPDATA%\Programs\Python\Python310\python.exe" (
    set "PYTHON=%LOCALAPPDATA%\Programs\Python\Python310\python.exe"
    goto :python_found
)

echo Error: Python 3.10 or newer was not found.
echo Download: https://www.python.org/downloads/
pause
exit /b 1

:python_found
%PYTHON% -c "import openpyxl, flask, click; import excelcompare.git.export_stage" >nul 2>&1
if errorlevel 1 (
    echo First run: installing Python dependencies...
    %PYTHON% -m pip install openpyxl flask click --quiet
    if errorlevel 1 (
        echo Error: dependency installation failed. Please run:
        echo   %PYTHON% -m pip install openpyxl flask click
        pause
        exit /b 1
    )
    %PYTHON% -c "import excelcompare.git.export_stage" >nul 2>&1
    if errorlevel 1 (
        echo Error: Excel merge helper module was not found under %TOOL_DIR%\src.
        pause
        exit /b 1
    )
)

if not "%TARGET_FILE%"=="" (
    call :merge_file "%TARGET_FILE%"
    set "MERGE_EXIT=!ERRORLEVEL!"
    exit /b !MERGE_EXIT!
)

set "FOUND=0"
if "%SCAN_ALL%"=="1" (
    echo Scanning conflicted xlsx files in the whole repository...
    for /f "delims=" %%F in ('git diff --name-only --diff-filter^=U -- "*.xlsx" 2^>nul') do (
        call :merge_scanned_file "%%F"
    )
) else (
    echo Scanning conflicted Luban xlsx files under %LUBAN_DIR%...
    for /f "delims=" %%F in ('git diff --name-only --diff-filter^=U -- "%LUBAN_DIR%/*.xlsx" "%LUBAN_DIR%/**/*.xlsx" 2^>nul') do (
        call :merge_scanned_file "%%F"
    )
)

if "!FOUND!"=="0" (
    echo No conflicted xlsx files found.
    echo.
    goto :usage
)

exit /b 0

:merge_scanned_file
set "FOUND=1"
echo.
echo ========================================
echo Merging conflicted file: %~1
echo ========================================
call :merge_file "%~1"
if errorlevel 1 (
    echo Merge failed or was cancelled: %~1
    exit /b 0
) else (
    git add "%~1"
    echo Merge completed and marked resolved: %~1
)
exit /b 0

:merge_file
set "FILE=%~1"
set "GIT_FILE=%FILE:\=/%"
set "TMPDIR=%TEMP%\excelmerge_%RANDOM%_%RANDOM%"
mkdir "%TMPDIR%" 2>nul

%PYTHON% -m excelcompare.git.export_stage ":1:%GIT_FILE%" "%TMPDIR%\base.xlsx" >nul 2>nul
set "EXPORT_CODE=%ERRORLEVEL%"
if not "%EXPORT_CODE%"=="0" (
    if not "%EXPORT_CODE%"=="1" (
        echo Error: failed to export base version for %FILE%.
        echo If this file uses Git LFS, run git lfs pull and try again.
        rmdir /s /q "%TMPDIR%" 2>nul
        exit /b 1
    )
    echo Warning: base version was not found; using an empty workbook placeholder.
    %PYTHON% -m excelcompare.git.export_stage --empty "%TMPDIR%\base.xlsx"
    if errorlevel 1 (
        echo Error: failed to create an empty workbook placeholder.
        rmdir /s /q "%TMPDIR%" 2>nul
        exit /b 1
    )
)
%PYTHON% -m excelcompare.git.export_stage ":2:%GIT_FILE%" "%TMPDIR%\ours.xlsx"
set "EXPORT_CODE=%ERRORLEVEL%"
if not "%EXPORT_CODE%"=="0" (
    echo Error: ours version was not found for %FILE%.
    if not "%EXPORT_CODE%"=="1" echo If this file uses Git LFS, run git lfs pull and try again.
    rmdir /s /q "%TMPDIR%" 2>nul
    exit /b 1
)
%PYTHON% -m excelcompare.git.export_stage ":3:%GIT_FILE%" "%TMPDIR%\theirs.xlsx"
set "EXPORT_CODE=%ERRORLEVEL%"
if not "%EXPORT_CODE%"=="0" (
    echo Error: theirs version was not found for %FILE%.
    if not "%EXPORT_CODE%"=="1" echo If this file uses Git LFS, run git lfs pull and try again.
    rmdir /s /q "%TMPDIR%" 2>nul
    exit /b 1
)

%PYTHON% -m excelcompare merge "%TMPDIR%\base.xlsx" "%TMPDIR%\ours.xlsx" "%TMPDIR%\theirs.xlsx" -o "%FILE%" --visual
set "EXITCODE=%ERRORLEVEL%"
rmdir /s /q "%TMPDIR%" 2>nul
exit /b %EXITCODE%

:usage
echo Usage:
echo   excel-merge.bat
echo   excel-merge.bat TbHero.xlsx
echo   excel-merge.bat Config\Luban\source\TbHero.xlsx
echo   excel-merge.bat --all-xlsx
exit /b 0
