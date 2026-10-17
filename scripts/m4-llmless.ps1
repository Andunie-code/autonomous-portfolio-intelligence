<#
.SYNOPSIS
  M4 Gate B: the pre-registered LLM-less research cycle (docs/m4-protocol.md v1). Local database only; no network
  and no AI call.
.DESCRIPTION
  Verifies docs/m4-hypotheses.v1.json against the pre-registered list hash, runs the 96 grid points of the
  12 hypotheses through the Researcher tools on the 20 core pairs (research window, < C), gates each hypothesis and
  writes docs/m4-llmless-results.md. Records hypotheses, trials and experiments in the dev database. The protocol
  allows exactly one run (m4-protocol §7). Apply migrations first: scripts/dev-up.ps1.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m4-llmless.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env not found; run scripts/dev-up.ps1 first.' }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
}
$port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
$env:ConnectionStrings__Ops = "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$($settings['PI_DEV_DB_PASSWORD'])"
# Engine identity: HEAD plus the git tree hash of src/ as it is now (a temporary index; the real index is untouched).
# After the code is committed, `git rev-parse <commit>:src` must give the same tree hash.
$tmpIndex = Join-Path ([System.IO.Path]::GetTempPath()) "pi-m4-index-$PID"
try {
    $env:GIT_INDEX_FILE = $tmpIndex
    git read-tree HEAD
    git add -A src
    $srcTree = (git write-tree --prefix=src/).Trim()
}
finally {
    Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    Remove-Item $tmpIndex -ErrorAction SilentlyContinue
}
$env:PI_ENGINE_SOURCE = "$((git rev-parse --short HEAD).Trim())+src-tree:$srcTree"
Write-Host "Engine: $env:PI_ENGINE_SOURCE"
try {
    dotnet run --project src/Tools/BacktestCli -c Release -- research-m4
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
    Remove-Item Env:PI_ENGINE_SOURCE -ErrorAction SilentlyContinue
}
