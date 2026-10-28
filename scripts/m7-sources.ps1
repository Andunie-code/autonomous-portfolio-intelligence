<#
.SYNOPSIS
  M7 sources command line (docs/m7-protocol.md §10): arXiv search, fetch and provenance on the local database.
.DESCRIPTION
  Passes its arguments to src/Tools/SourcesCli as research_app on the ops database, e.g.
    scripts/m7-sources.ps1 search --allow-network --terms "momentum,crypto" --max 10
    scripts/m7-sources.ps1 fetch --allow-network --ids 2301.01234v2,2210.04321v1
    scripts/m7-sources.ps1 docs
    scripts/m7-sources.ps1 provenance-report
    scripts/m7-sources.ps1 extract --allow-network     (only once FindingExtraction is qualified and configured)
  The user's runs only (CLAUDE.md, m7-protocol §11): network commands need the flag, an interactive terminal and the
  confirmation word. Only one sources run at a time (lock file next to the cache), so the per-host interval holds.
  If the execution policy blocks scripts: powershell -ExecutionPolicy Bypass -File scripts/m7-sources.ps1 <args>
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

. "$PSScriptRoot\lib\m5-common.ps1"
$env:ConnectionStrings__Ops = Get-OpsConnection 'research_app'
$cacheRoot = if ($env:PI_SOURCE_CACHE) { $env:PI_SOURCE_CACHE } else { Join-Path $env:LOCALAPPDATA 'PortfolioIntelligence\source-cache' }
New-Item -ItemType Directory -Force $cacheRoot | Out-Null
$lockPath = Join-Path $cacheRoot 'run.lock'
$lock = $null
try {
    try {
        $lock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch {
        throw "Another sources run is in progress ($lockPath); wait for it to finish."
    }

    dotnet run --project src/Tools/SourcesCli -c Release -- @args
    exit $LASTEXITCODE
}
finally {
    if ($lock) { $lock.Dispose() }
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
