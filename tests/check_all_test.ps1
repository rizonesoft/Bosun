# Tests scripts/check-all.ps1: with stand-in steps, that it runs every step once in order, stops at the first
# failure and names it, leaves nothing a step started running, and runs only what -Only names; that every test in
# tests/ is in the gate and in CI; and that a stale plan fails the real plan step. The stand-ins write one line each.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$gate = Join-Path $repo 'scripts/check-all.ps1'
. (Join-Path $repo 'scripts/lib/scratch.ps1')
$work = New-BosunScratchFolder -Name 'check-all-test'
$defaultLogRoot = Join-Path $work 'gate-logs'
New-Item -ItemType Directory -Path $defaultLogRoot | Out-Null
$origScratch = $env:BOSUN_SCRATCH
$env:BOSUN_SCRATCH = $defaultLogRoot
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
function Get-LogPath([string]$text) {
  $m = [regex]::Match($text, '(?m)^log: (.+?)\r?$')
  if ($m.Success) { $m.Groups[1].Value.Trim() } else { $null }
}
if (-not ('BosunTestAsyncCapture' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

public sealed class BosunTestAsyncCapture {
  private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
  public void Attach(Process proc) {
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
  }
  public string GetText() { return string.Join("\n", _lines.ToArray()); }
}
'@
}
function Start-GateAsync([string[]]$arguments, [string]$workingDirectory = $repo, [string]$script = $gate) {
  $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
  $psi.WorkingDirectory = $workingDirectory
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true
  $psi.ArgumentList.Add('-NoProfile')
  $psi.ArgumentList.Add('-File')
  $psi.ArgumentList.Add($script)
  foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }
  $proc = [Diagnostics.Process]::new()
  $proc.StartInfo = $psi
  $capture = [BosunTestAsyncCapture]::new()
  $null = $proc.Start()
  $capture.Attach($proc)
  [pscustomobject]@{ Process = $proc; Capture = $capture }
}
function Get-AsyncText($handle) { $handle.Capture.GetText() }
function Wait-Condition([scriptblock]$predicate, [int]$timeoutMs = 15000, [int]$pollMs = 50) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    if (& $predicate) { return $true }
    Start-Sleep -Milliseconds $pollMs
  }
  [bool](& $predicate)
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
      # D00 T03 §32 and D00 T07 §4: the mark names the run's scratch log, read by run-guard.py status.
      $outputs = @(Get-Content -LiteralPath (Join-Path $work 'mark-outputs.txt') -ErrorAction Ignore)
      $runLog = Get-LogPath $r.Text
      Check "the gate's mark names its output while it runs ($($case.Says))" ($runLog -and (Test-Path -LiteralPath $runLog -PathType Leaf) -and $outputs.Count -ge 1 -and -not ($outputs | Where-Object { $_ -ne $runLog })) "(log: '$runLog', outputs: $($outputs -join ', '))"
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
  $failLog = Get-LogPath $r.Text
  $failLogText = if ($failLog -and (Test-Path -LiteralPath $failLog)) { Get-Content -LiteralPath $failLog -Raw } else { '' }
  Check 'a failing step stops the run and nothing after it runs' ($r.Exit -eq 1 -and ($r.Ran -join ',') -eq 'one,two') "(exit $($r.Exit), ran: $($r.Ran -join ','))"
  Check 'it is named with its exit code and its command' ($r.Text -match 'FAIL two: exit 3 from: pwsh -NoProfile -File' -and $r.Text -notmatch 'steps passed' -and $failLog -and $failLogText -match 'FAIL two: exit 3 from: pwsh -NoProfile -File') "(log='$failLog': $($r.Text))"

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
    $qTimeoutLog = Get-LogPath $r.Text
    $qTimeoutLogText = if ($qTimeoutLog -and (Test-Path -LiteralPath $qTimeoutLog)) { Get-Content -LiteralPath $qTimeoutLog -Raw } else { '' }
    Check 'a queued step that never gets a slot is stopped at the queue allowance and named as waiting' ($r.Exit -eq 1 -and
      $r.Text -match 'waiting queued: queued for a build slot' -and $r.Text -notmatch 'started queued:' -and
      $r.Text -match 'FAIL queued: waited 0\.1 minutes for a queue slot and did not get one' -and
      $qTimeoutLogText -match 'waiting queued: queued for a build slot' -and $qTimeoutLogText -notmatch 'started queued:' -and
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
  foreach ($name in 'todo-graph.py', 'review-dispatch.py', 'check-all.ps1', 'lib/todo_graph.py', 'lib/host_caps.py', 'lib/job-measure.ps1', 'lib/gate-mark.ps1', 'lib/scratch.ps1') {
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
  foreach ($name in 'check-all.ps1', 'weakening-scan.py', 'lib/job-measure.ps1', 'lib/gate-mark.ps1', 'lib/scratch.ps1') {
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
    # End-to-end through the real -Wsl entry point when Ubuntu is available: a Linux fixture lands under the Windows scratch dir.
    $distros = if (Get-Command wsl.exe -CommandType Application -ErrorAction Ignore) { @((wsl.exe -l -q 2>$null) -replace "`0", '' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @() }
    if ($distros -contains 'Ubuntu') {
      $wslRealSteps = Join-Path $work 'wsl-real-steps.json'
      $pyCmd = "import sys; sys.path.insert(0, 'scripts/lib'); import scratch; print(scratch.scratch_dir('wsl-gate-e2e'))"
      ConvertTo-Json -InputObject @(@{ name = 'wsl-scratch'; file = 'wsl.exe'; args = @('-d', 'Ubuntu', '--cd', $repo, 'python3', '-B', '-c', $pyCmd) }) -Depth 4 | Set-Content -LiteralPath $wslRealSteps
      $rRealWsl = Invoke-Gate @('-StepFile', $wslRealSteps, '-Wsl')
      $landed = @(Get-ChildItem -LiteralPath $work -Directory -Filter 'bosun-wsl-gate-e2e-*' -ErrorAction Ignore)
      Check '-Wsl real guest step creates its fixture beneath the configured Windows BOSUN_SCRATCH' ($rRealWsl.Exit -eq 0 -and $landed.Count -eq 1) "(exit $($rRealWsl.Exit), landed=$($landed.Count): $($rRealWsl.Text))"
      foreach ($d in $landed) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Ignore }
    }
  } finally {
    if ($null -eq $savedScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $savedScratch }
    if ($null -eq $savedWslenv) { Remove-Item Env:WSLENV -ErrorAction Ignore } else { $env:WSLENV = $savedWslenv }
  }

  # --- D00 T07 §4: live incremental log streaming, post-timeout retention, distinct logs, boundary, write failure ---
  $liveDir = Join-Path $work 'live-stream'
  New-Item -ItemType Directory -Path $liveDir | Out-Null
  $liveMarks = Join-Path $liveDir 'marks'
  $liveLogs = Join-Path $liveDir 'logs'
  New-Item -ItemType Directory -Path $liveLogs | Out-Null
  $liveStepScript = Join-Path $liveDir 'live-step.ps1'
  Set-Content -LiteralPath $liveStepScript -Value @'
param([string]$Mode, [string]$FlagFile = '', [string]$ChildPidFile = '')
switch ($Mode) {
  'step1' {
    [Console]::Out.WriteLine('STEP1-OUT-MARKER'); [Console]::Out.Flush()
    exit 0
  }
  'live-hold' {
    [Console]::Out.WriteLine('LIVE-STDOUT-MARKER'); [Console]::Out.Flush()
    [Console]::Error.WriteLine('LIVE-STDERR-MARKER'); [Console]::Error.Flush()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $FlagFile) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
    exit 0
  }
  'timeout-hold' {
    [Console]::Out.WriteLine('TIMEDOUT-STDOUT-MARKER'); [Console]::Out.Flush()
    [Console]::Error.WriteLine('TIMEDOUT-STDERR-MARKER'); [Console]::Error.Flush()
    Start-Sleep 300
    exit 0
  }
  'midrun-fail' {
    $child = Start-Process pwsh -PassThru -NoNewWindow -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 300'
    Set-Content -LiteralPath $ChildPidFile -Value $child.Id
    [Console]::Out.WriteLine('MIDRUN-MARKER-1'); [Console]::Out.Flush()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $FlagFile) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
    [Console]::Out.WriteLine('MIDRUN-MARKER-2'); [Console]::Out.Flush()
    Start-Sleep 300
    exit 0
  }
  'tag' {
    [Console]::Out.WriteLine($FlagFile); [Console]::Out.Flush()
    exit 0
  }
}
'@

  # 1. Live stdout+stderr streaming while child is still running
  $releaseLive = Join-Path $liveDir 'release-live.flag'
  $liveStepsFile = Join-Path $liveDir 'live-steps.json'
  ConvertTo-Json -InputObject @(
    @{ name = 'first'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'step1') }
    @{ name = 'second'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'live-hold', $releaseLive) }
  ) -Depth 4 | Set-Content -LiteralPath $liveStepsFile
  $env:BOSUN_SCRATCH = $liveLogs
  $env:BOSUN_GATE_MARK_DIR = $liveMarks
  try {
    $h = Start-GateAsync @('-StepFile', $liveStepsFile)
    try {
      $liveState = @{ Log = $null }
      $seenLiveBoth = Wait-Condition {
        $markFile = Get-ChildItem -LiteralPath $liveMarks -Filter '*.json' -ErrorAction Ignore | Select-Object -First 1
        if (-not $markFile) { return $false }
        try { $liveState.Log = (Get-Content -LiteralPath $markFile.FullName -Raw | ConvertFrom-Json).output } catch { return $false }
        if (-not $liveState.Log -or -not (Test-Path -LiteralPath $liveState.Log)) { return $false }
        $logText = Get-Content -LiteralPath $liveState.Log -Raw -ErrorAction Ignore
        $conText = Get-AsyncText $h
        ($logText -match 'LIVE-STDOUT-MARKER' -and $logText -match 'LIVE-STDERR-MARKER' -and
         $conText -match 'LIVE-STDOUT-MARKER' -and $conText -match 'LIVE-STDERR-MARKER')
      } 15000
      $activeLog = $liveState.Log
      $stillRunning = -not $h.Process.HasExited
      # Regression (R1 F3): the gate holds its log write handle open continuously, so OpenRead (FileShare.Read)
      # cannot acquire a conflicting lock mid-run and abort a healthy build.
      $openReadRejected = $false
      try { $tmpFs = [IO.File]::OpenRead($activeLog); $tmpFs.Dispose() } catch [IO.IOException] { $openReadRejected = $true }
      Set-Content -LiteralPath $releaseLive -Value 'go'
      $null = $h.Process.WaitForExit(15000)
      $h.Process.WaitForExit()
      $finalText = Get-AsyncText $h
      $finalLogText = if ($activeLog -and (Test-Path -LiteralPath $activeLog)) { Get-Content -LiteralPath $activeLog -Raw } else { '' }
      $outCountCon = [regex]::Matches($finalText, 'LIVE-STDOUT-MARKER').Count
      $outCountLog = [regex]::Matches($finalLogText, 'LIVE-STDOUT-MARKER').Count
      Check 'live stdout and stderr stream once to console and active gate-mark log while the child is still running' ($seenLiveBoth -and $stillRunning -and $openReadRejected -and $h.Process.ExitCode -eq 0 -and (Get-LogPath $finalText) -eq $activeLog -and $outCountCon -eq 1 -and $outCountLog -eq 1) "(seenLive=$seenLiveBoth, stillRunning=$stillRunning, openReadRejected=$openReadRejected, exit=$($h.Process.ExitCode), activeLog='$activeLog', conCount=$outCountCon, logCount=$outCountLog)"
    } finally {
      if (-not $h.Process.HasExited) { $h.Process.Kill($true) }
      $h.Process.Dispose()
    }

    # 2. Post-timeout retention of earlier step output, timed-out markers, and diagnosis
    $toStepsFile = Join-Path $liveDir 'timeout-steps.json'
    ConvertTo-Json -InputObject @(
      @{ name = 'first'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'step1') }
      @{ name = 'hung'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'timeout-hold') }
    ) -Depth 4 | Set-Content -LiteralPath $toStepsFile
    $rTo = Invoke-Gate @('-StepFile', $toStepsFile, '-StepMinutes', '0.1')
    $toLog = Get-LogPath $rTo.Text
    $toLogText = if ($toLog -and (Test-Path -LiteralPath $toLog)) { Get-Content -LiteralPath $toLog -Raw } else { '' }
    Check 'a timed-out run keeps earlier step output, timed-out stdout/stderr markers, and timeout diagnosis in the named log' ($rTo.Exit -eq 1 -and $toLog -and
      $toLogText -match 'STEP1-OUT-MARKER' -and $toLogText -match 'TIMEDOUT-STDOUT-MARKER' -and $toLogText -match 'TIMEDOUT-STDERR-MARKER' -and
      $toLogText -match 'FAIL hung: stopped at its 0\.1 minute limit') "(exit $($rTo.Exit), log='$toLog')"

    # 3. Two invocations against one scratch root produce two distinct readable logs
    $twoRoot = Join-Path $liveDir 'two-logs'
    New-Item -ItemType Directory -Path $twoRoot | Out-Null
    $env:BOSUN_SCRATCH = $twoRoot
    $stepsA = Join-Path $liveDir 'steps-a.json'
    $stepsB = Join-Path $liveDir 'steps-b.json'
    ConvertTo-Json -InputObject @(@{ name = 'a'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'tag', 'RUN-ALPHA-MARKER') }) -Depth 4 | Set-Content -LiteralPath $stepsA
    ConvertTo-Json -InputObject @(@{ name = 'b'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'tag', 'RUN-BETA-MARKER') }) -Depth 4 | Set-Content -LiteralPath $stepsB
    $rA = Invoke-Gate @('-StepFile', $stepsA)
    $rB = Invoke-Gate @('-StepFile', $stepsB)
    $logA = Get-LogPath $rA.Text
    $logB = Get-LogPath $rB.Text
    $textA = if ($logA -and (Test-Path -LiteralPath $logA)) { Get-Content -LiteralPath $logA -Raw } else { '' }
    $textB = if ($logB -and (Test-Path -LiteralPath $logB)) { Get-Content -LiteralPath $logB -Raw } else { '' }
    Check 'two gates on one scratch root write two distinct logs that both remain readable' ($rA.Exit -eq 0 -and $rB.Exit -eq 0 -and $logA -and $logB -and $logA -ne $logB -and
      (Split-Path $logA -Parent) -eq (Resolve-Path -LiteralPath $twoRoot).ProviderPath -and
      (Split-Path $logB -Parent) -eq (Resolve-Path -LiteralPath $twoRoot).ProviderPath -and
      $textA -match 'RUN-ALPHA-MARKER' -and $textB -match 'RUN-BETA-MARKER') "(logA='$logA', logB='$logB')"

    # Regression (R2 F1): in-process PowerShell invocation (`$out = & scripts/check-all.ps1 ...`) captures gate summary and log: <path>
    $inProcOut = (& $gate -StepFile $stepsA) -join "`n"
    $inProcLog = Get-LogPath $inProcOut
    Check 'in-process PowerShell invocation captures the gate summary and log: <path> on the output stream' ($LASTEXITCODE -eq 0 -and
      $inProcOut -match 'all 1 steps passed' -and $inProcLog -and (Test-Path -LiteralPath $inProcLog -PathType Leaf)) "(exit=$LASTEXITCODE, log='$inProcLog', out='$inProcOut')"

    # 4. Exact log boundary: -List and unknown -Only create/print no log; unwritable root fails naming path & reason;
    #    post-log gate-mark failure records cause in the log and prints log: <path>
    $boundaryRoot = Join-Path $liveDir 'boundary-logs'
    New-Item -ItemType Directory -Path $boundaryRoot | Out-Null
    $env:BOSUN_SCRATCH = $boundaryRoot
    $rListB = Invoke-Gate @('-StepFile', $stepsA, '-List')
    $rBadOnly = Invoke-Gate @('-StepFile', $stepsA, '-Only', 'no-such-step')
    $boundaryFiles = @(Get-ChildItem -LiteralPath $boundaryRoot -ErrorAction Ignore)
    Check '-List and unknown -Only create no scratch log and print no log: line' ($rListB.Exit -eq 0 -and $rBadOnly.Exit -eq 2 -and
      -not (Get-LogPath $rListB.Text) -and -not (Get-LogPath $rBadOnly.Text) -and $boundaryFiles.Count -eq 0) "(listExit=$($rListB.Exit), badExit=$($rBadOnly.Exit), files=$($boundaryFiles.Count))"

    $roRoot = Join-Path $liveDir 'ro-logs'
    New-Item -ItemType Directory -Path $roRoot | Out-Null
    $acl = Get-Acl -LiteralPath $roRoot
    $denyRule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.AccessControl.FileSystemRights]::CreateFiles, [Security.AccessControl.AccessControlType]::Deny)
    $acl.AddAccessRule($denyRule)
    Set-Acl -LiteralPath $roRoot -AclObject $acl
    try {
      $env:BOSUN_SCRATCH = $roRoot
      Set-Steps @(@{ name = 'one' })
      $rRo = Invoke-Gate @('-StepFile', $stepFile)
      Check 'an unwritable log root fails before step 1 naming the attempted path and reason' ($rRo.Exit -eq 1 -and -not $rRo.Ran -and
        -not (Get-LogPath $rRo.Text) -and $rRo.Text -match [regex]::Escape($roRoot) -and $rRo.Text -match 'could not create run log') "(exit $($rRo.Exit): $($rRo.Text))"
    } finally {
      $acl = Get-Acl -LiteralPath $roRoot
      $null = $acl.RemoveAccessRule($denyRule)
      Set-Acl -LiteralPath $roRoot -AclObject $acl
    }

    $env:BOSUN_SCRATCH = $boundaryRoot
    $blockedMark = Join-Path $liveDir 'blocked-mark-file'
    Set-Content -LiteralPath $blockedMark -Value 'not a dir'
    $env:BOSUN_GATE_MARK_DIR = $blockedMark
    Set-Steps @(@{ name = 'one' })
    $rMarkFail = Invoke-Gate @('-StepFile', $stepFile)
    $markFailLog = Get-LogPath $rMarkFail.Text
    $markFailLogText = if ($markFailLog -and (Test-Path -LiteralPath $markFailLog)) { Get-Content -LiteralPath $markFailLog -Raw } else { '' }
    Check 'a post-log gate-mark failure records its cause in the log and prints log: <path>' ($rMarkFail.Exit -eq 1 -and -not $rMarkFail.Ran -and
      $markFailLog -and $markFailLogText -match "the gate's mark couldn't be written") "(exit $($rMarkFail.Exit), log='$markFailLog')"

    # 5. Mid-run log write failure exits 1, names partial log on console, stops active job, removes gate mark, runs no later step
    $midMarks = Join-Path $liveDir 'mid-marks'
    $midLogs = Join-Path $liveDir 'mid-logs'
    New-Item -ItemType Directory -Path $midLogs | Out-Null
    $env:BOSUN_SCRATCH = $midLogs
    $env:BOSUN_GATE_MARK_DIR = $midMarks
    $midFlag = Join-Path $liveDir 'mid-release.flag'
    $midChildPidFile = Join-Path $liveDir 'mid-child.pid'
    $midAfterFlag = Join-Path $liveDir 'mid-after-ran.txt'
    $midStepsFile = Join-Path $liveDir 'mid-steps.json'
    ConvertTo-Json -InputObject @(
      @{ name = 'one'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'midrun-fail', $midFlag, $midChildPidFile) }
      @{ name = 'two'; file = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath '$midAfterFlag' -Value ran") }
    ) -Depth 4 | Set-Content -LiteralPath $midStepsFile
    # Write 1 is '---- one: ...'; write 2 is 'MIDRUN-MARKER-1'; write 3 ('MIDRUN-MARKER-2') triggers the mid-run IOException.
    $hMid = Start-GateAsync @('-StepFile', $midStepsFile, '-FailLogAfterWrites', '2')
    $midState = @{ Log = $null }
    try {
      $midReady = Wait-Condition {
        $mf = Get-ChildItem -LiteralPath $midMarks -Filter '*.json' -ErrorAction Ignore | Select-Object -First 1
        if (-not $mf -or -not (Test-Path -LiteralPath $midChildPidFile)) { return $false }
        try { $midState.Log = (Get-Content -LiteralPath $mf.FullName -Raw | ConvertFrom-Json).output } catch { return $false }
        if (-not $midState.Log -or -not (Test-Path -LiteralPath $midState.Log)) { return $false }
        (Get-Content -LiteralPath $midState.Log -Raw -ErrorAction Ignore) -match 'MIDRUN-MARKER-1'
      } 15000
      $midLogPath = $midState.Log
      if ($midReady) { Set-Content -LiteralPath $midFlag -Value 'go' }
      $null = $hMid.Process.WaitForExit(15000)
      if (-not $hMid.Process.HasExited) { $hMid.Process.Kill($true) }
      $hMid.Process.WaitForExit()
      $midText = Get-AsyncText $hMid
      $midChildPid = if (Test-Path -LiteralPath $midChildPidFile) { [int](Get-Content -LiteralPath $midChildPidFile -Raw).Trim() } else { 0 }
      $midMarksLeft = @(Get-ChildItem -LiteralPath $midMarks -Filter '*.json' -ErrorAction Ignore)
      Check 'mid-run log write failure exits 1, names the partial log, stops the active job, removes the gate mark, and runs no later step' ($midReady -and
        $hMid.Process.ExitCode -eq 1 -and $midText -match [regex]::Escape($midLogPath) -and $midText -match 'failed to write run log' -and
        $midChildPid -gt 0 -and (Wait-Gone $midChildPid) -and $midMarksLeft.Count -eq 0 -and -not (Test-Path -LiteralPath $midAfterFlag)) "(ready=$midReady, exit=$($hMid.Process.ExitCode), child=$midChildPid, marks=$($midMarksLeft.Count), after=$(Test-Path -LiteralPath $midAfterFlag): $midText)"
    } finally {
      if (-not $hMid.Process.HasExited) { $hMid.Process.Kill($true) }
      $hMid.Process.Dispose()
    }

    # 6. Regression (R1 F1 & F2): a noisy child with a stalled stdout pipe still hits -StepMinutes and logs the timeout
    $stallLogs = Join-Path $liveDir 'stall-logs'
    New-Item -ItemType Directory -Path $stallLogs | Out-Null
    $env:BOSUN_SCRATCH = $stallLogs
    Remove-Item Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore
    $noisyScript = Join-Path $liveDir 'noisy.ps1'
    Set-Content -LiteralPath $noisyScript -Value '$line = "X" * 4096; while ($true) { [Console]::Out.WriteLine($line); Start-Sleep -Milliseconds 10 }'
    $stallSteps = Join-Path $liveDir 'stall-steps.json'
    ConvertTo-Json -InputObject @(@{ name = 'noisy'; file = 'pwsh'; args = @('-NoProfile', '-File', $noisyScript) }) -Depth 4 | Set-Content -LiteralPath $stallSteps
    $psiStall = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $psiStall.WorkingDirectory = $repo
    $psiStall.UseShellExecute = $false
    $psiStall.RedirectStandardOutput = $true
    $psiStall.RedirectStandardError = $true
    $psiStall.CreateNoWindow = $true
    foreach ($arg in @('-NoProfile', '-File', $gate, '-StepFile', $stallSteps, '-StepMinutes', '0.05')) { $psiStall.ArgumentList.Add($arg) }
    $pStall = [Diagnostics.Process]::Start($psiStall)
    try {
      # Intentionally do NOT read StandardOutput while waiting: the pipe fills, yet the gate must still time out cleanly.
      $exitedOnTime = $pStall.WaitForExit(20000)
      if (-not $exitedOnTime) { $pStall.Kill($true); $pStall.WaitForExit() }
      $stallLog = Get-ChildItem -LiteralPath $stallLogs -Filter 'bosun-check-all-*.log' | Select-Object -First 1
      $stallLogText = if ($stallLog) { Get-Content -LiteralPath $stallLog.FullName -Raw } else { '' }
      Check 'a stalled stdout pipe with a noisy child still enforces -StepMinutes and records timeout in the scratch log' ($exitedOnTime -and
        $pStall.ExitCode -eq 1 -and $stallLogText -match 'FAIL noisy: stopped at its 0\.05 minute limit') "(exitedOnTime=$exitedOnTime, exit=$($pStall.ExitCode), log=$($stallLog.FullName))"
    } finally {
      $pStall.Dispose()
    }
  } finally {
    Remove-Item Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore
    $env:BOSUN_SCRATCH = $defaultLogRoot
  }

  # --- D00 T07 §4: live queue waiting/started transitions, duration tolerance, and fast-exit StartedPath ---
  $qLiveDir = Join-Path $work 'live-queue'
  $qLiveBin = Join-Path $qLiveDir 'bin'
  $qLiveMarks = Join-Path $qLiveDir 'marks'
  $qLiveLogs = Join-Path $qLiveDir 'logs'
  New-Item -ItemType Directory -Path $qLiveBin, $qLiveLogs | Out-Null
  $slotFlag = Join-Path $qLiveDir 'slot-release.flag'
  $childFlag = Join-Path $qLiveDir 'child-release.flag'
  Set-Content -LiteralPath (Join-Path $qLiveBin 'fake-bosun.ps1') -Value @'
$flag = $env:BOSUN_TEST_SLOT_FLAG
if ($flag) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (-not (Test-Path -LiteralPath $flag) -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
}
$rest = @($args | Select-Object -Skip 3)
& $args[2] @rest
exit $LASTEXITCODE
'@
  Set-Content -LiteralPath (Join-Path $qLiveBin 'bosun.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-bosun.ps1`" %*"
  $qLiveSteps = Join-Path $qLiveDir 'steps.json'
  ConvertTo-Json -InputObject @(@{ name = 'queued-live'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'live-hold', $childFlag); queue = $true }) -Depth 4 | Set-Content -LiteralPath $qLiveSteps
  $savedPathQ = $env:PATH
  $env:PATH = "$qLiveBin;$env:PATH"
  $env:BOSUN_TEST_SLOT_FLAG = $slotFlag
  $env:BOSUN_GATE_MARK_DIR = $qLiveMarks
  $env:BOSUN_SCRATCH = $qLiveLogs
  try {
    $hQ = Start-GateAsync @('-StepFile', $qLiveSteps, '-StepMinutes', '1', '-QueueMinutes', '1')
    try {
      $qState = @{ Log = $null }
      $sawWaiting = Wait-Condition {
        $mf = Get-ChildItem -LiteralPath $qLiveMarks -Filter '*.json' -ErrorAction Ignore | Select-Object -First 1
        if (-not $mf) { return $false }
        try { $qState.Log = (Get-Content -LiteralPath $mf.FullName -Raw | ConvertFrom-Json).output } catch { return $false }
        if (-not $qState.Log -or -not (Test-Path -LiteralPath $qState.Log)) { return $false }
        $lt = Get-Content -LiteralPath $qState.Log -Raw -ErrorAction Ignore
        $ct = Get-AsyncText $hQ
        ($lt -match 'waiting queued-live: queued for a build slot' -and $ct -match 'waiting queued-live: queued for a build slot')
      } 15000
      $qLog = $qState.Log
      $waitSw = [Diagnostics.Stopwatch]::StartNew()
      Start-Sleep -Milliseconds 1200
      $noStartedBeforeSlot = ((Get-AsyncText $hQ) -notmatch 'started queued-live:') -and ($qLog -and (Get-Content -LiteralPath $qLog -Raw -ErrorAction Ignore) -notmatch 'started queued-live:')
      Set-Content -LiteralPath $slotFlag -Value 'go'
      $sawStarted = Wait-Condition {
        $lt = Get-Content -LiteralPath $qLog -Raw -ErrorAction Ignore
        $ct = Get-AsyncText $hQ
        ($lt -match 'started queued-live: got build slot after (\d+\.\d) s' -and $ct -match 'started queued-live: got build slot after (\d+\.\d) s')
      } 15000
      $stillRunningAfterStarted = -not $hQ.Process.HasExited
      $maxExpectedWait = $waitSw.Elapsed.TotalSeconds + 2.5
      $mWait = [regex]::Match((Get-AsyncText $hQ), 'started queued-live: got build slot after (\d+\.\d) s')
      $reportedWait = if ($mWait.Success) { [double]::Parse($mWait.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { -1.0 }
      Set-Content -LiteralPath $childFlag -Value 'go'
      $null = $hQ.Process.WaitForExit(15000)
      $hQ.Process.WaitForExit()
      Check 'queued step emits waiting before slot release and started before child exit with matching duration in console and log' ($sawWaiting -and $noStartedBeforeSlot -and $sawStarted -and $stillRunningAfterStarted -and $hQ.Process.ExitCode -eq 0 -and $reportedWait -ge 0.8 -and $reportedWait -le $maxExpectedWait) "(waiting=$sawWaiting, noBefore=$noStartedBeforeSlot, started=$sawStarted, stillRunning=$stillRunningAfterStarted, reported=$reportedWait, max=$maxExpectedWait, exit=$($hQ.Process.ExitCode))"
    } finally {
      if (-not $hQ.Process.HasExited) { $hQ.Process.Kill($true) }
      $hQ.Process.Dispose()
    }

    # Fast-exit StartedPath branch: step starts immediately and finishes fast (tests Measure-Job post-loop StartedPath check)
    Remove-Item Env:BOSUN_TEST_SLOT_FLAG -ErrorAction Ignore
    $fastSteps = Join-Path $qLiveDir 'fast-steps.json'
    ConvertTo-Json -InputObject @(@{ name = 'queued-fast'; file = 'cmd.exe'; args = @('/c', 'echo fast-done'); queue = $true }) -Depth 4 | Set-Content -LiteralPath $fastSteps
    $rFast = Invoke-Gate @('-StepFile', $fastSteps, '-StepMinutes', '1', '-QueueMinutes', '1')
    $fastLog = Get-LogPath $rFast.Text
    $fastLogText = if ($fastLog -and (Test-Path -LiteralPath $fastLog)) { Get-Content -LiteralPath $fastLog -Raw } else { '' }
    Check 'a fast queued step still emits both waiting and started in console and log' ($rFast.Exit -eq 0 -and
      $rFast.Text -match 'waiting queued-fast: queued for a build slot' -and $rFast.Text -match 'started queued-fast: got build slot after \d+\.\d s' -and
      $fastLogText -match 'started queued-fast: got build slot after \d+\.\d s') "(exit $($rFast.Exit): $($rFast.Text))"
  } finally {
    $env:PATH = $savedPathQ
    Remove-Item Env:BOSUN_TEST_SLOT_FLAG, Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore
    $env:BOSUN_SCRATCH = $defaultLogRoot
  }

  # --- D00 T07 §4: out-of-repo launch + dotnet --version probe in $repo, single probe across two .NET steps, zero without .NET, failure diagnostic ---
  $dotnetDir = Join-Path $work 'dotnet-probe'
  $dotnetBin = Join-Path $dotnetDir 'bin'
  $outsideDir = Join-Path $dotnetDir 'outside-repo'
  New-Item -ItemType Directory -Path $dotnetBin, $outsideDir | Out-Null
  $dotnetCalls = Join-Path $dotnetDir 'dotnet-calls.txt'
  Set-Content -LiteralPath (Join-Path $dotnetBin 'fake-dotnet.ps1') -Value @"
Add-Content -LiteralPath '$dotnetCalls' -Value ("`$((Get-Location).ProviderPath)|" + (`$args -join ' '))
if (`$args.Count -ge 1 -and `$args[0] -eq '--version') {
  if (`$env:BOSUN_FAKE_DOTNET_FAIL) {
    [Console]::Error.WriteLine('fake sdk error: no release SDK matches global.json')
    exit 145
  }
  [Console]::Out.WriteLine('10.0.199')
  exit 0
}
[Console]::Out.WriteLine("fake-dotnet-ran: `$(`$args -join ' ')")
exit 0
"@
  Set-Content -LiteralPath (Join-Path $dotnetBin 'dotnet.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-dotnet.ps1`" %*"
  $dotnetTwoSteps = Join-Path $dotnetDir 'two-dotnet.json'
  ConvertTo-Json -InputObject @(
    @{ name = 'dotnet-build'; file = 'dotnet'; args = @('build', 'Bosun.slnx') }
    @{ name = 'dotnet-tests'; file = 'dotnet'; args = @('test', '--solution', 'Bosun.slnx') }
  ) -Depth 4 | Set-Content -LiteralPath $dotnetTwoSteps
  $dotnetNoSteps = Join-Path $dotnetDir 'no-dotnet.json'
  ConvertTo-Json -InputObject @(@{ name = 'plain'; file = 'pwsh'; args = @('-NoProfile', '-File', $liveStepScript, 'step1') }) -Depth 4 | Set-Content -LiteralPath $dotnetNoSteps
  $dotnetFailSteps = Join-Path $dotnetDir 'fail-dotnet.json'
  $dotnetAfterFlag = Join-Path $dotnetDir 'after-ran.txt'
  ConvertTo-Json -InputObject @(
    @{ name = 'dotnet-build'; file = 'dotnet'; args = @('build', 'Bosun.slnx') }
    @{ name = 'after'; file = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath '$dotnetAfterFlag' -Value ran") }
  ) -Depth 4 | Set-Content -LiteralPath $dotnetFailSteps

  $savedPathD = $env:PATH
  $env:PATH = "$dotnetBin;$env:PATH"
  Push-Location -LiteralPath $outsideDir
  try {
    Remove-Item -LiteralPath $dotnetCalls -ErrorAction Ignore
    $rD2 = Invoke-Gate @('-StepFile', $dotnetTwoSteps)
    $calls2 = @(Get-Content -LiteralPath $dotnetCalls -ErrorAction Ignore)
    $logD2 = Get-LogPath $rD2.Text
    $logD2Text = if ($logD2 -and (Test-Path -LiteralPath $logD2)) { Get-Content -LiteralPath $logD2 -Raw } else { '' }
    $sdkMatchesCon = [regex]::Matches($rD2.Text, '(?m)^dotnet-sdk: 10\.0\.199\r?$').Count
    $sdkMatchesLog = [regex]::Matches($logD2Text, '(?m)^dotnet-sdk: 10\.0\.199\r?$').Count
    $resolvedRepo = (Resolve-Path -LiteralPath $repo).ProviderPath
    Check 'from outside $repo, dotnet --version runs once in $repo before two .NET steps and logs the exact SDK once' ($rD2.Exit -eq 0 -and
      $calls2.Count -eq 3 -and $calls2[0] -eq "$resolvedRepo|--version" -and $calls2[1] -eq "$resolvedRepo|build Bosun.slnx" -and $calls2[2] -eq "$resolvedRepo|test --solution Bosun.slnx" -and
      $sdkMatchesCon -eq 1 -and $sdkMatchesLog -eq 1) "(exit=$($rD2.Exit), calls=$($calls2 -join ' ; '), conMatches=$sdkMatchesCon, logMatches=$sdkMatchesLog)"

    Remove-Item -LiteralPath $dotnetCalls -ErrorAction Ignore
    $rD0 = Invoke-Gate @('-StepFile', $dotnetNoSteps)
    $logD0 = Get-LogPath $rD0.Text
    $logD0Text = if ($logD0 -and (Test-Path -LiteralPath $logD0)) { Get-Content -LiteralPath $logD0 -Raw } else { '' }
    Check 'with no .NET step selected, dotnet --version is not called and no dotnet-sdk line is printed' ($rD0.Exit -eq 0 -and
      -not (Test-Path -LiteralPath $dotnetCalls) -and $rD0.Text -notmatch 'dotnet-sdk:' -and $logD0Text -notmatch 'dotnet-sdk:') "(exit=$($rD0.Exit), callsExist=$(Test-Path -LiteralPath $dotnetCalls))"

    Remove-Item -LiteralPath $dotnetCalls, $dotnetAfterFlag -ErrorAction Ignore
    $env:BOSUN_FAKE_DOTNET_FAIL = '1'
    $rDFail = Invoke-Gate @('-StepFile', $dotnetFailSteps)
    $callsFail = @(Get-Content -LiteralPath $dotnetCalls -ErrorAction Ignore)
    $logDFail = Get-LogPath $rDFail.Text
    $logDFailText = if ($logDFail -and (Test-Path -LiteralPath $logDFail)) { Get-Content -LiteralPath $logDFail -Raw } else { '' }
    Check 'a failing dotnet --version probe diagnoses in the named log and runs no build or later step' ($rDFail.Exit -eq 1 -and
      $callsFail.Count -eq 1 -and $callsFail[0] -eq "$resolvedRepo|--version" -and -not (Test-Path -LiteralPath $dotnetAfterFlag) -and
      $logDFail -and $rDFail.Text -match 'FAIL dotnet-sdk:' -and $logDFailText -match 'fake sdk error: no release SDK matches global\.json' -and $logDFailText -match 'FAIL dotnet-sdk:') "(exit=$($rDFail.Exit), calls=$($callsFail -join ' ; '), after=$(Test-Path -LiteralPath $dotnetAfterFlag), log='$logDFail')"
  } finally {
    Pop-Location
    $env:PATH = $savedPathD
    Remove-Item Env:BOSUN_FAKE_DOTNET_FAIL -ErrorAction Ignore
  }
} finally {
  if ($null -eq $origScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $origScratch }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
