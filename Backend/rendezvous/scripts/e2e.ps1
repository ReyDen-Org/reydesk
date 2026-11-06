# End-to-end check against the local rendezvous service (standalone compose, own postgres).
# Usage: powershell -File scripts/e2e.ps1 [-Port 13260] [-KeepUp]
# Exit code 0 = green, nonzero = failure. Never touches the shared devdb postgres.
param(
    [int]$Port = 13260,
    [switch]$KeepUp,
    [int]$HealthTimeoutS = 120
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$compose = @('compose', '-f', 'compose.yaml', '-f', 'compose.standalone.yaml', '-p', 'reydesk-e2e')
$env:APP_PORT = "$Port"
$env:PUBLIC_WS_URL = "ws://127.0.0.1:$Port/v1/ws"
$code = 1
$started = $false
try {
    & docker @compose up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
    $started = $true

    $deadline = (Get-Date).AddSeconds($HealthTimeoutS)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/api/health" -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) {
        & docker @compose logs --tail 50 rendezvous
        throw "service did not become healthy within ${HealthTimeoutS}s"
    }

    & go run ./cmd/e2eclient -url $env:PUBLIC_WS_URL
    $code = $LASTEXITCODE
    if ($code -ne 0) { & docker @compose logs --tail 50 rendezvous }
} catch {
    Write-Error $_
    $code = 1
} finally {
    if ($started -and -not $KeepUp) {
        & docker @compose down -v --remove-orphans
    }
}
if ($code -eq 0) { Write-Host "e2e: PASS" } else { Write-Host "e2e: FAIL ($code)" }
exit $code
