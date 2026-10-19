<#
.SYNOPSIS
  Research-side M5 commands as research_app on the ops database (docs/m5-protocol.md §4.2, §5, §9; CLAUDE.md).
    powershell -ExecutionPolicy Bypass -File scripts/m5-research.ps1 <command> [arguments]
  Commands: parity-m3 | freeze-import <manifest> <cohortId> | parity-m5 <manifest> <cohortId> [--record] | p1-analysis <manifest>
.DESCRIPTION
  research_app reads research data (< C), its registries and the frozen-strategy and lifecycle tables; it cannot
  read live candles, trading.* or unrevealed cohorts. Nothing here touches the network or the holdout database.
  Before the M5 bootstrap (no ops database yet) parity-m3 falls back to the M0-M4 development database.
#>
param([Parameter(Mandatory = $true, Position = 0)] [string] $Command, [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$settings = Read-DotEnv
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
$researchSecret = Join-Path $script:OpsSecrets 'research_app'
if ($settings['PI_OPS_DB'] -eq 'ops' -and (Test-Path $researchSecret)) {
    $env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=ops;Username=research_app;Password=$(Get-Secret $script:OpsSecrets 'research_app')"
} elseif ($Command -eq 'parity-m3') {
    $env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$($settings['PI_DEV_DB_PASSWORD'])"
} else {
    throw 'The ops database is not bootstrapped yet (scripts/m5-bootstrap.ps1, user).'
}

try {
    if ($Command -in @('freeze-import', 'parity-m5', 'p1-analysis') -and $Arguments.Count -ge 1) { Assert-CleanFile $Arguments[0] }
    dotnet run --project src/Tools/BacktestCli -c Release -- $Command @Arguments
    exit $LASTEXITCODE
} finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
