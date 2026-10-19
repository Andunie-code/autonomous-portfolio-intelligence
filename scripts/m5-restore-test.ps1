<#
.SYNOPSIS
  Restore test of an ops backup (docs/m5-protocol.md §7.4). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-restore-test.ps1 -Backup <path to ops-*.dump.enc>
.DESCRIPTION
  Decrypts the backup into %TEMP%, restores it into the scratch database ops_restore_test, and compares every table
  of the project schemas with the live ops database: row count and a content hash. Output: table, counts, match.
  Tables written after the backup (append-only) may differ only by later rows; the report says which. The scratch
  database and the plaintext are removed at the end.
#>
param([Parameter(Mandatory = $true)] [string] $Backup)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$key = Join-Path $script:OpsSecrets 'backup_key'
$work = Join-Path $env:TEMP ("pi-restore-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    dotnet run --project src/Tools/BackupCli -c Release -- decrypt $Backup (Join-Path $work 'ops.dump') $key
    if ($LASTEXITCODE -ne 0) { throw 'Decryption failed (wrong key or damaged file).' }
    docker compose cp (Join-Path $work 'ops.dump') postgres:/tmp/ops_restore.dump | Out-Null
    Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS ops_restore_test; CREATE DATABASE ops_restore_test;" -Quiet
    docker compose exec -T -e "PGPASSWORD=$(Get-SuperuserPassword)" postgres pg_restore -U pi_dev -d ops_restore_test --no-owner --no-privileges /tmp/ops_restore.dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed.' }
    docker compose exec -T postgres rm -f /tmp/ops_restore.dump

    $tablesSql = "SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND n.nspname IN ('market', 'research', 'trading', 'audit', 'ai') ORDER BY 1;"
    $tables = @(Invoke-Psql 'ops_restore_test' $tablesSql)
    $lines = @()
    $mismatch = 0
    foreach ($table in $tables) {
        $probe = "SELECT count(*) || ' ' || md5(coalesce(string_agg(t::text, '|' ORDER BY t::text), '')) FROM $table t;"
        $restored = (Invoke-Psql 'ops_restore_test' $probe | Select-Object -First 1)
        $live = (Invoke-Psql 'ops' $probe | Select-Object -First 1)
        $equal = $restored -eq $live
        if (-not $equal) { $mismatch++ }
        $lines += "$table restored=$($restored.Split(' ')[0]) live=$($live.Split(' ')[0]) $(if ($equal) { 'MATCH' } else { 'DIFFERS' })"
    }
} finally {
    Invoke-Psql 'postgres' 'DROP DATABASE IF EXISTS ops_restore_test;' -Quiet
    Remove-Item $work -Recurse -Force
}
$report = ($lines -join "`n") + "`n"
Write-Host $report
Write-Host "Tables: $($tables.Count), differing: $mismatch (append-only tables may only have later rows in the live database)."
