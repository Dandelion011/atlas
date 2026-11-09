# ColorWitch for SAI2 - build
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$plugin = $PSScriptRoot                          # plugin/
$repo = Split-Path -Parent $plugin               # repo root
$hostDir = Join-Path $plugin 'host'
$dist = Join-Path $plugin 'dist'

Write-Host "Building host ($Configuration)..."
$publishArgs = @('publish', (Join-Path $hostDir 'ColorWitch.Sai2.csproj'), '-c', $Configuration, '-o', $dist)
& dotnet @publishArgs
$dotnetExit = $LASTEXITCODE
Write-Host "dotnet publish exit=$dotnetExit"
if ($dotnetExit -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Copying web assets and docs...'
Copy-Item (Join-Path $repo 'index.html') $dist -Force
Copy-Item (Join-Path $plugin 'embed.css') $dist -Force
Copy-Item (Join-Path $plugin 'embed.js') $dist -Force
Copy-Item (Join-Path $plugin 'README.md') (Join-Path $dist 'README.md') -Force

$docSrc = Join-Path $plugin 'docs'
$docDst = Join-Path $dist 'docs'
New-Item -ItemType Directory -Force -Path $docDst | Out-Null
Copy-Item (Join-Path $docSrc '*') $docDst -Recurse -Force -ErrorAction SilentlyContinue

# keep only the x64 WebView2Loader and strip docs/pdb to keep the plugin small
$loader = Join-Path $dist 'WebView2Loader.dll'
Get-ChildItem (Join-Path $dist 'runtimes\win-x64\native\WebView2Loader.dll') -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName $loader -Force }
Remove-Item (Join-Path $dist 'runtimes') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dist '*.xml') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dist '*.pdb') -Force -ErrorAction SilentlyContinue

Write-Host "Done: $dist"
