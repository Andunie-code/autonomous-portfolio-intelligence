<#
.SYNOPSIS
  Registers the committed Kohort 0 manifest on the synthetic evaluator database (M3; architecture §6.3 seed exception).
.DESCRIPTION
  registered_at = commit time of docs/cohorts/cohort-0.json; A_0 and the window end are derived by the database.
  Uses the local dev PostgreSQL: holdout_synth (created and migrated if missing) and portfolio_dev for the status
  relay. No real holdout data exists in M3. The real seed into the holdout database happens in M5.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File src/HoldoutEvaluator/evaluator-seed-cohort0.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

$manifest = 'docs/cohorts/cohort-0.json'
$registeredAt = git log -1 --format=%cI -- $manifest
if (-not $registeredAt) { throw "$manifest is not committed; freeze it with a local commit first." }
if (git status --porcelain -- $manifest) { throw "$manifest has uncommitted changes." }

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
$password = $settings['PI_DEV_DB_PASSWORD']
$env:ConnectionStrings__HoldoutDb = "Host=127.0.0.1;Port=$port;Database=holdout_synth;Username=pi_dev;Password=$password"
$env:ConnectionStrings__StatusRelay = "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$password"
$env:Evaluator__CreateDatabase = 'true'
try {
    dotnet run --project src/HoldoutEvaluator -c Release -- seed-cohort0 (Resolve-Path $manifest).Path $registeredAt
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__HoldoutDb, Env:ConnectionStrings__StatusRelay, Env:Evaluator__CreateDatabase -ErrorAction SilentlyContinue
}
