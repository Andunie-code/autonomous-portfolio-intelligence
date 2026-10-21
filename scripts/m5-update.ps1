<#
.SYNOPSIS
  Brings the running local system up to the committed code (M5b). User only, one interactive run:
    powershell -ExecutionPolicy Bypass -File scripts/m5-update.ps1 [-CohortId 2]
.DESCRIPTION
  1. Preflight: src/ and scripts/ are committed and clean.
  2. Ops migrations (scripts/m5-migrate.ps1; its own confirmation). Migration 0008 adds ops_actions.run_id, which
     the paper trader reads every cycle, and the dashboard's read grants: without it cycles fail and the dashboard's
     system view cannot read the AI and audit tables.
  3. Trading.Host rebuilt and started with live data (scripts/m5-paper-start.ps1; its own confirmation); waits
     for the first cycle after the restart (run at start) and stops if it failed.
  4. Activation checks of the cohort, read-only (scripts/m5-cohort1-activate.ps1 -CohortId <id> -ChecksOnly).
  5. Operational paper report (scripts/m5-paper-report.ps1).
  Every step stops the run on failure.
#>
param([long] $CohortId = 2)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

function Step([string] $Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }
function Run-Script([string] $Name, [string[]] $Arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Name) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
}

Step '1/5 Preflight'
if (git status --porcelain -- src scripts) { throw 'src/ or scripts/ has uncommitted changes: the update builds only committed code.' }
Write-Host "commit=$((git rev-parse HEAD).Trim())"

Step '2/5 Ops migrations'
Run-Script 'm5-migrate.ps1' @()

Step '3/5 Trading.Host rebuilt with live data'
$restartedAt = [DateTimeOffset]::UtcNow
Run-Script 'm5-paper-start.ps1' @()
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod 'http://127.0.0.1:55490/health/live' -TimeoutSec 5 | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 2 }
}
if (-not $ready) { throw 'Trading.Host did not answer /health/live within a minute.' }
# Trading.Host runs its first cycle right at start (reconciliation, then missed bars). The checks wait for it, so
# /status does not report the last cycle of the previous container.
$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$cycle = $null
for ($i = 0; $i -lt 60; $i++) {
    $status = Invoke-RestMethod 'http://127.0.0.1:55490/status' -Headers $headers
    $last = $status[0].last_cycle
    if ($last -and $last.finished_at) {
        $f = $last.finished_at
        $finished = if ($f -is [DateTime]) { [DateTimeOffset]$f.ToUniversalTime() } else { [DateTimeOffset]::Parse($f, [Globalization.CultureInfo]::InvariantCulture) }
        if ($finished -ge $restartedAt) { $cycle = $last; break }
    }
    Start-Sleep -Seconds 3
}
if (-not $cycle) { throw 'No cycle finished within three minutes of the restart (see docker compose logs trading-host).' }
Write-Host "firstCycleAfterRestart outcome=$($cycle.outcome) bars=$($cycle.bars_processed)"
if ($cycle.outcome -eq 'error') { throw 'The first cycle after the restart failed: the checks are not run.' }

Step "4/5 Activation checks (cohort $CohortId, read-only)"
Run-Script 'm5-cohort1-activate.ps1' @('-CohortId', "$CohortId", '-ChecksOnly')

Step '5/5 Operational paper report'
Run-Script 'm5-paper-report.ps1' @()

Write-Host ''
Write-Host 'Update complete. Dashboard: http://127.0.0.1:55490/dashboard (token: secrets\ops_api_token).' -ForegroundColor Green
