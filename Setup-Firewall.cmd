@echo off
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -Command "Get-NetFirewallRule -DisplayName 'CrossScreenBridge-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName 'CrossScreenBridge-Discovery' -Direction Inbound -Protocol UDP -LocalPort 45990 -Profile Private -Action Allow; New-NetFirewallRule -DisplayName 'CrossScreenBridge-Transfer' -Direction Inbound -Protocol TCP -LocalPort 45991 -Profile Private -Action Allow; New-NetFirewallRule -DisplayName 'CrossScreenBridge-MouseControl' -Direction Inbound -Protocol UDP -LocalPort 45992 -Profile Private -Action Allow"
echo.
echo CrossScreenBridge firewall rules are ready.
pause
