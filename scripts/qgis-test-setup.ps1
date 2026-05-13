<#
.SYNOPSIS
    Seeds a Registry instance with the OGC test dataset and prints the resulting
    WMS / WFS / WMTS / WCS / OGC API / MVT endpoints so they can be loaded into
    QGIS for smoke-testing the OGC service stack.

.PARAMETER BaseUrl
    Root URL of the Registry instance. Default: http://localhost:7000

.PARAMETER Username
    Admin username. Default: admin

.PARAMETER Password
    Admin password. Default: _Rainbow1

.PARAMETER OrgSlug
    Slug of the organization to (re)create. Default: qgis-test

.PARAMETER DsSlug
    Slug of the dataset to (re)create. Default: ogc-fixture

.PARAMETER SeedFolder
    Local folder containing the seed data. Default: ..\..\test_data\ogc-seed

.EXAMPLE
    .\qgis-test-setup.ps1
    .\qgis-test-setup.ps1 -BaseUrl https://hub.dronedb.app -OrgSlug demo
#>

[CmdletBinding()]
param(
    [string]$BaseUrl    = 'http://localhost:7000',
    [string]$Username   = 'admin',
    [string]$Password   = '_Rainbow1',
    [string]$OrgSlug    = 'qgis-test',
    [string]$DsSlug     = 'ogc-fixture',
    [string]$SeedFolder = (Join-Path $PSScriptRoot '..\..\test_data\ogc-seed')
)

$ErrorActionPreference = 'Stop'

function Invoke-RegistryApi {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Path,
        $Body = $null,
        [string]$Token,
        [string]$ContentType = 'application/json'
    )
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $url = "$BaseUrl$Path"
    $params = @{ Method = $Method; Uri = $url; Headers = $headers; SkipHttpErrorCheck = $true }
    if ($Body -ne $null) {
        if ($ContentType -eq 'application/json') {
            $params['Body'] = ($Body | ConvertTo-Json -Depth 12)
        } else {
            $params['Body'] = $Body
        }
        $params['ContentType'] = $ContentType
    }
    return Invoke-RestMethod @params
}

Write-Host "==> Authenticating against $BaseUrl as $Username ..." -ForegroundColor Cyan
$auth = Invoke-RegistryApi -Method POST -Path '/users/authenticate' `
    -Body @{ username = $Username; password = $Password }
if (-not $auth.token) { throw "Authentication failed: $($auth | ConvertTo-Json -Depth 6)" }
$token = $auth.token

Write-Host "==> Ensuring organization '$OrgSlug' ..." -ForegroundColor Cyan
try {
    Invoke-RegistryApi -Method POST -Path '/orgs' -Token $token `
        -Body @{ slug = $OrgSlug; name = 'QGIS Test Org'; isPublic = $true } | Out-Null
} catch { Write-Host "    (org exists)" -ForegroundColor DarkGray }

Write-Host "==> Recreating dataset '$DsSlug' ..." -ForegroundColor Cyan
try {
    Invoke-RegistryApi -Method DELETE -Path "/orgs/$OrgSlug/ds/$DsSlug" -Token $token | Out-Null
} catch { }
Invoke-RegistryApi -Method POST -Path "/orgs/$OrgSlug/ds" -Token $token `
    -Body @{ slug = $DsSlug; name = 'OGC Test Fixture'; isPublic = $true } | Out-Null

if (-not (Test-Path $SeedFolder)) {
    Write-Warning "Seed folder '$SeedFolder' not found. Skipping upload step."
} else {
    Write-Host "==> Uploading seed files from $SeedFolder ..." -ForegroundColor Cyan
    Get-ChildItem -Path $SeedFolder -Recurse -File | ForEach-Object {
        $file = $_
        $rel  = $file.FullName.Substring($SeedFolder.Length).TrimStart('\','/').Replace('\','/')
        Write-Host "    + $rel"
        $form = @{
            file = Get-Item $file.FullName
            path = $rel
        }
        try {
            Invoke-WebRequest -Method POST -Uri "$BaseUrl/orgs/$OrgSlug/ds/$DsSlug/obj" `
                -Headers @{ Authorization = "Bearer $token" } -Form $form | Out-Null
        } catch {
            Write-Warning "Upload of $rel failed: $($_.Exception.Message)"
        }
    }

    Write-Host "==> Triggering build (thumbs + COG + MVT) ..." -ForegroundColor Cyan
    try {
        Invoke-RegistryApi -Method POST -Path "/orgs/$OrgSlug/ds/$DsSlug/build" -Token $token `
            -Body @{} | Out-Null
    } catch { Write-Warning "Build request returned: $($_.Exception.Message)" }
}

$root = "$BaseUrl/orgs/$OrgSlug/ds/$DsSlug"

Write-Host ''
Write-Host '====================================================================' -ForegroundColor Green
Write-Host '  OGC endpoints (ready to paste into QGIS connections)' -ForegroundColor Green
Write-Host '====================================================================' -ForegroundColor Green
Write-Host ("  WMS  GetCapabilities  : {0}/wms?service=WMS&request=GetCapabilities&version=1.3.0" -f $root)
Write-Host ("  WFS  GetCapabilities  : {0}/wfs?service=WFS&request=GetCapabilities&version=2.0.0" -f $root)
Write-Host ("  WMTS GetCapabilities  : {0}/wmts?service=WMTS&request=GetCapabilities&version=1.0.0" -f $root)
Write-Host ("  WCS  GetCapabilities  : {0}/wcs?service=WCS&request=GetCapabilities&version=2.0.1" -f $root)
Write-Host ("  OGC API – Features    : {0}/ogcapi/features" -f $root)
Write-Host ("  OGC API – Tiles       : {0}/ogcapi/tiles" -f $root)
Write-Host ("  MVT pyramid template  : {0}/mvt/{{hash}}/{{z}}/{{x}}/{{y}}.pbf" -f $root)
Write-Host '====================================================================' -ForegroundColor Green
Write-Host ''
Write-Host "QGIS project template: scripts/qgis/ogc-test.qgz" -ForegroundColor Cyan
