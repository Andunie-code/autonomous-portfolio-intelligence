<#
.SYNOPSIS
  Applies the ops migrations as ops_owner through the loopback port (docs/m5-protocol.md §4.2). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m5-migrate.ps1
  Holdout migrations are applied by the evaluator itself (scripts/m5-deploy.ps1).
#>
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

Confirm-Interactive 'Apply the pending ops migrations as ops_owner?'
$env:ConnectionStrings__Ops = Get-OpsConnection 'ops_owner'
try {
    dotnet run --project src/Tools/DbMigrator -c Release
    exit $LASTEXITCODE
} finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
