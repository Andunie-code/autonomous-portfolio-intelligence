<#
.SYNOPSIS
  M4 AI command line (ADR-0017 §5): kill switch, ledger and reconciliation on the local database.
.DESCRIPTION
  Passes its arguments to src/Tools/AiCli, e.g.
    scripts/m4-ai.ps1 killswitch status
    scripts/m4-ai.ps1 killswitch clear --reason "..."      (interactive terminal only)
    scripts/m4-ai.ps1 ledger --month 2026-10
    scripts/m4-ai.ps1 reconcile --month 2026-10 --invoice-try 123.45 --usd-try 41.20
  Gate C only: the spike makes real Gemini calls and needs the network flag, an interactive confirmation and the
  user's own API key (Gemini__ApiKey in the session or dotnet user-secrets of the tool). Claude Code cannot start it.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m4-ai.ps1 <args>
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
    dotnet run --project src/Tools/AiCli -c Release -- @args
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
