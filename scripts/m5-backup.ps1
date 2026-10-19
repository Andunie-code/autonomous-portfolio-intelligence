<#
.SYNOPSIS
  Encrypted backup of the ops database and of the evaluation records (docs/m5-protocol.md §7.4; U-M5-3). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-backup.ps1
.DESCRIPTION
  pg_dump -Fc of ops inside the PostgreSQL container, encrypted with AES-256-GCM (secrets/backup_key) into
  PI_BACKUP_DIR (absolute, outside the repository and OneDrive); the plaintext only exists in %TEMP% and is
  deleted. The evaluator exports its records (manifests, members, releases, flags, runs, results; no candles) and
  they are encrypted the same way. The holdout window data is not backed up (reproducible from the archive).
  Copy the files to the external disk yourself. A daily run can be scheduled with the Windows Task Scheduler.
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$settings = Read-DotEnv
$backupDir = $settings['PI_BACKUP_DIR']
if (-not $backupDir) { throw 'PI_BACKUP_DIR is not set (scripts/m5-bootstrap.ps1).' }
dotnet run --project src/Tools/BackupCli -c Release -- check-target $backupDir $script:Root
if ($LASTEXITCODE -ne 0) { throw 'Backup directory rejected.' }
$key = Join-Path $script:OpsSecrets 'backup_key'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$work = Join-Path $env:TEMP ("pi-backup-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    docker compose exec -T -e "PGPASSWORD=$(Get-SuperuserPassword)" postgres pg_dump -U pi_dev -Fc -f /tmp/ops.dump ops
    if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }
    docker compose cp postgres:/tmp/ops.dump (Join-Path $work 'ops.dump') | Out-Null
    docker compose exec -T postgres rm -f /tmp/ops.dump
    dotnet run --project src/Tools/BackupCli -c Release -- encrypt (Join-Path $work 'ops.dump') (Join-Path $backupDir "ops-$stamp.dump.enc") $key
    if ($LASTEXITCODE -ne 0) { throw 'Encryption failed.' }

    $env:PI_EVALUATOR_DIGEST = $settings['PI_EVALUATOR_DIGEST']
    docker compose --profile evaluator run --rm -v "${work}:/export" holdout-evaluator export-records /export/records.json
    if ($LASTEXITCODE -ne 0) { throw 'Record export failed.' }
    dotnet run --project src/Tools/BackupCli -c Release -- encrypt (Join-Path $work 'records.json') (Join-Path $backupDir "evaluation-records-$stamp.json.enc") $key
    if ($LASTEXITCODE -ne 0) { throw 'Encryption failed.' }
} finally {
    Remove-Item $work -Recurse -Force
}
Write-Host "Backups written to $backupDir (ops-$stamp.dump.enc, evaluation-records-$stamp.json.enc). Copy them to the external disk."
