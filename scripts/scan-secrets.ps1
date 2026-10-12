<#
.SYNOPSIS
  Fails if any file that git would commit (tracked or untracked-but-not-ignored) looks like it contains a secret.
.DESCRIPTION
  A fast first line of defence run by verify.ps1 and before every push. It checks forbidden file names
  and well-known credential shapes. It is intentionally conservative; review any hit before allowing it.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$files = git ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed (is this a git repository?).' }

$forbiddenNames = '(^|/)\.env$|(^|/)\.env\.(?!example$)[^/]+$|\.(pem|pfx|p12|key)$|(^|/)id_(rsa|ed25519)|(^|/)secrets/'
$patterns = [ordered]@{
    'Google API key'      = 'AIza[0-9A-Za-z_\-]{35}'
    'Anthropic API key'   = 'sk-ant-[0-9A-Za-z_\-]{20,}'
    'GitHub token'        = 'gh[pousr]_[0-9A-Za-z]{30,}|github_pat_[0-9A-Za-z_]{20,}'
    'AWS access key'      = 'AKIA[0-9A-Z]{16}'
    'Private key block'   = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    # Literal values only: skips placeholders, variable/env references ($x, ${X}, %X%, {x}) and code ([Type]::..., (expr), ?x).
    'Password assignment' = '(?i)(password|pwd)\s*[=:]\s*["'']?(?!change-me|[\$\{\*<\[\(\?%])[^\s"'';,]{8,}'
}

# Always match culture-invariantly: PowerShell's -match is culture-sensitive, and under tr-TR
# case-insensitive [A-Z] does not match the letter I.
function Test-Pattern([string]$text, [string]$pattern, [switch]$IgnoreCase) {
    $options = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    if ($IgnoreCase) { $options = $options -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
    return [System.Text.RegularExpressions.Regex]::IsMatch($text, $pattern, $options)
}

$findings = New-Object System.Collections.Generic.List[string]
foreach ($file in $files) {
    if (Test-Pattern $file $forbiddenNames -IgnoreCase) { $findings.Add("$file : forbidden file name"); continue }
    if (-not (Test-Path $file -PathType Leaf)) { continue }
    if ((Get-Item $file).Length -gt 2MB) { continue }
    $content = Get-Content -Raw -LiteralPath $file -ErrorAction SilentlyContinue
    if ($null -eq $content -or $content.IndexOf([char]0) -ge 0) { continue }   # skip empty and binary files
    foreach ($name in $patterns.Keys) {
        if (Test-Pattern $content $patterns[$name]) { $findings.Add("$file : $name") }
    }
}

if ($findings.Count -gt 0) {
    Write-Host 'Possible secrets found:' -ForegroundColor Red
    $findings | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Secret scan passed ($(@($files).Count) files checked)." -ForegroundColor Green
