<#
.SYNOPSIS
  M8 Gate C step C0 (docs/m8-protocol.md §9; ADR-0023). User only, one interactive run:
    powershell -ExecutionPolicy Bypass -File scripts/m8-update.ps1 [-SetKey] [-ClearKey]
.DESCRIPTION
  -SetKey stores the Gemini runtime API key of the allocator in the Docker secret file secrets/gemini_runtime_api_key
  (git-ignored, read only by Trading.Host; hidden prompt, never printed). Use a runtime key, not the development key
  (architecture §3.4). -ClearKey empties it: every allocator decision is then the deterministic default policy T11.
  Then scripts/m6-update.ps1 runs (its own confirmations): preflight, migration 0011, Trading.Host rebuilt with live
  data (and Testnet when its key is stored), first cycle, checks and the operational paper report.
  Last, the allocator's state (operational fields only): mode, key present or not, decision counts by outcome.
  The allocator mode stays Disabled until scripts/m8-allocator-mode.ps1.
#>
param([switch] $SetKey, [switch] $ClearKey)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

function Read-Hidden([string] $Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Write-KeyFile([string] $Value) {
    $path = Ensure-EmptySecret $script:OpsSecrets 'gemini_runtime_api_key'
    [System.IO.File]::WriteAllText($path, $Value)
    Reset-SecretAcl $script:OpsSecrets 'gemini_runtime_api_key'
}

if ([Console]::IsInputRedirected) { throw 'This script needs interactive confirmations; input is redirected.' }
if ($SetKey -and $ClearKey) { throw 'Give -SetKey or -ClearKey, not both.' }
if ($ClearKey) {
    Confirm-Interactive 'Remove the Gemini runtime key from secrets/ (the allocator falls back to T11 at the next Trading.Host start)?'
    Write-KeyFile ''
} elseif ($SetKey) {
    Confirm-Interactive 'Store a Gemini runtime API key for Trading.Host (allocator shadow calls within the 10 USD monthly cap)?'
    $key = Read-Hidden 'Gemini runtime API key'
    if ($key.Length -lt 20) { throw 'The key looks too short; nothing was stored.' }
    Write-KeyFile $key
    $key = $null
} else {
    Ensure-EmptySecret $script:OpsSecrets 'gemini_runtime_api_key' | Out-Null
    Reset-SecretAcl $script:OpsSecrets 'gemini_runtime_api_key'
}

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'm6-update.ps1')
if ($LASTEXITCODE -ne 0) { throw "m6-update.ps1 failed (exit $LASTEXITCODE)." }

$headers = @{ Authorization = "Bearer $(Get-Secret $script:OpsSecrets 'ops_api_token')" }
$allocator = Invoke-RestMethod 'http://127.0.0.1:55490/allocator' -Headers $headers
$keyPresent = (Get-Item (Join-Path $script:OpsSecrets 'gemini_runtime_api_key')).Length -gt 0
$counts = @($allocator.counts | ForEach-Object { "$($_.role)/$($_.outcome)=$($_.decisions)" })
Write-Host ''
Write-Host "allocator mode=$($allocator.mode) band=$($allocator.band.version) runtimeKey=$(if ($keyPresent) { 'present' } else { 'absent' }) decisions $($counts -join ' ')"
Write-Host 'Next (C1): scripts/m4-ai.ps1 alloc-spike --allow-network; then (C2) alloc-register and scripts/m8-allocator-mode.ps1.' -ForegroundColor Green
