<#
.SYNOPSIS
  M6 Gate C step C1 (docs/m6-protocol.md §8.2, §10; D33). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m6-testnet-key.ps1 [-Clear]
.DESCRIPTION
  Stores the Binance Spot Testnet HMAC key in the Docker secret files secrets/testnet_api_key and
  secrets/testnet_api_secret (git-ignored, read only by Trading.Host). The values are read with a hidden prompt and
  never printed. Create the key on testnet.binance.vision (GitHub login), type HMAC, and enable trading for it (new
  keys cannot trade by default, S-B11). The Testnet has no withdrawals; no IP restriction is set (D33).
  -Clear empties both files: Trading.Host then runs without Testnet.
#>
param([switch] $Clear)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

function Read-Hidden([string] $Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Write-SecretFile([string] $Name, [string] $Value) {
    $path = Ensure-EmptySecret $script:OpsSecrets $Name
    [System.IO.File]::WriteAllText($path, $Value)
    # Same permissions as the other secrets/ files (inherited): Docker Desktop must be able to mount it.
    Reset-SecretAcl $script:OpsSecrets $Name
}

if ($Clear) {
    Confirm-Interactive 'Remove the Testnet key from secrets/ (Testnet execution stops at the next Trading.Host start)?'
    Write-SecretFile 'testnet_api_key' ''
    Write-SecretFile 'testnet_api_secret' ''
    Write-Host 'Testnet key removed.'
    exit 0
}

if ([Console]::IsInputRedirected) { throw 'This script reads the key interactively; input is redirected.' }
Confirm-Interactive 'Store a Binance Spot Testnet HMAC key (trading enabled, Testnet only) for Trading.Host?'
$key = Read-Hidden 'Testnet API key'
$secret = Read-Hidden 'Testnet secret key'
if ($key.Length -lt 16 -or $secret.Length -lt 16) { throw 'The key or the secret looks too short; nothing was stored.' }
Write-SecretFile 'testnet_api_key' $key
Write-SecretFile 'testnet_api_secret' $secret
$key = $null; $secret = $null
Write-Host 'Testnet key stored in secrets/ (not printed). Next: scripts/m6-testnet-drill.ps1'
