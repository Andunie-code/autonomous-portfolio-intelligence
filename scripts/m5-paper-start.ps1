<#
.SYNOPSIS
  M5 Gate C step 6 (docs/m5-protocol.md §2.3, §7.1; user decision A-3). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-paper-start.ps1 [-Stop]
.DESCRIPTION
  Starts Trading.Host with live market data enabled: closed 4h klines of the frozen strategies' pairs from
  https://data-api.binance.vision/api/v3/klines only (no key, no order endpoint), written to market.live_candles.
  The user's confirmation opens this network access; -Stop restarts Trading.Host with live data disabled.
  Testnet execution stays enabled when its key is stored (secrets/testnet_api_key and _secret not empty).
#>
param([switch] $Stop)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root
# M6: the compose file names the Testnet key secrets; empty placeholders keep Testnet off (scripts/m6-testnet-key.ps1).
Ensure-EmptySecret $script:OpsSecrets 'testnet_api_key' | Out-Null
Ensure-EmptySecret $script:OpsSecrets 'testnet_api_secret' | Out-Null
Ensure-EmptySecret $script:OpsSecrets 'gemini_runtime_api_key' | Out-Null
# M10 (runbook §3): a recreated Trading.Host keeps Testnet execution when the Testnet key is stored, as
# scripts/m6-update.ps1 does; otherwise a restart after sleep or reboot would silently turn Testnet off.
$testnetKey = Join-Path $script:OpsSecrets 'testnet_api_key'
$testnetSecret = Join-Path $script:OpsSecrets 'testnet_api_secret'
$env:PI_TRADING_TESTNET = if (((Get-Item $testnetKey).Length -gt 0) -and ((Get-Item $testnetSecret).Length -gt 0)) { 'enabled' } else { 'disabled' }
Write-Host "Testnet execution: $env:PI_TRADING_TESTNET (enabled when the Testnet key is stored)."

if ($Stop) {
    $env:PI_TRADING_LIVE_DATA = 'disabled'
    docker compose --profile trading up -d --force-recreate trading-host
    Write-Host 'Trading.Host restarted without live data.'
    exit $LASTEXITCODE
}

Confirm-Interactive 'Allow Trading.Host to fetch live klines from data-api.binance.vision and start paper trading?'
$env:PI_TRADING_LIVE_DATA = 'enabled'
docker compose --profile trading up -d --build --force-recreate trading-host
if ($LASTEXITCODE -ne 0) { throw 'Trading.Host did not start.' }
Write-Host 'Paper trading started. Ops API: http://127.0.0.1:55490 (bearer token in secrets/ops_api_token).'
Write-Host 'Operational report for Claude Code: scripts/m5-paper-report.ps1 (no prices, PnL or trades).'
