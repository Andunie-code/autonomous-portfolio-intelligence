<#
.SYNOPSIS
  M5 Gate C step 1 (docs/m5-protocol.md §4, §7.4; user decisions U-M5-2, U-M5-3). Run only by the user:
    powershell -ExecutionPolicy Bypass -File scripts/m5-bootstrap.ps1
.DESCRIPTION
  1. Encrypted backup of portfolio_dev into PI_BACKUP_DIR (absolute, outside the repository and OneDrive).
  2. Recreates the PostgreSQL container with the M5 compose file (pg_hba, networks, limits); the data volume is kept.
  3. Renames portfolio_dev to ops; generates the login secrets (never printed) and sets the role passwords.
  4. Transfers the ops objects to ops_owner, creates the holdout database (owner eval_owner), applies the ops
     migrations as ops_owner through the loopback port, and checks that path (fallback: measured source address).
  holdout_synth is kept (A-5); CONNECT on it stays revoked and pg_hba refuses it.
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$settings = Read-DotEnv
if (-not $settings['PI_DEV_DB_PASSWORD']) { throw '.env not found or incomplete; this machine has no M0-M4 environment.' }
$backupDir = $settings['PI_BACKUP_DIR']
if (-not $backupDir) {
    $backupDir = Read-Host 'Backup directory (absolute, outside the repository and OneDrive, e.g. D:\pi-backups)'
}
dotnet run --project src/Tools/BackupCli -c Release -- check-target $backupDir $script:Root
if ($LASTEXITCODE -ne 0) { throw 'Backup directory rejected.' }
if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
Set-DotEnvValue 'PI_BACKUP_DIR' $backupDir

Confirm-Interactive 'Back up portfolio_dev, recreate the PostgreSQL container, rename portfolio_dev to ops and create the holdout database?'

# 1. Backup of portfolio_dev from the running M0-M4 container.
$old = docker ps --filter 'label=com.docker.compose.project=portfolio-intelligence' --filter 'label=com.docker.compose.service=dev-db' --format '{{.Names}}'
$key = Join-Path $script:OpsSecrets 'backup_key'
if (-not (Test-Path $key)) {
    if (-not (Test-Path $script:OpsSecrets)) { New-Item -ItemType Directory -Path $script:OpsSecrets | Out-Null }
    dotnet run --project src/Tools/BackupCli -c Release -- keygen $key
    if ($LASTEXITCODE -ne 0) { throw 'Key generation failed.' }
    Write-Host 'A backup key was created in secrets/backup_key. Keep a copy apart from the backups (without it they cannot be restored).'
}
if ($old) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $plain = Join-Path $env:TEMP "pi-portfolio_dev-$stamp.dump"
    docker exec -e "PGPASSWORD=$(Get-SuperuserPassword)" $old pg_dump -U pi_dev -Fc -f /tmp/portfolio_dev.dump portfolio_dev
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }
    docker cp "${old}:/tmp/portfolio_dev.dump" $plain | Out-Null
    docker exec $old rm -f /tmp/portfolio_dev.dump
    $sealedFile = Join-Path $backupDir "portfolio_dev-before-m5-$stamp.dump.enc"
    dotnet run --project src/Tools/BackupCli -c Release -- encrypt $plain $sealedFile $key
    $encrypted = $LASTEXITCODE
    Remove-Item $plain -Force
    if ($encrypted -ne 0) { throw 'Encryption failed.' }
    Write-Host "Encrypted backup: $sealedFile"
} else {
    Write-Host 'No running M0-M4 dev-db container; continuing with the existing volume.'
}

# 2. Secrets (compose needs the files), then the M5 PostgreSQL container (same volume).
foreach ($role in $script:OpsRoles) { Ensure-Secret $script:OpsSecrets $role | Out-Null }
Ensure-Secret $script:OpsSecrets 'ops_api_token' | Out-Null
foreach ($role in $script:HoldoutRoles) { Ensure-Secret $script:HoldoutSecrets $role | Out-Null }
foreach ($token in @('submit_token', 'read_status_token')) { Ensure-Secret $script:HoldoutSecrets $token | Out-Null }
docker compose down --remove-orphans
docker compose up -d --wait postgres
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not start.' }

# 3. Rename and logins.

$exists = Invoke-Psql 'postgres' "SELECT string_agg(datname, ',') FROM pg_database WHERE datname IN ('portfolio_dev', 'ops', 'holdout')"
if ($exists -match 'portfolio_dev' -and $exists -notmatch '\bops\b') {
    Invoke-Psql 'postgres' 'ALTER DATABASE portfolio_dev RENAME TO ops;' -Quiet
    Write-Host 'portfolio_dev renamed to ops.'
}

$roleSql = @()
foreach ($role in $script:OpsRoles) { $roleSql += "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role; END IF; END `$`$;"; $roleSql += "ALTER ROLE $role WITH LOGIN PASSWORD '$(Get-Secret $script:OpsSecrets $role)';" }
foreach ($role in $script:HoldoutRoles) { $roleSql += "DO `$`$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') THEN CREATE ROLE $role; END IF; END `$`$;"; $roleSql += "ALTER ROLE $role WITH LOGIN PASSWORD '$(Get-Secret $script:HoldoutSecrets $role)';" }
Invoke-Psql 'postgres' ($roleSql -join "`n") -Quiet

# 4. Ownership of the ops objects; the holdout database.
Invoke-Psql 'ops' (Get-Content (Join-Path $script:Root 'deploy/postgres/ops-ownership.sql') -Raw) -Quiet
if ($exists -notmatch 'holdout') { Invoke-Psql 'postgres' 'CREATE DATABASE holdout OWNER eval_owner;' -Quiet }
Invoke-Psql 'postgres' @'
REVOKE CONNECT, TEMPORARY ON DATABASE holdout FROM PUBLIC;
GRANT CONNECT ON DATABASE holdout TO eval_registry, eval_diagnostic, eval_prospective;
DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_database WHERE datname = 'holdout_synth') THEN
    REVOKE CONNECT, TEMPORARY ON DATABASE holdout_synth FROM PUBLIC; END IF; END $$;
'@ -Quiet

# 5. Ops migrations as ops_owner through the loopback port (pg_hba "samenet"); fallback to the measured source.
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
# The migrator's output goes to the console (Out-Host): a PowerShell function returns every uncaptured output line,
# so returning only the exit code needs the output kept out of the pipeline.
function Invoke-OpsMigrations {
    $env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=ops;Username=ops_owner;Password=$(Get-Secret $script:OpsSecrets 'ops_owner')"
    try {
        dotnet run --project src/Tools/DbMigrator -c Release | Out-Host
        [int] $code = $LASTEXITCODE
        if ($code -ne 0) {
            # The server-side reason (no pg_hba entry, failed password, SQL error); these lines carry no secret.
            Write-Host 'PostgreSQL reported:' -ForegroundColor Yellow
            docker compose logs postgres --since 3m 2>$null | Select-String 'FATAL|ERROR|DETAIL' | Select-Object -Last 10 | ForEach-Object { Write-Host "  $($_.Line)" }
        }
        return $code
    }
    finally { Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue }
}
if ((Invoke-OpsMigrations) -ne 0) {
    # Fallback (m5-protocol §4.3): pg_hba "samenet" did not admit the loopback source; use the measured address.
    $source = (docker compose logs postgres --since 5m | Select-String 'connection received: host=([0-9.]+)' |
        Where-Object { $_.Matches[0].Groups[1].Value -ne '127.0.0.1' } | Select-Object -Last 1).Matches.Groups[1].Value
    if (-not $source) { throw 'Migrations failed and no loopback source address was observed.' }
    Write-Host "Loopback connections arrive from $source; writing deploy/postgres/pg_hba.local.conf."
    (Get-Content 'deploy/postgres/pg_hba.conf') -replace 'samenet', "$source/32" | Set-Content 'deploy/postgres/pg_hba.local.conf' -Encoding ascii
    Set-DotEnvValue 'PI_PG_HBA_FILE' './deploy/postgres/pg_hba.local.conf'
    docker compose up -d --wait postgres
    if ((Invoke-OpsMigrations) -ne 0) { throw 'Migrations failed.' }
}

Set-DotEnvValue 'PI_OPS_DB' 'ops'
Write-Host 'Bootstrap complete: ops (migrated, owner ops_owner), holdout (owner eval_owner, empty), logins set. Next: scripts/m5-deploy.ps1.'
