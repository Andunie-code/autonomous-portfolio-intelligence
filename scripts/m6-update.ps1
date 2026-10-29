<#
.SYNOPSIS
  M6 Gate C step C3 (docs/m6-protocol.md §10). User only, one interactive run:
    powershell -ExecutionPolicy Bypass -File scripts/m6-update.ps1 [-ChecksOnly]
.DESCRIPTION
  1. Preflight: src/ and scripts/ are committed and clean.
  2. Ops migrations (scripts/m5-migrate.ps1; its own confirmation): migration 0009 adds the Testnet outbox.
  3. Trading.Host rebuilt and started with live data, and with Testnet execution when the Testnet key is stored
     (scripts/m6-testnet-key.ps1); waits for the first cycle after the restart and stops if it failed.
  4. Checks (also alone with -ChecksOnly), operational fields only: paper runs and lifecycle states, Testnet runs,
     Unknown orders, positions without a protective stop on the exchange, the Testnet reconciliation, D30 progress.
  5. Operational paper report (scripts/m5-paper-report.ps1).
#>
param([switch] $ChecksOnly)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

function Step([string] $Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }
function Run-Script([string] $Name, [string[]] $Arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Name) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$base = 'http://127.0.0.1:55490'

function Show-Checks {
    $status = Invoke-RestMethod "$base/status" -Headers $headers
    $last = $status[0].last_cycle
    Write-Host "lastCycle outcome=$($last.outcome) degraded=$(@($last.detail.degraded) -join ',') bars=$($last.bars_processed) killSwitch=$($status[0].kill_switch)"
    # Windows PowerShell 5.1 returns a JSON array as one object; @() would wrap it once more. The pipeline unrolls it,
    # so each lifecycle event is visited and the latest one per member wins (events come in id order).
    $lifecycle = Invoke-RestMethod "$base/lifecycle" -Headers $headers
    $states = @{}
    $lifecycle | ForEach-Object { $states["$($_.cohort_id)/$($_.member_index)"] = $_.to_state }
    $byState = $states.Values | Group-Object | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }
    Write-Host "lifecycle $($byState -join ' ')"
    $t = Invoke-RestMethod "$base/testnet" -Headers $headers
    $open = @($t.runs | Where-Object { -not $_.closed_at }).Count
    $reconciliation = 'none'
    if ($t.reconciliation) { $reconciliation = "ok=$($t.reconciliation.ok) reason=$($t.reconciliation.reason)" }
    Write-Host "testnet enabled=$($t.enabled) openRuns=$open unknownOrders=$($t.unknown) unprotectedPositions=$($t.unprotected) reconciliation=$reconciliation"
    $outbox = @($t.outbox | ForEach-Object { "$($_.purpose)/$($_.status)=$($_.n)" })
    Write-Host "outbox $($outbox -join ' ')"
    $p = Invoke-RestMethod "$base/promotion" -Headers $headers
    foreach ($s in @($p.strategies | Where-Object { $_.state -in @('PaperTrading', 'TestnetActive') })) {
        Write-Host ("d30 {0} state={1} days={2}/{3} bars={4}/{5} criticalErrors7d={6} eligible={7}" -f $s.name, $s.state, $s.days, $p.minimumDays, $s.bars, $p.minimumBars, $s.criticalErrors7d, $s.eligible)
    }
}

if ($ChecksOnly) {
    Step 'Checks (read-only)'
    Show-Checks
    exit 0
}

Step '1/5 Preflight'
if (git status --porcelain -- src scripts) { throw 'src/ or scripts/ has uncommitted changes: the update builds only committed code.' }
Write-Host "commit=$((git rev-parse HEAD).Trim())"

Step '2/5 Ops migrations'
Run-Script 'm5-migrate.ps1' @()

Step '3/5 Trading.Host rebuilt (live data; Testnet when the key is stored)'
$keyFile = Ensure-EmptySecret $script:OpsSecrets 'testnet_api_key'
$secretFile = Ensure-EmptySecret $script:OpsSecrets 'testnet_api_secret'
Reset-SecretAcl $script:OpsSecrets 'testnet_api_key'
Reset-SecretAcl $script:OpsSecrets 'testnet_api_secret'
Ensure-EmptySecret $script:OpsSecrets 'gemini_runtime_api_key' | Out-Null  # M8: compose mounts it (empty: allocator stays T11)
Reset-SecretAcl $script:OpsSecrets 'gemini_runtime_api_key'
$testnet = ((Get-Item $keyFile).Length -gt 0) -and ((Get-Item $secretFile).Length -gt 0)
if ($testnet) {
    Confirm-Interactive 'Start Trading.Host with live klines and Testnet execution enabled (orders to testnet.binance.vision only for TestnetActive strategies)?'
    $env:PI_TRADING_TESTNET = 'enabled'
} else {
    Confirm-Interactive 'Start Trading.Host with live klines; no Testnet key is stored, so Testnet stays off?'
    $env:PI_TRADING_TESTNET = 'disabled'
}
$env:PI_TRADING_LIVE_DATA = 'enabled'
$restartedAt = [DateTimeOffset]::UtcNow
docker compose --profile trading up -d --build trading-host
if ($LASTEXITCODE -ne 0) { throw 'Trading.Host did not start.' }
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "$base/health/live" -TimeoutSec 5 | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 2 }
}
if (-not $ready) { throw 'Trading.Host did not answer /health/live within a minute.' }
$cycle = $null
for ($i = 0; $i -lt 60; $i++) {
    $last = (Invoke-RestMethod "$base/status" -Headers $headers)[0].last_cycle
    if ($last -and $last.finished_at) {
        $f = $last.finished_at
        $finished = if ($f -is [DateTime]) { [DateTimeOffset]$f.ToUniversalTime() } else { [DateTimeOffset]::Parse($f, [Globalization.CultureInfo]::InvariantCulture) }
        if ($finished -ge $restartedAt) { $cycle = $last; break }
    }
    Start-Sleep -Seconds 3
}
if (-not $cycle) { throw 'No cycle finished within three minutes of the restart (see docker compose logs trading-host).' }
Write-Host "firstCycleAfterRestart outcome=$($cycle.outcome) degraded=$(@($cycle.detail.degraded) -join ',') bars=$($cycle.bars_processed)"
if ($cycle.outcome -eq 'error') { throw 'The first cycle after the restart failed: the checks are not run.' }

Step '4/5 Checks'
Show-Checks

Step '5/5 Operational paper report'
Run-Script 'm5-paper-report.ps1' @()

Write-Host ''
Write-Host 'Update complete. Dashboard: http://127.0.0.1:55490/dashboard (Testnet tab).' -ForegroundColor Green
