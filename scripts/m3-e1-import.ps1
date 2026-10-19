<#
.SYNOPSIS
  M3 E1 subset import (docs/m3-protocol.md §11, decision D28) — Gate C.
.DESCRIPTION
  Without switches: prints the plan; no network access ("dry run").
  -CheckCutoff: H1 check against the local database (must report 0).
  -AllowNetwork: Gate C only. Lists the archive, ranks the E1 candidates and imports 1h and 4h data of the first
  five that pass the quality rules, after an interactive confirmation. Run it yourself in an interactive terminal;
  it refuses redirected input. Claude Code is denied this switch.
  Raw archive files go to %LOCALAPPDATA%\PortfolioIntelligence\archive-cache (outside the repository and OneDrive).
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m3-e1-import.ps1
#>
param(
    [switch]$AllowNetwork,
    [switch]$CheckCutoff
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$cli = 'src/Tools/MarketDataCli'
if (-not $AllowNetwork -and -not $CheckCutoff) {
    dotnet run --project $cli -c Release -- e1-plan
    exit $LASTEXITCODE
}

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    # -cmatch: culture-independent (see dev-up.ps1).
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
. "$PSScriptRoot\lib\m5-common.ps1"
$env:ConnectionStrings__Ops = Get-OpsConnection 'market_import'  # M5: market_import on ops after the bootstrap
try {
    if ($CheckCutoff) {
        dotnet run --project $cli -c Release -- check-cutoff
    }
    else {
        dotnet run --project $cli -c Release -- e1 --allow-network
    }
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
