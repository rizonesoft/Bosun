# Keeps the checkout's ignored CLAUDE.local.md importing .private/AGENTS.md exactly while .private is the marked
# overlay holding it (D00 T05 §5). scripts/hooks/post-merge runs it after each pull; setup and sync call the same
# function directly. Prints one line when it changed the file, nothing otherwise.
#
#   pwsh scripts/update-agent-rules.ps1 [-Repo <checkout>]
[CmdletBinding()]
param([string]$Repo = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')
# A file that cannot be written (held open by an editor, say) is said, with the step to take, and exits 1.
try { $result = Update-BosunAgentRules $Repo }
catch { "overlay: agent rules: CLAUDE.local.md could not be updated ($($_.Exception.Message)); close what holds it, then run: pwsh scripts/update-agent-rules.ps1"; exit 1 }
if ($result -eq 'added') { 'overlay: agent rules: CLAUDE.local.md now imports .private/AGENTS.md' }
elseif ($result -eq 'removed') { 'overlay: agent rules: the import of .private/AGENTS.md was taken out of CLAUDE.local.md' }
