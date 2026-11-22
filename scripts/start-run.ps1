# Starts the unattended run (the campaign): an interactive session in this checkout, in this window, with every
# permission prompt off, the run guard switched on for it, its model chosen and its goal set.
#
#   pwsh scripts/start-run.ps1                    start the campaign in Claude Code, in this window, at medium effort
#                                                 (the owner's choice, 2026-11-12), whatever the user's own default is
#   pwsh scripts/start-run.ps1 -Agent antigravity the same campaign in Google Antigravity on Gemini 4 Neon at medium effort
#                                                 (agy): every permission auto-approved, the goal as its opening prompt
#                                                 (auto mode is not offered to this account); -AgyModel names another
#                                                 model (non-orchestrator models also receive -Effort)
#   pwsh scripts/start-run.ps1 -Effort high       start either runner at another effort
#   pwsh scripts/start-run.ps1 -InstallShortcut   write both desktop shortcuts for this checkout, "Bosun Claude
#                                                 campaign" and "Bosun Gemini campaign", each running one line above
#   pwsh scripts/start-run.ps1 -DryRun            print what would run, and change nothing
#   pwsh scripts/start-run.ps1 -Model sonnet      start Claude Code on another model
#   pwsh scripts/start-run.ps1 -Minutes 90        hold the machine awake for 90 minutes (1 to 10080) instead of for as
#                                                 long as the writer runs
#
# While the writer runs, the machine is held awake and its display on by a power request (scripts/lib/hold-awake.ps1,
# D00 T03 §28), which Windows ends when the holder ends: the writer returning, its minutes running out, the window
# closing or a crash. Nothing on the machine is changed. The request does not stop the screen saver (Microsoft:
# https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate), so a saver that
# locks on resume is the owner's setting to turn off, which `pwsh scripts/campaign-preflight.ps1` checks.
#
# A run starts only when you start it, and it lives in this window (the owner's rule of 2026-11-12). Closing the
# window stops it, and everything it started stops with it: this process is put in a Windows job that ends its whole
# tree when it ends. A crash or a reboot ends the run. Nothing restarts or resumes it: there is no scheduled task, no
# watchdog and no hidden session. While the window is open the guard's hooks keep the session working (the Stop hook
# sends it back, and its own in-session heartbeat wakes it).
#
# An Antigravity run uses Antigravity's /goal (which has no continuation ceiling) together with its Stop hook
# (.agents/hooks.json): the guard is told the run lives as long as this window's process does, `agy` gets the run's
# id in BOSUN_RUN_SESSION, and the run is recorded ended once `agy` returns, since the window (-NoExit) outlives it.
# Antigravity has no heartbeat, so its goal tells it to wait in the session instead of ending the turn.
#
# The session gets its id here, so the guard can be told about it before it starts. Nothing is checked first, so a
# start is never blocked: `pwsh scripts/campaign-preflight.ps1` is there to run when a start misbehaves. The goal is a
# /goal prompt for the runner; the guard decides when the plan has no row left to run.
#
# The model is the alias `opus`, which Claude Code resolves to the newest Opus, so the shortcut never needs editing
# when a new one ships.
#
# Each shortcut carries the Bosun icon with its runner's badge in the corner, rendered for this PC by
# scripts/build-icons.py into the user's own application data folder (-IconDir), never into the repository. A
# runner's mark is its company's trademark and is not kept here: when marks\claude.svg or marks\gemini.svg exists in
# that folder it is drawn in the badge, and without it the badge carries the runner's colour alone; running
# -InstallShortcut again applies a mark put there later.
#
# The session signs in with the long-lived token in the user variable CLAUDE_CODE_OAUTH_TOKEN (see
# scripts/store-claude-token.ps1). A window opened before the token was stored doesn't have it yet, so it is read from
# the user's own variables here; when none is stored at all, it is imported once from the 1Password item the private
# overlay names (`vault_item` in `.private/config.toml`, or `BOSUN_VAULT_ITEM`).
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
  [string]$AgyModel = 'gemini-4-neon-medium',
  [string]$UserEnvKey = 'HKCU:\Environment',
  # Minutes to hold the machine awake (1 to 10080); 0, the default, holds it for as long as the writer runs.
  [int]$Minutes = 0,
  # Where the holder records its hold (the preflight reads it): the run guard's build folder, which RUN_GUARD_BUILD moves
  # for a test, so a test never writes the checkout's own.
  [string]$HoldRecord = (Join-Path ($env:RUN_GUARD_BUILD ? $env:RUN_GUARD_BUILD : (Join-Path (Split-Path $PSScriptRoot) 'build')) 'run-hold.json'),
  [string]$Goal = 'Finish the Bosun implementation plan 100% complete and aim for excellence: it should be the best project in its class, better than what any large vendor would ship. Act autonomously under the Autonomy and ambition rules in AGENTS.md: decide and act instead of asking, heal what breaks at its owning layer, and improve the repo, its runners, tools and methods whenever they slow the work or let a defect through. Work it with the process-plan skill, as the prompt `process plan` does, without stopping between sections or phases. The goal is met only when `python scripts/todo-graph.py query next` prints null and `python scripts/todo-graph.py query stats` shows every section outside phase 99 verified, or parked and named in docs/acceptance/. While `python scripts/run-guard.py status` prints waiting, or a review or a CI gate is in flight, end the turn without building or using tools: that is waiting, not stopping, and this session''s own heartbeat takes the run up again.'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$tokenName = 'CLAUDE_CODE_OAUTH_TOKEN'
if ($PSBoundParameters.ContainsKey('Minutes') -and ($Minutes -lt 1 -or $Minutes -gt 10080)) {
  "-Minutes takes 1 to 10080 (or leave it out to hold the machine for as long as the writer runs), not $Minutes"
  exit 2
}

# The power request that keeps the machine awake while the writer runs (D00 T03 §28): a holder process of its own,
# which says on its first line whether it holds, and lets go when its input closes, its minutes run out or this
# process ends. A failed hold is said and the start goes on: a start is never blocked.
function Start-Hold {
  $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
  # Test-only: a shorter minute, so a timed hold can be tested in seconds.
  $length = if ($Minutes -and $env:BOSUN_TEST_MINUTE_SECONDS) { $Minutes * [double]$env:BOSUN_TEST_MINUTE_SECONDS / 60 } else { $Minutes }
  foreach ($a in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'lib\hold-awake.ps1'), '-ParentPid', "$PID",
                   '-Record', $HoldRecord) + $(if ($Minutes) { '-Minutes', ([double]$length).ToString([Globalization.CultureInfo]::InvariantCulture) })) { $info.ArgumentList.Add($a) }  # one spelling, whatever the culture
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true
  $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true
  try {
    $holder = [Diagnostics.Process]::Start($info)
    # Bounded: the holder writes its record before it answers, and a start is never blocked by the hold (round 2 of
    # D00 T03 §28), so one that does not answer in time is stopped and the writer starts anyway.
    $answer = $holder.StandardOutput.ReadLineAsync()
    $seconds = if ($env:BOSUN_TEST_HOLD_ANSWER_SECONDS) { [double]$env:BOSUN_TEST_HOLD_ANSWER_SECONDS } else { 30 }
    if (-not $answer.Wait([TimeSpan]::FromSeconds($seconds))) {
      & taskkill.exe /T /F /PID $holder.Id 2>&1 | Out-Null
      "hold: the holder gave no answer within $seconds seconds and was stopped, so the machine may sleep during the run"
      return $null
    }
    $said = $answer.Result
  } catch {
    "hold: the holder could not start ($($_.Exception.Message)), so the machine may sleep during the run"
    return $null
  }
  if ($said -like 'held*') {
    "hold: the machine stays awake and its display on $(if ($Minutes) { "for $Minutes minute(s)" } else { 'while the writer runs' })"
    if ($said -ne 'held') { "hold: $($said.Substring(6)), so the preflight cannot see this hold" }
  } else {
    "hold: $said, so the machine may sleep during the run"
  }
  $holder
}
function Stop-Hold($holder) {
  # The writer has returned or never started, and the window (-NoExit) outlives it: the hold goes now.
  if (-not $holder -or $holder.HasExited) { return }
  # A holder whose input is already gone is ended below by the kill, so a failed close needs nothing more.
  try { $holder.StandardInput.Close() } catch [System.IO.IOException], [System.ObjectDisposedException] { }
  if (-not $holder.WaitForExit(2000)) { $holder.Kill($true) }
}
# One launcher for each runner, named as the owner's other campaigns are ("<project> <runner> campaign").
$launchers = @(
  @{ Agent = 'claude'; Name = 'Bosun Claude campaign'; Icon = 'bosun-campaign-claude.ico'; Fill = '#D97757'; Mark = 'claude.svg'
     Description = 'Start the unattended Bosun campaign in Claude Code' },
  @{ Agent = 'antigravity'; Name = 'Bosun Gemini campaign'; Icon = 'bosun-campaign-carbon.ico'; Fill = '#4C8DF6'; Mark = 'gemini.svg'
     Description = 'Start the unattended Bosun campaign in Google Antigravity on Gemini 4 Neon (medium effort)' }
)

$antigravity = $Agent -eq 'antigravity'
if ($InstallShortcut) {
  $pwsh = (Get-Process -Id $PID).Path
  # The app icon, generated from design/icon/ by scripts/build-icons.py (D02 T01 §5): the base of each badged icon,
  # and the icon a shortcut carries when its badge cannot be rendered.
  $plain = Join-Path $repo 'resources\bosun.ico'
  $python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  $shell = New-Object -ComObject WScript.Shell
  $failed = 0
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
    # Written beside its final name, read back, and only then put in place: a save that fails (a full disk, a denied
    # write) leaves the shortcut that was there as it was.
    $link = Join-Path $ShortcutDir "$($l.Name).lnk"
    $staged = Join-Path $ShortcutDir "$($l.Name).new.lnk"  # WScript.Shell saves only to a .lnk name
    $arguments = "-NoLogo -NoExit -NoProfile -File `"$PSCommandPath`"" + $(if ($l.Agent -ne 'claude') { " -Agent $($l.Agent)" })
    try {
      $shortcut = $shell.CreateShortcut($staged)
      $shortcut.TargetPath = $pwsh
      $shortcut.Arguments = $arguments
      $shortcut.WorkingDirectory = $repo
      $shortcut.Description = $l.Description
      if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" }
      $shortcut.WindowStyle = 1  # a normal window
      $shortcut.Save()
      $back = $shell.CreateShortcut($staged)
      $wrong = @(
        if ($back.TargetPath -ne $pwsh) { "target $($back.TargetPath)" }
        if ($back.Arguments -ne $arguments) { "arguments $($back.Arguments)" }
        if ($back.WorkingDirectory -ne $repo) { "folder $($back.WorkingDirectory)" }
        if ((Test-Path -LiteralPath $icon) -and $back.IconLocation -ne "$icon,0") { "icon $($back.IconLocation)" }
      )
      if ($wrong) { throw "it read back with $($wrong -join ', ')" }
      [IO.File]::Move($staged, $link, $true)
      "shortcut written: $link"
    } catch {
      Remove-Item -LiteralPath $staged -Force -ErrorAction Ignore
      "FAIL the shortcut '$($l.Name)' could not be written ($($_.Exception.Message)): the one already at $link, if any, is unchanged"
      $failed++
    }
  }
  if ($failed) { 'the earlier shortcuts are kept until both are written: fix what is named above, then run this again'; exit 1 }
  # Remove superseded names and the retired Codex writer shortcut after both writer shortcuts are saved.
  foreach ($old in 'Bosun unattended run.lnk', 'Bosun unattended run (Antigravity).lnk', 'Bosun Carbon unattended run.lnk', 'Bosun Codex campaign.lnk') {
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
  # Antigravity runs under /goal and has no heartbeat, so the session waits for reviews, local gates and CI inside
  # itself instead of ending the turn, and its Stop hook (.agents/hooks.json) tells it so.
  $goalText = ($Goal -replace '\s*While `python scripts/run-guard\.py status` prints waiting.*$', '') +
    ' Nothing wakes this session once a turn ends, so never end a turn while a ready row exists: when a review, a local gate or a CI gate is in flight, wait for it in this session, then continue.' +
    ' If the operator asks to stop or pause, run `python scripts/run-guard.py halt --ground operator --reason <their words>`, then end the turn; a `python scripts/run-guard.py status` of `halted`, `stalled`, `parked` or `complete` also ends this goal.'
  $prompt = if ($goalText.StartsWith('/goal ')) { $goalText } else { "/goal $goalText" }
  $agyEffort = if ($AgyModel -match '^(gemini-pro-orchestrator|Orchestrator\b)') { @() } else { @('--effort', $Effort) }
  $agyArgs = @('--dangerously-skip-permissions') + $agyEffort + $(if ($AgyModel) { '--model', $AgyModel }) + @('--prompt-interactive', $prompt)
  $session = [guid]::NewGuid().ToString()
  if ($DryRun) {
    "in $repo"
    "python scripts/run-guard.py start --session $session --runner antigravity --owner-pid <this window's process>"
    "agy $($agyArgs[0..($agyArgs.Count - 2)] -join ' ') `"$prompt`""
    exit 0
  }
  $agy = (Get-Command agy -ErrorAction Ignore | Select-Object -First 1).Source
  $python = (Get-Command python -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $agy) { 'agy is not on PATH: install the Antigravity CLI, then start again'; exit 1 }
  if (-not $python) { 'python is not on PATH: install Python 3, then start again'; exit 1 }
  Set-Location -LiteralPath $repo
  Join-WindowJob
  # Refused, with the reason, when a live session already owns the run: two writers on one tree is the worse failure.
  $guardScript = Join-Path $PSScriptRoot 'run-guard.py'
  & $python $guardScript start --session $session --runner antigravity --owner-pid $PID
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  $env:BOSUN_RUN_SESSION = $session
  $code = 1
  $hold = @(Start-Hold)
  $hold | Where-Object { $_ -is [string] }
  try {
    & $agy @agyArgs
    $code = $LASTEXITCODE
  } catch {
    "agy could not start: $($_.Exception.Message)"
  } finally {
    Stop-Hold ($hold | Where-Object { $_ -is [Diagnostics.Process] } | Select-Object -First 1)
    # The window outlives agy (-NoExit): its run ends here, unless it already ended (a halt, the plan done).
    & $python $guardScript end --session $session --reason "agy returned (exit $code); nothing resumes a run: start it again from its shortcut" | Out-Null
  }
  exit $code
}

$session = [guid]::NewGuid().ToString()
# --name keeps the shortcut's name in the window title: Claude Code otherwise replaces the title with its own topic.
$arguments = @('--session-id', $session, '--dangerously-skip-permissions', '--model', $Model, '--effort', $Effort, '--name', ($launchers | Where-Object Agent -EQ $Agent).Name, "/goal $Goal")
if ($DryRun) {
  "in $repo"
  "python scripts/run-guard.py start --session $session"
  "claude $($arguments[0..($arguments.Count - 2)] -join ' ') `"$($arguments[-1])`""
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
$hold = @(Start-Hold)
$hold | Where-Object { $_ -is [string] }
$code = 1
try {
  & $claude @arguments
  $code = $LASTEXITCODE
} catch {
  "claude could not start: $($_.Exception.Message)"
} finally {
  Stop-Hold ($hold | Where-Object { $_ -is [Diagnostics.Process] } | Select-Object -First 1)
}
exit $code
