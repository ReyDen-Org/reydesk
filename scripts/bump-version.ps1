<#
Updates ReyDesk/VERSION (the single source of truth for Windows and Android) and prints the git tag to create.
Does not commit and does not tag.
Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bump-version.ps1 -Version 0.3.0
#>
param([Parameter(Mandatory = $true)][string]$Version)
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Write-Error "Version must look like 1.2.3"; exit 1 }
$file = Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION'
$old = (Get-Content -LiteralPath $file -Raw).Trim()
[System.IO.File]::WriteAllText($file, "$Version`n")
Write-Host "VERSION: $old -> $Version"
Write-Host "Next (manual): review, commit, then create the tag with:  git tag v$Version"
