@echo off
cd /d "%~dp0"
start "" wscript.exe "%~dp0Start-CrossScreenBridge.vbs"
exit /b
