<#
.SYNOPSIS
  Single-command local verification (T16): build, all tests (incl. architecture and Testcontainers
  integration tests), secret scan. Exits non-zero on the first failing step. Requires Docker.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name" -ForegroundColor Red
        exit 1
    }
}

Invoke-Step 'Docker available' { docker info --format '{{.ServerVersion}}' | Out-Null }
Invoke-Step 'Build (Release, warnings as errors)' { dotnet build PortfolioIntelligence.slnx -c Release --nologo }
# Test projects run one after another, not in parallel: each starts its own Testcontainers PostgreSQL instances, and
# running all of them at once exhausts the memory of the 16 GB local machine (ADR-0013). Counts come from TRX files
# (independent of the console language).
function Invoke-Tests {
    $results = Join-Path ([System.IO.Path]::GetTempPath()) ("pi-verify-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $results | Out-Null
    $total = 0; $passed = 0; $failed = 0; $failedProjects = @()
    try {
        foreach ($project in Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*.Tests.csproj' | Sort-Object Name) {
            $name = $project.BaseName
            Write-Host "--- $name" -ForegroundColor DarkCyan
            dotnet test $project.FullName -c Release --no-build --nologo --results-directory $results --logger "trx;LogFileName=$name.trx"
            $exit = $LASTEXITCODE
            $trx = Join-Path $results "$name.trx"
            if (-not (Test-Path $trx)) { Write-Host "No result file for $name." -ForegroundColor Red; $failedProjects += $name; continue }
            $counters = ([xml](Get-Content -Raw $trx)).TestRun.ResultSummary.Counters
            $total += [int]$counters.total; $passed += [int]$counters.passed; $failed += [int]$counters.failed
            Write-Host ("    {0}: {1} passed, {2} failed, {3} total" -f $name, $counters.passed, $counters.failed, $counters.total)
            if ($exit -ne 0 -or [int]$counters.failed -gt 0) { $failedProjects += $name }
        }
    } finally {
        Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
    }

    Write-Host ("Tests: {0} passed, {1} failed, {2} total" -f $passed, $failed, $total)
    if ($failedProjects.Count -gt 0 -or $passed -ne $total) {
        Write-Host "Failed test projects: $($failedProjects -join ', ')" -ForegroundColor Red
        $global:LASTEXITCODE = 1
    } else {
        $global:LASTEXITCODE = 0
    }
}

Invoke-Step 'Tests (one project at a time)' { Invoke-Tests }
Invoke-Step 'Secret scan' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'scan-secrets.ps1') }

Write-Host 'VERIFY PASSED' -ForegroundColor Green
