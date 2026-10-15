<#
.SYNOPSIS
  Kohort 0 manifest (docs/m3-protocol.md §10, docs/m3-protocol-v2.md §5). Local database only; no network.
.DESCRIPTION
  Computes M_R, delta and the duration/power plan of the two DSL baselines on the research window, records the two
  M2 trials (dev-assisted) and writes docs/cohorts/cohort-0.json. Freezing = committing that file locally; then
  src/HoldoutEvaluator/evaluator-seed-cohort0.ps1 registers it on the synthetic evaluator database.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m3-cohort0.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
$env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$($settings['PI_DEV_DB_PASSWORD'])"
try {
    dotnet run --project src/Tools/BacktestCli -c Release -- cohort0
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
