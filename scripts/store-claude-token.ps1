# Keeps the long-lived token `claude setup-token` prints as the user variable CLAUDE_CODE_OAUTH_TOKEN (D00 T03 §10).
# Task Scheduler hands the user's variables to every process it starts, so the watchdog's Claude sessions sign in with
# it and outlast a claude.ai sign-in, which ends on the date `claude auth status --json` gives as loginExpiresAt. The
# token is read without echo and never printed.
#
#   pwsh scripts/store-claude-token.ps1            paste the token when asked
#   pwsh scripts/store-claude-token.ps1 -Remove    take it out again; sessions go back to the stored claude.ai sign-in
#
# Keep a copy in the 1Password Conclave vault too: this variable is not a recovery source.
param(
  [switch]$Remove,
  [string]$Key = 'HKCU:\Environment',
  [string]$Name = 'CLAUDE_CODE_OAUTH_TOKEN'
)
$ErrorActionPreference = 'Stop'

if ($Remove) {
  if (Test-Path -LiteralPath $Key) { Remove-ItemProperty -LiteralPath $Key -Name $Name -ErrorAction Ignore }
  if ((Get-Item -LiteralPath $Key -ErrorAction Ignore) -and $null -ne (Get-Item -LiteralPath $Key).GetValue($Name, $null)) {
    "could not remove $Name from $Key"
    exit 1
  }
  "removed $Name; open a new shell for it to take effect there"
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
"stored $Name ($($back.Length) characters); the watchdog's next session uses it. Run ``pwsh scripts/campaign-preflight.ps1 -Only claude`` to prove it works"
