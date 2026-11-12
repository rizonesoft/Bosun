# Sets up a clone of this repository, once. Safe to run again: it changes only what is not already set.
#
#   pwsh scripts/setup-clone.ps1                 turn on the repository's git hooks in this clone
#   pwsh scripts/setup-clone.ps1 -Repo <folder>  set up another clone (default: the one this script is in)
#
# Hooks: sets core.hooksPath to scripts/hooks in the clone's own config (--local), so scripts/hooks/pre-commit
# refuses a commit that would leave the generated plan stale or the TODO graph invalid.
#   Undo: git config --local --unset core.hooksPath   (the script prints the exact command, with any earlier value)
#
# Nothing is scheduled: the watchdog task that resumed an unattended run was retired on 2026-11-12, with the
# -Watchdog option that installed it. A clone that still has one removes it with `python scripts/run-guard.py uninstall`.
#
# Every change is said before it is made and read back after. Exit 0 when the clone is set up, 1 with the cause
# when something could not be done; nothing is reported as done that was not read back.
[CmdletBinding()]  # so an option this script doesn't have (the retired -Watchdog) is refused, never silently ignored
param(
  [string]$Repo = (Split-Path $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$hooks = 'scripts/hooks'

function Stop-Setup([string]$why) { "setup-clone: $why"; exit 1 }

if (-not (Get-Command git -CommandType Application -ErrorAction Ignore)) { Stop-Setup 'git is not on PATH: install Git for Windows, then run this again' }
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { Stop-Setup "$Repo is not a folder" }
$Repo = (Resolve-Path -LiteralPath $Repo).ProviderPath
$top = git -C $Repo rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or -not $top -or [IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ne $Repo.TrimEnd('\', '/')) {
  Stop-Setup "$Repo is not the top folder of a git checkout"
}
if (-not (Test-Path -LiteralPath (Join-Path $Repo "$hooks/pre-commit") -PathType Leaf)) {
  Stop-Setup "$Repo has no $hooks/pre-commit, so it is not a checkout of this repository"
}
# Git on Linux and WSL ignores a hook that is not executable, and what a clone there gets is the mode git recorded.
$recorded = "$(git -C $Repo ls-files -s -- "$hooks/pre-commit" 2>$null)"
if (-not $recorded.StartsWith('100755 ')) {
  Stop-Setup "$hooks/pre-commit is not recorded in git as an executable, so a clone on Linux or WSL would skip it: run 'git update-index --chmod=+x $hooks/pre-commit' and commit, then run this again"
}

# --- hooks ---
function Get-HooksPath {
  $value = git -C $Repo config --local --get core.hooksPath 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
$before = Get-HooksPath
if ($before -eq $hooks) { "hooks: already on (core.hooksPath is $hooks)" }
else {
  "hooks: core.hooksPath is $(if ($null -eq $before) { 'not set' } else { "'$before'" }) in $Repo; setting it to $hooks"
  $said = git -C $Repo config --local core.hooksPath $hooks 2>&1
  if ($LASTEXITCODE) { Stop-Setup "could not set core.hooksPath ($(($said | Out-String).Trim())); it is still $(if ($null -eq $before) { 'not set' } else { "'$before'" })" }
  if ((Get-HooksPath) -ne $hooks) { Stop-Setup "core.hooksPath was written but does not read back as $hooks" }
  $undo = if ($null -eq $before) { '--unset core.hooksPath' } else { "core.hooksPath `"$before`"" }
  "hooks: on. To put it back: git -C `"$Repo`" config --local $undo"
}

exit 0
