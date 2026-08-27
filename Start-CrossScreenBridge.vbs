Option Explicit

Dim shell, fileSystem, scriptDirectory, powerShellScript, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
powerShellScript = fileSystem.BuildPath(scriptDirectory, "CrossScreenBridge.ps1")
command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & powerShellScript & """"

' Window style 0 creates the process hidden; False returns immediately.
shell.Run command, 0, False

