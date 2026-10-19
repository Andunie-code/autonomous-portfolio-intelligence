<#
.SYNOPSIS
  M5 Gate C step 4 (architecture §6.3, V13 exception; docs/m5-protocol.md §6.3, user decision A-2). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-seed-cohort0.ps1
.DESCRIPTION
  Registers Kohort 0 in the real holdout database with its original registered_at (the commit time of
  docs/cohorts/cohort-0.json, be2bce3) and links it to the running evaluator release with an audited equivalence
  record. The evidence is the regression lock run here: the four M2 result hashes reproduced by the current engine
  (scripts/m3-parity.ps1 on the research window, research_app). The manifest is not changed.
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$manifest = 'docs/cohorts/cohort-0.json'
Assert-CleanFile $manifest
$registeredAt = (git log -1 --format=%cI -- $manifest).Trim()
$settings = Read-DotEnv
$digest = $settings['PI_EVALUATOR_DIGEST']
if (-not $digest) { throw 'PI_EVALUATOR_DIGEST is not set; run scripts/m5-deploy.ps1 first.' }

$parity = & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'm5-research.ps1') parity-m3
if ($LASTEXITCODE -ne 0) { throw 'The M2 regression lock failed; no equivalence can be recorded.' }
$evidence = @{
    kind = 'engine-equivalence'
    check = 'parity-m3: the four M2 result hashes reproduced by the coded and DSL baselines'
    engineSha = (git rev-parse HEAD).Trim()
    output = (@($parity) | Where-Object { $_ -match ' -> OK$' })
    outputSha256 = Get-Sha256Text ((@($parity) -join "`n"))
} | ConvertTo-Json -Compress
$evidenceDir = Join-Path $env:TEMP ("pi-seed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
try {
    [System.IO.File]::WriteAllText((Join-Path $evidenceDir 'evidence.json'), $evidence)
    Copy-Item $manifest (Join-Path $evidenceDir 'cohort-0.json')
    $env:PI_EVALUATOR_DIGEST = $digest
    Write-Host "Kohort 0: registered_at $registeredAt, evaluating release $digest."
    docker compose --profile evaluator run --rm -v "${evidenceDir}:/seed:ro" holdout-evaluator seed-cohort0 /seed/cohort-0.json $registeredAt /seed/evidence.json
    if ($LASTEXITCODE -ne 0) { throw 'Seed failed.' }
} finally {
    Remove-Item $evidenceDir -Recurse -Force
}
Write-Host 'Next (Claude Code may run these on the research side): m5-research.ps1 freeze-import docs/cohorts/cohort-0.json <cohort id>, then parity-m5.'
Write-Host 'Repeat H22 on the real database: scripts/m5-henv.ps1 -Cohort <cohort id>.'
