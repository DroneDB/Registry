#!/usr/bin/env pwsh
# run-stac-compliance.ps1
# Builds Registry, starts the server, runs the STAC compliance validator, and reports results.
# Run from the Registry repository root.
#
# Usage:
#   .\run-stac-compliance.ps1                         # build + validate
#   .\run-stac-compliance.ps1 -SkipBuild              # skip dotnet build
#   .\run-stac-compliance.ps1 -Collection "admin/test" -Port 7001

param(
    [switch]$SkipBuild,
    [string]$Collection = "admin/test",
    [int]$Port = 7000,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = Split-Path $PSScriptRoot -Parent
$WebProject  = Join-Path $RepoRoot "Registry.Web\Registry.Web.csproj"
$Tfm         = "net10.0"
$Dll         = Join-Path $RepoRoot "Registry.Web\bin\$Configuration\$Tfm\Registry.Web.dll"
$DataDir     = Join-Path $RepoRoot "Registry.Web\registry-data"
$RootUrl     = "http://localhost:$Port/stac"

function Stop-RegistryServer {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600
}

# ── 1. Build ─────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "`n==> Building Registry ($Configuration)..." -ForegroundColor Cyan
    dotnet build $WebProject -c $Configuration --nologo 2>&1 |
        Select-String -Pattern "error CS|Build succeeded|Build FAILED" |
        Select-Object -ExpandProperty Line
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }
    Write-Host "    Build succeeded." -ForegroundColor Green
}

# ── 2. Start server ───────────────────────────────────────────────────────────
Write-Host "`n==> Stopping any existing server on port $Port..." -ForegroundColor Cyan
Stop-RegistryServer

Write-Host "==> Starting Registry server on port $Port..." -ForegroundColor Cyan
$env:ASPNETCORE_ENVIRONMENT = "Development"
$serverJob = Start-Job -ScriptBlock {
    param($dll, $data, $port)
    & dotnet $dll $data --address "localhost:$port" 2>&1
} -ArgumentList $Dll, $DataDir, $Port

# Wait for port to open (up to 30 s)
$up = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        $up = $true; break
    }
}
if (-not $up) {
    Write-Error "Server did not start within 30 seconds."
    Stop-Job $serverJob; Remove-Job $serverJob
    exit 1
}
Write-Host "    Server is up." -ForegroundColor Green

# ── 3. Run validator ──────────────────────────────────────────────────────────
Write-Host "`n==> Running stac-api-validator..." -ForegroundColor Cyan
$env:PYTHONIOENCODING = "utf-8"
$env:PYTHONUTF8       = "1"

$output = stac-api-validator `
    --root-url $RootUrl `
    --conformance core `
    --conformance collections `
    --conformance features `
    --conformance item-search `
    --collection $Collection 2>$null

$output | Write-Host

# ── 4. Stop server ────────────────────────────────────────────────────────────
Write-Host "`n==> Stopping server..." -ForegroundColor Cyan
Stop-Job $serverJob; Remove-Job $serverJob
Stop-RegistryServer

# ── 5. Report ─────────────────────────────────────────────────────────────────
$errorsLine = ($output | Where-Object { $_ -match "^Errors:" }) -join ""
$errorItems = @()
$inErrors = $false
foreach ($line in $output) {
    if ($line -match "^Errors:") { $inErrors = $true; continue }
    if ($inErrors -and $line -match "^- ") { $errorItems += $line }
    elseif ($inErrors -and $line -notmatch "^- ") { $inErrors = $false }
}

Write-Host ""
if ($errorsLine -match "none" -or $errorItems.Count -eq 0) {
    Write-Host "RESULT: PASS - Errors: none" -ForegroundColor Green
    exit 0
} else {
    Write-Host "RESULT: FAIL" -ForegroundColor Red
    $errorItems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
