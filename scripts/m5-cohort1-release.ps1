<#
.SYNOPSIS
  M5b Gate C step 1 (ADR-0019; docs/cohort-1-preregistration.md §3). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-cohort1-release.ps1
.DESCRIPTION
  Prints the evaluator release that Kohort 1's manifest must name: the running evaluator image digest
  (PI_EVALUATOR_DIGEST in .env) and the engine git SHA registered for it in results.evaluator_releases, plus whether
  H-ENV verified that digest. Reads one release row of the holdout database through the local superuser socket; it
  reads no window data, no result and no secret. The two values are not secrets: pass them to Claude Code, which
  generates docs/cohorts/cohort-1.json with scripts/m5-research.ps1 cohort-manifest.
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$settings = Read-DotEnv
$digest = $settings['PI_EVALUATOR_DIGEST']
if (-not $digest) { throw '.env has no PI_EVALUATOR_DIGEST (scripts/m5-deploy.ps1 sets it).' }
if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'PI_EVALUATOR_DIGEST is not an image digest.' }

$sql = "SELECT engine_sha, results.environment_verified('$digest') FROM results.evaluator_releases WHERE image_digest = '$digest';"
$row = @(Invoke-Psql 'holdout' $sql)
if ($row.Count -ne 1 -or -not $row[0]) { throw "No evaluator release is registered for $digest (scripts/m5-deploy.ps1)." }
$engineSha, $verified = $row[0].Split('|')

Write-Host ''
Write-Host 'Kohort 1 evaluator release (give these two lines to Claude Code):' -ForegroundColor Cyan
Write-Host "  --image-digest $digest"
Write-Host "  --engine-sha $engineSha"
Write-Host ''
Write-Host "HoldoutEnvironmentVerified for this digest: $verified"
if ($verified -ne 't') { Write-Host 'Warning: H-ENV has not verified this digest; Kohort 1 is operational only either way.' -ForegroundColor Yellow }
