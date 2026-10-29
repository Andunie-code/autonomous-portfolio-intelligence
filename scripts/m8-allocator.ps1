<#
.SYNOPSIS
  Shadow-window report and qualification (docs/m8-protocol.md §6–§7, §9). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m8-allocator.ps1 alloc-report --protocol docs/<protocol>.md
    powershell -ExecutionPolicy Bypass -File scripts/m8-allocator.ps1 alloc-qualify --protocol docs/<protocol>.md [--close-incomplete]
.DESCRIPTION
  The allocator tables are in the trading schema, which research_app cannot read, and trading_app logs in only from the
  Trading.Host container (pg_hba). These two commands therefore run as ops_owner through the loopback port, as
  scripts/m5-migrate.ps1 does; they read trading.allocator_* and the cost ledger, and alloc-qualify adds one row to
  ai.task_qualifications. No network call. The console shows operational fields only; the user-only performance part is
  written to a separate file in the reports directory (outside the repository).
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

$command = $args | Select-Object -First 1
if ($command -notin @('alloc-report', 'alloc-qualify')) { throw 'Only alloc-report and alloc-qualify run here (others: scripts/m4-ai.ps1).' }
if ($command -eq 'alloc-qualify') {
    if ([Console]::IsInputRedirected) { throw 'alloc-qualify needs an interactive confirmation; input is redirected.' }
    Confirm-Interactive 'Close the qualification window and record its TaskQualification (append-only, cannot be undone)?'
}
$env:ConnectionStrings__Ops = Get-OpsConnection 'ops_owner'
try {
    dotnet run --project src/Tools/AiCli -c Release -- @args
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
