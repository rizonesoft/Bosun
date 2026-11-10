# Starts the unattended run: an interactive Claude Code session in this checkout, with every permission prompt off,
# the run guard switched on for it, and its goal set.
#
#   pwsh scripts/start-run.ps1                    start the run in this window
#   pwsh scripts/start-run.ps1 -InstallShortcut   put "Bosun unattended run" on the desktop, which runs the line above
#   pwsh scripts/start-run.ps1 -DryRun            print what would run, and change nothing
#
# The session gets its id here, so the guard can be told about it before it starts: the guard's hooks then hold the run
# open, and this repo's own watchdog task resumes it after a crash. Nothing is checked first, so a start is never
# blocked: `pwsh scripts/campaign-preflight.ps1` is there to run when a start misbehaves. The goal is a Claude Code
# /goal, which is checked after every turn; the guard decides when the plan has no row left to run.
param(
  [switch]$InstallShortcut,
  [switch]$DryRun,
  [string]$ShortcutDir = [Environment]::GetFolderPath('Desktop'),
  [string]$Goal = 'Finish the Bosun implementation plan 100% complete and aim for excellence: it should be the best project in its class, better than what any large vendor would ship. Act autonomously under the Autonomy and ambition rules in AGENTS.md: decide and act instead of asking, heal what breaks at its owning layer, and improve the repo, its runners, tools and methods whenever they slow the work or let a defect through. Work it with the process-plan skill, as the prompt `process plan` does, without stopping between sections or phases. The goal is met only when `python scripts/todo-graph.py query next` prints null and `python scripts/todo-graph.py query stats` shows every section outside phase 99 verified, or parked and named in docs/acceptance/. While `python scripts/run-guard.py status` prints waiting, or a review or a CI gate is in flight, end the turn without building or using tools: that is waiting, not stopping, and the run guard resumes the run.'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot

if ($InstallShortcut) {
  $pwsh = (Get-Process -Id $PID).Path
  $link = Join-Path $ShortcutDir 'Bosun unattended run.lnk'
  $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
  $shortcut.TargetPath = $pwsh
  $shortcut.Arguments = "-NoExit -NoProfile -File `"$PSCommandPath`""
  $shortcut.WorkingDirectory = $repo
  $shortcut.Description = 'Start the unattended Bosun run in Claude Code'
  $shortcut.Save()
  "shortcut written: $link"
  exit 0
}

$session = [guid]::NewGuid().ToString()
$arguments = @('--session-id', $session, '--dangerously-skip-permissions', "/goal $Goal")
if ($DryRun) {
  "in $repo"
  "python scripts/run-guard.py start --session $session --permission-mode bypassPermissions"
  "claude $($arguments[0..2] -join ' ') `"$($arguments[3])`""
  exit 0
}

# Looked up, not assumed: a missing tool is named with its fix instead of failing as a bare "not recognized".
$python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
$claude = (Get-Command claude -ErrorAction Ignore | Select-Object -First 1).Source
if (-not $python) { 'python is not on PATH: install Python 3, then start again'; exit 1 }
if (-not $claude) { 'claude is not on PATH: install Claude Code, then start again'; exit 1 }

Set-Location -LiteralPath $repo
# Refused, with the reason, when a live session already owns the run: two writers on one tree is the worse failure.
& $python (Join-Path $PSScriptRoot 'run-guard.py') start --session $session --permission-mode bypassPermissions
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $python (Join-Path $PSScriptRoot 'run-guard.py') status --verify | Select-Object -First 4
& $claude @arguments
