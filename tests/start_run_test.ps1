# scripts/start-run.ps1: the shortcuts it writes start the run from this checkout, each with its runner's icon; a dry
# run changes nothing; and when the launcher's window process ends, nothing it started keeps running.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts\start-run.ps1'
$pwsh = (Get-Process -Id $PID).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-start-run-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$failed = 0
function Check([bool]$ok, [string]$what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
try {
  # One call writes a launcher for each runner, and removes the names the same launchers had before.
  $icons = Join-Path $work 'icons'
  # Distinct marks, as a runner's trademark the owner keeps on this PC: each must reach its own icon.
  New-Item -ItemType Directory -Force (Join-Path $icons 'marks') | Out-Null
  Set-Content (Join-Path $icons 'marks\claude.svg') '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect x="1" y="1" width="8" height="8"/></svg>' -Encoding ascii
  Set-Content (Join-Path $icons 'marks\gemini.svg') '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4"/></svg>' -Encoding ascii
  $earlier = 'Bosun unattended run.lnk', 'Bosun unattended run (Antigravity).lnk', 'Bosun Carbon unattended run.lnk', 'Bosun Codex campaign.lnk'
  foreach ($name in $earlier) { Set-Content -LiteralPath (Join-Path $work $name) -Value 'an earlier shortcut' }
  $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work -IconDir $icons) -join "`n"
  $link = Join-Path $work 'Bosun Claude campaign.lnk'
  $carbonLink = Join-Path $work 'Bosun Gemini campaign.lnk'
  Check ($LASTEXITCODE -eq 0 -and (Test-Path $link) -and (Test-Path $carbonLink)) "both shortcuts are written where asked ($out)"
  Check (-not ($earlier | Where-Object { Test-Path -LiteralPath (Join-Path $work $_) })) 'the shortcuts'' earlier names are removed'
  $shell = New-Object -ComObject WScript.Shell
  $read = $shell.CreateShortcut($link)
  $carbon = $shell.CreateShortcut($carbonLink)
  Check ($read.WorkingDirectory -eq $repo -and $carbon.WorkingDirectory -eq $repo) 'each starts in this checkout, wherever that is'
  Check ($read.Arguments -match '-NoExit' -and $read.Arguments.TrimEnd().EndsWith("`"$script`"")) 'the Claude shortcut runs this checkout''s own start script and keeps the window'
  Check ($carbon.Arguments -match '-NoExit' -and $carbon.Arguments.EndsWith("`"$script`" -Agent antigravity")) 'the Carbon shortcut starts the same script with -Agent antigravity'
  Check ((Split-Path $read.TargetPath -Leaf) -match '^pwsh') 'it runs PowerShell 7'
  Check ($read.Description -eq 'Start the unattended Bosun campaign in Claude Code' -and $carbon.Description -match 'Google Antigravity on Gemini 4 Neon \(medium effort\)$') 'each says which runner it starts'
  # Each carries its own badged icon, rendered for this PC outside the repository (needs scripts/requirements-icons.txt).
  $claudeIcon = Join-Path $icons 'bosun-campaign-claude.ico'
  $carbonIcon = Join-Path $icons 'bosun-campaign-carbon.ico'
  Check ($read.IconLocation -eq "$claudeIcon,0" -and $carbon.IconLocation -eq "$carbonIcon,0" -and (Test-Path $claudeIcon) -and (Test-Path $carbonIcon)) "each shortcut names its own icon file ($($read.IconLocation); $($carbon.IconLocation))"
  $hashes = @($claudeIcon, $carbonIcon, (Join-Path $repo 'resources\bosun.ico') | Where-Object { Test-Path $_ } | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
  Check (@($hashes | Select-Object -Unique).Count -eq 3) 'the two badges differ from each other and from the plain Bosun icon'
  # Each installed icon is exactly what the icon builder makes from its own colour and its own mark (the build is
  # reproducible), and not what the other runner's mark makes.
  $iconPython = (Get-Command python -CommandType Application | Select-Object -First 1).Source
  $builder = Join-Path $repo 'scripts\build-icons.py'
  function Expected([string]$fill, [string]$mark) {
    $at = Join-Path $work "expected-$([guid]::NewGuid()).ico"
    & $iconPython $builder --out $at --badge-fill $fill --badge-mark (Join-Path $icons "marks\$mark") | Out-Null
    (Get-FileHash -LiteralPath $at).Hash
  }
  $claudeHash = (Get-FileHash -LiteralPath $claudeIcon).Hash
  $carbonHash = (Get-FileHash -LiteralPath $carbonIcon).Hash
  Check ($claudeHash -eq (Expected '#D97757' 'claude.svg') -and $carbonHash -eq (Expected '#4C8DF6' 'gemini.svg')) 'each icon carries its own runner''s mark, byte for byte as the builder draws it'
  Check ($claudeHash -ne (Expected '#D97757' 'gemini.svg') -and $carbonHash -ne (Expected '#4C8DF6' 'claude.svg')) 'and not the other runner''s mark'
  # A shortcut that can't be replaced (here a read-only one, as a denied write is): the one there is left as it was,
  # no staged file is left beside it, the earlier names are kept, and the install says so and fails.
  $kept = [IO.File]::ReadAllBytes($link)
  Set-Content -LiteralPath (Join-Path $work 'Bosun unattended run.lnk') -Value 'an earlier shortcut'
  Set-ItemProperty -LiteralPath $link -Name IsReadOnly -Value $true
  try {
    $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work -IconDir $icons) -join "`n"
    $code = $LASTEXITCODE
  } finally { Set-ItemProperty -LiteralPath $link -Name IsReadOnly -Value $false }
  $leftStaged = @(Get-ChildItem -LiteralPath $work -Filter '*.new.lnk')
  Check ($code -eq 1 -and $out -match "FAIL the shortcut 'Bosun Claude campaign' could not be written" -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($link)) -eq [Convert]::ToBase64String($kept) -and -not $leftStaged -and (Test-Path -LiteralPath (Join-Path $work 'Bosun unattended run.lnk'))) "a shortcut that can't be replaced is left as it was, with no staged file and the earlier names kept (exit $code)"
  $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work -IconDir $icons) -join "`n"
  Check ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $work 'Bosun unattended run.lnk')) -and $shell.CreateShortcut($link).IconLocation -eq "$claudeIcon,0") 'once it can be, the install writes both and then removes the earlier names'

  # An icon folder that can't be made: the shortcuts are still written, with the plain icon and a notice.
  $blocked = Join-Path $work 'not-a-folder'
  Set-Content -LiteralPath $blocked -Value 'a file where the icon folder would go'
  $plainDir = Join-Path $work 'plain'
  New-Item -ItemType Directory -Force $plainDir | Out-Null
  $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $plainDir -IconDir $blocked) -join "`n"
  $plainIcon = "$(Join-Path $repo 'resources\bosun.ico'),0"
  $fallbacks = @(foreach ($n in 'Bosun Claude campaign', 'Bosun Gemini campaign') { $at = Join-Path $plainDir "$n.lnk"; if (Test-Path -LiteralPath $at) { $shell.CreateShortcut($at).IconLocation } else { 'missing' } })
  Check ($LASTEXITCODE -eq 0 -and $fallbacks.Count -eq 2 -and -not ($fallbacks | Where-Object { $_ -ne $plainIcon }) -and $out -match 'could not be rendered') "a badge that can't be rendered leaves both shortcuts with the plain Bosun icon and says so ($($fallbacks -join '; '))"
  Check ($out -match "the icon folder .* can't be made" -and $out -notmatch 'pip install') "an icon folder that can't be made says that, not pip"

  # D00 T03 §31: one fix for each way a badge fails, the shortcut left with the plain icon and reopened to prove it.
  function Install-Case([string]$name, [scriptblock]$setup, [scriptblock]$undo, [string]$fix, [string]$iconDir, [switch]$ClaudeOnly) {
    $dir = Join-Path $work "case-$name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    & $setup
    try { $said = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $dir -IconDir $iconDir 2>&1) -join "`n"; $code = $LASTEXITCODE } finally { & $undo }
    # Both shortcuts, reopened, and the install's own exit (round 3 of D00 T03 §31): a fallback that wrote one only fails.
    $icons = @(foreach ($n in 'Bosun Claude campaign', 'Bosun Gemini campaign') { $at = Join-Path $dir "$n.lnk"; if (Test-Path -LiteralPath $at) { $shell.CreateShortcut($at).IconLocation } else { 'missing' } })
    $pip = $fix -match 'pip install'
    # A cause that breaks only the Claude badge leaves the Gemini one its own; one that breaks both leaves both plain.
    $want = if ($ClaudeOnly) { $icons[0] -eq $plainIcon -and $icons[1] -ne $plainIcon -and $icons[1] -ne 'missing' } else { -not ($icons | Where-Object { $_ -ne $plainIcon }) }
    Check ($code -eq 0 -and $said -match [regex]::Escape($fix) -and $icons.Count -eq 2 -and $want -and ($pip -or $said -notmatch 'pip install')) "a badge that fails by $name says its one fix and falls back where it failed (exit $code; $($icons -join ', '))"
  }
  $hide = Join-Path $work 'hide-resvg'
  New-Item -ItemType Directory -Force $hide | Out-Null
  Set-Content -LiteralPath (Join-Path $hide 'resvg_py.py') -Value 'raise ImportError("No module named ''resvg_py''", name="resvg_py")'
  $keptPath = $env:PYTHONPATH
  Install-Case 'a missing package' { $env:PYTHONPATH = $hide } { $env:PYTHONPATH = $keptPath } 'python -m pip install -r scripts/requirements-icons.txt' (Join-Path $work 'icons-pkg')
  $badIcons = Join-Path $work 'icons-badmark'
  New-Item -ItemType Directory -Force (Join-Path $badIcons 'marks') | Out-Null
  Set-Content -LiteralPath (Join-Path $badIcons 'marks\claude.svg') -Value '<svg this is truncated'
  Install-Case 'a malformed mark' {} {} 'mend or remove it' $badIcons -ClaudeOnly
  $roIcons = Join-Path $work 'icons-readonly'
  New-Item -ItemType Directory -Force $roIcons | Out-Null
  $roIcon = Join-Path $roIcons 'bosun-campaign-claude.ico'
  Set-Content -LiteralPath $roIcon -Value 'an icon from before'
  Install-Case 'an icon it cannot replace' { Set-ItemProperty -LiteralPath $roIcon -Name IsReadOnly -Value $true } { Set-ItemProperty -LiteralPath $roIcon -Name IsReadOnly -Value $false } 'make it writable or choose another -IconDir' $roIcons -ClaudeOnly
  $keptEnvPath = $env:Path
  $noPython = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'python.exe')) -and -not (Test-Path -LiteralPath (Join-Path $_ 'python.cmd')) }) -join ';'
  Install-Case 'no python' { $env:Path = $noPython } { $env:Path = $keptEnvPath } 'python is not on PATH: install Python 3' (Join-Path $work 'icons-nopy')
  # Round 1 of D00 T03 §31: a drive that is not there, a builder that exits without a word, and a caller that makes
  # native exits throw, each still leave both shortcuts with the plain icon.
  $free = [char[]](70..90) | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
  Install-Case 'a drive that is not there' {} {} "the icon folder $($free):\no-such-folder can't be made" "$($free):\no-such-folder"
  $silent = Join-Path $work 'silent-python'
  New-Item -ItemType Directory -Force $silent | Out-Null
  Set-Content -LiteralPath (Join-Path $silent 'python.cmd') -Value '@exit /b 9' -Encoding ascii
  Install-Case 'a builder that exits without a word' { $env:Path = "$silent;$noPython" } { $env:Path = $keptEnvPath } 'the badge builder exited 9 and said nothing' (Join-Path $work 'icons-silent')
  $badIcons2 = Join-Path $work 'icons-badmark-native'
  New-Item -ItemType Directory -Force (Join-Path $badIcons2 'marks') | Out-Null
  Set-Content -LiteralPath (Join-Path $badIcons2 'marks\claude.svg') -Value '<svg this is truncated'
  $nativeDir = Join-Path $work 'case-native'
  New-Item -ItemType Directory -Force $nativeDir | Out-Null
  $said = (& $pwsh -NoProfile -Command "`$PSNativeCommandUseErrorActionPreference = `$true; & '$script' -InstallShortcut -ShortcutDir '$nativeDir' -IconDir '$badIcons2'" 2>&1) -join "`n"
  $nativeLink = Join-Path $nativeDir 'Bosun Claude campaign.lnk'
  $nativeIcon = if (Test-Path -LiteralPath $nativeLink) { $shell.CreateShortcut($nativeLink).IconLocation } else { 'missing' }
  Check ($said -match 'mend or remove it' -and $nativeIcon -eq $plainIcon) "with native exits made to throw, a failed badge still falls back to the plain icon ($nativeIcon)"

  $state = Join-Path $work 'state.json'
  $env:RUN_GUARD_STATE = $state
  # The guard's waits too (review and gate marks, the CI gate's wait), so a live session's never reach a case.
  $env:RUN_GUARD_BUILD = Join-Path $work 'guard-build'
  # With nothing but Windows on PATH, as on a CI runner with no Claude Code: a dry run starts nothing, so it needs none.
  $path = $env:Path
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -DryRun) -join "`n" } finally { $env:Path = $path }
  Remove-Item Env:RUN_GUARD_STATE
  Remove-Item Env:RUN_GUARD_BUILD -ErrorAction Ignore
  Check ($LASTEXITCODE -eq 0 -and -not (Test-Path $state)) 'a dry run leaves the run state alone'
  Check ($out -match '(?m)run-guard\.py start --session ([0-9a-f-]{36})\s*$' -and $out -match "claude --session-id $($Matches[1]) --dangerously-skip-permissions --model opus --effort medium --name Bosun Claude campaign `"/goal ") 'the guard is told the same session id the session starts with, in bypass mode, at medium effort, with its goal'
  Check ($out -notmatch 'claude-opus-\d') 'the model is the alias for the newest Opus, never one pinned version'
  Check ($out -match '100% complete' -and $out -match 'query next') 'the goal names the finish line'
  Check ($out -notmatch 'guard resumes' -and $out -match 'own heartbeat takes the run up again') 'the goal promises no resume: the session''s own heartbeat carries a wait'

  # The Antigravity run: every permission auto-approved, on Gemini 4 Neon at medium effort, and /goal as its opening prompt.
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try {
    $out = (& $pwsh -NoProfile -File $script -Agent antigravity -DryRun) -join "`n"
    $agyOut = $out
    $agyCode = $LASTEXITCODE
    $agyOrch = (& $pwsh -NoProfile -File $script -Agent antigravity -AgyModel gemini-pro-orchestrator -Effort high -DryRun) -join "`n"
    $agyOrchCode = $LASTEXITCODE
  } finally { $env:Path = $path }
  Check ($agyCode -eq 0 -and $out -match 'agy --dangerously-skip-permissions --effort medium --model gemini-4-neon-medium --prompt-interactive "/goal Finish the Bosun' -and $out -notmatch '--settings|maxStopHookContinuations' -and $out -match 'run-guard\.py start --session [0-9a-f-]{36} --runner antigravity --owner-pid') 'Antigravity starts with every prompt off, on Gemini 4 Neon at medium effort, under /goal, with no --settings, after claiming the guard'
  Check ($out -match 'query next' -and $out -match 'wait for it in this session' -and $out -match 'run-guard\.py halt --ground operator' -and $out -match 'ends this goal' -and $out -notmatch 'heartbeat takes') 'its goal keeps the finish line, waits in-session (Antigravity has no heartbeat) and names the halt'
  Check ($agyOrchCode -eq 0 -and $agyOrch -match 'agy --dangerously-skip-permissions --model gemini-pro-orchestrator --prompt-interactive "/goal ' -and $agyOrch -notmatch '--effort|--settings|maxStopHookContinuations') 'an orchestrator model omits --effort and still passes no --settings'

  # A real start against a fake claude, a test registry key and a state file of its own: nothing real is started.
  $fake = Join-Path $work 'fake'
  New-Item -ItemType Directory -Force $fake | Out-Null
  Set-Content (Join-Path $fake 'claude.cmd') "@echo off`r`nif defined CLAUDE_CODE_OAUTH_TOKEN (echo TOKEN=set) else (echo TOKEN=none)`r`necho ARGS=%*" -Encoding ascii
  $envKey = "HKCU:\Software\BosunStartRunTest-$PID"
  New-Item -Path $envKey -Force | Out-Null
  Set-ItemProperty -LiteralPath $envKey -Name 'CLAUDE_CODE_OAUTH_TOKEN' -Value 'tok-start-run-test'
  $had = $env:CLAUDE_CODE_OAUTH_TOKEN
  $env:RUN_GUARD_STATE = $state
  # The guard's waits too (review and gate marks, the CI gate's wait), so a live session's never reach a case.
  $env:RUN_GUARD_BUILD = Join-Path $work 'guard-build'
  $env:Path = "$fake;$path"
  try {
    $env:CLAUDE_CODE_OAUTH_TOKEN = $null
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal') -join "`n"
    $code = $LASTEXITCODE
    $saved = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json }
    Check ($code -eq 0 -and $out -match 'sign-in: the long-lived token from your user variables' -and $out -match 'TOKEN=set') 'a window without the token gets it from the user''s variables'
    Check ($out -notmatch 'tok-start-run-test') 'and the token is never printed'
    Check ($out -match 'ARGS=--session-id [0-9a-f-]{36} --dangerously-skip-permissions --model opus --effort medium --name "Bosun Claude campaign" "/goal test goal"') 'the session starts on the newest Opus at medium effort, whatever the user''s own default, with every prompt off and its goal'
    $kept = @($saved.PSObject.Properties.Name)
    Check ($saved.active -and $saved.session_id -match '^[0-9a-f-]{36}$' -and -not ($kept | Where-Object { $_ -in 'model', 'permission_mode', 'child' })) "the guard holds the run for that session and keeps nothing a resume would need ($($kept -join ', '))"
    Remove-Item $state -ErrorAction Ignore
    $env:CLAUDE_CODE_OAUTH_TOKEN = 'tok-in-this-window'
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal' -Model sonnet) -join "`n"
    Check ($out -match 'sign-in: the long-lived token this window already has' -and $out -match 'TOKEN=set' -and $out -notmatch 'tok-in-this-window') 'a window that has the token uses it as it is'
    Check ($out -match '--model sonnet --effort medium --name "Bosun Claude campaign" "/goal') '-Model starts another model'
    Remove-Item $state -ErrorAction Ignore
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal' -Effort low) -join "`n"
    Check ($out -match '--model opus --effort low --name "Bosun Claude campaign" "/goal') '-Effort starts another effort'

    # The window closes: the launcher's process ends, and nothing it started keeps running. The stand-in claude
    # starts a process of its own and both wait; each ends by itself after two minutes whatever happens here.
    Remove-Item $state -ErrorAction Ignore
    $tree = Join-Path $work 'tree'
    New-Item -ItemType Directory -Force $tree | Out-Null
    $pids = Join-Path $tree 'pids.txt'
    Set-Content (Join-Path $tree 'linger.ps1') @'
param($pids)
$child = Start-Process (Get-Process -Id $PID).Path -NoNewWindow -PassThru -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120'
Set-Content -LiteralPath $pids -Value "$PID $($child.Id)"
Start-Sleep 120
'@
    Set-Content (Join-Path $tree 'claude.cmd') "@`"$pwsh`" -NoProfile -File `"%~dp0linger.ps1`" `"$pids`"" -Encoding ascii
    $env:Path = "$tree;$path"
    $launcher = Start-Process $pwsh -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $tree 'out.txt') -ArgumentList '-NoProfile', '-File', "`"$script`"", '-UserEnvKey', "`"$envKey`"", '-Goal', 'tree-test'
    $started = @()
    try {
      # Read only once it is whole: the file exists a moment before its line is written.
      $line = ''
      foreach ($i in 1..300) {
        $line = if (Test-Path -LiteralPath $pids) { "$(Get-Content -LiteralPath $pids -Raw -ErrorAction Ignore)".Trim() } else { '' }
        if ($line -match '^\d+ \d+$') { break }
        Start-Sleep -Milliseconds 200
      }
      $started = @(if ($line -match '^\d+ \d+$') { $line -split ' ' | ForEach-Object { [int]$_ } })
      $before = @($started | Where-Object { Get-Process -Id $_ -ErrorAction Ignore })
      Check ($started.Count -eq 2 -and $before.Count -eq 2 -and -not $launcher.HasExited) "the launcher's session and the process it started are running ($($started -join ', '))"
      Stop-Process -Id $launcher.Id -Force  # as closing the window ends the launcher: no script of its own gets to run
      $left = $started
      foreach ($i in 1..75) { $left = @($started | Where-Object { Get-Process -Id $_ -ErrorAction Ignore }); if (-not $left) { break }; Start-Sleep -Milliseconds 200 }
      Check ($started.Count -eq 2 -and -not $left) "when the launcher ends, its whole process tree ends with it (still running: $($left -join ', '))"
    } finally {
      # Whatever is left is stopped and waited for, so nothing outlives the test or holds its folder open.
      $ids = @($launcher.Id) + $started
      foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction Ignore }
      foreach ($i in 1..50) { if (-not ($ids | Where-Object { Get-Process -Id $_ -ErrorAction Ignore })) { break }; Start-Sleep -Milliseconds 200 }
    }
  } finally {
    $env:CLAUDE_CODE_OAUTH_TOKEN = $had
    $env:Path = $path
    Remove-Item Env:RUN_GUARD_STATE
    Remove-Item Env:RUN_GUARD_BUILD -ErrorAction Ignore
    Remove-Item -LiteralPath $envKey -Recurse -Force
  }

  # D00 T03 §28: the launcher holds the machine awake while its writer runs, through a holder it starts before the
  # writer and releases when the writer returns, the window (-NoExit) outliving it; -Minutes times the hold. Each case
  # runs the launcher in a shell kept alive after it, as the shortcut's -NoExit is; every console child has no window.
  $holdDir = Join-Path $work 'hold'
  New-Item -ItemType Directory -Force $holdDir | Out-Null
  $holdKey = "HKCU:\Software\BosunStartRunHold-$PID"
  New-Item -Path $holdKey -Force | Out-Null
  Set-ItemProperty -LiteralPath $holdKey -Name 'CLAUDE_CODE_OAUTH_TOKEN' -Value 'tok-hold-test'
  # The fake writer says whether a holder held when it started, sleeps as long as asked, and exits as asked.
  Set-Content (Join-Path $holdDir 'writer.ps1') @'
$record = Join-Path $env:RUN_GUARD_BUILD 'run-hold.json'
$held = if (Test-Path $record) { $r = Get-Content $record -Raw | ConvertFrom-Json; if ($r.acquired -and (Get-Process -Id $r.pid -ErrorAction Ignore)) { "alive $($r.pid)" } else { 'gone' } } else { 'none' }
Set-Content -LiteralPath (Join-Path $env:RUN_GUARD_BUILD 'writer-saw.txt') -Value $held
Start-Sleep -Seconds ([int]$env:FAKE_WRITER_SECONDS)
Set-Content -LiteralPath (Join-Path $env:RUN_GUARD_BUILD 'done.txt') -Value ([datetime]::UtcNow.ToString('o'))
exit ([int]$env:FAKE_WRITER_EXIT)
'@
  Set-Content (Join-Path $holdDir 'claude.cmd') "@`"$pwsh`" -NoProfile -File `"%~dp0writer.ps1`"" -Encoding ascii
  function Invoke-Kept([string[]]$extra) {
    # The launcher in a shell that stays alive after it, as the shortcut's -NoExit keeps the window; returns that shell.
    # Plain, not quoted: a quoted '-Minutes' is a positional string to PowerShell, not a parameter name.
    $quoted = $extra -join ' '
    $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($a in '-NoProfile', '-NoExit', '-Command', "& '$script' -UserEnvKey '$holdKey' -Goal 'test goal' $quoted; 'LAUNCHER-DONE'") { $info.ArgumentList.Add($a) }
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardInput = $true
    [Diagnostics.Process]::Start($info)
  }
  function Wait-Done($shell, [int]$seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds); $seen = @()
    $pending = $null
    while ($true) {
      $left = [int]($deadline - (Get-Date)).TotalMilliseconds
      if ($left -le 0) { break }
      if (-not $pending) { $pending = $shell.StandardOutput.ReadLineAsync() }
      if (-not $pending.Wait($left)) { break }
      $line = $pending.Result; $pending = $null
      if ($null -eq $line) { break }
      $seen += $line; if ($line -eq 'LAUNCHER-DONE') { break }
    }
    $seen
  }
  $holdState = Join-Path $holdDir 'state.json'
  $env:RUN_GUARD_STATE = $holdState
  $env:Path = "$holdDir;$path"
  $shells = @()
  try {
    foreach ($case in @(@{ Name = 'a writer that returns'; Exit = '0' }, @{ Name = 'a writer that fails at once'; Exit = '1' })) {
      $build = Join-Path $holdDir "build-$($case.Exit)"
      New-Item -ItemType Directory -Force $build | Out-Null
      $env:RUN_GUARD_BUILD = $build; $env:FAKE_WRITER_SECONDS = '1'; $env:FAKE_WRITER_EXIT = $case.Exit
      Remove-Item -LiteralPath $holdState -ErrorAction Ignore
      $shell = Invoke-Kept @(); $shells += $shell
      $said = Wait-Done $shell
      $saw = "$(Get-Content -LiteralPath (Join-Path $build 'writer-saw.txt') -Raw -ErrorAction Ignore)".Trim()
      $record = Get-Content -LiteralPath (Join-Path $build 'run-hold.json') -Raw | ConvertFrom-Json
      # Timed from the writer's own exit to the holder's recorded release, not from when this test saw the end.
      $exited = [datetime]::Parse((Get-Content -LiteralPath (Join-Path $build 'done.txt') -Raw).Trim(), $null, 'RoundtripKind')
      # ConvertFrom-Json gives a DateTime already; a release never recorded is a failure, not a throw.
      $gap = if ($record.released) { (([datetime]$record.released).ToUniversalTime() - $exited.ToUniversalTime()).TotalSeconds } else { -1 }
      Check ($record.released_because -eq 'its launcher released it' -and $gap -ge 0 -and $gap -le 1) "$($case.Name): the hold is released within a second of the writer's exit ($([math]::Round($gap, 2)) s)"
      Check ($saw -match '^alive \d+$' -and ($said -match '^hold: the machine stays awake') -and -not (Get-Process -Id $record.pid -ErrorAction Ignore) -and -not $shell.HasExited) "$($case.Name): the holder held before the writer started and was gone within a second of it returning, the shell still open ($saw)"
    }
    # A writer that fails to start (here a claude.exe Windows will not run) releases the hold at once, the shell kept
    # open (round 7 of D00 T03 §28): the failure follows the hold's answer, so the release is timed from it.
    $build = Join-Path $holdDir 'build-nostart'
    $broken = Join-Path $holdDir 'broken'
    New-Item -ItemType Directory -Force $build, $broken | Out-Null
    [IO.File]::WriteAllText((Join-Path $broken 'claude.exe'), 'not a program')
    $env:RUN_GUARD_BUILD = $build
    Remove-Item -LiteralPath $holdState -ErrorAction Ignore
    $env:Path = "$broken;$holdDir;$path"
    try { $shell = Invoke-Kept @(); $shells += $shell; $said = Wait-Done $shell } finally { $env:Path = "$holdDir;$path" }
    $record = Get-Content -LiteralPath (Join-Path $build 'run-hold.json') -Raw | ConvertFrom-Json
    $gap = if ($record.released) { (([datetime]$record.released).ToUniversalTime() - ([datetime]$record.at).ToUniversalTime()).TotalSeconds } else { -1 }
    Check (($said -match 'claude could not start') -and $record.acquired -and $record.released_because -eq 'its launcher released it' -and $gap -ge 0 -and $gap -le 1 -and -not (Get-Process -Id $record.pid -ErrorAction Ignore) -and -not $shell.HasExited) "a writer that fails to start releases the hold within a second ($([math]::Round($gap, 2)) s), its shell still open"
    # A holder that does not answer is stopped at its bound and the writer starts anyway (round 2 of D00 T03 §28).
    $build = Join-Path $holdDir 'build-stall'
    New-Item -ItemType Directory -Force $build | Out-Null
    $env:RUN_GUARD_BUILD = $build; $env:FAKE_WRITER_SECONDS = '1'; $env:FAKE_WRITER_EXIT = '0'
    $env:BOSUN_TEST_HOLD_STALL = '60'; $env:BOSUN_TEST_HOLD_ANSWER_SECONDS = '2'
    Remove-Item -LiteralPath $holdState -ErrorAction Ignore
    try { $shell = Invoke-Kept @(); $shells += $shell; $said = Wait-Done $shell }
    finally { Remove-Item Env:BOSUN_TEST_HOLD_STALL, Env:BOSUN_TEST_HOLD_ANSWER_SECONDS -ErrorAction Ignore }
    $saw = "$(Get-Content -LiteralPath (Join-Path $build 'writer-saw.txt') -Raw -ErrorAction Ignore)".Trim()
    $stalled = @(Get-CimInstance Win32_Process -Filter "Name = 'pwsh.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($build) -and $_.CommandLine -match 'hold-awake' })
    # The window records the failed hold, which the writer saw on entry, and marks it released once the writer returns.
    $failedHold = Get-Content -LiteralPath (Join-Path $build 'run-hold.json') -Raw | ConvertFrom-Json
    $after = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-preflight.ps1') -Only hold -RunHold (Join-Path $build 'run-hold.json') -HoldRecord (Join-Path $build 'none.json') 2>&1
    Check ($failedHold.acquired -eq $false -and $failedHold.why -match 'no answer within 2 seconds' -and $failedHold.released_because -eq 'its launcher released it' -and "$after" -match 'no run holds the machine now') "a stalled holder's window records the failed hold and releases it with the writer ($($failedHold.why))"
    Check (($said -match 'the holder gave no answer within 2 seconds and was stopped') -and $saw -eq 'gone' -and $stalled.Count -eq 0) "a holder that does not answer is stopped at its bound and the writer still starts ($($said -match '^hold:'))"
    # A timed hold, with a six-second minute: held, then gone while the writer runs on.
    $build = Join-Path $holdDir 'build-timed'
    New-Item -ItemType Directory -Force $build | Out-Null
    $env:RUN_GUARD_BUILD = $build; $env:FAKE_WRITER_SECONDS = '16'; $env:FAKE_WRITER_EXIT = '0'; $env:BOSUN_TEST_MINUTE_SECONDS = '6'
    Remove-Item -LiteralPath $holdState -ErrorAction Ignore
    $shell = Invoke-Kept @('-Minutes', '1'); $shells += $shell
    foreach ($i in 1..50) { if (Test-Path (Join-Path $build 'writer-saw.txt')) { break }; Start-Sleep -Milliseconds 200 }
    $record = Get-Content -LiteralPath (Join-Path $build 'run-hold.json') -Raw | ConvertFrom-Json
    $heldEarly = [bool](Get-Process -Id $record.pid -ErrorAction Ignore)
    Start-Sleep -Seconds 7
    $goneLater = -not (Get-Process -Id $record.pid -ErrorAction Ignore)
    $writerOn = -not (Test-Path (Join-Path $build 'done.txt')) -and -not $shell.HasExited
    $timedOut = (Get-Content -LiteralPath (Join-Path $build 'run-hold.json') -Raw | ConvertFrom-Json).released_because -match 'minute\(s\) ran out'
    $null = Wait-Done $shell
    Check ($heldEarly -and $goneLater -and $writerOn -and $timedOut -and $record.minutes -gt 0) "-Minutes 1 holds for its minute and lets go while the writer runs on (held early: $heldEarly, gone later: $goneLater, writer on: $writerOn, minutes recorded: $($record.minutes))"
    Remove-Item Env:BOSUN_TEST_MINUTE_SECONDS
    # Outside its range, -Minutes is refused before anything starts.
    foreach ($bad in '0', '10081') {
      $build = Join-Path $holdDir "build-bad-$bad"
      New-Item -ItemType Directory -Force $build | Out-Null
      $env:RUN_GUARD_BUILD = $build
      $out = (& $pwsh -NoProfile -File $script -UserEnvKey $holdKey -Goal 'test goal' -Minutes $bad 2>&1) -join "`n"
      Check ($LASTEXITCODE -eq 2 -and $out -match '-Minutes takes 1 to 10080' -and -not (Test-Path (Join-Path $build 'writer-saw.txt')) -and -not (Test-Path (Join-Path $build 'run-hold.json'))) "-Minutes $bad is refused with nothing started"
    }
  } finally {
    foreach ($shell in $shells) { if (-not $shell.HasExited) { & taskkill /T /F /PID $shell.Id 2>&1 | Out-Null } }
    $env:Path = $path
    Remove-Item Env:RUN_GUARD_STATE, Env:RUN_GUARD_BUILD, Env:FAKE_WRITER_SECONDS, Env:FAKE_WRITER_EXIT, Env:BOSUN_TEST_MINUTE_SECONDS -ErrorAction Ignore
    Remove-Item -LiteralPath $holdKey -Recurse -Force -ErrorAction Ignore
  }

  # The Antigravity run against a fake agy and a state file of its own (D00 T03 §19): the guard is claimed for this
  # window's process before agy starts, agy gets the run's id, and the run ends when agy returns, however it returns,
  # or when the window's process ends.
  $agyHome = Join-Path $work 'fake-agy'
  New-Item -ItemType Directory -Force $agyHome | Out-Null
  $agySeen = Join-Path $agyHome 'seen.json'
  Set-Content (Join-Path $agyHome 'fake_agy.py') @"
import json, os, sys, time
seen = {'args': sys.argv[1:], 'session': os.environ.get('BOSUN_RUN_SESSION'), 'pid': os.getpid()}
# Whether the hold was taken before agy began (completion review of D00 T03 §28): a record held and not yet released.
try:
    with open(os.path.join(os.environ['RUN_GUARD_BUILD'], 'run-hold.json'), encoding='utf-8-sig') as hold:
        record = json.load(hold)
    seen['held_on_entry'] = bool(record.get('acquired')) and not record.get('released')
except OSError:
    seen['held_on_entry'] = False
with open(r'$agySeen', 'w', encoding='utf-8') as out:
    json.dump(seen, out)
time.sleep(float(os.environ.get('FAKE_AGY_SLEEP') or 0))
sys.exit(int(os.environ.get('FAKE_AGY_EXIT') or 0))
"@ -Encoding ascii
  $pythonExe = (Get-Command python -CommandType Application | Select-Object -First 1).Source
  Set-Content (Join-Path $agyHome 'agy.cmd') "@`"$pythonExe`" `"%~dp0fake_agy.py`" %*" -Encoding ascii
  # Verify every CLI flag emitted for Antigravity is accepted by the installed agy binary (or the known flag set on CI).
  $emittedFlags = @(
    foreach ($text in $agyOut, $agyOrch) {
      $cmdLine = ($text -split "`n" | Where-Object { $_ -like 'agy *' } | Select-Object -Last 1)
      $head = ($cmdLine -split ' "/goal ', 2)[0]
      [regex]::Matches($head, '(?<= )--[a-z-]+') | ForEach-Object { $_.Value }
    }
  ) | Select-Object -Unique
  $realAgy = Get-Command agy -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if ($realAgy) {
    $agyHelp = (& $realAgy.Source --help 2>&1) -join "`n"
    $unknownFlags = @($emittedFlags | Where-Object { $agyHelp -notmatch "(?m)^\s*$([regex]::Escape($_))\b" })
    Check ($LASTEXITCODE -eq 0 -and $emittedFlags.Count -ge 3 -and -not $unknownFlags -and $emittedFlags -notcontains '--settings') "every Antigravity CLI flag appears in agy --help ($($emittedFlags -join ' '))"
  } else {
    $knownFlags = @('--dangerously-skip-permissions', '--model', '--effort', '--prompt-interactive')
    $unknownFlags = @($emittedFlags | Where-Object { $_ -notin $knownFlags })
    Check ($emittedFlags.Count -ge 3 -and -not $unknownFlags -and $emittedFlags -notcontains '--settings') "every Antigravity CLI flag is in the known agy set ($($emittedFlags -join ' '))"
  }
  $agyState = Join-Path $work 'agy-state.json'
  $guardCli = Join-Path $repo 'scripts\run-guard.py'
  function AgyState { if (Test-Path $agyState) { Get-Content $agyState -Raw | ConvertFrom-Json } }
  $env:RUN_GUARD_STATE = $agyState
  $env:RUN_GUARD_BUILD = Join-Path $work 'guard-build'
  $env:Path = "$agyHome;$path"
  try {
    foreach ($exit in 0, 1) {
      Remove-Item -LiteralPath $agySeen -ErrorAction Ignore
      $env:FAKE_AGY_EXIT = "$exit"
      $out = (& $pwsh -NoProfile -File $script -Agent antigravity) -join "`n"
      $code = $LASTEXITCODE
      $seen = if (Test-Path $agySeen) { Get-Content $agySeen -Raw | ConvertFrom-Json }
      $saved = AgyState
      Check ($seen -and $seen.session -eq $saved.session_id -and $saved.runner -eq 'antigravity' -and $saved.owner_pid -gt 0) "agy gets the run's id in BOSUN_RUN_SESSION, and the guard was claimed for the window's process (exit $exit)"
      Check (@($seen.args) -notcontains '--settings' -and (@($seen.args) -join ' ') -notmatch 'maxStopHookContinuations' -and @($seen.args)[-1] -like '/goal Finish the Bosun*') "agy gets its /goal prompt with no unknown --settings (exit $exit)"
      # D00 T03 §28: the Antigravity writer is held for too, and released when agy returns.
      $agyHold = Get-Content -LiteralPath (Join-Path $env:RUN_GUARD_BUILD 'run-hold.json') -Raw | ConvertFrom-Json
      Check ($seen.held_on_entry -eq $true) "the Antigravity run is held before agy begins (exit $exit)"
      Check ($out -match 'hold: the machine stays awake' -and $agyHold.acquired -and $agyHold.released_because -eq 'its launcher released it' -and -not (Get-Process -Id $agyHold.pid -ErrorAction Ignore)) "the Antigravity run is held while agy runs, and released when agy returns with $exit"
      Check ($code -eq $exit -and $saved.status -eq 'ended' -and -not $saved.active -and $saved.why -match "agy returned \(exit $exit\)") "when agy returns with $exit, the run is recorded ended though the window lives on ($($saved.status): $($saved.why))"
    }
    Remove-Item Env:FAKE_AGY_EXIT
    # While a Claude Code run holds the tree, the Antigravity start is refused before agy runs.
    $claudeSession = [guid]::NewGuid().ToString()
    & $pythonExe $guardCli start --session $claudeSession | Out-Null
    Remove-Item -LiteralPath $agySeen -ErrorAction Ignore
    $out = (& $pwsh -NoProfile -File $script -Agent antigravity) -join "`n"
    Check ($LASTEXITCODE -ne 0 -and -not (Test-Path $agySeen) -and (AgyState).session_id -eq $claudeSession) "a live Claude Code run refuses the Antigravity start before agy runs ($(($out -split "`n" | Select-Object -Last 1)))"
    Remove-Item -LiteralPath $agyState -ErrorAction Ignore

    # The window's process killed while agy runs: the run is recorded ended at the next look, and agy is gone with it.
    Remove-Item -LiteralPath $agySeen -ErrorAction Ignore
    $env:FAKE_AGY_SLEEP = '120'
    $launcher = Start-Process $pwsh -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $agyHome 'out.txt') -ArgumentList '-NoProfile', '-File', "`"$script`"", '-Agent', 'antigravity'
    $agyPid = 0
    try {
      foreach ($i in 1..150) { if (Test-Path $agySeen) { $agyPid = [int]((Get-Content $agySeen -Raw -ErrorAction Ignore | ConvertFrom-Json -ErrorAction Ignore).pid); if ($agyPid) { break } }; Start-Sleep -Milliseconds 200 }
      $live = (& $pythonExe $guardCli status | Select-Object -First 1)
      Check ($agyPid -and $live -eq 'active') "while agy runs, the Antigravity run is active ($live)"
      Stop-Process -Id $launcher.Id -Force
      foreach ($i in 1..50) { if (-not (Get-Process -Id $launcher.Id -ErrorAction Ignore)) { break }; Start-Sleep -Milliseconds 200 }
      $after = (& $pythonExe $guardCli status | Select-Object -First 1)
      $gone = $true
      foreach ($i in 1..50) { if ($agyPid -and (Get-Process -Id $agyPid -ErrorAction Ignore)) { $gone = $false; Start-Sleep -Milliseconds 200 } else { $gone = $true; break } }
      Check ($after -eq 'ended' -and $gone) "when the window's process is killed, the run is recorded ended and agy ends with it ($after)"
      Remove-Item Env:FAKE_AGY_SLEEP
      Remove-Item -LiteralPath $agySeen -ErrorAction Ignore
      $out = (& $pwsh -NoProfile -File $script -Agent antigravity) -join "`n"
      Check ($LASTEXITCODE -eq 0 -and (Test-Path $agySeen) -and (AgyState).status -eq 'ended') 'a new start then succeeds'
    } finally {
      foreach ($id in @($launcher.Id, $agyPid) | Where-Object { $_ }) { Stop-Process -Id $id -Force -ErrorAction Ignore }
    }
  } finally {
    Remove-Item Env:FAKE_AGY_EXIT, Env:FAKE_AGY_SLEEP, Env:RUN_GUARD_STATE, Env:RUN_GUARD_BUILD -ErrorAction Ignore
    $env:Path = $path
  }

  # The one-time import from the vault, against a fake `op`: the token lands in the given key and is never printed.
  $vaultKey = "HKCU:\Software\BosunStartRunVault-$PID"
  $record = Join-Path $work 'claude-token.json'
  $store = Join-Path $repo 'scripts\store-claude-token.ps1'
  Set-Content (Join-Path $fake 'op.cmd') "@echo off`r`nif `"%1 %2`"==`"read op://Vault/item/credential`" (echo tok-from-vault& exit /b 0)`r`nif `"%1 %2`"==`"read op://Vault/nodate/credential`" (echo tok-no-date& exit /b 0)`r`nif `"%1 %2 %3`"==`"item get item`" (echo {`"created_at`":`"2026-03-05T08:04:52Z`"}& exit /b 0)`r`necho not signed in 1>&2`r`nexit /b 1" -Encoding ascii
  try {
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record -IssuedOn 2026-11-10) -join "`n"
    Check ($LASTEXITCODE -eq 0 -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-from-vault' -and $out -notmatch 'tok-from-vault') '-FromVault stores the vault''s token without printing it'
    Check ((Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-11-10') 'and records the day it was made'
    & $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record | Out-Null
    Check ((Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-03-05') 'with no day given, the day is the one the vault item was created, never today'
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/nodate/credential' -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 0 -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-no-date' -and -not (Test-Path $record) -and $out -match '-RecordOnly -IssuedOn') 'a vault item with no readable day stores the token with no day, and drops the old token''s'
    & $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record -IssuedOn 2026-11-10 | Out-Null
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/other/credential' -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 1 -and $out -match 'could not read the token from the vault' -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-from-vault') 'a vault that can''t be read changes nothing and says so'
    # The missing CLI is given an explicit item, so it is told apart from a missing setting (D00 T05 §7).
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $work 'no-op.exe') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 1 -and $out -match 'is not on PATH') 'a missing 1Password CLI is named'

    # Where the vault item comes from (D00 T05 §7): -VaultItem, else BOSUN_VAULT_ITEM, else the overlay's vault_item,
    # in a scratch checkout holding copies of the script and its library, so this checkout's own overlay is never read.
    # A logging stand-in `op` records every call, so both of the importer's calls are checked for the item they name.
    $kit = Join-Path $work 'vault-kit'
    New-Item -ItemType Directory -Path (Join-Path $kit 'scripts\lib'), (Join-Path $kit '.private') -Force | Out-Null
    Copy-Item -LiteralPath $store -Destination (Join-Path $kit 'scripts\store-claude-token.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'scripts\lib\overlay.ps1') -Destination (Join-Path $kit 'scripts\lib\overlay.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'scripts\lib\token-record.ps1') -Destination (Join-Path $kit 'scripts\lib\token-record.ps1')
    $kitStore = Join-Path $kit 'scripts\store-claude-token.ps1'
    $config = Join-Path $kit '.private\config.toml'
    $opLog = Join-Path $work 'op-calls.txt'
    Set-Content (Join-Path $fake 'op-log.cmd') "@echo off`r`necho %*>>`"$opLog`"`r`nif `"%1`"==`"read`" (echo tok-%2& exit /b 0)`r`nif `"%1 %2`"==`"item get`" (echo {`"created_at`":`"2026-04-02T10:00:00Z`"}& exit /b 0)`r`nexit /b 1" -Encoding ascii
    $opLogged = Join-Path $fake 'op-log.cmd'
    $savedItem = $env:BOSUN_VAULT_ITEM
    function Invoke-Import([string[]]$more) {
      Remove-Item -LiteralPath $opLog -ErrorAction Ignore
      $said = (& $pwsh -NoProfile -File $kitStore -FromVault -Op $opLogged -Key $vaultKey -Record $record @more) -join "`n"
      [pscustomobject]@{ Exit = $LASTEXITCODE; Said = $said; Calls = @(if (Test-Path $opLog) { Get-Content $opLog }) }
    }
    try {
      Set-Content -LiteralPath $config -Value 'vault_item = "op://FileVault/fileitem/credential"'
      $env:BOSUN_VAULT_ITEM = 'op://EnvVault/envitem/credential'
      $r = Invoke-Import @('-VaultItem', 'op://ArgVault/argitem/credential')
      Check ($r.Exit -eq 0 -and $r.Calls -contains 'read op://ArgVault/argitem/credential' -and $r.Calls -contains 'item get argitem --vault ArgVault --format json') 'an explicit -VaultItem wins over the variable and the overlay, for both op calls'
      $r = Invoke-Import @()
      Check ($r.Exit -eq 0 -and $r.Calls -contains 'read op://EnvVault/envitem/credential' -and $r.Calls -contains 'item get envitem --vault EnvVault --format json') 'BOSUN_VAULT_ITEM wins over the overlay, for both op calls'
      # Whitespace is no setting: spaces in the variable or in -VaultItem fall through to the overlay's item.
      $env:BOSUN_VAULT_ITEM = '   '
      $r = Invoke-Import @('-VaultItem', '  ')
      Check ($r.Exit -eq 0 -and $r.Calls -contains 'read op://FileVault/fileitem/credential') 'spaces in BOSUN_VAULT_ITEM or -VaultItem count as unset, never as an item'
      Remove-Item -LiteralPath Env:BOSUN_VAULT_ITEM
      $r = Invoke-Import @()
      Check ($r.Exit -eq 0 -and $r.Calls -contains 'read op://FileVault/fileitem/credential' -and $r.Calls -contains 'item get fileitem --vault FileVault --format json') 'without either, the overlay''s vault_item is read, for both op calls'
      Check ((Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-op://FileVault/fileitem/credential' -and (Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-04-02') 'and the token and its day read back from the key and the record'
      $beforeToken = (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN'); $beforeDay = Get-Content $record -Raw
      Remove-Item -LiteralPath $config
      $r = Invoke-Import @()
      # Each name on its own: the file key must appear apart from the variable it is the suffix of (case-sensitive).
      Check ($r.Exit -eq 1 -and $r.Said -cmatch 'BOSUN_VAULT_ITEM' -and $r.Said -cmatch '(^|[^A-Z_])vault_item' -and -not $r.Calls.Count -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq $beforeToken -and (Get-Content $record -Raw) -eq $beforeDay) 'with no item set up anywhere it exits 1 naming both, calls no op and changes nothing'
      Set-Content -LiteralPath $config -Value 'vault_item = "unterminated'
      $r = Invoke-Import @()
      Check ($r.Exit -eq 1 -and $r.Said -match 'could not be read from .private/config.toml' -and $r.Said -cmatch 'BOSUN_VAULT_ITEM' -and $r.Said -cmatch '(^|[^A-Z_])vault_item' -and -not $r.Calls.Count -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq $beforeToken -and (Get-Content $record -Raw) -eq $beforeDay) 'a malformed overlay config is refused the same way, naming both, the token and its day unchanged'
      # The other modes never read the setting: they work with the malformed config still there.
      $out = (& $pwsh -NoProfile -File $kitStore -RecordOnly -IssuedOn 2026-04-01 -Key $vaultKey -Record $record) -join "`n"
      Check ($LASTEXITCODE -eq 0 -and (Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-04-01') '-RecordOnly never reads the vault setting'
      $out = ('tok-pasted' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-04-01) -join "`n"
      Check ($LASTEXITCODE -eq 0 -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-pasted') 'a pasted token never reads the vault setting'
      $out = (& $pwsh -NoProfile -File $kitStore -Remove -Key $vaultKey -Record $record) -join "`n"
      Check ($LASTEXITCODE -eq 0 -and -not (Test-Path $record)) '-Remove never reads the vault setting'

      # The day is tied to its token (D00 T03 §23): every record names the fingerprint of the token stored with it.
      . (Join-Path $repo 'scripts\lib\token-record.ps1')
      Set-Content -LiteralPath $config -Value 'vault_item = "op://FileVault/fileitem/credential"'
      function Get-RecordFingerprint { "$((Get-Content $record -Raw | ConvertFrom-Json).token)" }
      function Get-Stored { (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') }
      $null = Invoke-Import @()
      Check ((Get-RecordFingerprint) -eq (Get-TokenFingerprint (Get-Stored)) -and (Get-Content $record -Raw) -notmatch 'tok-') 'a vault import records the fingerprint of the token it stored, never the token'
      'tok-pasted-two' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-04-01 | Out-Null
      Check ((Get-RecordFingerprint) -eq (Get-TokenFingerprint 'tok-pasted-two')) 'a pasted token records its fingerprint'
      & $pwsh -NoProfile -File $kitStore -RecordOnly -IssuedOn 2026-04-03 -Key $vaultKey -Record $record | Out-Null
      Check ((Get-RecordFingerprint) -eq (Get-TokenFingerprint 'tok-pasted-two') -and (Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-04-03') '-RecordOnly records the day for the token stored now'

      # A record that cannot be written (held open without sharing): the store to repair is named, the record is left
      # as it was, so the preflight distrusts it for the new token, and no temporary file is left behind.
      $nodateOp = Join-Path $fake 'op-nodate.cmd'
      Set-Content $nodateOp "@echo off`r`nif `"%1`"==`"read`" (echo tok-vault-nodate& exit /b 0)`r`nexit /b 1" -Encoding ascii
      $cases = @(
        @{ Name = 'a pasted token with a known day'; Run = { 'tok-pasted-three' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-04-05 }; Says = 'its day could not be recorded'; Token = 'tok-pasted-three' },
        @{ Name = 'a vault import with a known day'; Run = { & $pwsh -NoProfile -File $kitStore -FromVault -Op $opLogged -Key $vaultKey -Record $record }; Says = 'its day could not be recorded'; Token = 'tok-op://FileVault/fileitem/credential' },
        @{ Name = 'a vault import whose day cannot be read'; Run = { & $pwsh -NoProfile -File $kitStore -FromVault -Op $nodateOp -Key $vaultKey -Record $record }; Says = 'could not be removed'; Token = 'tok-vault-nodate' }
      )
      foreach ($case in $cases) {
        'tok-old-seed' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-03-01 | Out-Null
        $before = Get-Content $record -Raw
        $hold = [IO.File]::Open($record, 'Open', 'Read', 'None')
        try { $said = (& $case.Run 2>&1) -join "`n"; $code = $LASTEXITCODE } finally { $hold.Dispose() }
        $left = @(Get-ChildItem -LiteralPath (Split-Path $record) -Filter '*.tmp' -Force).Count
        Check ($code -eq 1 -and $said -match $case.Says -and $said.Contains($record) -and $said -match '-RecordOnly -IssuedOn' -and (Get-Stored) -ceq $case.Token -and (Get-Content $record -Raw) -eq $before -and (Get-RecordFingerprint) -ne (Get-TokenFingerprint (Get-Stored)) -and -not $said.Contains($case.Token) -and $left -eq 0) "$($case.Name), its record held open, exits 1 naming the record, the old record kept and distrusted, no temporary file left ($said)"
      }

      # A key the token cannot be written to: the token store is named, and the record is left as it was.
      'tok-before-lock' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-03-02 | Out-Null
      $before = Get-Content $record -Raw
      # The rule is set on the test's own key through the registry API (Set-Acl does not find HKCU keys by this path).
      $sub = $vaultKey -replace '^HKCU:\\', ''
      $deny = [Security.AccessControl.RegistryAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, 'SetValue', 'Deny')
      function Set-KeyRule([bool]$add) {
        $handle = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($sub, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::ChangePermissions -bor [Security.AccessControl.RegistryRights]::ReadPermissions)
        try { $acl = $handle.GetAccessControl(); if ($add) { $acl.AddAccessRule($deny) } else { [void]$acl.RemoveAccessRule($deny) }; $handle.SetAccessControl($acl) } finally { $handle.Dispose() }
      }
      Set-KeyRule $true
      try { $said = ('tok-after-lock' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-04-06 2>&1) -join "`n"; $code = $LASTEXITCODE }
      finally { Set-KeyRule $false }
      Check ($code -eq 1 -and $said -match 'could not be stored' -and $said.Contains($vaultKey) -and (Get-Stored) -ceq 'tok-before-lock' -and (Get-Content $record -Raw) -eq $before -and -not $said.Contains('tok-after-lock')) "a token the key will not take exits 1 naming the key, the old token and its record kept ($said)"

      # A correction that cannot be written leaves the old day for this same token in force, and says so plainly.
      $hold = [IO.File]::Open($record, 'Open', 'Read', 'None')
      try { $said = (& $pwsh -NoProfile -File $kitStore -RecordOnly -IssuedOn 2026-01-15 -Key $vaultKey -Record $record) -join "`n"; $code = $LASTEXITCODE } finally { $hold.Dispose() }
      Check ($code -eq 1 -and $said -match 'may still stand' -and $said -match 'before starting a campaign' -and (Get-Content $record -Raw) -eq $before) "a failed -RecordOnly correction on a record it cannot read says the old day may still stand ($said)"
      # Readable but not writable (read-only): the record is known to be this token's, so the old day surely stands.
      Set-ItemProperty -LiteralPath $record -Name IsReadOnly -Value $true
      try { $said = (& $pwsh -NoProfile -File $kitStore -RecordOnly -IssuedOn 2026-01-15 -Key $vaultKey -Record $record) -join "`n"; $code = $LASTEXITCODE } finally { Set-ItemProperty -LiteralPath $record -Name IsReadOnly -Value $false }
      Check ($code -eq 1 -and $said -match 'still stands and is still trusted' -and $said -match 'before starting a campaign' -and (Get-Content $record -Raw) -eq $before) "a failed -RecordOnly correction on a read-only record says the old day still stands for this token ($said)"
      # A renewal written into the first token's recovery item: the item's creation day is the old token's, so an item
      # changed on a later day gives no day, and one changed the day it was made keeps it.
      $editedOp = Join-Path $fake 'op-edited.cmd'
      Set-Content $editedOp "@echo off`r`nif `"%1`"==`"read`" (echo tok-renewed& exit /b 0)`r`nif `"%1 %2`"==`"item get`" (echo {`"created_at`":`"2025-12-01T08:00:00Z`",`"updated_at`":`"2026-11-01T09:00:00Z`"}& exit /b 0)`r`nexit /b 1" -Encoding ascii
      'tok-old-seed' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $record -IssuedOn 2026-03-01 | Out-Null
      $said = (& $pwsh -NoProfile -File $kitStore -FromVault -Op $editedOp -Key $vaultKey -Record $record 2>&1) -join "`n"; $code = $LASTEXITCODE
      Check ($code -eq 0 -and (Get-Stored) -ceq 'tok-renewed' -and -not (Test-Path $record) -and $said -match 'changed after it was made' -and $said -match '-RecordOnly -IssuedOn') "a vault item changed after it was made records no day for its token ($said)"
      $sameDayOp = Join-Path $fake 'op-sameday.cmd'
      Set-Content $sameDayOp "@echo off`r`nif `"%1`"==`"read`" (echo tok-sameday& exit /b 0)`r`nif `"%1 %2`"==`"item get`" (echo {`"created_at`":`"2026-02-10T08:00:00Z`",`"updated_at`":`"2026-02-10T17:00:00Z`"}& exit /b 0)`r`nexit /b 1" -Encoding ascii
      $null = & $pwsh -NoProfile -File $kitStore -FromVault -Op $sameDayOp -Key $vaultKey -Record $record
      Check ($LASTEXITCODE -eq 0 -and (Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-02-10' -and (Get-RecordFingerprint) -eq (Get-TokenFingerprint 'tok-sameday')) 'an item changed the day it was made keeps that day for its token'
      # A relative -Record is one path for the write and the rename, after Set-Location too.
      $relative = Join-Path $work 'relative-place'
      New-Item -ItemType Directory -Path (Join-Path $relative 'inner') -Force | Out-Null
      $said = ('tok-relative' | & $pwsh -NoProfile -Command "Set-Location -LiteralPath '$relative\inner'; & '$kitStore' -Key '$vaultKey' -Record '.\day.json' -IssuedOn 2026-04-08; exit `$LASTEXITCODE") -join "`n"; $code = $LASTEXITCODE
      Check ($code -eq 0 -and (Test-Path -LiteralPath (Join-Path $relative 'inner\day.json')) -and ((Get-Content (Join-Path $relative 'inner\day.json') -Raw | ConvertFrom-Json).token -eq (Get-TokenFingerprint 'tok-relative'))) "a relative -Record after Set-Location is written where PowerShell is, whole ($said)"
      # A -Record that names a folder is refused, never reported saved inside it.
      $folder = Join-Path $work 'record-is-a-folder'
      New-Item -ItemType Directory -Path $folder | Out-Null
      $said = ('tok-to-folder' | & $pwsh -NoProfile -File $kitStore -Key $vaultKey -Record $folder -IssuedOn 2026-04-07) -join "`n"; $code = $LASTEXITCODE
      Check ($code -eq 1 -and $said -match 'could not be recorded' -and @(Get-ChildItem -LiteralPath $folder -Force).Count -eq 0) "a -Record that names a folder is refused and nothing is left in it ($said)"
    } finally {
      if ($null -eq $savedItem) { Remove-Item -LiteralPath Env:BOSUN_VAULT_ITEM -ErrorAction Ignore } else { $env:BOSUN_VAULT_ITEM = $savedItem }
    }
  } finally {
    Remove-Item -LiteralPath $vaultKey -Recurse -Force -ErrorAction Ignore
  }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
