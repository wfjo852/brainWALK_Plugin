@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET 9 SDK is not installed or dotnet is not in PATH.
    exit /b 1
)

echo Building brainWALK-setting...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo Build completed.
echo Output: %~dp0bin\Release\net9.0-windows\win-x86\publish-pc\brainWALK-setting.exe
exit /b 0
