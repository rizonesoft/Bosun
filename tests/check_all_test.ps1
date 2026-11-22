# Tests scripts/check-all.ps1: with stand-in steps, that it runs every step once in order, stops at the first
# failure and names it, leaves nothing a step started running, and runs only what -Only names; that every test in
# tests/ is in the gate and in CI; and that a stale plan fails the real plan step. The stand-ins write one line each.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$gate = Join-Path $repo 'scripts/check-all.ps1'
. (Join-Path $repo 'scripts/lib/scratch.ps1')
$work = New-BosunScratchFolder -Name 'check-all-test'
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
function Wait-Gone([int]$id) {
  foreach ($n in 1..50) { if (-not (Get-Process -Id $id -ErrorAction Ignore)) { return $true }; Start-Sleep -Milliseconds 100 }
  $false
}

# A stand-in step: records its name, then passes, fails, or starts a child that sleeps on and then fails or hangs.
$stub = Join-Path $work 'stub.ps1'
Set-Content -LiteralPath $stub -Value @'
param($name, $mode = 'pass')
$here = $PSScriptRoot
Add-Content -LiteralPath (Join-Path $here 'ran.txt') -Value $name
if ($mode -like 'child-*') {
  $child = Start-Process pwsh -PassThru -NoNewWindow -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 300'
  Set-Content -LiteralPath (Join-Path $here 'child.pid') -Value $child.Id
}
# What a step sees of the gate's own mark while it runs (D00 T03 §21).
if ($env:BOSUN_GATE_MARK_DIR) { Add-Content -LiteralPath (Join-Path $here 'marks.txt') -Value @(Get-ChildItem -LiteralPath $env:BOSUN_GATE_MARK_DIR -Filter '*.json' -ErrorAction Ignore).Count }
if ($env:BOSUN_GATE_MARK_DIR) { Add-Content -LiteralPath (Join-Path $here 'mark-outputs.txt') -Value ((@(Get-ChildItem -LiteralPath $env:BOSUN_GATE_MARK_DIR -Filter '*.json' -ErrorAction Ignore) | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).output }) -join '|') }
switch ($mode) {
  'fail' { exit 3 }
  'child-fail' { exit 5 }
  'child-hang' { Start-Sleep 300 }
}
exit 0
'@
$ran = Join-Path $work 'ran.txt'
$stepFile = Join-Path $work 'steps.json'
function Set-Steps($steps) {
  ConvertTo-Json -InputObject @($steps | ForEach-Object {
      if ($_.file) { $_ } else { @{ name = $_.name; file = 'pwsh'; args = @('-NoProfile', '-File', $stub, $_.name, $(if ($_.mode) { $_.mode } else { 'pass' })) } }
    }) -Depth 4 | Set-Content -LiteralPath $stepFile
  Remove-Item -LiteralPath $ran, (Join-Path $work 'child.pid') -ErrorAction Ignore
}
function Invoke-Gate([string[]]$arguments, [string]$script = $gate) {
  $text = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $script @arguments 2>&1 | Out-String
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Ran = @(Get-Content -LiteralPath $ran -ErrorAction Ignore) }
}

try {
  # --- the gate's mark: present while its steps run, gone after a pass and after a failure, the exit code kept ---
  $marks = Join-Path $work 'marks'
  $env:BOSUN_GATE_MARK_DIR = $marks
  try {
    foreach ($case in @(@{ Steps = @(@{ name = 'one' }, @{ name = 'two' }); Exit = 0; Says = 'a pass' }, @{ Steps = @(@{ name = 'one' }, @{ name = 'two'; mode = 'fail' }); Exit = 1; Says = 'a failure' })) {
      Set-Steps $case.Steps
      Remove-Item -LiteralPath (Join-Path $work 'marks.txt') -ErrorAction Ignore
      $r = Invoke-Gate @('-StepFile', $stepFile)
      $seen = @(Get-Content -LiteralPath (Join-Path $work 'marks.txt') -ErrorAction Ignore)
      # D00 T03 §32: the mark says where the gate's output goes, read by run-guard.py status.
      $outputs = @(Get-Content -LiteralPath (Join-Path $work 'mark-outputs.txt') -ErrorAction Ignore)
      Check "the gate's mark names its output while it runs ($($case.Says))" ($outputs.Count -ge 1 -and -not ($outputs | Where-Object { $_ -ne 'the console that started it' })) "(outputs: $($outputs -join ', '))"
      Remove-Item -LiteralPath (Join-Path $work 'mark-outputs.txt') -ErrorAction Ignore
      $left = @(Get-ChildItem -LiteralPath $marks -Filter '*.json' -ErrorAction Ignore)
      Check "the gate's mark is there while each step runs and gone after $($case.Says), exit $($case.Exit) kept" ($r.Exit -eq $case.Exit -and $seen.Count -eq 2 -and -not ($seen | Where-Object { $_ -ne '1' }) -and -not $left) "(exit $($r.Exit), seen: $($seen -join ','), left: $($left.Count))"
    }
    # A mark that can't be written (here a file where its folder would go): the gate stops before its first step.
    $blocked = Join-Path $work 'not-a-folder'
    Set-Content -LiteralPath $blocked -Value 'a file where the marks folder would go'
    $env:BOSUN_GATE_MARK_DIR = $blocked
    Set-Steps @(@{ name = 'one' })
    $r = Invoke-Gate @('-StepFile', $stepFile)
    Check "a gate whose mark can't be written stops before its first step, saying why" ($r.Exit -eq 1 -and -not $r.Ran -and $r.Text -match "the gate's mark couldn't be written") "(exit $($r.Exit), ran: $($r.Ran -join ','): $($r.Text))"
  } finally { Remove-Item Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore }

  # --- the runner, with stand-in steps ---
  Set-Steps @(@{ name = 'one' }, @{ name = 'two' }, @{ name = 'three' })
  $listed = Invoke-Gate @('-StepFile', $stepFile, '-List')
  Check '-List prints the steps and runs none' ($listed.Exit -eq 0 -and -not $listed.Ran -and ($listed.Text -split "`r?`n" | Where-Object { $_ }).Count -eq 3) "(exit $($listed.Exit), ran: $($listed.Ran -join ','): $($listed.Text))"
  $order = @($listed.Text -split "`r?`n" | Where-Object { $_ } | ForEach-Object { ($_ -split ':')[0] })
  $r = Invoke-Gate @('-StepFile', $stepFile)
  Check 'a run where every step passes runs each once, in the listed order, and exits 0' ($r.Exit -eq 0 -and ($r.Ran -join ',') -eq ($order -join ',') -and ($r.Ran -join ',') -eq 'one,two,three' -and $r.Text -match 'all 3 steps passed') "(exit $($r.Exit), ran: $($r.Ran -join ','))"

  Set-Steps @(@{ name = 'one' }, @{ name = 'two'; mode = 'fail' }, @{ name = 'three' })
  $r = Invoke-Gate @('-StepFile', $stepFile)
  Check 'a failing step stops the run and nothing after it runs' ($r.Exit -eq 1 -and ($r.Ran -join ',') -eq 'one,two') "(exit $($r.Exit), ran: $($r.Ran -join ','))"
  Check 'it is named with its exit code and its command' ($r.Text -match 'FAIL two: exit 3 from: pwsh -NoProfile -File' -and $r.Text -notmatch 'steps passed') "($($r.Text))"

  Set-Steps @(@{ name = 'one' }, @{ name = 'gone'; file = 'no-such-program-here'; args = @('x') }, @{ name = 'three' })
  $r = Invoke-Gate @('-StepFile', $stepFile)
  Check 'a step whose program is not on PATH is named as that and stops the run' ($r.Exit -eq 1 -and ($r.Ran -join ',') -eq 'one' -and $r.Text -match 'FAIL gone: no-such-program-here is not on PATH') "(exit $($r.Exit), ran: $($r.Ran -join ','): $($r.Text))"

  Set-Steps @(@{ name = 'one' }, @{ name = 'two' }, @{ name = 'three' })
  $r = Invoke-Gate @('-StepFile', $stepFile, '-Only', 'three,one')
  Check '-Only runs just the steps it names, in the gate''s order' ($r.Exit -eq 0 -and ($r.Ran -join ',') -eq 'one,three') "(ran: $($r.Ran -join ','))"
  Remove-Item -LiteralPath $ran -ErrorAction Ignore
  $r = Invoke-Gate @('-StepFile', $stepFile, '-Only', 'two,four')
  Check '-Only with a name that is not a step exits 2 and runs nothing' ($r.Exit -eq 2 -and -not $r.Ran -and $r.Text -match 'no step named four') "(exit $($r.Exit), ran: $($r.Ran -join ','))"

  # --- nothing a step started outlives it ---
  Set-Steps @(@{ name = 'one'; mode = 'child-fail' }, @{ name = 'two' })
  $r = Invoke-Gate @('-StepFile', $stepFile)
  $child = [int](Get-Content -LiteralPath (Join-Path $work 'child.pid') -ErrorAction Ignore)
  Check 'a step that fails leaves no child running' ($r.Exit -eq 1 -and $r.Text -match 'FAIL one: exit 5' -and $child -gt 0 -and (Wait-Gone $child) -and ($r.Ran -join ',') -eq 'one') "(exit $($r.Exit), child $child)"

  Set-Steps @(@{ name = 'one'; mode = 'child-hang' }, @{ name = 'two' })
  $r = Invoke-Gate @('-StepFile', $stepFile, '-StepMinutes', '0.1')
  $child = [int](Get-Content -LiteralPath (Join-Path $work 'child.pid') -ErrorAction Ignore)
  Check 'a step past its limit is stopped and named, with its child, and the run ends' ($r.Exit -eq 1 -and $r.Text -match 'FAIL one: stopped at its 0\.1 minute limit' -and $child -gt 0 -and (Wait-Gone $child) -and ($r.Ran -join ',') -eq 'one') "(exit $($r.Exit), child ${child}: $($r.Text))"

  # --- a queued step may wait for its turn without using up its own limit ---
  # A stand-in bosun that waits for its "slot" as long as BOSUN_FAKE_WAIT says, then runs the command it was given.
  $queueBin = Join-Path $work 'queue-bin'
  New-Item -ItemType Directory -Path $queueBin | Out-Null
  Set-Content -LiteralPath (Join-Path $queueBin 'fake-bosun.ps1') -Value 'Start-Sleep ([int]$env:BOSUN_FAKE_WAIT); $rest = @($args | Select-Object -Skip 3); & $args[2] @rest; exit $LASTEXITCODE'
  Set-Content -LiteralPath (Join-Path $queueBin 'bosun.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-bosun.ps1`" %*"
  $savedPath = $env:PATH
  function Set-Queued([string]$mode) {
    ConvertTo-Json -InputObject @(@{ name = 'queued'; file = 'pwsh'; args = @('-NoProfile', '-File', $stub, 'queued', $mode); queue = $true }, @{ name = 'after'; file = 'pwsh'; args = @('-NoProfile', '-File', $stub, 'after', 'pass') }) -Depth 4 | Set-Content -LiteralPath $stepFile
    Remove-Item -LiteralPath $ran, (Join-Path $work 'child.pid') -ErrorAction Ignore
  }
  try {
    $env:PATH = "$queueBin;$env:PATH"
    # The wait for a slot (9 s) is longer than the step's own limit (6 s): the limit starts when the turn comes.
    $env:BOSUN_FAKE_WAIT = '9'
    Set-Queued 'pass'
    $r = Invoke-Gate @('-StepFile', $stepFile, '-StepMinutes', '0.1', '-QueueMinutes', '1')
    Check 'a queued step that waits longer than its own limit for a slot still runs and passes' ($r.Exit -eq 0 -and ($r.Ran -join ',') -eq 'queued,after') "(exit $($r.Exit): $($r.Text))"

    # The slot comes at once and the step hangs: it gets its own limit and no more, whatever the queue allowance.
    $env:BOSUN_FAKE_WAIT = '0'
    Set-Queued 'child-hang'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Gate @('-StepFile', $stepFile, '-StepMinutes', '0.1', '-QueueMinutes', '5')
    $child = [int](Get-Content -LiteralPath (Join-Path $work 'child.pid') -ErrorAction Ignore)
    Check 'a queued step that has its slot is stopped at its own limit, not its limit plus the queue allowance' ($r.Exit -eq 1 -and $r.Text -match 'FAIL queued: stopped at its 0\.1 minute limit' -and
      $clock.Elapsed.TotalSeconds -lt 60 -and $child -gt 0 -and (Wait-Gone $child) -and ($r.Ran -join ',') -eq 'queued') "(exit $($r.Exit), $([int]$clock.Elapsed.TotalSeconds) s, child ${child}: $($r.Text))"

    # No slot ever comes: the wait itself is bounded, said as that, and the step never ran.
    $env:BOSUN_FAKE_WAIT = '300'
    Set-Queued 'pass'
    $clock.Restart()
    $r = Invoke-Gate @('-StepFile', $stepFile, '-StepMinutes', '5', '-QueueMinutes', '0.1')
    Check 'a queued step that never gets a slot is stopped at the queue allowance and named as waiting' ($r.Exit -eq 1 -and $r.Text -match 'FAIL queued: waited 0\.1 minutes for a queue slot and did not get one' -and
      $clock.Elapsed.TotalSeconds -lt 60 -and -not $r.Ran) "(exit $($r.Exit), $([int]$clock.Elapsed.TotalSeconds) s: $($r.Text))"
  } finally { $env:PATH = $savedPath; Remove-Item Env:BOSUN_FAKE_WAIT -ErrorAction Ignore }

  # The wrapper marks its start before it runs anything: a mark it cannot write means the step is not started, since
  # it would otherwise run without its own time limit.
  $wrapper = Join-Path $repo 'scripts/lib/queued-step.ps1'
  function ConvertTo-Command([string[]]$words) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($words) -Compress))) }
  Remove-Item -LiteralPath $ran -ErrorAction Ignore
  $unwritable = Join-Path $stub 'mark'  # a path under a file, so it can never be created
  $text = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $wrapper $unwritable (ConvertTo-Command @('pwsh', '-NoProfile', '-File', $stub, 'wrapped', 'pass')) 2>&1 | Out-String
  Check 'a start mark that cannot be written stops the wrapper before the command runs' ($LASTEXITCODE -eq 3 -and $text -match 'could not write its start mark' -and -not (Test-Path -LiteralPath $ran)) "(exit ${LASTEXITCODE}: $text)"
  # One argument, none, and several all reach the command whole: a lone argument must not be splatted by letter, and
  # one shaped like a parameter with a value must not be split in two, as `pwsh -File` would split it.
  # The echo is Python, which gets its arguments as given: a PowerShell echo run with -File would split them itself.
  $echo = Join-Path $work 'echo.py'
  Set-Content -LiteralPath $echo -Value 'import os, sys; open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "echo.txt"), "w").write("count=%d %s" % (len(sys.argv) - 1, "|".join(sys.argv[1:])))'
  foreach ($case in @(@{ Give = @('scripts/run-tests.py'); Want = 'count=1 scripts/run-tests.py' }, @{ Give = @(); Want = 'count=0 ' }, @{ Give = @('a b', 'c'); Want = 'count=2 a b|c' }, @{ Give = @('-maxCpuCount:2', 'x'); Want = 'count=2 -maxCpuCount:2|x' })) {
    $give = $case.Give
    & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $wrapper (Join-Path $work 'echo.started') (ConvertTo-Command (@('python', $echo) + $give)) | Out-Null
    $got = Get-Content -LiteralPath (Join-Path $work 'echo.txt') -ErrorAction Ignore
    Check "the wrapper passes $($give.Count) argument(s) on whole" ($got -eq $case.Want) "(got '$got', wanted '$($case.Want)')"
  }
  $good = Join-Path $work 'good.started'
  $text = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $wrapper $good (ConvertTo-Command @('pwsh', '-NoProfile', '-File', $stub, 'wrapped', 'fail')) 2>&1 | Out-String
  Check 'with its mark written the wrapper runs the command and passes its exit code on' ($LASTEXITCODE -eq 3 -and (Test-Path -LiteralPath $good) -and (@(Get-Content -LiteralPath $ran) -join ',') -eq 'wrapped') "(exit ${LASTEXITCODE}: $text)"

  # --- what the real gate covers ---
  $real = (Invoke-Gate @('-List')).Text
  $tests = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*_test.ps1' | ForEach-Object Name)
  $missing = @($tests | Where-Object { $real -notmatch [regex]::Escape("tests/$_") })
  Check 'every PowerShell test in tests/ is a step of the gate, or listed as left out with its reason' (-not $missing) "(neither run nor listed: $($missing -join ', '))"
  Check 'the queue test runs with -SkipWsl by default, not dropped' ($real -match 'tests/queue_test\.ps1 -SkipWsl')
  Check 'what a default run leaves out is listed with a reason and a command' ($real -match 'tests/test_bosun_log\.py: .+ Run: pwsh scripts/check-all\.ps1 -Wsl')
  $wsl = (Invoke-Gate @('-List', '-Wsl')).Text
  Check '-Wsl adds the WSL side and runs the whole queue test' ($wsl -match 'wsl:logger-tests: .*tests/test_bosun_log\.py' -and $wsl -match 'wsl:syntax:bosun' -and $wsl -match 'tests/queue_test\.ps1\r?\n')
  # The syntax step covers the shell scripts that are really there, and so does the Linux job of the workflow.
  $scripts = @('wsl/bosun') + @(Get-ChildItem -LiteralPath (Join-Path $repo 'wsl') -Filter '*.sh' | ForEach-Object { "wsl/$($_.Name)" })
  $unchecked = @($scripts | Where-Object { $wsl -notmatch ('(?m)^wsl:syntax:' + [regex]::Escape((Split-Path $_ -Leaf)) + ': wsl --cd .+ bash -n ' + [regex]::Escape($_) + '\r?$') })
  Check 'with -Wsl every shell script in wsl/ has its own syntax step' ($scripts.Count -ge 4 -and -not $unchecked) "(not checked: $($unchecked -join ', '))"
  Check 'the Linux job checks the same scripts by pattern, not by a list that can fall behind' ((Get-Content -LiteralPath (Join-Path $repo '.github/workflows/build.yml') -Raw) -match 'for f in wsl/bosun wsl/\*\.sh; do bash -n')
  $names = @($real -split "`r?`n" | Where-Object { $_ -match '^[\w:-]+: ' } | ForEach-Object { ($_ -split ': ')[0] })
  Check 'the steps come in the order the gate documents' (($names[0..10] -join ',') -eq 'plan,theme,icons,weakening,tiers,build,native-std,native-pe,core-tests,dotnet-build,dotnet-tests' -and $names[-1] -eq 'python-tests')
  # The heavy steps wait their turn through `bosun run --`, and -List says which: a step that lost its -Queue would
  # build or test beside another repository's job.
  $queued = @($real -split "`r?`n" | Where-Object { $_ -match '^[\w:-]+: \[queued\] ' } | ForEach-Object { ($_ -split ': ')[0] })
  Check 'the build, the .NET builds and tests and the Python tests are queued, and nothing else' (($queued -join ',') -eq 'build,dotnet-build,dotnet-tests,smoke-build,python-tests') "(queued: $($queued -join ', '))"
  Check 'the .NET steps build the solution once and test what was built' ($real -match '(?m)^dotnet-build: \[queued\] dotnet build Bosun\.slnx -nologo -maxCpuCount:2\r?$' -and $real -match '(?m)^dotnet-tests: \[queued\] dotnet test --solution Bosun\.slnx --no-build')

  # CI runs every PowerShell test the gate runs, and the two native checks after its build.
  $workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/build.yml') -Raw
  $default = @($tests | Where-Object { $real -match ('(?m)^test:[\w]+: .*' + [regex]::Escape("tests/$_")) })
  $notInCi = @($default | Where-Object { $workflow -notmatch [regex]::Escape("tests\$_") })
  # The start script test renders the shortcut badges, so the Windows job installs the icon packages before it runs.
  Check 'the Windows workflow installs the icon packages before the start script test' ($workflow -match '(?s)pip install [^\r\n]*-r scripts/requirements-icons\.txt.*tests\\start_run_test\.ps1')
  Check 'the Windows workflow runs every PowerShell test a default run of the gate runs' ($default.Count -ge 15 -and -not $notInCi) "(not in build.yml: $($notInCi -join ', '))"
  # A test that needs WSL is left out of both, with its reason, and comes back with -Wsl: the hosted runner has no
  # WSL, and a default run must not start it.
  $wslOnly = @($tests | Where-Object { $_ -notin $default })
  $inCiAnyway = @($wslOnly | Where-Object { $workflow -match [regex]::Escape("tests\$_") })
  $notWithWsl = @($wslOnly | Where-Object { $wsl -notmatch ('(?m)^test:[\w]+: .*' + [regex]::Escape("tests/$_")) })
  Check 'a test left out of a default run is out of the Windows workflow too, and runs with -Wsl' (($wslOnly -join ',') -eq 'stage_wsl_test.ps1' -and -not $inCiAnyway -and -not $notWithWsl) "(left out: $($wslOnly -join ', '); in CI anyway: $($inCiAnyway -join ', '); missing with -Wsl: $($notWithWsl -join ', '))"
  Check 'the Windows workflow runs the native checks' ($workflow -match 'native-std-check\.py' -and $workflow -match 'native-pe-check\.py build[\\/]Release')

  # --- the real plan step against a stale plan, in a temporary copy of the plan tooling ---
  $copy = Join-Path $work 'copy'
  New-Item -ItemType Directory -Path (Join-Path $copy 'scripts/lib'), (Join-Path $copy 'todo/00-workspace'), (Join-Path $copy 'tests') | Out-Null
  foreach ($name in 'todo-graph.py', 'review-dispatch.py', 'check-all.ps1', 'lib/todo_graph.py', 'lib/host_caps.py', 'lib/job-measure.ps1', 'lib/gate-mark.ps1') {
    Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $copy "scripts/$name")
  }
  # A valid one-section TODO file, from the fixture the graph's own tests use.
  # Python writes the file itself: the section sign in it would not survive a trip through the console's code page.
  $fixture = "import sys, pathlib; sys.path[:0] = [r'$repo/scripts/tests', r'$repo/scripts/lib']; from test_todo_graph import todo_file; pathlib.Path(sys.argv[1]).write_text(todo_file([(1, 'A', 0, 'none', '--')]), encoding='utf-8', newline=chr(10))"
  & python -c $fixture (Join-Path $copy 'todo/00-workspace/TODO-01-a.md')
  [IO.File]::WriteAllText((Join-Path $copy 'todo/00-workspace/INDEX.md'), "# Index`n- TODO-01-a.md`n")
  [IO.File]::WriteAllText((Join-Path $copy 'todo/TODO-00-INDEX.md'), "# Index`n- 00-workspace/TODO-01-a.md`n")
  & python (Join-Path $copy 'scripts/todo-graph.py') plan --sync | Out-Null
  $copyGate = Join-Path $copy 'scripts/check-all.ps1'
  $r = Invoke-Gate @('-Only', 'plan') $copyGate
  Check 'the plan step passes on a plan that is in step' ($r.Exit -eq 0 -and $r.Text -match 'ok   plan') "(exit $($r.Exit): $($r.Text))"
  Add-Content -LiteralPath (Join-Path $copy 'todo/implementation-plan.md') -Value 'a line the generator did not write'
  $r = Invoke-Gate @('-Only', 'plan') $copyGate
  Check 'a stale plan fails the gate at the plan step, naming it' ($r.Exit -eq 1 -and $r.Text -match 'FAIL plan: exit \d+ from: python scripts/todo-graph\.py plan --check') "(exit $($r.Exit): $($r.Text))"

  # --- the real weakening step: an unanswered removal in what is committed stops the gate ---
  $scanRepo = Join-Path $work 'scan'
  New-Item -ItemType Directory -Path (Join-Path $scanRepo 'scripts/lib'), (Join-Path $scanRepo 'tests') | Out-Null
  foreach ($name in 'check-all.ps1', 'weakening-scan.py', 'lib/job-measure.ps1', 'lib/gate-mark.ps1') {
    Copy-Item -LiteralPath (Join-Path $repo "scripts/$name") -Destination (Join-Path $scanRepo "scripts/$name")
  }
  Set-Content -LiteralPath (Join-Path $scanRepo 'tests/a_test.ps1') -Value "Check 'one thing holds' (1 -eq 1)", "Check 'another thing holds' (2 -eq 2)"
  $git = @('-C', $scanRepo, '-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'core.autocrlf=false')
  git -C $scanRepo init -q -b main
  git @git add -A; git @git commit -q -m start
  git @git update-ref refs/remotes/origin/main HEAD
  $scanGate = Join-Path $scanRepo 'scripts/check-all.ps1'
  $r = Invoke-Gate @('-Only', 'weakening') $scanGate
  Check 'the weakening step passes when nothing committed weakens a test' ($r.Exit -eq 0 -and $r.Text -match 'ok   weakening') "(exit $($r.Exit): $($r.Text))"
  Set-Content -LiteralPath (Join-Path $scanRepo 'tests/a_test.ps1') -Value "Check 'one thing holds' (1 -eq 1)"
  git @git add -A; git @git commit -q -m 'drop a check'
  $r = Invoke-Gate @('-Only', 'weakening') $scanGate
  Check 'a committed test removal with no answer stops the gate at the weakening step' ($r.Exit -eq 1 -and $r.Text -match 'unanswered test-removed tests/a_test\.ps1:another thing holds' -and $r.Text -match 'FAIL weakening: exit 1') "(exit $($r.Exit): $($r.Text))"
  git @git commit -q --allow-empty -m 'explain' -m 'Weakening scan: test-removed tests/a_test.ps1:another thing holds -- the option it covered was removed'
  $r = Invoke-Gate @('-Only', 'weakening') $scanGate
  Check 'the same removal answered in a commit body lets the gate pass, and the answer is shown' ($r.Exit -eq 0 -and $r.Text -match 'answered   test-removed tests/a_test\.ps1:another thing holds -- the option it covered was removed') "(exit $($r.Exit): $($r.Text))"

  # --- -Wsl forwards BOSUN_SCRATCH to WSL steps via WSLENV (D00 T07 §3) ---
  $wslProbe = Join-Path $work 'wslenv-probe.ps1'
  $wslSeen = Join-Path $work 'wslenv-seen.txt'
  Set-Content -LiteralPath $wslProbe -Value 'Set-Content -LiteralPath (Join-Path $PSScriptRoot ''wslenv-seen.txt'') -Value (($env:WSLENV ?? '''') + "`n" + ($env:BOSUN_SCRATCH ?? ''''))'
  $wslStepFile = Join-Path $work 'wsl-steps.json'
  ConvertTo-Json -InputObject @(@{ name = 'probe'; file = 'pwsh'; args = @('-NoProfile', '-File', $wslProbe) }) -Depth 4 | Set-Content -LiteralPath $wslStepFile
  $savedScratch = $env:BOSUN_SCRATCH
  $savedWslenv = $env:WSLENV
  try {
    $env:BOSUN_SCRATCH = "  $work  "
    $env:WSLENV = 'LOCALAPPDATA'
    $r = Invoke-Gate @('-StepFile', $wslStepFile, '-Wsl')
    $seenLines = @(Get-Content -LiteralPath $wslSeen -ErrorAction Ignore)
    $seenWslenv = if ($seenLines.Count -ge 1) { $seenLines[0].Trim() } else { '' }
    $seenScratch = if ($seenLines.Count -ge 2) { $seenLines[1] } else { '' }
    Check '-Wsl adds BOSUN_SCRATCH/p to WSLENV and normalizes trimmed BOSUN_SCRATCH' ($r.Exit -eq 0 -and ($seenWslenv -split ':') -contains 'BOSUN_SCRATCH/p' -and ($seenWslenv -split ':') -contains 'LOCALAPPDATA' -and $seenScratch -eq (Resolve-Path -LiteralPath $work).ProviderPath) "(exit $($r.Exit), WSLENV='$seenWslenv', BOSUN_SCRATCH='$seenScratch')"
    $rNoWsl = Invoke-Gate @('-StepFile', $wslStepFile)
    $seenNoWslLines = @(Get-Content -LiteralPath $wslSeen -ErrorAction Ignore)
    $seenNoWsl = if ($seenNoWslLines.Count -ge 1) { $seenNoWslLines[0].Trim() } else { '' }
    Check 'without -Wsl, WSLENV is not modified' ($rNoWsl.Exit -eq 0 -and $seenNoWsl -eq 'LOCALAPPDATA') "(exit $($rNoWsl.Exit), WSLENV='$seenNoWsl')"
  } finally {
    if ($null -eq $savedScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $savedScratch }
    if ($null -eq $savedWslenv) { Remove-Item Env:WSLENV -ErrorAction Ignore } else { $env:WSLENV = $savedWslenv }
  }
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore }
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
