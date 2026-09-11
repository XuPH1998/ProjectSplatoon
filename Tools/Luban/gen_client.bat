@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen_client.ps1" %*
exit /b %ERRORLEVEL%
