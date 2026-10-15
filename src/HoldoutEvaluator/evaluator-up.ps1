<#
.SYNOPSIS
  Builds and starts the local holdout evaluator in synthetic mode (M3). Run by the user, interactively (T19).
.DESCRIPTION
  1. Creates src/HoldoutEvaluator/.env.evaluator (git-ignored) with the connection strings and two random API
     tokens if it does not exist. Holdout secret names live only in this directory (ADR-0013).
  2. Starts the dev database and the evaluator container (Compose profile "evaluator", 127.0.0.1:55480).
  The evaluator creates and migrates the synthetic database holdout_synth itself. No real holdout data exists in M3.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File src/HoldoutEvaluator/evaluator-up.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}

function New-Token {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes) -replace '[+/=]', '')
}

$evaluatorEnv = Join-Path $PSScriptRoot '.env.evaluator'
if (-not (Test-Path $evaluatorEnv)) {
    $password = $settings['PI_DEV_DB_PASSWORD']
    Set-Content -Path $evaluatorEnv -Encoding ascii -Value @(
        "ConnectionStrings__HoldoutDb=Host=dev-db;Port=5432;Database=holdout_synth;Username=pi_dev;Password=$password",
        "ConnectionStrings__StatusRelay=Host=dev-db;Port=5432;Database=portfolio_dev;Username=pi_dev;Password=$password",
        "Evaluator__SubmitToken=$(New-Token)",
        "Evaluator__ReadStatusToken=$(New-Token)",
        'Evaluator__SyntheticSeed=1',
        'Evaluator__CreateDatabase=true')
    Write-Host 'src/HoldoutEvaluator/.env.evaluator created (git-ignored).'
}

docker compose --profile evaluator up -d --build --wait holdout-evaluator
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }
Write-Host 'Holdout evaluator (synthetic) is up on http://127.0.0.1:55480.'
