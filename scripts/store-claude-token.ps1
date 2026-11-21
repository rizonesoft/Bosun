# Keeps the long-lived token `claude setup-token` prints as the user variable CLAUDE_CODE_OAUTH_TOKEN (D00 T03 §10).
# Windows hands the user's variables to every window opened after they are set, so a run's Claude session signs in with
# it and outlast a claude.ai sign-in, which ends on the date `claude auth status --json` gives as loginExpiresAt. The
# token is read without echo and never printed.
#
# A setup token lasts one year from the day it was made (https://code.claude.com/docs/en/authentication), and nothing
# reports when, so this also records that day in %LOCALAPPDATA%\Bosun\claude-token.json (the date only, never the token)
# for the preflight to check.
#
#   pwsh scripts/store-claude-token.ps1                          paste the token made today when asked
#   pwsh scripts/store-claude-token.ps1 -FromVault               read it from the 1Password item the overlay names instead,
#                                                                made on the day the vault item was created
#   pwsh scripts/store-claude-token.ps1 -IssuedOn 2026-11-01     a token made on an earlier day
#   pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <day>   record the day for a token already stored
#   pwsh scripts/store-claude-token.ps1 -Remove                  take both out; sessions go back to the claude.ai sign-in
#
# The 1Password item the private overlay names (vault_item, or BOSUN_VAULT_ITEM) holds the recovery copy: this variable
# is not a recovery source. -FromVault is a one-time import through the `op` CLI; nothing reads the vault after that.
param(
  [switch]$Remove,
  [switch]$RecordOnly,
  [switch]$FromVault,
  # The 1Password item (op://<vault>/<item>/<field>); without it, BOSUN_VAULT_ITEM, then vault_item in the private
  # overlay's .private/config.toml, through Get-BosunOverlayValue (D00 T05 §7).
  [string]$VaultItem = '',
  [string]$Op = 'op',
  [string]$IssuedOn = '',
  [string]$Key = 'HKCU:\Environment',
  [string]$Name = 'CLAUDE_CODE_OAUTH_TOKEN',
  [string]$Record = (Join-Path $env:LOCALAPPDATA 'Bosun\claude-token.json')
)
$ErrorActionPreference = 'Stop'

# The day is tied to the token it belongs to (D00 T03 §23): the record holds the first 16 hex digits of the token's
# SHA-256, which say nothing of the token, and scripts/campaign-preflight.ps1 trusts the day only for the token whose
# fingerprint it is. So a record left from another token, by an import stopped between its two writes or a record
# that could not be written, is never read as this token's day, whatever order the writes are in.
. (Join-Path $PSScriptRoot 'lib/token-record.ps1')

# What a failed record write leaves the preflight trusting: the previous day, when the record already on disk belongs
# to this same token (a correction with -RecordOnly, or the same token imported again), or no day at all otherwise.
# The record is read once, before anything is written: when it cannot be read (a file held open without sharing), the
# note says the old day may still stand, since this script cannot tell whose it is.
$script:priorFingerprint = if (Test-Path -LiteralPath $Record -PathType Leaf) { try { "$((Get-Content -LiteralPath $Record -Raw -ErrorAction Stop | ConvertFrom-Json).token)" } catch { $null } } else { '' }
function Get-FailedDayNote([string]$fingerprint) {
  if ($null -eq $script:priorFingerprint) { 'the day recorded before could not be read, so if it was for this token it may still stand and be trusted: correct it before starting a campaign' }
  elseif ($script:priorFingerprint -and $script:priorFingerprint -ceq $fingerprint) { 'the day recorded before for this token still stands and is still trusted, so correct it before starting a campaign' }
  else { 'the preflight will not trust a day for it' }
}
# Written to a temporary file and renamed over the record; the temporary file goes again if either step fails.
function Save-Issued([string]$day, [string]$fingerprint) {
  New-Item -ItemType Directory -Force (Split-Path $Record) | Out-Null
  $tmp = "$Record.$PID.tmp"
  try {
    Set-Content -LiteralPath $tmp -Value (@{ issued = $day; token = $fingerprint } | ConvertTo-Json -Compress) -Encoding utf8
    # File.Move replaces a file and refuses a folder (Move-Item would move the file into one and report success).
    [IO.File]::Move($tmp, $Record, $true)
  } finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction Ignore }
  }
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
  $storedFingerprint = Get-TokenFingerprint (Get-Item -LiteralPath $Key).GetValue($Name, $null)
  try { Save-Issued $IssuedOn $storedFingerprint }
  catch { "the day could not be recorded at $Record ($($_.Exception.Message)); the stored token is unchanged, and $(Get-FailedDayNote $storedFingerprint)"; exit 1 }
  "recorded that the stored token was made on $IssuedOn; it lasts until $($day.AddYears(1).ToString('yyyy-MM-dd'))"
  exit 0
}

# The token comes from the vault when asked, arrives on stdin when piped (as the tests do), or is asked for without echo.
$plain = if ($FromVault) {
  # The item comes from -VaultItem, else BOSUN_VAULT_ITEM, else the overlay's vault_item; it is settled before any
  # `op` call, so a checkout with none set up says what to set and reads nothing.
  if (-not "$VaultItem".Trim()) {
    . (Join-Path $PSScriptRoot 'lib/overlay.ps1')
    try { $lookup = Get-BosunOverlayValue -Key vault_item }
    catch { "the vault item could not be read from .private/config.toml ($($_.Exception.Message)); fix vault_item there or set BOSUN_VAULT_ITEM; nothing changed"; exit 1 }
    if (-not $lookup.Value) { "no vault item is set up for -FromVault: $($lookup.Message); nothing changed"; exit 1 }
    $VaultItem = $lookup.Value
  }
  $tool = (Get-Command $Op -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $tool) { "the 1Password CLI ($Op) is not on PATH, so the vault can't be read: install it and sign in, or paste the token instead; nothing changed"; exit 1 }
  $read = & $tool read $VaultItem 2>$null
  if ($LASTEXITCODE -or -not "$read".Trim()) { "``op read`` could not read the token from the vault (exit $LASTEXITCODE): check ``op whoami`` and that the vault item is the one set up; nothing changed"; exit 1 }
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

# Each failure names the store to repair. Once the token is stored, nothing claims "nothing changed": a day that
# could not be recorded for it is distrusted by the preflight (its fingerprint is another token's, or there is none).
$remedy = "``pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <the day you ran claude setup-token>``"
try {
  if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }
  Set-ItemProperty -LiteralPath $Key -Name $Name -Value $plain -Type String
  $back = (Get-Item -LiteralPath $Key).GetValue($Name, $null)
} catch { $back = $null }
# Read back exactly, the same string: anything else is not the token given.
if (-not ($back -is [string] -and $back -ceq $plain)) { $plain = $null; "the token could not be stored in $Key ($Name reads back differently or not at all); the recorded day was left as it was"; exit 1 }
$fingerprint = Get-TokenFingerprint $plain
$plain = $null
if ($dayUnknown) {
  # The record on disk belongs to the token this one replaced, so it goes too.
  Remove-Item -LiteralPath $Record -ErrorAction Ignore
  if (Test-Path -LiteralPath $Record) { "stored $Name ($($back.Length) characters); the vault didn't say when it was made, and the day at $Record could not be removed, so $(Get-FailedDayNote $fingerprint): close what holds it, then run $remedy"; exit 1 }
  "stored $Name ($($back.Length) characters), but the vault didn't say when it was made, so no day is recorded: run $remedy"
  exit 0
}
try { Save-Issued $IssuedOn $fingerprint }
catch { "stored $Name ($($back.Length) characters), but its day could not be recorded at $Record ($($_.Exception.Message)); $(Get-FailedDayNote $fingerprint): close what holds it, then run $remedy"; exit 1 }
"stored $Name ($($back.Length) characters), made on $IssuedOn and lasting until $($day.AddYears(1).ToString('yyyy-MM-dd')); the next run's session uses it. Run ``pwsh scripts/campaign-preflight.ps1 -Only claude`` to prove it works"
