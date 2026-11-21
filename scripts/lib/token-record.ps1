# The long-lived Claude Code token's issue-day record, shared by scripts/store-claude-token.ps1 (which writes it) and
# scripts/campaign-preflight.ps1 (which reads it) (D00 T03 §23). The record holds the day and a fingerprint of the token
# it belongs to: the first 16 hex digits of the token's SHA-256, which say nothing of the token. The preflight trusts
# the day only for the token whose fingerprint it is, so a record left from another token is never read as this one's.
function Get-TokenFingerprint([string]$token) {
  $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($token))
  ([Convert]::ToHexString($hash)).Substring(0, 16).ToLowerInvariant()
}
