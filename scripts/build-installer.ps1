# Builds the ReyDesk Windows installer: dotnet publish (self-contained win-x64, untrimmed) then Inno Setup.
# Usage (repo root): powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1 [-SkipPublish]
# Output: Installer/out/ReyDesk-Setup-<VERSION>.exe
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$pub = Join-Path $root 'Installer\build\publish'
if (-not $SkipPublish) {
  if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
  # PublishTrimmed=false: the trimmed Release build crashes at window creation (0xc000027b in Microsoft.UI.Xaml).
  dotnet publish (Join-Path $root 'Windows\ReyDesk.Windows\ReyDesk.Windows.csproj') -c Release -p:Platform=x64 -p:PublishProfile=win-x64 -p:PublishTrimmed=false -o $pub
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
node (Join-Path $PSScriptRoot 'generate-installer-art.mjs')
if ($LASTEXITCODE -ne 0) { throw 'installer art failed' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install: winget install JRSoftware.InnoSetup' }
& $iscc "/DAppVersion=$version" "/DSourceDir=$pub" (Join-Path $root 'Installer\ReyDesk.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Built Installer\out\ReyDesk-Setup-$version.exe"
