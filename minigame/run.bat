@echo off
chcp 65001 > nul
cd /d "%~dp0agent"
echo Femto Bolt 카메라 모드로 실행합니다...
dotnet run -c Release -- %*
pause
