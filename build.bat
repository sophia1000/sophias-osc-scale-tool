@echo off
setlocal
cd /d "%~dp0"
dotnet restore "VrcHeightOsc.sln"
if errorlevel 1 goto :error
dotnet build "VrcHeightOsc.sln" --configuration Release --no-restore
if errorlevel 1 goto :error
dotnet test "VrcHeightOsc.sln" --configuration Release --no-build
if errorlevel 1 goto :error
echo.
echo Build and tests succeeded.
exit /b 0
:error
echo.
echo Build or tests failed.
pause
exit /b 1
