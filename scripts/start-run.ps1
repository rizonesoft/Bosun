# Starts the unattended run (the campaign): an interactive session in this checkout, in this window, with every
# permission prompt off, the run guard switched on for it, its model chosen and its goal set.
#
#   pwsh scripts/start-run.ps1                    start the campaign in Claude Code, in this window
#   pwsh scripts/start-run.ps1 -Agent antigravity the same campaign in Google Antigravity on Gemini 4 Carbon (agy):
#                                                 every permission auto-approved, medium effort (the owner's choice,
#                                                 2026-11-12), the goal as its opening prompt (auto mode is not
#                                                 offered to this account); -AgyModel names another model
#   pwsh scripts/start-run.ps1 -InstallShortcut   write both desktop shortcuts for this checkout, "Bosun Claude
#                                                 campaign" and "Bosun Carbon campaign", each running one line above
#   pwsh scripts/start-run.ps1 -DryRun            print what would run, and change nothing
#   pwsh scripts/start-run.ps1 -Model sonnet      start Claude Code on another model
#
# A run starts only when you start it, and it lives in this window (the owner's rule of 2026-11-12). Closing the
# window stops it, and everything it started stops with it: this process is put in a Windows job that ends its whole
# tree when it ends. A crash or a reboot ends the run. Nothing restarts or resumes it: there is no scheduled task, no
# watchdog and no hidden session. While the window is open the guard's hooks keep the session working (the Stop hook
# sends it back, and its own in-session heartbeat wakes it).
#
# The session gets its id here, so the guard can be told about it before it starts. Nothing is checked first, so a
# start is never blocked: `pwsh scripts/campaign-preflight.ps1` is there to run when a start misbehaves. The goal is a
# Claude Code /goal, which is checked after every turn; the guard decides when the plan has no row left to run.
#
# The model is the alias `opus`, which Claude Code resolves to the newest Opus, so the shortcut never needs editing
# when a new one ships.
#
# Each shortcut carries the Bosun icon with its runner's badge in the corner, rendered for this PC by
# scripts/build-icons.py into the user's own application data folder (-IconDir), never into the repository. A
# runner's mark is its company's trademark and is not kept here: when marks\claude.svg or marks\gemini.svg exists in
# that folder it is drawn in the badge, and without it the badge carries the runner's colour alone.
#
# The session signs in with the long-lived token in the user variable CLAUDE_CODE_OAUTH_TOKEN (see
# scripts/store-claude-token.ps1). A window opened before the token was stored doesn't have it yet, so it is read from
# the user's own variables here; when none is stored at all, it is imported once from the 1Password Conclave vault.
# Without either, the session uses the claude.ai sign-in, and the start goes ahead. The token is never printed.
param(
  [switch]$InstallShortcut,
  [switch]$DryRun,
  [string]$ShortcutDir = [Environment]::GetFolderPath('Desktop'),
  [string]$IconDir = (Join-Path $env:LOCALAPPDATA 'Bosun'),
  [string]$Model = 'opus',
  [ValidateSet('claude', 'antigravity')]
  [string]$Agent = 'claude',
  [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')]
  [string]$Effort = 'medium',
  [string]$AgyModel = 'gemini-4-carbon-medium',
  [string]$UserEnvKey = 'HKCU:\Environment',
  [string]$Goal = 'Finish the Bosun implementation plan 100% complete and aim for excellence: it should be the best project in its class, better than what any large vendor would ship. Act autonomously under the Autonomy and ambition rules in AGENTS.md: decide and act instead of asking, heal what breaks at its owning layer, and improve the repo, its runners, tools and methods whenever they slow the work or let a defect through. Work it with the process-plan skill, as the prompt `process plan` does, without stopping between sections or phases. The goal is met only when `python scripts/todo-graph.py query next` prints null and `python scripts/todo-graph.py query stats` shows every section outside phase 99 verified, or parked and named in docs/acceptance/. While `python scripts/run-guard.py status` prints waiting, or a review or a CI gate is in flight, end the turn without building or using tools: that is waiting, not stopping, and this session''s own heartbeat takes the run up again.'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$tokenName = 'CLAUDE_CODE_OAUTH_TOKEN'
# One launcher for each runner, named as the owner's other campaigns are ("<project> <runner> campaign").
$launchers = @(
  @{ Agent = 'claude'; Name = 'Bosun Claude campaign'; Icon = 'bosun-campaign-claude.ico'; Fill = '#D97757'; Mark = 'claude.svg'
     Description = 'Start the unattended Bosun campaign in Claude Code' },
  @{ Agent = 'antigravity'; Name = 'Bosun Carbon campaign'; Icon = 'bosun-campaign-carbon.ico'; Fill = '#4C8DF6'; Mark = 'gemini.svg'
     Description = 'Start the unattended Bosun campaign in Google Antigravity on Gemini 4 Carbon' }
)

$antigravity = $Agent -eq 'antigravity'
if ($InstallShortcut) {
  $pwsh = (Get-Process -Id $PID).Path
  # The app icon, generated from design/icon/ by scripts/build-icons.py (D02 T01 §5): the base of each badged icon,
  # and the icon a shortcut carries when its badge cannot be rendered.
  $plain = Join-Path $repo 'resources\bosun.ico'
  $python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  foreach ($l in $launchers) {
    $icon = Join-Path $IconDir $l.Icon
    $said = 'python is not on PATH'
    $code = 1
    if ($python) {
      try {
        New-Item -ItemType Directory -Force -Path $IconDir | Out-Null
        $said = (& $python (Join-Path $PSScriptRoot 'build-icons.py') --out $icon --badge-fill $l.Fill --badge-mark (Join-Path $IconDir "marks\$($l.Mark)") 2>&1 | Select-Object -Last 1)
        $code = $LASTEXITCODE
      } catch { $said = $_.Exception.Message }
    }
    if ($code) {
      "the badged icon for '$($l.Name)' could not be rendered ($said): the shortcut carries the plain Bosun icon. Fix: python -m pip install -r scripts/requirements-icons.txt, then this again"
      $icon = $plain
    }
    $link = Join-Path $ShortcutDir "$($l.Name).lnk"
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $pwsh
    $shortcut.Arguments = "-NoLogo -NoExit -NoProfile -File `"$PSCommandPath`"" + $(if ($l.Agent -ne 'claude') { " -Agent $($l.Agent)" })
    $shortcut.WorkingDirectory = $repo
    $shortcut.Description = $l.Description
    if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" }
    $shortcut.WindowStyle = 1  # a normal window
    $shortcut.Save()
    "shortcut written: $link"
  }
  # The names this file, and a hand-made copy, gave the same two launchers before 2026-11-12.
  foreach ($old in 'Bosun unattended run.lnk', 'Bosun unattended run (Antigravity).lnk', 'Bosun Carbon unattended run.lnk') {
    $path = Join-Path $ShortcutDir $old
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path; "removed the earlier shortcut: $path" }
  }
  exit 0
}

# From here on, whatever this window starts ends with it. Not for a dry run, which starts nothing.
function Join-WindowJob {
  . (Join-Path $PSScriptRoot 'lib\job-measure.ps1')
  Stop-TreeWithThisWindow
  $Host.UI.RawUI.WindowTitle = ($launchers | Where-Object Agent -EQ $Agent).Name
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
  Join-WindowJob
  & $agy @agyArgs
  exit $LASTEXITCODE
}

$session = [guid]::NewGuid().ToString()
$arguments = @('--session-id', $session, '--dangerously-skip-permissions', '--model', $Model, "/goal $Goal")
if ($DryRun) {
  "in $repo"
  "python scripts/run-guard.py start --session $session"
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
& $python (Join-Path $PSScriptRoot 'run-guard.py') start --session $session
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Join-WindowJob
& $claude @arguments
