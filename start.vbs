Option Explicit

Dim shell, fileSystem, projectFolder, executablePath, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

projectFolder = fileSystem.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = projectFolder
executablePath = projectFolder & "\src\VrcHeightOsc.App\bin\Release\net8.0-windows\VrcHeightOsc.App.exe"

If fileSystem.FileExists(executablePath) Then
    ' Launch the exact same WinExe used by a pinned taskbar shortcut.
    shell.Run """" & executablePath & """", 1, False
Else
    ' First-run fallback: build and start without showing the dotnet host window.
    command = "dotnet run --project ""src\VrcHeightOsc.App\VrcHeightOsc.App.csproj"" --configuration Release"
    shell.Run command, 0, False
End If
