# Sophia's OSC Scale Tool

Windows desktop controller for VRChat avatar eye height, rewritten in C# on .NET 8.

## Run

Double-click `VRC Height OSC.lnk` for the no-console icon launcher. It starts the app without showing a Command Prompt window. You can also run `start.vbs` directly, or use:

```powershell
dotnet run --project src/VrcHeightOsc.App/VrcHeightOsc.App.csproj --configuration Release
```

The first run restores NuGet packages. Keep VRChat OSC enabled. The status bar shows the local OSC/OSCQuery ports and the currently discovered VRChat endpoint. A local `vrc_height_osc_config.json` is created automatically; `vrc_height_osc_config.example.json` is the safe public example.

## Reconnection behavior

The app advertises its receiving endpoints through OSCQuery and continuously discovers VRChat through mDNS. It polls VRChat's `HOST_INFO`, tolerates two missed heartbeats, and automatically discovers a replacement OSCQuery/OSC endpoint after VRChat or the connection restarts. Its own receiving ports stay stable during reconnection.

The implementation deliberately separates the two protocols:

- `BuildSoft.OscCore` sends and receives OSC UDP packets.
- `VRChat.OSCQuery` serves the OSCQuery tree and performs mDNS discovery/advertising.

Both package versions are pinned in the project file.

## Configuration migration

The C# app reads and writes the existing `vrc_height_osc_config.json` version 3 format. Existing rules and UI settings migrate automatically. The legacy `height_rules.json` file remains as reference but is not loaded, matching the Python behavior.

The previous implementation and its separate playspace experiment are preserved in `old python version`.

## Build and test

Run `build.bat`, or:

```powershell
dotnet restore VrcHeightOsc.sln
dotnet build VrcHeightOsc.sln --configuration Release --no-restore
dotnet test VrcHeightOsc.sln --configuration Release --no-build
```
