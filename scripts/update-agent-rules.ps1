# Keeps the checkout's ignored CLAUDE.local.md importing .private/AGENTS.md exactly while .private is the marked
# overlay holding it (D00 T05 §5). scripts/hooks/post-merge runs it after each pull; setup and sync call the same
# function directly. Prints one line when it changed the file, nothing otherwise.
#
#   pwsh scripts/update-agent-rules.ps1 [-Repo <checkout>]
[CmdletBinding()]
param([string]$Repo = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')
$result = Update-BosunAgentRules $Repo
if ($result -eq 'added') { 'overlay: agent rules: CLAUDE.local.md now imports .private/AGENTS.md' }
elseif ($result -eq 'removed') { 'overlay: agent rules: the import of .private/AGENTS.md was taken out of CLAUDE.local.md' }
