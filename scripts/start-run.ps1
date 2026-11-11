# Starts the unattended run: an interactive Claude Code session in this checkout, with every permission prompt off,
# the run guard switched on for it, its model chosen and its goal set.
#
#   pwsh scripts/start-run.ps1                    start the run in this window
#   pwsh scripts/start-run.ps1 -InstallShortcut   put "Bosun unattended run" on the desktop, which runs the line above
#   pwsh scripts/start-run.ps1 -DryRun            print what would run, and change nothing
#   pwsh scripts/start-run.ps1 -Model sonnet      start on another model
#   pwsh scripts/start-run.ps1 -Agent antigravity [-InstallShortcut]
#                                                 the same run in the Antigravity CLI (agy): every permission
#                                                 auto-approved, medium effort (the owner's choice, 2026-11-12), the
#                                                 goal as its opening prompt (auto mode is not offered to this account),
#                                                 on Antigravity's default model unless -AgyModel names one
#
# The session gets its id here, so the guard can be told about it before it starts: the guard's hooks then hold the run
# open, and this repo's own watchdog task resumes it after a crash. Nothing is checked first, so a start is never
# blocked: `pwsh scripts/campaign-preflight.ps1` is there to run when a start misbehaves. The goal is a Claude Code
# /goal, which is checked after every turn; the guard decides when the plan has no row left to run.
#
# The model is the alias `opus`, which Claude Code resolves to the newest Opus, so the shortcut never needs editing
# when a new one ships. The guard keeps it, and every session it resumes or starts afresh runs the same one.
#
# The session signs in with the long-lived token in the user variable CLAUDE_CODE_OAUTH_TOKEN (see
# scripts/store-claude-token.ps1). A window opened before the token was stored doesn't have it yet, so it is read from
# the user's own variables here; when none is stored at all, it is imported once from the 1Password Conclave vault.
# Without either, the session uses the claude.ai sign-in, and the start goes ahead. The token is never printed.
param(
  [switch]$InstallShortcut,
  [switch]$DryRun,
  [string]$ShortcutDir = [Environment]::GetFolderPath('Desktop'),
  [string]$Model = 'opus',
  [ValidateSet('claude', 'antigravity')]
  [string]$Agent = 'claude',
  [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')]
  [string]$Effort = 'medium',
  [string]$AgyModel = '',
  [string]$UserEnvKey = 'HKCU:\Environment',
  [string]$Goal = 'Finish the Bosun implementation plan 100% complete and aim for excellence: it should be the best project in its class, better than what any large vendor would ship. Act autonomously under the Autonomy and ambition rules in AGENTS.md: decide and act instead of asking, heal what breaks at its owning layer, and improve the repo, its runners, tools and methods whenever they slow the work or let a defect through. Work it with the process-plan skill, as the prompt `process plan` does, without stopping between sections or phases. The goal is met only when `python scripts/todo-graph.py query next` prints null and `python scripts/todo-graph.py query stats` shows every section outside phase 99 verified, or parked and named in docs/acceptance/. While `python scripts/run-guard.py status` prints waiting, or a review or a CI gate is in flight, end the turn without building or using tools: that is waiting, not stopping, and the run guard resumes the run.'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$tokenName = 'CLAUDE_CODE_OAUTH_TOKEN'

$antigravity = $Agent -eq 'antigravity'
if ($InstallShortcut) {
  $pwsh = (Get-Process -Id $PID).Path
  $link = Join-Path $ShortcutDir $(if ($antigravity) { 'Bosun unattended run (Antigravity).lnk' } else { 'Bosun unattended run.lnk' })
  $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
  $shortcut.TargetPath = $pwsh
  $shortcut.Arguments = "-NoExit -NoProfile -File `"$PSCommandPath`"" + $(if ($antigravity) { ' -Agent antigravity' })
  $shortcut.WorkingDirectory = $repo
  $shortcut.Description = "Start the unattended Bosun run in $(if ($antigravity) { 'the Antigravity CLI' } else { 'Claude Code' })"
  # The app icon, generated from design/icon/ by scripts/build-icons.py (D02 T01 §5); without it the shortcut shows PowerShell's.
  $icon = Join-Path $repo 'resources\bosun.ico'
  if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" }
  $shortcut.Save()
  "shortcut written: $link"
  exit 0
}

if ($antigravity) {
  # Antigravity has no /goal and the run guard cannot hold it yet (D00 T03 §19), so the goal is the opening prompt
  # and the session waits for reviews and CI itself instead of ending the turn.
  $prompt = ($Goal -replace '\s*While `python scripts/run-guard\.py status` prints waiting.*$', '') +
    ' Nothing holds this session open between turns, so never end a turn while a ready row exists: when a review or a CI gate is in flight, wait for it in this session, then continue.'
  $agyArgs = @('--dangerously-skip-permissions', '--effort', $Effort) + $(if ($AgyModel) { '--model', $AgyModel }) + @('--prompt-interactive', $prompt)
  if ($DryRun) {
    "in $repo"
    "agy $($agyArgs[0..($agyArgs.Count - 2)] -join ' ') `"$prompt`""
    exit 0
  }
  $agy = (Get-Command agy -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $agy) { 'agy is not on PATH: install the Antigravity CLI, then start again'; exit 1 }
  $python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  Set-Location -LiteralPath $repo
  # Two writers on one tree is the worse failure: a run the guard holds for Claude Code keeps the tree.
  if ($python) {
    $guard = (& $python (Join-Path $PSScriptRoot 'run-guard.py') status 2>$null | Select-Object -First 1)
    if ($guard -in 'active', 'waiting') { "a Claude Code run is live here (run guard: $guard): stop it first, then start again"; exit 1 }
  }
  & $agy @agyArgs
  exit $LASTEXITCODE
}

$session = [guid]::NewGuid().ToString()
$arguments = @('--session-id', $session, '--dangerously-skip-permissions', '--model', $Model, "/goal $Goal")
if ($DryRun) {
  "in $repo"
  "python scripts/run-guard.py start --session $session --permission-mode bypassPermissions --model $Model"
  "claude $($arguments[0..4] -join ' ') `"$($arguments[5])`""
  exit 0
}

# Looked up, not assumed: a missing tool is named with its fix instead of failing as a bare "not recognized".
$python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
$claude = (Get-Command claude -ErrorAction Ignore | Select-Object -First 1).Source
if (-not $python) { 'python is not on PATH: install Python 3, then start again'; exit 1 }
if (-not $claude) { 'claude is not on PATH: install Claude Code, then start again'; exit 1 }

# The sign-in, in the order the header gives. Each line says where it came from, never the token itself.
function Stored { if (Test-Path -LiteralPath $UserEnvKey) { (Get-Item -LiteralPath $UserEnvKey).GetValue($tokenName, $null) } }
if ([Environment]::GetEnvironmentVariable($tokenName)) {
  'sign-in: the long-lived token this window already has'
} else {
  if (-not (Stored)) {
    & (Join-Path $PSScriptRoot 'store-claude-token.ps1') -FromVault -Key $UserEnvKey | Select-Object -First 2
  }
  $token = Stored
  if ($token) {
    [Environment]::SetEnvironmentVariable($tokenName, $token)
    $token = $null
    'sign-in: the long-lived token from your user variables'
  } else {
    'sign-in: no long-lived token is stored, so the session uses the claude.ai sign-in'
  }
}

Set-Location -LiteralPath $repo
# Refused, with the reason, when a live session already owns the run: two writers on one tree is the worse failure.
& $python (Join-Path $PSScriptRoot 'run-guard.py') start --session $session --permission-mode bypassPermissions --model $Model
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $python (Join-Path $PSScriptRoot 'run-guard.py') status --verify | Select-Object -First 4
& $claude @arguments
