# Tests scripts/check-all.ps1: with stand-in steps, that it runs every step once in order, stops at the first
# failure and names it, leaves nothing a step started running, and runs only what -Only names; that every test in
# tests/ is in the gate and in CI; and that a stale plan fails the real plan step. The stand-ins write one line each.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$gate = Join-Path $repo 'scripts/check-all.ps1'
# Scratch files go where BOSUN_SCRATCH says, and to the system's temporary folder only when it is not set.
$scratch = if ($env:BOSUN_SCRATCH -and (Test-Path -LiteralPath $env:BOSUN_SCRATCH -PathType Container)) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ("bosun-check-all-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
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
  Check 'the steps come in the order the gate documents' (($names[0..5] -join ',') -eq 'plan,tiers,build,native-std,native-pe,core-tests' -and $names[-1] -eq 'python-tests')

  # CI runs every PowerShell test the gate runs, and the two native checks after its build.
  $workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/build.yml') -Raw
  $default = @($tests | Where-Object { $real -match ('(?m)^test:[\w]+: .*' + [regex]::Escape("tests/$_")) })
  $notInCi = @($default | Where-Object { $workflow -notmatch [regex]::Escape("tests\$_") })
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
  foreach ($name in 'todo-graph.py', 'review-dispatch.py', 'check-all.ps1', 'lib/todo_graph.py', 'lib/host_caps.py', 'lib/job-measure.ps1') {
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
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore }
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
