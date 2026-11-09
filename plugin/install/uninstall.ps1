# ColorWitch for SAI2 - uninstall
# Removes the plugin folder and Start-menu shortcut. SAI2 itself is untouched.

$ErrorActionPreference = 'Stop'
$target = if ($PSScriptRoot -match 'ColorWitch$') { $PSScriptRoot } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'ColorWitch' }

$lnkPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ColorWitch for SAI2.lnk'
if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force }

Get-Process ColorWitch.Sai2 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
    Write-Host "Removed $target"
}
Write-Host 'Uninstall done.'
