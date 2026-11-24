@echo off
chcp 65001 > nul
cd /d "%~dp0agent"
echo 데모 모드(카메라 없이 가짜 스켈레톤)로 실행합니다...
dotnet run -c Release -- --demo %*
pause
