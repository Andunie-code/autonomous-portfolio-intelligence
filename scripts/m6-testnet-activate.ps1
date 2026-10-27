<#
.SYNOPSIS
  M6 Gate C step C4 (docs/m6-protocol.md §6.2, §4.4; V10, D30). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m6-testnet-activate.ps1 -Strategy <name> -Reason "<text>"
    powershell -ExecutionPolicy Bypass -File scripts/m6-testnet-activate.ps1 -Rebaseline -Reason "<text>"
.DESCRIPTION
  -Strategy: shows the strategy's D30 progress, asks for an interactive confirmation and sends
  POST /strategies/<name>/testnet-activate with confirm = true (the user's Ops API session). Trading.Host refuses it
  (409) unless the strategy is PaperTrading, Testnet is enabled and D30 holds. The next cycle opens the cohort's
  Testnet run with a balance baseline; the paper run goes on unchanged. Testnet needs no validation; a strategy that
  is not Validated carries the "experimental" label.
  -Rebaseline: after a Testnet reset (about monthly, S-B19), closes the open Testnet runs and starts new ones with a
  new balance baseline at the next cycle.
  Piped input is refused.
#>
param([string] $Strategy, [switch] $Rebaseline, [string] $Reason)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

if (-not $Reason) { throw 'A -Reason is required.' }
if ([Console]::IsInputRedirected) { throw 'This step needs an interactive confirmation; input is redirected.' }
$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$base = 'http://127.0.0.1:55490'

function Post-Json([string] $Path, [hashtable] $Body) {
    try {
        return Invoke-RestMethod -Method Post "$base$Path" -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress)
    } catch {
        $message = $_.ErrorDetails.Message
        if (-not $message) { $message = $_.Exception.Message }
        throw "Ops API refused the request: $message"
    }
}

if ($Rebaseline) {
    Confirm-Interactive 'Close the open Testnet runs and start new ones with a new balance baseline at the next cycle?'
    Post-Json '/testnet/rebaseline' @{ reason = $Reason; confirm = $true } | Out-Null
    Write-Host 'Rebaseline requested; it takes effect at the next cycle (HH:00:15 UTC).'
    exit 0
}

if (-not $Strategy) { throw 'Give -Strategy <name> or -Rebaseline.' }
$promotion = Invoke-RestMethod "$base/promotion" -Headers $headers
$status = @($promotion.strategies) | Where-Object { $_.name -eq $Strategy } | Select-Object -Last 1
if (-not $status) { throw "Unknown strategy $Strategy." }
Write-Host ("strategy={0} state={1} days={2}/{3} bars={4}/{5} criticalErrors7d={6} outageRatio={7} eligible={8} experimental={9}" -f `
    $status.name, $status.state, $status.days, $promotion.minimumDays, $status.bars, $promotion.minimumBars, $status.criticalErrors7d, $status.outageRatio, $status.eligible, $status.experimental)
if (-not $status.eligible) { throw "D30 is not met: $(@($status.missing) -join ', ')." }

Confirm-Interactive "Move $Strategy from PaperTrading to TestnetActive (real Testnet orders with virtual funds; experimental=$($status.experimental))?"
Post-Json "/strategies/$Strategy/testnet-activate" @{ reason = $Reason; confirm = $true } | Out-Null
Write-Host "$Strategy is TestnetActive. The next cycle opens its cohort's Testnet run; check with scripts/m6-update.ps1 -ChecksOnly."
