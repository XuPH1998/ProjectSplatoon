@echo off
setlocal

if not "%~1"=="" (
    echo Usage: Config\Luban\gen_luban.bat
    exit /b 2
)

for %%I in ("%~dp0..\..") do set "PROJECT_ROOT=%%~fI"
set "SOURCE_ROOT=%~dp0source"
set "LUBAN=%PROJECT_ROOT%\Tools\Luban\tools\Luban\Luban.dll"
set "CODE_OUTPUT=%PROJECT_ROOT%\Assets\Splatoon\Config\Generated"
set "DATA_OUTPUT=%PROJECT_ROOT%\Assets\GameResource\Bootstrap\Config\Luban"

dotnet "%LUBAN%" -t client -c cs-simple-json -d json --conf "%SOURCE_ROOT%\luban.conf" -x "outputCodeDir=%CODE_OUTPUT%" -x "outputDataDir=%DATA_OUTPUT%"
if errorlevel 1 exit /b %ERRORLEVEL%

echo Luban generation completed.
exit /b 0
