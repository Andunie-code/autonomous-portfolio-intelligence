<#
.SYNOPSIS
  M2 baseline backtest (docs/m2-protocol.md). Local database only; no network.
.DESCRIPTION
  Runs the pre-registered baselines (policy and no-halt runs) and benchmarks on the 20 core pairs, records the
  experiments in research.experiments and writes a metrics-only report to
  %LOCALAPPDATA%\PortfolioIntelligence\archive-cache\reports.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m2-backtest.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    # -cmatch: culture-independent (see dev-up.ps1).
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
. "$PSScriptRoot\lib\m5-common.ps1"
$env:ConnectionStrings__Ops = Get-OpsConnection 'research_app'  # M5: research_app on ops after the bootstrap
try {
    dotnet run --project src/Tools/BacktestCli -c Release -- run-baselines
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
