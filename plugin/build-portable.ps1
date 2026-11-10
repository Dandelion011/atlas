# Atlas for SAI2 - portable build
#
# Produces a self-contained folder (no .NET install needed) whose web assets are
# embedded in the exe. Zip it for GitHub releases.
#
#   powershell -ExecutionPolicy Bypass -File build-portable.ps1 [-Version 1.0.0]

param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$plugin = $PSScriptRoot
$repo = Split-Path -Parent $plugin
$proj = Join-Path $plugin 'host\Atlas.Sai2.csproj'
$portableRoot = Join-Path $plugin 'portable'
$stage = Join-Path $portableRoot "Atlas-for-SAI2-v$Version-win-x64"

Write-Host "Portable build v$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$args = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $stage,
    '-p:PublishTrimmed=false',
    "-p:Version=$Version",
    "-p:FileVersion=$Version.0"
)
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Get-ChildItem $stage -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $stage -Filter '*.xml' | Remove-Item -Force

Copy-Item (Join-Path $plugin 'portable\README-portable.txt') (Join-Path $stage '使用说明.txt') -Force
$license = Join-Path $repo 'LICENSE'
if (Test-Path $license) { Copy-Item $license (Join-Path $stage 'LICENSE.txt') -Force }

$zip = Join-Path $portableRoot "Atlas-for-SAI2-v$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Stage: $stage"
Write-Host "Zip:   $zip"
Get-ChildItem $stage | Select-Object Name, Length | Format-Table -AutoSize
