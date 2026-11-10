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
#   pwsh scripts/store-claude-token.ps1 -FromVault               read it from the 1Password Conclave vault instead,
#                                                                made on the day the vault item was created
#   pwsh scripts/store-claude-token.ps1 -IssuedOn 2026-11-01     a token made on an earlier day
#   pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <day>   record the day for a token already stored
#   pwsh scripts/store-claude-token.ps1 -Remove                  take both out; sessions go back to the claude.ai sign-in
#
# The 1Password Conclave vault holds the recovery copy ("Claude Code OAuth token (1 year, setup-token)"): this variable
# is not a recovery source. -FromVault is a one-time import through the `op` CLI; nothing reads the vault after that.
param(
  [switch]$Remove,
  [switch]$RecordOnly,
  [switch]$FromVault,
  [string]$VaultItem = 'op://Conclave/pm5vqgypmvjok23xwlaacbwlji/credential',
  [string]$Op = 'op',
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
  # The token first: its issue day is kept while the token stays, so the preflight still checks a token it can't remove.
  if (Test-Path -LiteralPath $Key) { Remove-ItemProperty -LiteralPath $Key -Name $Name -ErrorAction Ignore }
  if ((Get-Item -LiteralPath $Key -ErrorAction Ignore) -and $null -ne (Get-Item -LiteralPath $Key).GetValue($Name, $null)) {
    "could not remove $Name from $Key; its issue day is kept at $Record"
    exit 1
  }
  Remove-Item -LiteralPath $Record -ErrorAction Ignore
  if (Test-Path -LiteralPath $Record) {
    "removed $Name, but its issue day is still at $Record (delete it by hand); open a new shell for the removal to take effect there"
    exit 1
  }
  "removed $Name and its issue day; open a new shell for it to take effect there"
  exit 0
}

$today = [datetime]::UtcNow.ToString('yyyy-MM-dd')
$dayGiven = [bool]$IssuedOn
$dayUnknown = $false
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

# The token comes from the vault when asked, arrives on stdin when piped (as the tests do), or is asked for without echo.
$plain = if ($FromVault) {
  $tool = (Get-Command $Op -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $tool) { "the 1Password CLI ($Op) is not on PATH, so the vault can't be read: install it and sign in, or paste the token instead; nothing changed"; exit 1 }
  $read = & $tool read $VaultItem 2>$null
  if ($LASTEXITCODE -or -not "$read".Trim()) { "``op read`` could not read the token from the vault (exit $LASTEXITCODE): check ``op whoami`` and that the item is in the Conclave vault; nothing changed"; exit 1 }
  if (-not $dayGiven) {
    # A token from the vault was not made today: its day is the day the vault item was created, and when that can't
    # be read no day is recorded, so the preflight asks for one instead of trusting an invented year.
    $dayUnknown = $true
    $parts = $VaultItem -replace '^op://', '' -split '/'
    $item = & $tool item get $parts[1] --vault $parts[0] --format json 2>$null
    if (-not $LASTEXITCODE -and $item) {
      try {
        $created = ($item -join "`n" | ConvertFrom-Json).created_at
        if ($created -isnot [datetime]) { $created = [datetime]::Parse("$created", [Globalization.CultureInfo]::InvariantCulture, 'AdjustToUniversal') }
        $day = $created.ToUniversalTime().Date
        $IssuedOn = $day.ToString('yyyy-MM-dd')
        $dayUnknown = $IssuedOn -gt $today
      } catch { $dayUnknown = $true }
    }
    $item = $null
  }
  "$read"
} elseif ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else {
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
if ($dayUnknown) {
  # The record on disk belongs to the token this one replaced, so it goes too.
  Remove-Item -LiteralPath $Record -ErrorAction Ignore
  "stored $Name ($($back.Length) characters), but the vault didn't say when it was made, so no day is recorded: run ``pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <the day you ran claude setup-token>``"
  exit 0
}
Save-Issued $IssuedOn
"stored $Name ($($back.Length) characters), made on $IssuedOn and lasting until $($day.AddYears(1).ToString('yyyy-MM-dd')); the watchdog's next session uses it. Run ``pwsh scripts/campaign-preflight.ps1 -Only claude`` to prove it works"
