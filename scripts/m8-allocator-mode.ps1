<#
.SYNOPSIS
  Allocator mode transition (docs/m8-protocol.md §4.2; ADR-0023). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m8-allocator-mode.ps1 -Mode Shadow -ProtocolHash <hash> -Reason "<text>"
    powershell -ExecutionPolicy Bypass -File scripts/m8-allocator-mode.ps1 -Mode Active -ProtocolHash <hash> -QualificationId <id> -Reason "<text>"
    powershell -ExecutionPolicy Bypass -File scripts/m8-allocator-mode.ps1 -Mode Disabled -Reason "<text>"
.DESCRIPTION
  Shows the current mode, asks for an interactive confirmation and sends POST /allocator/mode with confirm = true (the
  user's Ops API session). Trading.Host refuses it (409) unless: Shadow names a registered allocator protocol
  (scripts/m4-ai.ps1 alloc-register); Active comes from Shadow with a Passed AllocationProposal qualification of a window
  of at least 28 days. Disabled is always allowed. Active adjusts only Validated strategies; until one exists it changes nothing.
  Piped input is refused.
#>
param([ValidateSet('Disabled', 'Shadow', 'Active')] [string] $Mode, [string] $ProtocolHash, [long] $QualificationId = 0, [string] $Reason)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

if (-not $Mode) { throw 'A -Mode (Disabled, Shadow or Active) is required.' }
if (-not $Reason) { throw 'A -Reason is required.' }
if ([Console]::IsInputRedirected) { throw 'This step needs an interactive confirmation; input is redirected.' }
$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$base = 'http://127.0.0.1:55490'

$current = Invoke-RestMethod "$base/allocator" -Headers $headers
Write-Host "allocator mode=$($current.mode) band=$($current.band.version)"
$question = switch ($Mode) {
    'Shadow' { "Switch the allocator to Shadow under protocol $ProtocolHash (model calls in qualification mode, real orders stay T11)?" }
    'Active' { "Switch the allocator to Active with qualification $QualificationId (overrides applied to Validated strategies only)?" }
    default { 'Disable the allocator (every decision T11, no model calls)?' }
}
Confirm-Interactive $question

$body = @{ mode = $Mode; reason = $Reason; confirm = $true }
if ($ProtocolHash) { $body.protocolHash = $ProtocolHash }
if ($QualificationId -gt 0) { $body.qualificationId = $QualificationId }
try {
    Invoke-RestMethod -Method Post "$base/allocator/mode" -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) | Out-Null
} catch {
    $message = $_.ErrorDetails.Message
    if (-not $message) { $message = $_.Exception.Message }
    throw "Ops API refused the request: $message"
}
Write-Host "Allocator mode is now $Mode; it applies from the next cycle (HH:00:15 UTC)." -ForegroundColor Green
