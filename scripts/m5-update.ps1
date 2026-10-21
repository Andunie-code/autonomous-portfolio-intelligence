<#
.SYNOPSIS
  Brings the running local system up to the committed code (M5b). User only, one interactive run:
    powershell -ExecutionPolicy Bypass -File scripts/m5-update.ps1 [-CohortId 2]
.DESCRIPTION
  1. Preflight: src/ and scripts/ are committed and clean.
  2. Ops migrations (scripts/m5-migrate.ps1; its own confirmation). Migration 0008 adds ops_actions.run_id, which
     the paper trader reads every cycle, and the dashboard's read grants: without it cycles fail and the dashboard's
     system view cannot read the AI and audit tables.
  3. Trading.Host rebuilt and started with live data (scripts/m5-paper-start.ps1; its own confirmation).
  4. Activation checks of the cohort, read-only (scripts/m5-cohort1-activate.ps1 -CohortId <id> -ChecksOnly).
  5. Operational paper report (scripts/m5-paper-report.ps1). The first cycle after the restart runs at the next
     HH:00:15 UTC: run the report again after it to see that cycle.
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
Run-Script 'm5-paper-start.ps1' @()
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod 'http://127.0.0.1:55490/health/live' -TimeoutSec 5 | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 2 }
}
if (-not $ready) { throw 'Trading.Host did not answer /health/live within a minute.' }

Step "4/5 Activation checks (cohort $CohortId, read-only)"
Run-Script 'm5-cohort1-activate.ps1' @('-CohortId', "$CohortId", '-ChecksOnly')

Step '5/5 Operational paper report'
Run-Script 'm5-paper-report.ps1' @()

Write-Host ''
Write-Host 'Update complete. Dashboard: http://127.0.0.1:55490/dashboard (token: secrets\ops_api_token).' -ForegroundColor Green
Write-Host 'The first cycle after the restart runs at the next HH:00:15 UTC; run scripts/m5-paper-report.ps1 again after it.'
