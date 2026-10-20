<#
.SYNOPSIS
  M5 Gate C step 3: H-ENV on the real local environment (docs/m5-protocol.md §6; testing-strategy §7.2). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-henv.ps1 [-Cohort <id>]
.DESCRIPTION
  Runs H5, H6, H6b, H14, H19-H28 from their network positions and writes a report of "id PASS|FAIL check reason"
  lines (no data, no values, no passwords) to %LOCALAPPDATA%\PortfolioIntelligence\reports, with its SHA-256.
  H19 runs on the scratch database holdout_henv (created and dropped here). -Cohort repeats H22 on the real holdout
  database for that cohort after the Kohort 0 seed (nothing is written). Trading.Host must be running (H23, H24).
  If every line passes, set the flag (interactive, audited):
    docker compose --profile evaluator run --rm holdout-evaluator henv-mark-verified <digest> <report hash>
#>
param([string] $Cohort = '')
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$settings = Read-DotEnv
$digest = $settings['PI_EVALUATOR_DIGEST']
if (-not $digest) { throw 'PI_EVALUATOR_DIGEST is not set; run scripts/m5-deploy.ps1 first.' }
$env:PI_EVALUATOR_DIGEST = $digest
Confirm-Interactive 'Run H-ENV (connection attempts from every network position; holdout_henv is created and dropped)?'
$lines = New-Object System.Collections.Generic.List[string]
function Add-Lines($output) { foreach ($l in @($output)) { if ("$l" -match '^(H\d+[a-z]?) (PASS|FAIL) ') { $lines.Add("$l".Trim()) } } }
function Add-Result([string] $Id, [string] $Check, [bool] $Pass, [string] $Reason) { $lines.Add("$Id $(if ($Pass) { 'PASS' } else { 'FAIL' }) $Check $Reason") }

# H19: scratch database with the real image, logins and pg_hba.
Invoke-Psql 'postgres' @'
DROP DATABASE IF EXISTS holdout_henv;
CREATE DATABASE holdout_henv OWNER eval_owner;
REVOKE CONNECT, TEMPORARY ON DATABASE holdout_henv FROM PUBLIC;
GRANT CONNECT ON DATABASE holdout_henv TO eval_registry, eval_diagnostic, eval_prospective;
'@ -Quiet
$env:PI_EVAL_DATABASE = 'holdout_henv'
$env:PI_EVALUATOR_WINDOW_SOURCE = 'synthetic'
try {
    Add-Lines (Invoke-Native docker @('compose', '--profile', 'evaluator', 'run', '--rm', 'holdout-evaluator', 'henv-selftest'))
} finally {
    Remove-Item Env:PI_EVAL_DATABASE, Env:PI_EVALUATOR_WINDOW_SOURCE -ErrorAction SilentlyContinue
}

# H20: the running image digest has its release record.
Add-Lines (Invoke-Native docker @('compose', '--profile', 'evaluator', 'run', '--rm', 'holdout-evaluator', 'release-check', $digest))

# H6, H6b, H21 (outside position), H25 (database part): Research.Host position. H14: Trading.Host position.
Add-Lines (Invoke-Native docker @('compose', '--profile', 'henv', 'run', '--rm', 'henv-probe-research', '--ops-db', 'ops', '--holdout-dbs', 'holdout,holdout_henv'))
Add-Lines (Invoke-Native docker @('compose', '--profile', 'henv', 'run', '--rm', 'henv-probe-trading'))
# H21, H22 (real database, optional), H26: the evaluator's own position.
$probe = @('compose', '--profile', 'evaluator', 'run', '--rm', '--entrypoint', 'dotnet', 'holdout-evaluator', '/app/probe/PortfolioIntelligence.HoldoutEvaluator.EnvironmentProbe.dll', 'evaluator', '--ops-db', 'ops', '--holdout-dbs', 'holdout')
if ($Cohort) { $probe += @('--cohort', $Cohort) }
Add-Lines (Invoke-Native docker $probe)
# H25: no status_relay credential inside the Research.Host container.
Add-Lines (Invoke-Native docker @('compose', '--profile', 'research', 'run', '--rm', 'research-host', 'job', 'henv-credentials'))

Invoke-Psql 'postgres' 'DROP DATABASE IF EXISTS holdout_henv;' -Quiet

# H5, H23 (database part), H26: the host through the loopback port. Passwords come from the secret files.
$hostSecrets = Join-Path $env:TEMP ("pi-henv-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hostSecrets | Out-Null
try {
    foreach ($role in @('research_app', 'market_import', 'trading_app', 'ops_owner')) { Copy-Item (Join-Path $script:OpsSecrets $role) $hostSecrets }
    foreach ($role in @('eval_registry', 'eval_owner')) { Copy-Item (Join-Path $script:HoldoutSecrets $role) $hostSecrets }
    [System.IO.File]::WriteAllText((Join-Path $hostSecrets 'pi_dev'), (Get-SuperuserPassword))
    $port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
    Add-Lines (Invoke-Native dotnet @('run', '--project', 'src/HoldoutEvaluator/EnvironmentProbe', '-c', 'Release', '--', 'host', '--host', '127.0.0.1', '--port', $port, '--secrets', $hostSecrets, '--ops-db', 'ops', '--holdout-dbs', 'holdout,holdout_henv,holdout_synth'))
} finally {
    Remove-Item $hostSecrets -Recurse -Force
}

# H23 (API part), H24: Ops API on loopback.
$token = Get-Secret $script:OpsSecrets 'ops_api_token'
function Invoke-Ops([string] $Method, [string] $Path, [string] $Body, [bool] $WithToken) {
    $headers = @{}
    if ($WithToken) { $headers['Authorization'] = "Bearer $token" }
    # Body and content type only with a body: Windows PowerShell 5.1 refuses a body on GET (ProtocolViolationException),
    # and a [string] parameter turns $null into "", so the check is on the length.
    $request = @{ UseBasicParsing = $true; Method = $Method; Uri = "http://127.0.0.1:55490$Path"; Headers = $headers; TimeoutSec = 10 }
    if ($Body.Length -gt 0) { $request['Body'] = $Body; $request['ContentType'] = 'application/json' }
    try {
        $response = Invoke-WebRequest @request
        return [int]$response.StatusCode
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}
Add-Result 'H23' 'ops-api-risk-config-without-token-refused' ((Invoke-Ops 'POST' '/risk-config' '{}' $false) -eq 401) 'http'
Add-Result 'H24' 'ops-api-pnl-by-cohort-visible' ((Invoke-Ops 'GET' '/pnl?by=cohort' $null $true) -eq 200) 'http'
foreach ($path in @('/cohorts', '/results', '/manifests', '/validation')) {
    Add-Result 'H24' "ops-api-no-route-$($path.Trim('/'))" ((Invoke-Ops 'POST' $path '{}' $true) -eq 404) 'http'
}

# H27: limits on every service; no holdout data or dump in the repository.
$env:PI_EVALUATOR_DIGEST = $digest
$config = docker compose --profile evaluator --profile trading --profile research --profile henv config --format json | ConvertFrom-Json
foreach ($service in $config.services.PSObject.Properties) {
    $limited = $service.Value.mem_limit -and $service.Value.cpus
    Add-Result 'H27' "limits-$($service.Name)" ([bool]$limited) 'compose'
}
Add-Result 'H27' 'evaluator-publishes-no-port' (-not $config.services.'holdout-evaluator'.ports) 'compose'
$stray = Get-ChildItem -Path $script:Root -Recurse -File -Include '*.dump', '*.enc', '*.zip', '*.csv', '*.parquet' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' }
Add-Result 'H27' 'no-market-or-holdout-files-in-repository' (@($stray).Count -eq 0) "files:$(@($stray).Count)"

# H28: the Claude Code deny rules (configuration check, not a trust boundary).
$deny = (Get-Content '.claude/settings.json' -Raw | ConvertFrom-Json).permissions.deny
foreach ($rule in @('Bash(docker exec*)', 'Bash(docker compose exec*)', 'Bash(docker cp*)', 'PowerShell(docker exec*)', 'PowerShell(docker compose exec*)', 'PowerShell(docker cp*)', 'PowerShell(*-File*m5-henv*)', 'WebFetch(domain:data-api.binance.vision)')) {
    Add-Result 'H28' "deny-$rule" ($deny -contains $rule) 'settings'
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$report = ($lines -join "`n") + "`n"
$hash = Get-Sha256Text $report
$path = Join-Path (Get-ReportDirectory) "m5-henv-$stamp.txt"
[System.IO.File]::WriteAllText($path, $report)
$failed = @($lines | Where-Object { $_ -match ' FAIL ' }).Count
Write-Host $report
Write-Host "H-ENV report: $path"
Write-Host "Report SHA-256: $hash"
Write-Host "Lines: $($lines.Count), failures: $failed"
if ($failed -eq 0) {
    Write-Host "All passed. To set the flag: docker compose --profile evaluator run --rm holdout-evaluator henv-mark-verified $digest $hash"
} else {
    Write-Host 'Do not set the flag; fix and run again.'
    exit 1
}
