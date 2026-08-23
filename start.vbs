Option Explicit

Dim shell, fileSystem, projectFolder, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

projectFolder = fileSystem.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = projectFolder
command = "dotnet run --project ""src\VrcHeightOsc.App\VrcHeightOsc.App.csproj"" --configuration Release"

' Window style 0 keeps the dotnet host hidden; the WinForms app still opens normally.
shell.Run command, 0, False
