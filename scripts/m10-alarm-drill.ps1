<#
.SYNOPSIS
  M10 Gate C step C3: alarm drill (docs/m9-m10-protocol.md §3.4; ADR-0024). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m10-alarm-drill.ps1 [-SkipTradingKillSwitch] [-SkipAiKillSwitch]
.DESCRIPTION
  Exercises the computed alarms on the real local environment and writes an operational report (alarm codes,
  severities, counts and times only; no price, balance or PnL) with its SHA-256 to the reports directory.
    1. Baseline: GET /alarms and the kill switch states.
    2. Trading kill switch (A1): engage through the Ops API, see A1, check that Testnet entry orders are cancelled
       and protective stops stay (counts only; m6-protocol §5.5, also M6 C4's kill switch check), release, see A1 clear.
    3. AI kill switch (A2): trip with scripts/m4-ai.ps1, see A2 (a manual trip raises no A4), clear interactively
       (type EVET), see A2 clear. While it is on, the allocator falls back to T11 without a call.
    4. Budget (A3): prints this month's use against the thresholds; no real spend (the trigger is proven offline).
    5. H-ENV (A5): shows whether the monthly run is due; if so, run scripts/m5-henv.ps1 and this drill again.
    6. Heartbeat (A6): shown only (stopping Trading.Host would break the Testnet rehearsal).
  A switch that is already on is left alone and that step is skipped. Piped input is refused.
#>
param([switch] $SkipTradingKillSwitch, [switch] $SkipAiKillSwitch)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

if ([Console]::IsInputRedirected) { throw 'The alarm drill needs interactive confirmations; input is redirected.' }
$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$base = 'http://127.0.0.1:55490'
$steps = New-Object System.Collections.Generic.List[object]

function Get-Ops([string] $Path) { return Invoke-RestMethod "$base$Path" -Headers $headers -TimeoutSec 30 }
function Post-Ops([string] $Path, [hashtable] $Body) {
    try {
        return Invoke-RestMethod -Method Post "$base$Path" -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 30
    } catch {
        $message = $_.ErrorDetails.Message
        if (-not $message) { $message = $_.Exception.Message }
        throw "Ops API refused the request: $message"
    }
}
function Get-Codes { return @((Get-Ops '/alarms').alarms | ForEach-Object { $_.code }) }
function Wait-Code([string] $Code, [bool] $Present, [int] $Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        if (((Get-Codes) -contains $Code) -eq $Present) { return $true }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}
function Add-Step([string] $Name, [bool] $Pass, [hashtable] $Detail) {
    $steps.Add([ordered]@{ step = $Name; pass = $Pass; detail = $Detail })
    Write-Host ("{0,-24} {1}  {2}" -f $Name, $(if ($Pass) { 'PASS' } else { 'FAIL' }), (($Detail.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '))
}
function Get-TestnetCounts {
    $t = Get-Ops '/testnet'
    $outbox = @($t.outbox)
    return [ordered]@{
        enabled = [bool]$t.enabled
        positions = @($t.positions).Count
        unprotected = [int]$t.unprotected
        acceptedStops = [int](($outbox | Where-Object { $_.purpose -eq 'Stop' -and $_.status -in @('Accepted', 'PartiallyFilled') } | Measure-Object -Property n -Sum).Sum)
        openEntries = [int](($outbox | Where-Object { $_.purpose -eq 'Entry' -and $_.status -in @('Accepted', 'PartiallyFilled', 'Submitting', 'Unknown') } | Measure-Object -Property n -Sum).Sum)
    }
}

# 1. Baseline.
$start = Get-Ops '/alarms'
$system = Get-Ops '/system'
Write-Host "Alarms now: $(@($start.alarms).Count) (critical $($start.critical), warning $($start.warning)): $((@($start.alarms) | ForEach-Object { $_.code }) -join ', ')"
$baseline = [ordered]@{ alarms = @($start.alarms | ForEach-Object { [ordered]@{ code = $_.code; severity = $_.severity; since = $_.since } }) }

# 2. Trading kill switch (A1).
$tradingOn = $system.tradingKillSwitch -and $system.tradingKillSwitch.engaged
if ($SkipTradingKillSwitch -or $tradingOn) {
    Add-Step 'trading-kill-switch' $true @{ skipped = $(if ($tradingOn) { 'already-engaged' } else { 'by-flag' }) }
} else {
    Confirm-Interactive 'Engage the trading kill switch for the drill (no new entries; open Testnet entry orders are cancelled; stops stay), then release it?'
    $before = Get-TestnetCounts
    Post-Ops '/kill-switch' @{ engaged = $true; reason = 'M10 alarm drill (m10-alarm-drill.ps1)' } | Out-Null
    $seen = Wait-Code 'trading-kill-switch' $true
    Start-Sleep -Seconds 20 # the exchange maintenance runs at once (MaintenanceSignal)
    $during = Get-TestnetCounts
    Post-Ops '/kill-switch' @{ engaged = $false; reason = 'M10 alarm drill over' } | Out-Null
    $cleared = Wait-Code 'trading-kill-switch' $false
    $stopsKept = (-not $before.enabled) -or (($during.acceptedStops -eq $before.acceptedStops) -and ($during.unprotected -eq 0))
    Add-Step 'trading-kill-switch' ($seen -and $cleared -and $stopsKept -and $during.openEntries -eq 0) @{
        alarmSeen = $seen; alarmCleared = $cleared; testnet = $before.enabled; positions = $before.positions
        acceptedStopsBefore = $before.acceptedStops; acceptedStopsDuring = $during.acceptedStops; openEntriesBefore = $before.openEntries; openEntriesDuring = $during.openEntries
        unprotectedDuring = $during.unprotected
    }
}

# 3. AI kill switch (A2).
$ai = $system.aiKillSwitch
$aiOn = $ai -and $ai.tripped
if ($SkipAiKillSwitch -or $aiOn) {
    Add-Step 'ai-kill-switch' $true @{ skipped = $(if ($aiOn) { 'already-tripped' } else { 'by-flag' }) }
} else {
    Confirm-Interactive 'Trip the AI kill switch for the drill (no LLM call; the allocator falls back to T11), then clear it (you will type EVET)?'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'm4-ai.ps1') killswitch trip --reason 'M10 alarm drill (m10-alarm-drill.ps1)'
    if ($LASTEXITCODE -ne 0) { throw 'm4-ai.ps1 killswitch trip failed.' }
    $seen = Wait-Code 'ai-kill-switch' $true
    $autoTrip = (Get-Codes) -contains 'ai-auto-trip'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'm4-ai.ps1') killswitch clear --reason 'M10 alarm drill over'
    $clearExit = $LASTEXITCODE
    $cleared = Wait-Code 'ai-kill-switch' $false
    $autoBefore = @($start.alarms | Where-Object { $_.code -eq 'ai-auto-trip' }).Count -gt 0
    Add-Step 'ai-kill-switch' ($seen -and $cleared -and $clearExit -eq 0 -and ($autoBefore -or -not $autoTrip)) @{
        alarmSeen = $seen; alarmCleared = $cleared; clearExit = $clearExit; manualTripRaisedAutoTrip = ($autoTrip -and -not $autoBefore)
    }
    if ($clearExit -ne 0) { Write-Warning 'The AI kill switch is still ON: clear it with scripts/m4-ai.ps1 killswitch clear --reason "..." (runbook §1 A2).' }
}

# 4. Budget (A3): read only.
$costs = Get-Ops '/ai/costs'
$budgetAlarm = @((Get-Ops '/alarms').alarms | Where-Object { $_.code -eq 'ai-budget' }) | Select-Object -First 1
$share = [double]$costs.monthShare
$expected = if ($share -ge 1.0) { 'critical' } elseif ($share -ge [double]$costs.warningShare) { 'warning' } else { 'none' }
$actual = if ($budgetAlarm) { $budgetAlarm.severity } else { 'none' }
Add-Step 'ai-budget' ($expected -eq $actual) @{ month = $costs.month; usedUsd = $costs.monthUsedUsd; capUsd = $costs.monthlyCapUsd; share = [math]::Round($share, 4); expected = $expected; alarm = $actual }

# 5. H-ENV (A5) and 6. heartbeat (A6): state only.
$final = Get-Ops '/alarms'
$henv = @($final.alarms | Where-Object { $_.code -eq 'henv-stale' }) | Select-Object -First 1
Add-Step 'henv' $true @{ due = [bool]$henv; severity = $(if ($henv) { $henv.severity } else { 'none' }) }
if ($henv) { Write-Host 'The monthly H-ENV run is due: run scripts/m5-henv.ps1, then this drill again (with -SkipTradingKillSwitch -SkipAiKillSwitch) to see A5 clear.' }
$heartbeat = @($final.alarms | Where-Object { $_.code -eq 'heartbeat' }).Count -gt 0
Add-Step 'heartbeat' (-not $heartbeat) @{ alarm = $heartbeat }

$report = [ordered]@{
    kind = 'm10-alarm-drill'
    at = (Get-Date).ToUniversalTime().ToString('o')
    pass = -not ($steps | Where-Object { -not $_.pass })
    baseline = $baseline
    steps = $steps
    after = [ordered]@{ critical = $final.critical; warning = $final.warning; codes = @($final.alarms | ForEach-Object { $_.code }) }
}
$json = $report | ConvertTo-Json -Depth 8
$hash = Get-Sha256Text $json
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$path = Join-Path (Get-ReportDirectory) "m10-alarm-drill-$stamp.json"
[System.IO.File]::WriteAllText($path, $json)
Write-Host "Alarm drill report: $path"
Write-Host "Report SHA-256: $hash"
Write-Host "pass=$($report.pass); alarms after: critical $($final.critical), warning $($final.warning): $((@($final.alarms) | ForEach-Object { $_.code }) -join ', ')"
if (-not $report.pass) { exit 1 }
