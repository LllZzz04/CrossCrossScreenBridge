$ErrorActionPreference = 'Stop'

$appDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $appDir 'CrossScreenBridge.cs'
$env:CROSSSCREENBRIDGE_HOME = $appDir

if (-not (Test-Path -LiteralPath $sourcePath)) {
    [System.Windows.Forms.MessageBox]::Show('CrossScreenBridge.cs is missing.', 'Cross Screen Bridge')
    exit 1
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Path $sourcePath -ReferencedAssemblies @(
    'System.Windows.Forms.dll',
    'System.Drawing.dll',
    'System.dll',
    'System.Core.dll'
)

[CrossScreenBridge.Program]::Run()
