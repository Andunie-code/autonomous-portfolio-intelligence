<#
.SYNOPSIS
  Local environment entry point.
.DESCRIPTION
  M0-M4: started the development PostgreSQL and applied migrations as the superuser over the loopback port.
  M5 (docs/m5-protocol.md §4): the M5 pg_hba refuses the superuser on the network, so environment set-up is the
  user's bootstrap. This script now:
  1. creates .env with a random local-only superuser password if it does not exist (new machine);
  2. without an M5 environment: stops and points to scripts/m5-bootstrap.ps1 (user);
  3. with one: starts the PostgreSQL container if it is not running. Ops migrations are applied by the user
     with scripts/m5-migrate.ps1 (ops_owner); research tools run through scripts/m5-research.ps1 (research_app).
  Works with Windows PowerShell 5.1 and PowerShell 7.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes) -replace '[+/=]', ''
    Set-Content -Path $envFile -Encoding ascii -Value @("PI_DEV_DB_PASSWORD=$password", 'PI_DEV_DB_PORT=55432')
    Write-Host '.env created with a random local database password.'
}

. "$PSScriptRoot\lib\m5-common.ps1"
$settings = Read-DotEnv
if ($settings['PI_OPS_DB'] -ne 'ops') {
    Write-Host 'No M5 environment yet. The user runs: powershell -ExecutionPolicy Bypass -File scripts/m5-bootstrap.ps1'
    exit 2
}

docker compose up -d --wait postgres
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }
Write-Host 'PostgreSQL is up (127.0.0.1, ops). Migrations: scripts/m5-migrate.ps1 (user). Research tools: scripts/m5-research.ps1.'
