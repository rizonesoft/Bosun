# Sets up a clone of this repository, once. Safe to run again: it changes only what is not already set.
#
#   pwsh scripts/setup-clone.ps1                 turn on the repository's git hooks in this clone
#   pwsh scripts/setup-clone.ps1 -Watchdog       and install the scheduled task that resumes an unattended run
#   pwsh scripts/setup-clone.ps1 -Repo <folder>  set up another clone (default: the one this script is in)
#
# Hooks: sets core.hooksPath to scripts/hooks in the clone's own config (--local), so scripts/hooks/pre-commit
# refuses a commit that would leave the generated plan stale or the TODO graph invalid.
#   Undo: git config --local --unset core.hooksPath   (the script prints the exact command, with any earlier value)
#
# -Watchdog is only for a machine that runs the plan unattended; Bosun itself needs no unattended run. It needs
# Python and Windows Task Scheduler. The guard that owns the task is asked what state it is in
# (`python scripts/run-guard.py status --verify --json`, `verify.task`), and only a missing task is installed: one
# that already runs this clone's guard is left alone, so a setup run never replaces the task guarding a live run,
# and one that exists but is another clone's, another user's or disabled is reported, not replaced.
#   Undo: python scripts/run-guard.py uninstall
#
# Every change is said before it is made and read back after. Exit 0 when the clone is set up, 1 with the cause
# when something could not be done; nothing is reported as done that was not read back.
param(
  [string]$Repo = (Split-Path $PSScriptRoot),
  [switch]$Watchdog
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

# --- watchdog ---
if ($Watchdog) {
  $python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $python) { Stop-Setup 'python is not on PATH, so the watchdog was not looked at or installed: install Python 3, then run this again' }
  $guard = Join-Path $Repo 'scripts/run-guard.py'
  if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) { Stop-Setup "$guard is missing, so the watchdog was not installed" }

  # What the guard says about its own task, or $null with the reason in $script:why. The exit code is not used: an
  # unhealthy watchdog exits 1 and still answers.
  function Get-Watchdog {
    Push-Location -LiteralPath $Repo
    try { $text = (& $python $guard status --verify --json 2>$null | Out-String).Trim() } finally { Pop-Location }
    if (-not $text) { $script:why = 'run-guard.py status printed nothing'; return $null }
    try { $answer = $text | ConvertFrom-Json -AsHashtable } catch { $script:why = 'run-guard.py status did not answer in JSON'; return $null }
    $verify = if ($answer -is [Collections.IDictionary]) { $answer['verify'] }
    if ($verify -isnot [Collections.IDictionary] -or $verify['task'] -isnot [string] -or $verify['task'] -notin 'ok', 'missing', 'mismatched', 'unreadable') {
      $script:why = 'run-guard.py status did not say what state the task is in'; return $null
    }
    $verify
  }

  $state = Get-Watchdog
  if (-not $state) { Stop-Setup "$why, so nothing was installed: run 'python scripts/run-guard.py status --verify' in $Repo and fix what it says" }
  $name = $state['watchdog']
  switch ($state['task']) {
    'ok' {
      "watchdog: already installed for this checkout ('$name'), left as it is"
      foreach ($problem in @($state['problems'])) { "  note: $problem" }
    }
    'missing' {
      "watchdog: installing the scheduled task '$name', which runs 'python scripts/run-guard.py watch' for $Repo every few minutes while you are signed in"
      Push-Location -LiteralPath $Repo
      try { $said = & $python $guard install 2>&1; $code = $LASTEXITCODE } finally { Pop-Location }
      if ($code) { Stop-Setup "the watchdog could not be installed (exit ${code}): $(($said | Out-String).Trim())" }
      $after = Get-Watchdog
      if (-not $after) { Stop-Setup "the install ran, but ${why}: it is not confirmed" }
      if ($after['task'] -ne 'ok') { Stop-Setup "the install reported success, but the task reads '$($after['task'])': $(@($after['problems']) -join '; ')" }
      "watchdog: installed and read back. To remove it: python scripts/run-guard.py uninstall  (run in $Repo)"
    }
    default {
      "setup-clone: the task '$name' is $($state['task']), so it was left alone and nothing was installed:"
      foreach ($problem in @($state['problems'])) { "  $problem" }
      if ($state['task'] -eq 'mismatched') {
        '  If another checkout in a folder of the same name owns that task, rename this folder and run this again.'
        "  If it is this checkout's own and only broken, remove it with 'python scripts/run-guard.py uninstall' and run this again."
      } else { "  Check that Task Scheduler is running and that you may read the task, then run this again." }
      exit 1
    }
  }
}
exit 0
