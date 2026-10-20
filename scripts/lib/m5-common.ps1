<#
  Shared helpers of the M5 scripts (docs/m5-protocol.md §4, §6, §7). Dot-source: . "$PSScriptRoot\lib\m5-common.ps1"
  Secrets are generated into secrets/ (ops roles, Ops API token, backup key) and src/HoldoutEvaluator/secrets/
  (holdout roles, status_relay, evaluator tokens); both directories are git-ignored and never printed.
#>
$ErrorActionPreference = 'Stop'
$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:EnvFile = Join-Path $script:Root '.env'
$script:OpsSecrets = Join-Path $script:Root 'secrets'
$script:HoldoutSecrets = Join-Path $script:Root 'src\HoldoutEvaluator\secrets'
$script:OpsRoles = @('ops_owner', 'research_app', 'trading_app', 'ops_api', 'market_import')
$script:HoldoutRoles = @('eval_owner', 'eval_registry', 'eval_diagnostic', 'eval_prospective', 'status_relay')

function Read-DotEnv {
    $settings = @{}
    if (Test-Path $script:EnvFile) {
        foreach ($line in Get-Content $script:EnvFile) {
            if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { $settings[$Matches[1]] = $Matches[2] }
        }
    }
    return $settings
}

function Set-DotEnvValue([string] $Name, [string] $Value) {
    $lines = @()
    if (Test-Path $script:EnvFile) { $lines = @(Get-Content $script:EnvFile | Where-Object { $_ -cnotmatch "^\s*$Name\s*=" }) }
    $lines += "$Name=$Value"
    Set-Content -Path $script:EnvFile -Value $lines -Encoding ascii
}

function Confirm-Interactive([string] $Question) {
    if ([Console]::IsInputRedirected) { throw 'This step needs an interactive confirmation; input is redirected.' }
    $answer = Read-Host "$Question Type 'yes' to continue"
    if ($answer -cne 'yes') { throw 'Cancelled by the user.' }
}

function New-SecretValue {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}

function Ensure-Secret([string] $Directory, [string] $Name) {
    if (-not (Test-Path $Directory)) { New-Item -ItemType Directory -Path $Directory | Out-Null }
    $path = Join-Path $Directory $Name
    if (-not (Test-Path $path)) { [System.IO.File]::WriteAllText($path, (New-SecretValue)) }
    return $path
}

function Get-Secret([string] $Directory, [string] $Name) {
    return ([System.IO.File]::ReadAllText((Join-Path $Directory $Name))).Trim()
}

function Get-SuperuserPassword {
    $settings = Read-DotEnv
    if (-not $settings['PI_DEV_DB_PASSWORD']) { throw '.env has no PI_DEV_DB_PASSWORD.' }
    return $settings['PI_DEV_DB_PASSWORD']
}

# psql inside the PostgreSQL container over the local socket (the only superuser path, pg_hba line 1).
function Invoke-Psql([string] $Database, [string] $Sql, [switch] $Quiet) {
    $secret = Get-SuperuserPassword
    $arguments = @('compose', 'exec', '-T', '-e', "PGPASSWORD=$secret", 'postgres', 'psql', '-U', 'pi_dev', '-d', $Database, '-v', 'ON_ERROR_STOP=1', '-At')
    $output = $Sql | & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw "psql failed on $Database." }
    if (-not $Quiet) { return $output }
}

function Assert-CleanFile([string] $RelativePath) {
    Push-Location $script:Root
    try {
        if (git status --porcelain -- $RelativePath) { throw "$RelativePath has uncommitted changes." }
    } finally { Pop-Location }
}

function Get-ReportDirectory {
    $directory = Join-Path $env:LOCALAPPDATA 'PortfolioIntelligence\reports'
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
    return $directory
}

function Get-Sha256Text([string] $Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') }) -join '')
}

# Connection string for the host-side tools. After the M5 bootstrap (PI_OPS_DB=ops): the given role's login on ops
# through the loopback port. Before it: the M0-M4 development database (superuser; the loopback superuser path is
# refused by the M5 pg_hba).
function Get-OpsConnection([string] $Role) {
    $settings = Read-DotEnv
    $port = if ($settings['PI_DEV_DB_PORT']) { $settings['PI_DEV_DB_PORT'] } else { '55432' }
    if ($settings['PI_OPS_DB'] -eq 'ops') {
        return "Host=127.0.0.1;Port=$port;Database=ops;Username=$Role;Password=$(Get-Secret $script:OpsSecrets $Role)"
    }
    return "Host=127.0.0.1;Port=$port;Database=portfolio_dev;Username=pi_dev;Password=$($settings['PI_DEV_DB_PASSWORD'])"
}

# Runs a native command and returns its stdout lines. Its stderr lines (docker "Creating ...", psql NOTICE, library
# warnings) are shown on the console and never stop the script: under Windows PowerShell 5.1 a redirected native
# stderr becomes ErrorRecords, which $ErrorActionPreference = 'Stop' would turn into a terminating error.
# Failure is decided by the exit code, which stays in $LASTEXITCODE for the caller.
function Invoke-Native([string] $Command, [string[]] $Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host "  $($_.ToString())" -ForegroundColor DarkGray } else { "$_" }
        }
    } finally {
        $ErrorActionPreference = $previous
    }
}
