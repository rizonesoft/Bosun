# Keeps the long-lived token `claude setup-token` prints as the user variable CLAUDE_CODE_OAUTH_TOKEN (D00 T03 §10).
# Task Scheduler hands the user's variables to every process it starts, so the watchdog's Claude sessions sign in with
# it and outlast a claude.ai sign-in, which ends on the date `claude auth status --json` gives as loginExpiresAt. The
# token is read without echo and never printed.
#
# A setup token lasts one year from the day it was made (https://code.claude.com/docs/en/authentication), and nothing
# reports when, so this also records that day in %LOCALAPPDATA%\Bosun\claude-token.json (the date only, never the token)
# for the preflight to check.
#
#   pwsh scripts/store-claude-token.ps1                          paste the token made today when asked
#   pwsh scripts/store-claude-token.ps1 -IssuedOn 2026-11-01     a token made on an earlier day
#   pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <day>   record the day for a token already stored
#   pwsh scripts/store-claude-token.ps1 -Remove                  take both out; sessions go back to the claude.ai sign-in
#
# Keep a copy in the 1Password Conclave vault too: this variable is not a recovery source.
param(
  [switch]$Remove,
  [switch]$RecordOnly,
  [string]$IssuedOn = '',
  [string]$Key = 'HKCU:\Environment',
  [string]$Name = 'CLAUDE_CODE_OAUTH_TOKEN',
  [string]$Record = (Join-Path $env:LOCALAPPDATA 'Bosun\claude-token.json')
)
$ErrorActionPreference = 'Stop'

function Save-Issued([string]$day) {
  New-Item -ItemType Directory -Force (Split-Path $Record) | Out-Null
  $tmp = "$Record.$PID.tmp"
  Set-Content -LiteralPath $tmp -Value (@{ issued = $day } | ConvertTo-Json -Compress) -Encoding utf8
  Move-Item -LiteralPath $tmp -Destination $Record -Force
}

if ($Remove) {
  if (Test-Path -LiteralPath $Key) { Remove-ItemProperty -LiteralPath $Key -Name $Name -ErrorAction Ignore }
  Remove-Item -LiteralPath $Record -ErrorAction Ignore
  if ((Get-Item -LiteralPath $Key -ErrorAction Ignore) -and $null -ne (Get-Item -LiteralPath $Key).GetValue($Name, $null)) {
    "could not remove $Name from $Key"
    exit 1
  }
  "removed $Name and its issue date; open a new shell for it to take effect there"
  exit 0
}

$today = [datetime]::UtcNow.ToString('yyyy-MM-dd')
if (-not $IssuedOn) {
  if ($RecordOnly) { '-RecordOnly needs -IssuedOn <the day you ran claude setup-token>; nothing changed'; exit 2 }
  $IssuedOn = $today
}
$day = [datetime]::MinValue
if (-not [datetime]::TryParseExact($IssuedOn, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$day) -or $IssuedOn -gt $today) {
  "-IssuedOn must be a day in yyyy-MM-dd form, not after today ($today); nothing changed"
  exit 2
}

if ($RecordOnly) {
  if (-not (Get-Item -LiteralPath $Key -ErrorAction Ignore) -or -not (Get-Item -LiteralPath $Key).GetValue($Name, $null)) {
    "no $Name is stored in $Key to record a day for; nothing changed"
    exit 2
  }
  Save-Issued $IssuedOn
  "recorded that the stored token was made on $IssuedOn; it lasts until $($day.AddYears(1).ToString('yyyy-MM-dd'))"
  exit 0
}

# The token arrives on stdin when piped (as the tests and a vault read do), or is asked for without echo.
$plain = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else {
  $secure = Read-Host -AsSecureString "Paste the token claude setup-token printed (it won't show)"
  [Net.NetworkCredential]::new('', $secure).Password
}
$plain = "$plain".Trim()
if (-not $plain) { 'no token given, nothing changed'; exit 2 }
if ($plain -match '\s') { 'that has spaces or line breaks inside it, so it is not one token; nothing changed'; exit 2 }

if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }
Set-ItemProperty -LiteralPath $Key -Name $Name -Value $plain -Type String
$back = (Get-Item -LiteralPath $Key).GetValue($Name, $null)
$plain = $null
if (-not $back) { "could not read $Name back from $Key"; exit 1 }
Save-Issued $IssuedOn
"stored $Name ($($back.Length) characters), made on $IssuedOn and lasting until $($day.AddYears(1).ToString('yyyy-MM-dd')); the watchdog's next session uses it. Run ``pwsh scripts/campaign-preflight.ps1 -Only claude`` to prove it works"
