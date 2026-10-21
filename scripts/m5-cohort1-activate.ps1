<#
.SYNOPSIS
  M5b Gate C step 3 (ADR-0019 §5; docs/cohort-1-preregistration.md §5). User only, one interactive run:
    powershell -ExecutionPolicy Bypass -File scripts/m5-cohort1-activate.ps1 [-Manifest docs/cohorts/cohort-1.json]
.DESCRIPTION
  Registers Kohort 1 with the evaluator, freezes it on the ops side, runs the parity replay on R, puts it into paper
  trading in its own run and adds the D29 RiskConfig version. Each step asks for 'yes' and stops on any failure:
    1. Preflight: the manifest is committed and names the running evaluator release; src/ is clean.
    2. Trading.Host restarts without live data (no cycle runs against a half-registered cohort).
    3. Registration: the evaluator serves on the internal eval network; Research.Host's submit-cohort job sends the
       manifest with the submit-only token (POST /cohorts). registered_at, A_1 and the window end come from the server.
    4. freeze-import, then parity-m5 (dry); only if L1/L2 are equal and reconciliation is clean: parity-m5 --record
       (Frozen -> PaperTrading).
    5. Trading.Host is rebuilt and started with live data (scripts/m5-paper-start.ps1; its own confirmation).
    6. D29: a RiskConfig version with the cluster cap 0.70 / 0.60 / 0.54 (a tightening: confirm = false), unless the
       current version already carries it.
    7. Checks: lifecycle (three PaperTrading members of the new cohort), status, risk config. A report with operational
       and registration fields only (no price, PnL or position) is written to the report directory with its SHA-256.
  -CohortId <id>: the manifest is already registered (registration is append-only and is never sent twice). Step 3
  is replaced by a check of the server's own record of that cohort (member definition hashes, release, kind) and
  its server-assigned fields are reported; the run continues with steps 4-7.
#>
param([string] $Manifest = 'docs/cohorts/cohort-1.json', [long] $CohortId = 0)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$report = [System.Collections.Generic.List[string]]::new()
function Note([string] $Line) { $report.Add($Line); Write-Host $Line }
function Step([string] $Title) { Write-Host ''; Write-Host "==> $Title" -ForegroundColor Cyan }

# 1. Preflight -------------------------------------------------------------------------------------------------
Step '1/7 Preflight'
if (-not (Test-Path $Manifest)) { throw "$Manifest does not exist." }
Assert-CleanFile $Manifest
if (-not (git ls-files -- $Manifest)) { throw "$Manifest is not committed." }
if (git status --porcelain -- src) { throw 'src/ has uncommitted changes.' }
$settings = Read-DotEnv
$digest = $settings['PI_EVALUATOR_DIGEST']
$document = Get-Content $Manifest -Raw | ConvertFrom-Json
if ($document.imageDigest -ne $digest) { throw "The manifest names $($document.imageDigest), the running evaluator is $digest." }
$commit = (git log -1 --format=%H -- $Manifest).Trim()
Note "manifest=$Manifest commit=$commit sha256=$(Get-Sha256Text (Get-Content $Manifest -Raw))"
Note "members=$(@($document.strategies | ForEach-Object { $_.definition.name }) -join ',') underpowered=$($document.power.underpowered)"
if ($CohortId -gt 0) {
    Confirm-Interactive "Continue the activation of the already registered cohort $CohortId (no new registration)?"
} else {
    Confirm-Interactive 'Register this manifest as a new cohort (append-only, cannot be withdrawn) and activate it?'
}

# 2. Trading.Host without live data -------------------------------------------------------------------------------
Step '2/7 Trading.Host without live data'
$env:PI_TRADING_LIVE_DATA = 'disabled'
Invoke-Native docker @('compose', '--profile', 'trading', 'up', '-d', 'trading-host')
if ($LASTEXITCODE -ne 0) { throw 'Trading.Host did not restart.' }

# 3. Registration ---------------------------------------------------------------------------------------------------
# Note: PowerShell variables are case-insensitive; $cohortId below is the -CohortId parameter.
if ($CohortId -gt 0) {
    Step "3/7 Registration already done: checking the server's record of cohort $CohortId (nothing is sent)"
    $sql = "SELECT manifest_hash, kind, engine_sha, image_digest, registered_at, a_c, window_end, protocol_complete, underpowered, " +
           "(SELECT string_agg(s ->> 'definitionHash', ',' ORDER BY o) FROM jsonb_array_elements(manifest -> 'strategies') WITH ORDINALITY AS t(s, o)) " +
           "FROM results.cohort_manifests WHERE cohort_id = $CohortId;"
    $row = @(Invoke-Psql 'holdout' $sql)
    if ($row.Count -ne 1 -or -not $row[0]) { throw "The holdout registry has no cohort $CohortId." }
    $r = $row[0].Trim().Split('|')
    $expected = @($document.strategies | ForEach-Object { $_.definitionHash }) -join ','
    if ($r[9] -ne $expected) { throw "Cohort $CohortId has other members than $Manifest." }
    if ($r[2] -ne $document.engineSha -or $r[3] -ne $document.imageDigest) { throw "Cohort $CohortId names another evaluator release." }
    if ($r[1] -ne 'normal') { throw "Cohort $CohortId is not a normally registered cohort ($($r[1]))." }
    Note "cohortId=$CohortId"
    Note 'registration=earlier run of this script (server record checked; not sent again)'
    Note "manifestHash=$($r[0])"
    Note "registeredAt=$($r[4])"
    Note "startsAt=$($r[5])"
    Note "windowEnd=$($r[6])"
    Note "protocolComplete=$($r[7]) underpowered=$($r[8])"
} else {
Step '3/7 Registration (POST /cohorts, submit-only token)'
$env:PI_EVALUATOR_DIGEST = $digest
Invoke-Native docker @('compose', '--profile', 'research', 'build', 'research-host')
if ($LASTEXITCODE -ne 0) { throw 'Research.Host image build failed.' }
Invoke-Native docker @('compose', '--profile', 'evaluator', 'up', '-d', 'holdout-evaluator')
if ($LASTEXITCODE -ne 0) { throw 'The evaluator did not start.' }
$mount = "$((Resolve-Path (Split-Path $Manifest -Parent)).Path):/cohorts:ro"
$fields = @{}
try {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        $lines = @(Invoke-Native docker @('compose', '--profile', 'research', 'run', '--rm', '--no-deps', '-v', $mount, 'research-host',
            'job', 'submit-cohort', "--Registration:Manifest=/cohorts/$(Split-Path $Manifest -Leaf)"))
        $exit = $LASTEXITCODE
        $lines | ForEach-Object { Write-Host "  $_" }
        if ($exit -ne 4) { break }
        Start-Sleep -Seconds 3   # the evaluator is still starting
    }
} finally {
    Invoke-Native docker @('compose', '--profile', 'evaluator', 'stop', 'holdout-evaluator') | Out-Null
}
if ($exit -ne 0) { throw "Registration failed (exit $exit); nothing else was changed. If the registry rejected a manifest that is already registered (409), rerun with -CohortId <id>." }
# Output lines may carry leading spaces or a trailing carriage return (docker compose run). -cmatch: the case-insensitive
# -match fails on 'cohortId' under tr-TR ('I' folds to the dotless 'ı', outside [A-Za-z]).
foreach ($line in $lines) { if ("$line" -cmatch '^\s*([A-Za-z]+)=(.*?)\s*$') { $fields[$Matches[1]] = $Matches[2] } }
if ($fields['cohortId'] -cnotmatch '^\d+$') {
    throw 'The evaluator returned no readable cohort id. The cohort may be registered: rerun with -CohortId <id> (see the output above).'
}
$cohortId = [long]$fields['cohortId']
foreach ($name in 'cohortId', 'status', 'registeredAt', 'startsAt', 'windowEnd', 'eligibleForValidation', 'manifestHash') { Note "$name=$($fields[$name])" }
}

# 4. Freeze and parity ----------------------------------------------------------------------------------------------
Step "4/7 freeze-import and parity replay on R (cohort $cohortId)"
$research = Join-Path $PSScriptRoot 'm5-research.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $research freeze-import $Manifest $cohortId
if ($LASTEXITCODE -eq 3) { Note "freeze=already frozen (cohort $cohortId; earlier run)" }   # freeze-import refuses a second import
elseif ($LASTEXITCODE -ne 0) { throw 'freeze-import failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File $research parity-m5 $Manifest $cohortId
if ($LASTEXITCODE -ne 0) { throw 'Parity replay failed: the cohort stays Frozen and is not paper traded.' }
Confirm-Interactive 'Parity passed. Record Frozen -> PaperTrading?'
& powershell -NoProfile -ExecutionPolicy Bypass -File $research parity-m5 $Manifest $cohortId --record
if ($LASTEXITCODE -ne 0) { throw 'parity-m5 --record failed.' }
Note 'parity=passed recorded=PaperTrading'

# 5. Trading.Host with live data ------------------------------------------------------------------------------------
Step '5/7 Trading.Host rebuilt with live data'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'm5-paper-start.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Trading.Host did not start with live data.' }

# 6. D29 RiskConfig -------------------------------------------------------------------------------------------------
Step '6/7 D29 cluster cap (RiskConfig version)'
$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$api = 'http://127.0.0.1:55490'
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "$api/health/live" -TimeoutSec 5 | Out-Null; break } catch { Start-Sleep -Seconds 2 }
}
$current = @(Invoke-RestMethod "$api/risk-config" -Headers $headers)
$config = if ($current.Count -gt 0 -and $current[0].config) { $current[0].config } else {
    [pscustomobject]@{ maxOpenPositions = 5; maxAssetWeight = 0.20; maxTotalExposure = 0.80; dailyLossLimit = 0.03; maxDrawdown = 0.15
                       drawdownHaltEnabled = $true; maxPositionLoss = 0.10; experimentalPairsMaxExposure = 0.10; maxDataLagBars = 2 } }
if ($config.clusterCapMaxExposure -eq 0.60 -and $config.clusterCapThreshold -eq 0.70 -and $config.clusterWarningShare -eq 0.54) {
    Note 'riskConfig=D29 already in force'
} else {
    $config | Add-Member -NotePropertyName clusterCapThreshold -NotePropertyValue 0.70 -Force
    $config | Add-Member -NotePropertyName clusterCapMaxExposure -NotePropertyValue 0.60 -Force
    $config | Add-Member -NotePropertyName clusterWarningShare -NotePropertyValue 0.54 -Force
    $body = @{ config = $config; reason = 'P1 / D29 (Kohort 1 activation)'; confirm = $false } | ConvertTo-Json -Depth 4
    Invoke-RestMethod -Method Post "$api/risk-config" -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
    Note 'riskConfig=D29 version added'
}

# 7. Checks ---------------------------------------------------------------------------------------------------------
Step '7/7 Checks'
$lifecycle = @(Invoke-RestMethod "$api/lifecycle" -Headers $headers)
$members = @($lifecycle | Where-Object { $_.cohort_id -eq [long]$cohortId } | Group-Object member_index | ForEach-Object { $_.Group[-1] })
$paper = @($members | Where-Object { $_.to_state -eq 'PaperTrading' }).Count
Note "lifecycle cohort=$cohortId paperTrading=$paper/$($document.strategies.Count)"
$status = Invoke-RestMethod "$api/status" -Headers $headers
Note "killSwitch=$($status[0].kill_switch) lastCycleOutcome=$($status[0].last_cycle.outcome) reconciliationOk=$($status[0].last_reconciliation.ok)"
$risk = @(Invoke-RestMethod "$api/risk-config" -Headers $headers)[0].config
Note "clusterCap=$($risk.clusterCapThreshold)/$($risk.clusterCapMaxExposure) warning=$($risk.clusterWarningShare)"

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$path = Join-Path (Get-ReportDirectory) "m5-cohort1-activation-$stamp.txt"
$text = ($report -join "`n") + "`n"
[System.IO.File]::WriteAllText($path, $text)
Write-Host ''
Write-Host "Report: $path" -ForegroundColor Green
Write-Host "SHA-256: $(Get-Sha256Text $text)"
Write-Host 'Dashboard: http://127.0.0.1:55490/dashboard (token: secrets\ops_api_token).'
if ($paper -ne $document.strategies.Count) { Write-Host 'Warning: not every member is in PaperTrading.' -ForegroundColor Yellow; exit 1 }
