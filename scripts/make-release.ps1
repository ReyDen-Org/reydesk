# Builds the publishable ReyDesk release: Windows installer + signed Android APK, SHA256, and latest.json.
# Usage (repo root): powershell -ExecutionPolicy Bypass -File scripts/make-release.ps1 [-SkipWindows] [-SkipAndroid] [-Notes en] [-NotesFa fa]
# Output (git-ignored): release/ReyDesk-Setup-<v>.exe, release/ReyDesk-<v>.apk, release/latest.json
# Android signing: Android/keystore.properties (or REYDESK_KEYSTORE* env vars), see Android/app/build.gradle.kts.
param(
  [switch]$SkipWindows, [switch]$SkipAndroid,
  [string]$Notes = 'First public release of ReyDesk.',
  [string]$NotesFa = ([string]::Join('', [char[]](0x0646,0x0633,0x062E,0x0647,0x200C,0x06CC,0x20,0x0646,0x062E,0x0633,0x062A,0x0020,0x0639,0x0645,0x0648,0x0645,0x06CC,0x20,0x0631,0x06CC,0x062F,0x0633,0x06A9)))
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Force $out | Out-Null
$setup = Join-Path $out "ReyDesk-Setup-$version.exe"
$apk = Join-Path $out "ReyDesk-$version.apk"

if (-not $SkipWindows) {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-installer.ps1')
  if ($LASTEXITCODE -ne 0) { throw 'build-installer failed' }
  Copy-Item (Join-Path $root "Installer\out\ReyDesk-Setup-$version.exe") $setup -Force
}
if (-not $SkipAndroid) {
  $props = Join-Path $root 'Android\keystore.properties'
  if (-not (Test-Path $props) -and -not $env:REYDESK_KEYSTORE) { throw 'No release keystore: create Android/keystore.properties or set REYDESK_KEYSTORE*' }
  if (-not $env:JAVA_HOME) { throw 'Set JAVA_HOME to a JDK 17+ (for example the Android Studio jbr folder).' }
  Push-Location (Join-Path $root 'Android')
  try {
    & .\gradlew.bat --offline :app:assembleRelease
    if ($LASTEXITCODE -ne 0) { throw 'gradle assembleRelease failed' }
  } finally { Pop-Location }
  $built = Join-Path $root 'Android\app\build\outputs\apk\release\app-release.apk'
  if (-not (Test-Path $built)) { throw "Signed APK not found ($built). Unsigned output means the keystore was not picked up." }
  Copy-Item $built $apk -Force
}
foreach ($f in @($setup, $apk)) { if (-not (Test-Path $f)) { throw "Missing $f (run without -Skip flags)" } }

$manifest = [ordered]@{
  version  = $version
  released = (Get-Date -Format 'yyyy-MM-dd')
  notes    = [ordered]@{ en = $Notes; fa = $NotesFa }
  windows  = [ordered]@{ url = "https://reyden.ir/dl/ReyDesk-Setup-$version.exe"; sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower() }
  android  = [ordered]@{ url = "https://reyden.ir/dl/ReyDesk-$version.apk"; sha256 = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLower() }
  page     = 'https://reyden.ir/reydesk'
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $out 'latest.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Release $version ready in $out"
Write-Host $json
