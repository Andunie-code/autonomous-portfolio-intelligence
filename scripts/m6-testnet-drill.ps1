<#
.SYNOPSIS
  M6 Gate C step C2 (docs/m6-protocol.md §8.3, §10). User only:
    powershell -ExecutionPolicy Bypass -File scripts/m6-testnet-drill.ps1 [-Symbol BTCUSDT] [-Rounds 4] [-Capital 20000]
.DESCRIPTION
  Runs the Testnet drill in a one-off trading-host container (same image and secrets as Trading.Host): at least 20
  full order lifecycles with small notionals on one symbol, the injected timeout recovery and the kill switch rule.
  Real orders go to testnet.binance.vision (virtual funds). Run it before the first PaperTrading -> TestnetActive
  activation: with an active Testnet run the drill refuses (its trades would change the account balance).
  The report has operational fields only (no price, quantity, balance or PnL). It is saved outside the repository with
  its SHA-256; share the summary lines (or the file) with Claude Code.
#>
param([string] $Symbol = 'BTCUSDT', [int] $Rounds = 4, [decimal] $Capital = 20000)
. "$PSScriptRoot\lib\m5-common.ps1"
Set-Location $script:Root

foreach ($name in @('testnet_api_key', 'testnet_api_secret')) {
    $path = Ensure-EmptySecret $script:OpsSecrets $name
    if ((Get-Item $path).Length -eq 0) { throw "secrets/$name is empty: run scripts/m6-testnet-key.ps1 first." }
}

Confirm-Interactive "Run the Testnet drill on $Symbol ($Rounds rounds, real Testnet orders with virtual funds)?"
$arguments = @('compose', '--profile', 'trading', 'run', '--rm', '--no-deps', 'trading-host', 'testnet-drill',
    '--symbol', $Symbol, '--rounds', "$Rounds", '--capital', $Capital.ToString([Globalization.CultureInfo]::InvariantCulture))
$lines = Invoke-Native 'docker' $arguments
$exit = $LASTEXITCODE
$json = ($lines | Where-Object { $_ -ne $null }) -join "`n"
$start = $json.IndexOf('{')
if ($start -lt 0) { throw "The drill produced no report (exit $exit)." }
$report = $json.Substring($start)
$file = Join-Path (Get-ReportDirectory) ("m6-testnet-drill-{0}.json" -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
[System.IO.File]::WriteAllText($file, $report)
$hash = Get-Sha256Text $report
$r = $report | ConvertFrom-Json

Write-Host ''
Write-Host "report=$file"
Write-Host "sha256=$hash"
Write-Host "pass=$($r.pass) orders=$($r.orders) completedLifecycles=$($r.completedLifecycles) duplicates=$($r.duplicateOrders)"
Write-Host "unknown injected=$($r.unknownInjected) resolved=$($r.unknownResolved) neverAccepted=$($r.neverAccepted) localRejectionsWithoutRequest=$($r.localRejectionsWithoutRequest)"
Write-Host "stopLossSupported=$($r.stopLossSupported)/$($r.symbolsChecked) canTrade=$($r.canTrade) capitalSufficient=$($r.capitalSufficient) clockOffsetMs=$($r.clockOffsetMs)"
Write-Host "latencyMs median=$($r.latencyMedianMs) p95=$($r.latencyP95Ms)"
foreach ($s in @($r.scenarios)) { Write-Host ("  {0,-40} {1} {2}" -f $s.name, $s.result, $s.reason) }
if ($exit -ne 0) { exit $exit }
