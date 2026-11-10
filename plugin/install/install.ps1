# Atlas for SAI2 - install
#
# Installs the plugin into a SAI2 program folder. It only creates a Atlas\
# subfolder there and a Start-menu shortcut; SAI2 itself is not modified.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Sai2Path 'F:\[PivInk]SAI Ver.2'

param(
    [Parameter(Mandatory = $true)]
    [string]$Sai2Path,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'

$pluginRoot = Split-Path -Parent $PSScriptRoot      # plugin/
$dist = Join-Path $pluginRoot 'dist'
$repo = Split-Path -Parent $pluginRoot              # repo root

if (-not (Test-Path (Join-Path $Sai2Path 'sai2.exe'))) {
    throw "sai2.exe not found under '$Sai2Path'"
}
if (-not (Test-Path (Join-Path $dist 'Atlas.Sai2.exe'))) {
    throw "missing build output in $dist - run plugin\build.ps1 first"
}

$target = Join-Path $Sai2Path 'Atlas'
Write-Host "Installing to $target"

# stop a running panel instance so files can be replaced
Get-Process Atlas.Sai2 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $dist '*') $target -Recurse -Force

foreach ($asset in @('index.html', 'embed.css', 'embed.js')) {
    $src = if ($asset -eq 'index.html') { Join-Path $repo $asset } else { Join-Path $pluginRoot $asset }
    if (Test-Path $src) { Copy-Item $src $target -Force }
}

Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $target -Force

if (-not $NoShortcut) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $lnkPath = Join-Path $startMenu 'Atlas for SAI2.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = Join-Path $target 'Atlas.Sai2.exe'
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = (Join-Path $target 'Atlas.Sai2.exe') + ',0'
    $lnk.Description = 'Atlas 3D color panel (SAI2 plugin)'
    $lnk.Save()
    Write-Host "Start menu shortcut: $lnkPath"
}

Write-Host ''
Write-Host 'Done. Usage:'
Write-Host '  1) open SAI2'
Write-Host '  2) run Atlas for SAI2 (Start menu, or Atlas.Sai2.exe in the install dir)'
Write-Host '  3) the panel docks to the right side of SAI2; Ctrl+Alt+C toggles it'
