# Claude Code's SessionStart hook for the overlay's owner-only rules (D00 T05 §5), wired in .claude/settings.json.
# At every session start it checks the overlay as it is then (Get-BosunOverlayRules in scripts/lib/overlay.ps1) and,
# when .private is the marked overlay's own clone holding its own AGENTS.md, hands that file to the session as
# context; otherwise it prints nothing. It writes no file, and a failure to read is a note, never a blocked session.
#
#   pwsh scripts/overlay-rules-hook.ps1 [-Repo <checkout>]   (Claude Code passes CLAUDE_PROJECT_DIR)
[CmdletBinding()]
param([string]$Repo = $(if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { Split-Path $PSScriptRoot }))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')
try { $rules = Get-BosunOverlayRules $Repo }
catch { [Console]::Error.WriteLine("overlay rules: .private/AGENTS.md could not be read ($($_.Exception.Message)); this session runs without them"); exit 0 }
if ($null -eq $rules) { exit 0 }
$context = "The owner's own rules for this checkout, from the private overlay (.private/AGENTS.md). Follow them as part of the project's rules:`n`n$rules"
@{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $context } } | ConvertTo-Json -Compress -Depth 3
