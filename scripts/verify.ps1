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
Invoke-Step 'Tests' { dotnet test PortfolioIntelligence.slnx -c Release --no-build --nologo }
Invoke-Step 'Secret scan' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'scan-secrets.ps1') }

Write-Host 'VERIFY PASSED' -ForegroundColor Green
