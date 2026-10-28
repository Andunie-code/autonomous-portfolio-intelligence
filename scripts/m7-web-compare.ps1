<#
.SYNOPSIS
  M7 Gate C6 (docs/m7-protocol.md §9): web vs. no-web comparison as research_app on the ops database.
    powershell -ExecutionPolicy Bypass -File scripts/m7-web-compare.ps1 --baseline <list.json> --baseline-hash <h> [--web <list.json> --web-hash <h>] [--author <name>]
.DESCRIPTION
  Every point is a trial on the research window and enters the global counter (T18); run it only after the user has
  approved the hypothesis lists. While FindingExtraction is not qualified the web arm is not run and the report says
  web research is closed (D20). Writes docs/m7-web-comparison.md. The user's run only (CLAUDE.md, m7-protocol §11).
#>
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root
$env:ConnectionStrings__Ops = Get-OpsConnection 'research_app'
try {
    foreach ($option in @('--web', '--baseline')) {
        $index = [array]::IndexOf($args, $option)
        if ($index -ge 0 -and $index + 1 -lt $args.Count) { Assert-CleanFile $args[$index + 1] }
    }

    dotnet run --project src/Tools/BacktestCli -c Release -- web-compare @args
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ConnectionStrings__Ops -ErrorAction SilentlyContinue
}
