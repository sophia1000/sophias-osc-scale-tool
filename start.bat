@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "src\VrcHeightOsc.App\VrcHeightOsc.App.csproj" --configuration Release
if errorlevel 1 pause
