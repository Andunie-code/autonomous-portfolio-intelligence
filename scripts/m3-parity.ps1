<#
.SYNOPSIS
  M3 regression lock and DSL parity on the research window (docs/m3-protocol.md §3, §12). Local database only.
.DESCRIPTION
  Re-runs the M2 baselines (coded and DSL) on the 20 core pairs and compares the four result hashes with
  docs/m2-baseline-results.md. Nothing is recorded (not new trials). Reads only market.candles (< C).
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m3-parity.ps1
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
    dotnet run --project src/Tools/BacktestCli -c Release -- parity-m3
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
