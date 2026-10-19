<#
.SYNOPSIS
  M5 Gate C step 2 (T19, ADR-0013 §4; docs/m5-protocol.md §6.1). Run only by the user:
    powershell -ExecutionPolicy Bypass -File scripts/m5-deploy.ps1
.DESCRIPTION
  Builds the evaluator, Trading.Host and Research.Host images from a clean working tree, records the evaluator image
  digest as PI_EVALUATOR_DIGEST, migrates the holdout database as eval_owner from the evaluator's own network
  position and registers the EvaluatorRelease (digest, engine git SHA). A new digest is not verified until H-ENV
  passes for it (results.environment_verified(digest)).
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

if (git status --porcelain -- src) { throw 'src/ has uncommitted changes; the release must correspond to a commit.' }
$engineSha = (git rev-parse HEAD).Trim()
Confirm-Interactive "Build the M5 images at $engineSha and register the evaluator release?"

docker compose --profile evaluator --profile trading --profile research build
if ($LASTEXITCODE -ne 0) { throw 'Image build failed.' }
$digest = (docker image inspect portfolio-intelligence/holdout-evaluator:local --format '{{.Id}}').Trim()
Set-DotEnvValue 'PI_EVALUATOR_DIGEST' $digest
$env:PI_EVALUATOR_DIGEST = $digest

docker compose --profile evaluator run --rm holdout-evaluator migrate
if ($LASTEXITCODE -ne 0) { throw 'Holdout migration failed.' }
docker compose --profile evaluator run --rm holdout-evaluator register-release $digest $engineSha
if ($LASTEXITCODE -ne 0) { throw 'Release registration failed.' }
docker compose --profile evaluator run --rm holdout-evaluator release-check $digest
if ($LASTEXITCODE -ne 0) { throw 'H20 failed.' }

Write-Host "Evaluator release $digest ($engineSha) registered. Next: start Trading.Host without live data, then scripts/m5-henv.ps1:"
Write-Host '  docker compose --profile trading up -d trading-host'
