<#
.SYNOPSIS
  One command to bring up the local development environment: dev PostgreSQL + migrations.
.DESCRIPTION
  1. Creates .env with a random local-only database password if it does not exist.
  2. Starts the dev database container and waits until it is healthy.
  3. Applies the embedded SQL migrations with the DbMigrator tool.
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

$settings = @{}
foreach ($line in Get-Content $envFile) {
    # -cmatch (case-sensitive): case-insensitive -match follows the current culture, and under tr-TR
    # the letter I does not fall inside [A-Z], so keys like PI_DEV_DB_PASSWORD would silently not match.
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
if (-not $settings['PI_DEV_DB_PASSWORD']) { throw '.env does not define PI_DEV_DB_PASSWORD.' }
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }

docker compose up -d --wait dev-db
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }

$env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$($settings['PI_DEV_DB_PASSWORD'])"
try {
    dotnet run --project src/Tools/DbMigrator -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Migrations failed.' }
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}

Write-Host "Development environment is up (PostgreSQL on 127.0.0.1:$port, migrations applied)."
