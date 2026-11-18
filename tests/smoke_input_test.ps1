# Tests scripts/smoke-input.ps1 (D02 T02 §12) with stand-ins: a dotnet that lists, runs and reports tests as each case
# tells it, an input watcher that only keeps its file, and the lock and idle probes read from files. It never runs the
# real smoke or touches the desktop: the lock has a name of its own, the state folder is the test's, and every process
# a case starts is ended as a tree at the end. Console children start without a window of their own.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts/smoke-input.ps1'
$capture = Join-Path $repo 'scripts/capture-window.ps1'
$shell = (Get-Process -Id $PID).Path
$failed = [System.Collections.Generic.List[string]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Said = '') {
  if ($Ok) { "ok   $Name" } else { "FAIL $Name"; if ($Said) { $Said.TrimEnd() -split "`r?`n" | Select-Object -Last 12 | ForEach-Object { "     | $_" } }; $failed.Add($Name) }
}

$scratch = if ($env:BOSUN_SCRATCH -and (Test-Path -LiteralPath $env:BOSUN_SCRATCH -PathType Container)) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$dir = Join-Path $scratch ('smoke-input-test-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$fakes = Join-Path $dir 'fakes'
New-Item -ItemType Directory -Force -Path $fakes | Out-Null
$lockName = 'test-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$taskName = 'Bosun smoke-input test ' + [guid]::NewGuid().ToString('N').Substring(0, 8)

# The stand-in dotnet: what it does is in <state>\mode.txt (one word, or a word and a class), the tests it knows in
# <state>\tests.txt (one full name a line). It writes the results the runner asked for as TRX.
Set-Content -LiteralPath (Join-Path $fakes 'dotnet.ps1') -Encoding utf8 -Value @'
$state = $env:STANDIN_STATE
$mode = (Get-Content -LiteralPath (Join-Path $state 'mode.txt') -Raw).Trim()
$word, $which = $mode -split ' ', 2
$tests = @(Get-Content -LiteralPath (Join-Path $state 'tests.txt') | Where-Object { $_.Trim() })
if ($args[0] -eq 'build') { Set-Content -LiteralPath (Join-Path $state 'build-args.txt') -Value ($args -join ' '); if ($word -eq 'build-fails') { 'error: the build broke'; exit 1 }; 'Build succeeded.'; exit 0 }
if ($args -contains '--list-tests') {
  if ($word -eq 'none') { $tests = @() }
  if ($word -eq 'list-stall') { Start-Sleep 120 }
  @{ version = '1.0'; testContainers = @(@{ tests = @($tests | ForEach-Object { @{ displayName = $_ } }) }) } | ConvertTo-Json -Depth 6
  exit 0
}
$class = $args[[array]::IndexOf($args, '--filter-class') + 1]
$dir = $args[[array]::IndexOf($args, '--results-directory') + 1]
$file = $args[[array]::IndexOf($args, '--report-xunit-trx-filename') + 1]
Add-Content -LiteralPath (Join-Path $state 'classes.txt') -Value $class
$mine = @($tests | Where-Object { ($_ -replace '\.[^.]+$', '') -eq $class })
if ($which -and $which -eq $class) {
  if ($word -eq 'person') {
    $record = (Get-ChildItem -LiteralPath $state -Filter 'watch-*.txt' | Select-Object -First 1).FullName
    Set-Content -LiteralPath "$record.tmp" -Value (Get-Date).ToUniversalTime().AddSeconds(1).ToString('o')
    Move-Item -LiteralPath "$record.tmp" -Destination $record -Force
  }
  if ($word -eq 'lock') { Set-Content -LiteralPath (Join-Path $state 'probe-locked') -Value 'locked' }
  if ($word -eq 'watcher-dies') { Stop-Process -Id ([int](Get-Content -LiteralPath (Join-Path $state 'watcher.pid') -Raw)) -Force }
  if ($word -eq 'person-dies') {
    # A person comes (the watcher's event, as its hook sets it), then the watcher ends.
    ([Threading.EventWaitHandle]::OpenExisting((Get-Content -LiteralPath (Join-Path $state 'watcher.event') -Raw).Trim())).Set() | Out-Null
    Stop-Process -Id ([int](Get-Content -LiteralPath (Join-Path $state 'watcher.pid') -Raw)) -Force
  }
  if ($word -eq 'hold' -or $word -eq 'hold-crash' -or $word -eq 'hold-dies') {
    # F24 pressed and never let go, as a drag ended between its button-down and button-up.
    Add-Type 'using System; using System.Runtime.InteropServices; public static class F24 { [StructLayout(LayoutKind.Explicit, Size = 40)] struct K { [FieldOffset(0)] public int type; [FieldOffset(8)] public ushort vk; [FieldOffset(10)] public ushort scan; [FieldOffset(12)] public int flags; [FieldOffset(16)] public int time; [FieldOffset(24)] public IntPtr extra; } [DllImport("user32.dll")] static extern uint SendInput(uint n, K[] i, int s); public static uint Down() { var k = new K[1]; k[0].type = 1; k[0].vk = 0x87; k[0].extra = new IntPtr(0x42534E31); return SendInput(1, k, Marshal.SizeOf(typeof(K))); } }'
    if ([F24]::Down() -eq 1) {
      Set-Content -LiteralPath (Join-Path $state 'held.txt') -Value 'down'
      # What the real watcher writes when the smoke presses a key and does not let go of it: its record's second line.
      $record = (Get-ChildItem -LiteralPath $state -Filter 'watch-*.txt' | Select-Object -First 1).FullName
      # Whole, through a temporary file and a replace, as the real watcher writes it.
      Set-Content -LiteralPath "$record.tmp" -Value @((@(Get-Content -LiteralPath $record) + @('none'))[0], '135')
      Move-Item -LiteralPath "$record.tmp" -Destination $record -Force
    }
    # The watcher dies while the key is held, as a watcher ended mid-drag.
    if ($word -eq 'hold-dies') { Stop-Process -Id ([int](Get-Content -LiteralPath (Join-Path $state 'watcher.pid') -Raw)) -Force }
    # A test host that dies holding it, before it ever lets go, its report cut off.
    if ($word -eq 'hold-crash') { Set-Content -LiteralPath (Join-Path $dir $file) -Value '<TestRun><Results><UnitTestResult testName='; exit 3 }
    Start-Sleep 120
  }
  if ($word -eq 'sleep') {
    # A report begun and never finished: the class is ended mid-write.
    Set-Content -LiteralPath (Join-Path $dir $file) -Value '<TestRun><Results><UnitTestResult testName='
    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -NoNewWindow -PassThru
    Set-Content -LiteralPath (Join-Path $state 'child.txt') -Value $child.Id
    Start-Sleep 120
  }
}
$pause = @(Get-Content -LiteralPath (Join-Path $state 'pauses.txt') -ErrorAction Ignore | Where-Object { ($_ -split ' ')[0] -eq $class })
if ($pause) { Start-Sleep -Seconds ([int]($pause[0] -split ' ')[1]) }
$rows = foreach ($t in $mine) {
  $outcome = 'Passed'
  if ($word -eq 'omit' -and $t -eq $which) { continue }
  if ($word -eq 'skip' -and $t -eq $which) { $outcome = 'NotExecuted' }
  if ($word -eq 'fail' -and $t -eq $which) { $outcome = 'Failed' }
  "<UnitTestResult testName=`"$t`" outcome=`"$outcome`" />"
  if ($word -eq 'repeat' -and $t -eq $which) { "<UnitTestResult testName=`"$t`" outcome=`"Passed`" />" }
}
Set-Content -LiteralPath (Join-Path $dir $file) -Value "<TestRun><Results>$($rows -join '')</Results></TestRun>"
# A test host that fails after its report is written whole.
if ($word -eq 'exit1' -and $which -eq $class) { exit 1 }
exit 0
'@
Set-Content -LiteralPath (Join-Path $fakes 'dotnet.cmd') -Value "@`"$shell`" -NoProfile -File `"$(Join-Path $fakes 'dotnet.ps1')`" %*"
# The stand-in watcher: it starts its file as the real one does and keeps it; a case writes a person's time into it.
Set-Content -LiteralPath (Join-Path $fakes 'watcher.ps1') -Encoding utf8 -Value @'
$state = $env:STANDIN_STATE
if ((Get-Content -LiteralPath (Join-Path $state 'mode.txt') -Raw).Trim() -eq 'watcher-fails') { [Console]::Error.WriteLine('the hooks could not be set'); exit 70 }
Set-Content -LiteralPath (Join-Path $state 'watcher.pid') -Value $PID
if ($args.Count -gt 2) { Set-Content -LiteralPath (Join-Path $state 'watcher.event') -Value $args[2] }
Set-Content -LiteralPath $args[1] -Value none
'watching'
# Asked to let go of what the smoke holds, it does as the real one does, from the list the case left in its record.
Add-Type 'using System; using System.Runtime.InteropServices; public static class Up { [StructLayout(LayoutKind.Explicit, Size = 40)] struct K { [FieldOffset(0)] public int type; [FieldOffset(8)] public ushort vk; [FieldOffset(12)] public int flags; [FieldOffset(24)] public IntPtr extra; } [DllImport("user32.dll")] static extern uint SendInput(uint n, K[] i, int s); public static uint Key(int vk) { var k = new K[1]; k[0].type = 1; k[0].vk = (ushort)vk; k[0].flags = 2; k[0].extra = new IntPtr(0x42534E31); return SendInput(1, k, Marshal.SizeOf(typeof(K))); } }'
for ($line = [Console]::In.ReadLine(); $null -ne $line; $line = [Console]::In.ReadLine()) {
  if ($line.Trim() -ne 'release') { continue }
  $held = @((@(Get-Content -LiteralPath $args[1]) + @('', ''))[1] -split ',' | Where-Object { $_ -match '^\d+$' })
  $let = @($held | Where-Object { [Up]::Key([int]$_) -eq 1 })
  "released $($let -join ',')"
}
Start-Sleep 600
'@
Set-Content -LiteralPath (Join-Path $fakes 'watcher.cmd') -Value "@`"$shell`" -NoProfile -File `"$(Join-Path $fakes 'watcher.ps1')`" %*"

$tests = @(
  'Bosun.Panel.E2E.Alpha.First', 'Bosun.Panel.E2E.Alpha.Second',
  'Bosun.Panel.E2E.Beta.Only',
  'Bosun.Panel.E2E.Gamma.One', 'Bosun.Panel.E2E.Gamma.Two'
)
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

# A run of the script, in a state folder of the case's own (or the one given), its output and exit code.
function Invoke-Run([string]$Mode, [string[]]$Extra = @(), [string]$State = '', [hashtable]$Probes = @{}, [string[]]$Pauses = @()) {
  if (-not $State) { $State = Join-Path $dir ('state-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
  New-Item -ItemType Directory -Force -Path $State | Out-Null
  Set-Content -LiteralPath (Join-Path $State 'mode.txt') -Value $Mode
  Set-Content -LiteralPath (Join-Path $State 'tests.txt') -Value $tests
  Remove-Item -LiteralPath (Join-Path $State 'classes.txt'), (Join-Path $State 'probe-locked'), (Join-Path $State 'probe-idle'), (Join-Path $State 'pauses.txt') -ErrorAction Ignore
  if ($Pauses) { Set-Content -LiteralPath (Join-Path $State 'pauses.txt') -Value $Pauses }
  if ($Probes.locked) { Set-Content -LiteralPath (Join-Path $State 'probe-locked') -Value 'locked' }
  Set-Content -LiteralPath (Join-Path $State 'probe-idle') -Value $(if ($Probes.ContainsKey('idle')) { $Probes.idle } else { 3600 })
  $env:STANDIN_STATE = $State
  $env:BOSUN_DESKTOP_LOCK = $lockName
  try {
    $said = & $shell -NoProfile -File $script -StateFolder $State -Dotnet (Join-Path $fakes 'dotnet.cmd') -Watcher (Join-Path $fakes 'watcher.cmd') -TestProbes -NoQueue @Extra 2>&1 | Out-String
    $code = $LASTEXITCODE
  } finally { Remove-Item Env:STANDIN_STATE, Env:BOSUN_DESKTOP_LOCK -ErrorAction Ignore }
  $records = @(if (Test-Path -LiteralPath (Join-Path $State 'input-history.jsonl')) { Get-Content -LiteralPath (Join-Path $State 'input-history.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } })
  [pscustomobject]@{ Code = $code; Said = $said; State = $State; Last = if ($records) { $records[-1] } else { $null }; Records = $records }
}

try {
  # A complete run: every test found ran once and passed, one class at a time.
  $run = Invoke-Run 'pass'
  Check "a complete run is passed with exit 0 ($($run.Code))" ($run.Code -eq 0 -and $run.Last.outcome -eq 'passed' -and $run.Last.passed -eq 5 -and $run.Last.discovered -eq 5 -and $run.Last.mode -eq 'manual') $run.Said
  # The stand-in is a PowerShell script, whose -File parsing splits "-maxCpuCount:2" in two: either form is the limit.
  Check 'the build is the two-node build the gate uses' ((Get-Content -LiteralPath (Join-Path $run.State 'build-args.txt') -Raw) -match 'build .*Bosun\.Panel\.E2E.* -maxCpuCount[: ]2')
  Check 'each class ran once, in turn' ((Get-Content -LiteralPath (Join-Path $run.State 'classes.txt')) -join ',' -eq 'Bosun.Panel.E2E.Alpha,Bosun.Panel.E2E.Beta,Bosun.Panel.E2E.Gamma') $run.Said

  # Whatever keeps a run from being whole fails it, with the names and exit 1.
  foreach ($case in @(
      @{ mode = 'omit Bosun.Panel.E2E.Alpha.Second'; says = 'did not run: Bosun.Panel.E2E.Alpha.Second' },
      @{ mode = 'repeat Bosun.Panel.E2E.Beta.Only'; says = 'ran twice: Bosun.Panel.E2E.Beta.Only' },
      @{ mode = 'skip Bosun.Panel.E2E.Gamma.Two'; says = 'did not pass: Bosun.Panel.E2E.Gamma.Two (NotExecuted)' },
      @{ mode = 'fail Bosun.Panel.E2E.Gamma.One'; says = 'did not pass: Bosun.Panel.E2E.Gamma.One (Failed)' },
      @{ mode = 'none'; says = 'no real-input test was found' },
      @{ mode = 'build-fails'; says = 'the build failed' },
      @{ mode = 'exit1 Bosun.Panel.E2E.Beta'; says = "a class's run failed: Bosun.Panel.E2E.Beta (exit 1)" })) {
    $run = Invoke-Run $case.mode
    Check "'$($case.mode)' is failed with exit 1, saying so" ($run.Code -eq 1 -and $run.Last.outcome -eq 'failed' -and "$($run.Last.reason)" -like "*$($case.says)*") $run.Said
  }

  # The nightly run's guard: a locked or recently used desk is a skip, exit 0, nothing run.
  $run = Invoke-Run 'pass' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Probes @{ locked = $true }
  Check "a locked desktop skips the night (exit $($run.Code))" ($run.Code -eq 0 -and $run.Last.outcome -eq 'skipped-locked' -and -not (Test-Path -LiteralPath (Join-Path $run.State 'classes.txt'))) $run.Said
  $run = Invoke-Run 'pass' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Probes @{ idle = 60 }
  Check "a desk used a minute ago skips the night (exit $($run.Code))" ($run.Code -eq 0 -and $run.Last.outcome -eq 'skipped-in-use' -and $run.Last.mode -eq 'scheduled') $run.Said

  # A person or a lock during a class ends that class at once, not at its end, and nothing after it runs.
  $clock = [Diagnostics.Stopwatch]::StartNew()
  $run = Invoke-Run 'person Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Pauses @('Bosun.Panel.E2E.Alpha 60')
  $took = $clock.Elapsed.TotalSeconds
  Check "a person during a class ends it at once ($([int]$took) s of its 60, exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'stopped-in-use' -and "$($run.Last.reason)" -like '*during Bosun.Panel.E2E.Alpha*' -and $took -lt 40 -and @(Get-Content -LiteralPath (Join-Path $run.State 'classes.txt')).Count -eq 1) $run.Said
  $clock.Restart()
  $run = Invoke-Run 'lock Bosun.Panel.E2E.Beta' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Pauses @('Bosun.Panel.E2E.Beta 60')
  $took = $clock.Elapsed.TotalSeconds
  Check "a desktop locked during a class ends it at once ($([int]$took) s, exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'stopped-locked' -and $took -lt 40 -and @(Get-Content -LiteralPath (Join-Path $run.State 'classes.txt')).Count -eq 2) $run.Said
  # The watcher fails closed: one that never says it is ready, or ends during the run, stops the run unwatched.
  $run = Invoke-Run 'watcher-fails' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s'))
  Check "a watcher that never starts fails the night before any class (exit $($run.Code))" ($run.Code -eq 1 -and "$($run.Last.reason)" -like '*the input watcher did not start*' -and -not (Test-Path -LiteralPath (Join-Path $run.State 'classes.txt'))) $run.Said
  $run = Invoke-Run 'watcher-dies Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Pauses @('Bosun.Panel.E2E.Alpha 60')
  Check "a watcher that ends during a class stops the run (exit $($run.Code))" ($run.Code -eq 1 -and "$($run.Last.reason)" -like '*the input watcher ended*' -and @(Get-Content -LiteralPath (Join-Path $run.State 'classes.txt')).Count -eq 1) $run.Said
  # One that ends after a person came is trusted for nothing: what it last listed may be theirs.
  $run = Invoke-Run 'person-dies Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s')) -Pauses @('Bosun.Panel.E2E.Alpha 60')
  Check "a watcher that ends after a person came lets go of nothing (exit $($run.Code))" ($run.Code -eq 1 -and (Get-Content -LiteralPath $run.Last.log -Raw) -like '*nothing let go of: the input watcher had ended*') $run.Said
  # The watcher ends with the runner, however the runner ends: killed alone, with no finally run.
  $state = Join-Path $dir 'state-killed'
  New-Item -ItemType Directory -Force -Path $state | Out-Null
  Set-Content -LiteralPath (Join-Path $state 'mode.txt') -Value 'pass'
  Set-Content -LiteralPath (Join-Path $state 'tests.txt') -Value $tests
  Set-Content -LiteralPath (Join-Path $state 'pauses.txt') -Value 'Bosun.Panel.E2E.Alpha 60'
  Set-Content -LiteralPath (Join-Path $state 'probe-idle') -Value 3600
  $env:STANDIN_STATE = $state; $env:BOSUN_DESKTOP_LOCK = $lockName
  $info = [System.Diagnostics.ProcessStartInfo]::new($shell)
  foreach ($a in @('-NoProfile', '-File', $script, '-StateFolder', $state, '-Dotnet', (Join-Path $fakes 'dotnet.cmd'), '-Watcher', (Join-Path $fakes 'watcher.cmd'), '-TestProbes', '-NoQueue', '-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s'))) { $info.ArgumentList.Add($a) }
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true
  $runner = [System.Diagnostics.Process]::Start($info); $started.Add($runner)
  try {
    for ($i = 0; $i -lt 150 -and -not (Test-Path -LiteralPath (Join-Path $state 'classes.txt')); $i++) { Start-Sleep -Milliseconds 200 }
    $watcherPid = [int](Get-Content -LiteralPath (Join-Path $state 'watcher.pid') -Raw)
    $runner.Kill()
    $runner.WaitForExit(10000) | Out-Null
    $gone = $false
    for ($i = 0; $i -lt 50 -and -not $gone; $i++) { $gone = -not (Get-Process -Id $watcherPid -ErrorAction Ignore); if (-not $gone) { Start-Sleep -Milliseconds 100 } }
    Check 'a runner killed alone takes its watcher with it' $gone
  } finally {
    Remove-Item Env:STANDIN_STATE, Env:BOSUN_DESKTOP_LOCK -ErrorAction Ignore
    if (-not $runner.HasExited) { & taskkill.exe /T /F /PID $runner.Id *> $null }
  }
  # Each run keeps a watcher file of its own: a run refused the lock beside a running one leaves the running one's alone.
  $watchFiles = @(Get-ChildItem -LiteralPath $state -Filter 'watch-*.txt' -ErrorAction Ignore)
  Check "a run's watcher file is its own, named for it ($(@($watchFiles).Count) left by the killed run)" (@($watchFiles).Count -le 1 -and (@($watchFiles | Where-Object { $_.Name -match '^watch-\d{8}-\d{6}-\d+-[0-9a-f]{8}\.txt$' }).Count -eq @($watchFiles).Count))

  # A class ended while it holds a key down (a drag between button-down and button-up) has it let go of by the runner.
  $run = Invoke-Run 'hold Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddSeconds(20).ToString('s'))
  Add-Type 'using System.Runtime.InteropServices; public static class F24State { [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k); public static bool Down() { return (GetAsyncKeyState(0x87) & 0x8000) != 0; } }' -ErrorAction Ignore
  Check "a key held by an ended class is let go of (exit $($run.Code), held: $(Test-Path -LiteralPath (Join-Path $run.State 'held.txt')))" ($run.Last.outcome -eq 'stopped-deadline' -and (Test-Path -LiteralPath (Join-Path $run.State 'held.txt')) -and -not [F24State]::Down() -and (Get-Content -LiteralPath $run.Last.log -Raw) -like '*let go of*0x87*') $run.Said

  # The same when the class's host dies holding it: its failure is recorded, and the key is let go of before the next.
  $run = Invoke-Run 'hold-crash Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s'))
  Check "a key held by a class whose host died is let go of (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'failed' -and (Test-Path -LiteralPath (Join-Path $run.State 'held.txt')) -and -not [F24State]::Down() -and (Get-Content -LiteralPath $run.Last.log -Raw) -like '*let go of*0x87*') $run.Said

  # The real watcher ends when it can no longer keep its record (its folder gone, as a full or failed disk would), so
  # the runner's "the watcher ended" stop takes over; it never goes on with an old "none" on file.
  $watcherExe = Join-Path $repo 'build/dotnet/bin/Bosun.TestChild/debug/Bosun.TestChild.exe'
  if (Test-Path -LiteralPath $watcherExe) {
    $gone = Join-Path $dir 'watch-gone'
    New-Item -ItemType Directory -Force -Path $gone | Out-Null
    $info = [System.Diagnostics.ProcessStartInfo]::new($watcherExe)
    $info.ArgumentList.Add('watch-input'); $info.ArgumentList.Add((Join-Path $gone 'watch.txt'))
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $real = [System.Diagnostics.Process]::Start($info); $started.Add($real)
    $ready = $real.StandardOutput.ReadLine()
    Remove-Item -LiteralPath $gone -Recurse -Force
    $real.StandardInput.WriteLine('person'); $real.StandardInput.Flush()
    $ended = $real.WaitForExit(5000)
    Check "the real watcher ends when its record cannot be written ($ready, exit $(if ($ended) { $real.ExitCode } else { 'none: still running' }))" ($ready -eq 'watching' -and $ended -and $real.ExitCode -eq 71)
    # And it says a person is there through the named event at once, with no disk in the way.
    $eventName = "Local\bosun-smoke-test-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $seen = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)
    try {
      $info = [System.Diagnostics.ProcessStartInfo]::new($watcherExe)
      $info.ArgumentList.Add('watch-input'); $info.ArgumentList.Add((Join-Path $dir 'watch-event.txt')); $info.ArgumentList.Add($eventName)
      $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true
      $real = [System.Diagnostics.Process]::Start($info); $started.Add($real)
      $ready = $real.StandardOutput.ReadLine()
      $before = $seen.WaitOne(0)
      $real.StandardInput.WriteLine('person'); $real.StandardInput.Flush()
      Check "the real watcher sets the person event the moment a person comes ($ready, before: $before)" ($ready -eq 'watching' -and -not $before -and $seen.WaitOne(3000))
      # Asked to release, it lets go of what the smoke holds by its own list, and leaves a key a person took over.
      Add-Type 'using System; using System.Runtime.InteropServices; public static class F24Send { [StructLayout(LayoutKind.Explicit, Size = 40)] struct K { [FieldOffset(0)] public int type; [FieldOffset(8)] public ushort vk; [FieldOffset(12)] public int flags; [FieldOffset(24)] public IntPtr extra; } [DllImport("user32.dll")] static extern uint SendInput(uint n, K[] i, int s); public static uint Key(bool up, long tag) { var k = new K[1]; k[0].type = 1; k[0].vk = 0x87; k[0].flags = up ? 2 : 0; k[0].extra = new IntPtr(tag); return SendInput(1, k, Marshal.SizeOf(typeof(K))); } }' -ErrorAction Ignore
      Add-Type 'using System.Runtime.InteropServices; public static class F24State { [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k); public static bool Down() { return (GetAsyncKeyState(0x87) & 0x8000) != 0; } }' -ErrorAction Ignore
      try {
        [void][F24Send]::Key($false, 0x42534E31); Start-Sleep -Milliseconds 200
        $real.StandardInput.WriteLine('release'); $real.StandardInput.Flush()
        $answer = "$($real.StandardOutput.ReadLine())".Trim()
        Check "the real watcher lets go of the smoke's key when asked ('$answer')" ($answer -eq 'released 135' -and -not [F24State]::Down())
        [void][F24Send]::Key($false, 0x42534E31); Start-Sleep -Milliseconds 200
        [void][F24Send]::Key($false, 0); Start-Sleep -Milliseconds 200
        $real.StandardInput.WriteLine('release'); $real.StandardInput.Flush()
        $answer = "$($real.StandardOutput.ReadLine())".Trim()
        Check "and leaves one a person took over ('$answer')" ($answer -eq 'released' -and [F24State]::Down())
        # A release of the watcher's own arriving for a key a person now holds (sent before they took it) is stopped in
        # the hook as it arrives, so no application sees their key let go of.
        [void][F24Send]::Key($true, 0x42534E32); Start-Sleep -Milliseconds 200
        Check 'a late release of a key a person holds is stopped in the hook' ([F24State]::Down())
        # So is a test's own key-up (the smoke's ordinary mark) once a person holds the key.
        [void][F24Send]::Key($true, 0x42534E31); Start-Sleep -Milliseconds 200
        Check "a test's own release of a key a person holds is stopped in the hook" ([F24State]::Down())
      } finally { [void][F24Send]::Key($true, 0) }
      if (-not $real.HasExited) { $real.Kill($true) }
      # Guarded (the nightly run), once a person has come no new press of the smoke's passes: a person's F24 down, the
      # smoke's down and up after it, and the person's key is still down (unguarded, the smoke's up would let it go).
      $info = [System.Diagnostics.ProcessStartInfo]::new($watcherExe)
      $info.ArgumentList.Add('watch-input'); $info.ArgumentList.Add((Join-Path $dir 'watch-guard.txt')); $info.ArgumentList.Add($eventName); $info.ArgumentList.Add('guard')
      $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true
      $real = [System.Diagnostics.Process]::Start($info); $started.Add($real)
      [void]$real.StandardOutput.ReadLine()
      try {
        [void][F24Send]::Key($false, 0); Start-Sleep -Milliseconds 200
        [void][F24Send]::Key($false, 0x42534E31); Start-Sleep -Milliseconds 200
        [void][F24Send]::Key($true, 0x42534E31); Start-Sleep -Milliseconds 200
        Check "guarded, the smoke's press after a person's is stopped, and so its release" ([F24State]::Down())
        # And its pointer moves: the smoke's move after a person's leaves the pointer where the person put it.
        Add-Type 'using System; using System.Runtime.InteropServices; public static class MoveSend { [StructLayout(LayoutKind.Explicit, Size = 40)] struct M { [FieldOffset(0)] public int type; [FieldOffset(8)] public int dx; [FieldOffset(12)] public int dy; [FieldOffset(20)] public int flags; [FieldOffset(32)] public IntPtr extra; } [DllImport("user32.dll")] static extern uint SendInput(uint n, M[] i, int s); [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; } [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p); public static uint By(int dx, long tag) { var m = new M[1]; m[0].type = 0; m[0].dx = dx; m[0].flags = 1; m[0].extra = new IntPtr(tag); return SendInput(1, m, Marshal.SizeOf(typeof(M))); } }' -ErrorAction Ignore
        $at = [MoveSend+P]::new(); [void][MoveSend]::GetCursorPos([ref]$at)
        [void][MoveSend]::By(40, 0x42534E31); Start-Sleep -Milliseconds 200
        $then = [MoveSend+P]::new(); [void][MoveSend]::GetCursorPos([ref]$then)
        Check "guarded, the smoke's pointer move after a person is stopped ($($at.X) -> $($then.X))" ($then.X -eq $at.X)
      } finally { [void][F24Send]::Key($true, 0) }
    } finally {
      if ($real -and -not $real.HasExited) { $real.Kill($true) }
      $seen.Dispose()
    }
  } else {
    Check 'Bosun.TestChild is built, for the watcher check (run dotnet build first)' $false
  }

  # On demand too: the watcher runs for what the smoke holds, though it stops nothing for a person.
  $run = Invoke-Run 'hold-crash Bosun.Panel.E2E.Alpha'
  Check "an on-demand run lets go of what its crashed class held (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.mode -eq 'manual' -and (Test-Path -LiteralPath (Join-Path $run.State 'held.txt')) -and -not [F24State]::Down() -and (Get-Content -LiteralPath $run.Last.log -Raw) -like '*let go of*0x87*') $run.Said

  # A watcher that ends while the smoke holds a key: who holds it can no longer be told, so nothing is let go of.
  $run = Invoke-Run 'hold-dies Bosun.Panel.E2E.Alpha' @('-Scheduled', '-Deadline', (Get-Date).AddHours(1).ToString('s'))
  $leftDown = [F24State]::Down()
  [void][F24Send]::Key($true, 0)
  Check "a watcher that ends mid-hold lets go of nothing (exit $($run.Code), F24 left down: $leftDown)" ($run.Code -eq 1 -and "$($run.Last.reason)" -like '*the input watcher ended*' -and $leftDown -and (Get-Content -LiteralPath $run.Last.log -Raw) -like '*nothing let go of: the input watcher had ended*') $run.Said

  # On demand the watcher is kept too: one that ends stops the run.
  $run = Invoke-Run 'watcher-dies Bosun.Panel.E2E.Alpha' -Pauses @('Bosun.Panel.E2E.Alpha 60')
  Check "an on-demand run whose watcher ends stops (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.mode -eq 'manual' -and "$($run.Last.reason)" -like '*the input watcher ended*') $run.Said

  # The window has two ends: a scheduled run started before it (03:00, three hours before its 06:00 deadline) is a skip.
  $run = Invoke-Run 'pass' @('-Scheduled', '-Deadline', (Get-Date).AddHours(4).ToString('s'))
  Check "a scheduled run started before its window is skipped (exit $($run.Code))" ($run.Code -eq 0 -and $run.Last.outcome -eq 'skipped-outside-window' -and -not (Test-Path -LiteralPath (Join-Path $run.State 'classes.txt'))) $run.Said
  # A deadline never moves the window: outside the script's own tests, a scheduled run in the day is refused before it
  # touches the desk, whatever -Deadline it is given. (Between 03:00 and 06:00 the real window is open, so it is not
  # checked then.)
  $hour = (Get-Date).Hour
  if ($hour -lt 3 -or $hour -ge 6) {
    $state = Join-Path $dir 'state-daytime'
    New-Item -ItemType Directory -Force -Path $state | Out-Null
    $env:BOSUN_DESKTOP_LOCK = $lockName
    try { $said = & $shell -NoProfile -File $script -Scheduled -Deadline (Get-Date).AddHours(1).ToString('s') -StateFolder $state -Dotnet (Join-Path $fakes 'dotnet.cmd') -Watcher (Join-Path $fakes 'watcher.cmd') -NoQueue 2>&1 | Out-String; $code = $LASTEXITCODE }
    finally { Remove-Item Env:BOSUN_DESKTOP_LOCK -ErrorAction Ignore }
    $last = @(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })[-1]
    Check "a daytime -Deadline does not open the window (exit $code, $($last.outcome))" ($last.outcome -in 'stopped-deadline', 'skipped-outside-window' -and -not (Test-Path -LiteralPath (Join-Path $state 'classes.txt'))) $said
  }

  # Finding the tests keeps the deadline too.
  $run = Invoke-Run 'list-stall' @('-Scheduled', '-Deadline', (Get-Date).AddSeconds(15).ToString('s'))
  Check "discovery that stalls past the deadline is ended and recorded (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'stopped-deadline' -and "$($run.Last.reason)" -like '*finding the tests*') $run.Said

  # The window's close is a deadline: a class still running then is ended with what it started, and the counts so far kept.
  $state = Join-Path $dir 'state-deadline'
  $run = Invoke-Run 'sleep Bosun.Panel.E2E.Beta' @('-Scheduled', '-Deadline', (Get-Date).AddSeconds(25).ToString('s')) -State $state
  $child = [int](Get-Content -LiteralPath (Join-Path $state 'child.txt') -Raw)
  # Its report was left half written: the deadline is still what is recorded.
  Check "a class running at the deadline is ended, recorded with the counts so far (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'stopped-deadline' -and $run.Last.passed -eq 2) $run.Said
  Check 'and what that class started is gone with it' (-not (Get-Process -Id $child -ErrorAction Ignore))
  # The on-demand run has no deadline: at any hour it runs whole.
  $run = Invoke-Run 'pass' @('-Deadline', (Get-Date).AddSeconds(-5).ToString('s'))
  Check "an on-demand run has no deadline (exit $($run.Code))" ($run.Code -eq 0 -and $run.Last.outcome -eq 'passed') $run.Said

  # The lock is held across the classes: a capture started in the first class still waits when the next begins,
  # and is refused once its wait runs out in the last.
  $state = Join-Path $dir 'state-lock'
  New-Item -ItemType Directory -Force -Path $state | Out-Null
  Set-Content -LiteralPath (Join-Path $state 'mode.txt') -Value 'pass'
  Set-Content -LiteralPath (Join-Path $state 'tests.txt') -Value $tests
  Set-Content -LiteralPath (Join-Path $state 'pauses.txt') -Value @('Bosun.Panel.E2E.Alpha 4', 'Bosun.Panel.E2E.Gamma 12')
  Set-Content -LiteralPath (Join-Path $state 'probe-idle') -Value 3600
  $env:STANDIN_STATE = $state; $env:BOSUN_DESKTOP_LOCK = $lockName
  $info = [System.Diagnostics.ProcessStartInfo]::new($shell)
  foreach ($a in @('-NoProfile', '-File', $script, '-StateFolder', $state, '-Dotnet', (Join-Path $fakes 'dotnet.cmd'), '-Watcher', (Join-Path $fakes 'watcher.cmd'), '-TestProbes', '-NoQueue')) { $info.ArgumentList.Add($a) }
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true
  $long = [System.Diagnostics.Process]::Start($info); $started.Add($long)
  try {
    for ($i = 0; $i -lt 150 -and -not (Test-Path -LiteralPath (Join-Path $state 'classes.txt')); $i++) { Start-Sleep -Milliseconds 200 }
    $env:BOSUN_DESKTOP_WAIT_S = '8'
    $said = & $shell -NoProfile -File $capture -Path $shell '-LaunchArgs:-NoProfile -Command Start-Sleep 5' -NoNewWindow -WaitSeconds 1 -Out (Join-Path $dir 'capture.png') 2>&1 | Out-String
    $code = $LASTEXITCODE
    $classesThen = @(Get-Content -LiteralPath (Join-Path $state 'classes.txt'))
    Check "a capture started in the first class waits past the next and is refused (exit $code, classes $($classesThen.Count))" ($code -eq 4 -and $said -like '*smoke-input.ps1*' -and $classesThen.Count -eq 3) $said
    $long.WaitForExit(60000) | Out-Null
    $record = @(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })[-1]
    Check 'and the run it waited on passed whole' ($long.HasExited -and $long.ExitCode -eq 0 -and $record.outcome -eq 'passed')
  } finally {
    Remove-Item Env:BOSUN_DESKTOP_WAIT_S, Env:STANDIN_STATE, Env:BOSUN_DESKTOP_LOCK -ErrorAction Ignore
    if (-not $long.HasExited) { & taskkill.exe /T /F /PID $long.Id *> $null }
  }

  # A smoke class run under the runner's hold works under it: the real E2E fixture, its wait cut to a second, starts
  # at once in a child of a holder of the lock and leaves the holder's record as it was.
  $e2e = Join-Path $repo 'tests/Bosun.Panel.E2E'
  if (Test-Path -LiteralPath (Join-Path $repo 'build/dotnet/bin/Bosun.Panel.E2E')) {
    $holderScript = Join-Path $dir 'holder.ps1'
    Set-Content -LiteralPath $holderScript -Encoding utf8 -Value @"
`$ErrorActionPreference = 'Stop'
Add-Type -Path '$(Join-Path $repo 'scripts/lib/DesktopLock.cs')'
`$folder = [Bosun.Desktop.DesktopLock]::RecordFolder()
`$waiter = [Bosun.Desktop.DesktopWaiter]::new('$lockName', `$folder, 'holder.ps1', `$null)
try { `$holder = `$waiter.TryTake([TimeSpan]::FromSeconds(5)) } finally { `$waiter.Dispose() }
if (-not `$holder) { 'could not take the lock'; exit 9 }
try {
  `$path = [Bosun.Desktop.DesktopLock]::RecordPath('$lockName', `$folder)
  `$before = Get-Content -LiteralPath `$path -Raw
  `$env:BOSUN_DESKTOP_HELD = `$holder.HeldValue
  `$env:BOSUN_DESKTOP_LOCK = '$lockName'
  `$env:BOSUN_DESKTOP_WAIT_S = '1'
  dotnet test --project '$e2e' --no-build --filter-method '*The_run_holds_the_desktop_lock_and_names_itself' 2>&1 | Where-Object { "`$_" -match 'total:|failed:|succeeded:|error' } | ForEach-Object { "`$_" }
  `$code = `$LASTEXITCODE
  if ((Get-Content -LiteralPath `$path -Raw) -ne `$before) { 'the record changed'; exit 8 }
  exit `$code
} finally { `$holder.Dispose() }
"@
    $said = & $shell -NoProfile -File $holderScript 2>&1 | Out-String
    Check "the fixture under a holder's lock starts at once and leaves its record (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and $said -match 'succeeded: 1') $said
  } else {
    Check 'the E2E project is built, for the inherited-hold check (run dotnet build first)' $false
  }

  # A holder of the desktop lock, for the cases that find it taken: it takes the test's lock and keeps it until its
  # file is removed, or for two minutes at most.
  $holdFile = Join-Path $dir 'hold.txt'
  $holdScript = Join-Path $dir 'hold.ps1'
  Set-Content -LiteralPath $holdScript -Encoding utf8 -Value @"
Add-Type -Path '$(Join-Path $repo 'scripts/lib/DesktopLock.cs')'
`$waiter = [Bosun.Desktop.DesktopWaiter]::new('$lockName', [Bosun.Desktop.DesktopLock]::RecordFolder(), 'hold.ps1', `$null)
try { `$holder = `$waiter.TryTake([TimeSpan]::FromSeconds(5)) } finally { `$waiter.Dispose() }
if (-not `$holder) { exit 9 }
Set-Content -LiteralPath '$holdFile' -Value held
for (`$i = 0; `$i -lt 600 -and (Test-Path -LiteralPath '$holdFile'); `$i++) { Start-Sleep -Milliseconds 200 }
`$holder.Dispose()
"@
  $hold = Start-Process -FilePath $shell -ArgumentList @('-NoProfile', '-File', $holdScript) -NoNewWindow -PassThru
  $started.Add($hold)
  try {
    for ($i = 0; $i -lt 100 -and -not (Test-Path -LiteralPath $holdFile); $i++) { Start-Sleep -Milliseconds 100 }
    # The deadline coming while the run waits for the desktop is the deadline, not a desk in use.
    $env:BOSUN_DESKTOP_WAIT_S = '60'
    $run = Invoke-Run 'pass' @('-Scheduled', '-Deadline', (Get-Date).AddSeconds(6).ToString('s'))
    Check "the deadline reached while waiting for the desktop is stopped-deadline (exit $($run.Code))" ($run.Code -eq 1 -and $run.Last.outcome -eq 'stopped-deadline' -and "$($run.Last.reason)" -like '*while it waited for the desktop*') $run.Said
    # Runs refused the lock at once write their records without it: six at once in one state folder keep all six.
    $env:BOSUN_DESKTOP_WAIT_S = '0'
    $state = Join-Path $dir 'state-many'
    New-Item -ItemType Directory -Force -Path $state | Out-Null
    $env:BOSUN_DESKTOP_LOCK = $lockName
    $many = foreach ($i in 1..6) {
      $info = [System.Diagnostics.ProcessStartInfo]::new($shell)
      foreach ($a in @('-NoProfile', '-File', $script, '-StateFolder', $state, '-Dotnet', (Join-Path $fakes 'dotnet.cmd'), '-TestProbes', '-NoQueue')) { $info.ArgumentList.Add($a) }
      $info.UseShellExecute = $false; $info.CreateNoWindow = $true
      $p = [System.Diagnostics.Process]::Start($info); $started.Add($p); $p
    }
    foreach ($p in $many) { $p.WaitForExit(60000) | Out-Null }
    $kept = @(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    Check "six refused runs at once keep six records ($($kept.Count))" ($kept.Count -eq 6 -and @($kept | Where-Object { $_.reason -like '*lock was held by*hold.ps1*' }).Count -eq 6)
    Check 'and six logs of their own' (@($kept.log | Select-Object -Unique).Count -eq 6)
  } finally {
    Remove-Item Env:BOSUN_DESKTOP_WAIT_S, Env:BOSUN_DESKTOP_LOCK -ErrorAction Ignore
    Remove-Item -LiteralPath $holdFile -ErrorAction Ignore
    $hold.WaitForExit(10000) | Out-Null
  }

  # The history: each run kept with its own log, the last 60 and 30 kept, read back in words.
  $state = Join-Path $dir 'state-history'
  $a = Invoke-Run 'pass' -State $state
  $b = Invoke-Run 'pass' -State $state
  Check 'two runs in one minute keep two logs' ($a.Last.log -ne $b.Last.log -and (Test-Path -LiteralPath $a.Last.log) -and (Test-Path -LiteralPath $b.Last.log))
  $lines = @(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl'))
  $filler = foreach ($i in 1..70) { (@{ at = (Get-Date).Date.AddDays(-80 + $i).AddHours(3).AddMinutes(1).ToUniversalTime().ToString('o'); mode = 'scheduled'; outcome = 'skipped-locked'; reason = 'the desktop was locked'; discovered = 0; passed = 0; failed = 0; skipped = 0; log = '' } | ConvertTo-Json -Compress) }
  Set-Content -LiteralPath (Join-Path $state 'input-history.jsonl') -Value (@($filler) + $lines)
  foreach ($i in 1..31) { Set-Content -LiteralPath (Join-Path $state "input-20200101-0000$('{0:d2}' -f $i).log") -Value old; (Get-Item (Join-Path $state "input-20200101-0000$('{0:d2}' -f $i).log")).LastWriteTime = (Get-Date).AddDays(-30) }
  # The oldest run's results are held open: they stay, with the log that names them, and go after the next run.
  New-Item -ItemType Directory -Force -Path (Join-Path $state 'results-20200101-000001') | Out-Null
  $heldPath = Join-Path $state 'results-20200101-000001/build.log'
  Set-Content -LiteralPath $heldPath -Value held
  (Get-Item (Join-Path $state 'input-20200101-000001.log')).LastWriteTime = (Get-Date).AddDays(-40)
  $held = [IO.File]::Open($heldPath, 'Open', 'Read', 'Read')
  try { $c = Invoke-Run 'pass' -State $state } finally { $held.Dispose() }
  Check 'results held open stay, with their log, for the next run' ((Test-Path -LiteralPath (Join-Path $state 'input-20200101-000001.log')) -and (Test-Path -LiteralPath $heldPath))
  $d = Invoke-Run 'pass' -State $state
  Check 'and the next run removes them' (-not (Test-Path -LiteralPath (Join-Path $state 'results-20200101-000001')) -and -not (Test-Path -LiteralPath (Join-Path $state 'input-20200101-000001.log')))
  Check 'the history keeps its last 60 entries' (@(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl')).Count -eq 60)
  Check 'the logs keep their last 30' (@(Get-ChildItem -LiteralPath $state -Filter 'input-*.log').Count -eq 30 -and (Test-Path -LiteralPath $c.Last.log))
  $status = & $shell -NoProfile -File $script -Status -StateFolder $state -TaskName $taskName 2>&1 | Out-String
  # Of the 70 filler nights and the four runs since, the last 60 entries are kept: 56 nights skipped in a row.
  Check 'the status reads the last scheduled run, the skips in a row and the last on-demand run' ($status -like '*last scheduled run: skipped*the desktop was locked*' -and $status -like '*Skipped 56 nights in a row*' -and $status -like '*Last on-demand run: passed 5 of 5*' -and $status -like '*is not installed*') $status

  # The nightly task: installed as asked, read back whole, started from outside the checkout, and removed.
  $state = Join-Path $dir 'state-task'
  New-Item -ItemType Directory -Force -Path $state | Out-Null
  Set-Content -LiteralPath (Join-Path $state 'mode.txt') -Value 'pass'
  Set-Content -LiteralPath (Join-Path $state 'tests.txt') -Value $tests
  # The state folder is not among these: -Install writes it into the task from its own -StateFolder.
  $taskArgs = "-Dotnet `"$(Join-Path $fakes 'dotnet.cmd')`" -Watcher `"$(Join-Path $fakes 'watcher.cmd')`" -TestProbes -NoQueue"
  # The colon form: a value that starts with a dash would be read as a parameter of its own.
  $said = & $shell -NoProfile -File $script -Install -StateFolder $state -TaskName $taskName "-TaskArguments:$taskArgs" 2>&1 | Out-String
  try {
    $task = Get-ScheduledTask -TaskName $taskName
    $trigger = $task.Triggers[0]
    Check 'the task runs daily at 03:00' ($trigger.CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger' -and $trigger.DaysInterval -eq 1 -and ([DateTime]$trigger.StartBoundary).ToString('HH:mm') -eq '03:00') $said
    Check 'never woken, never run late, ended when the window closes, three hours on' (-not $task.Settings.WakeToRun -and -not $task.Settings.StartWhenAvailable -and $task.Settings.ExecutionTimeLimit -eq 'PT3H')
    Check 'for this user, only while logged on' ($task.Principal.UserId -like "*$env:USERNAME" -and $task.Principal.LogonType -eq 'Interactive')
    $action = $task.Actions[0]
    Check 'its action runs this script, scheduled, in the checkout' ($action.Execute -eq $shell -and $action.Arguments -like "*$script*-Scheduled*-Invocation task*" -and $action.Arguments -like "*$state*" -and $action.WorkingDirectory -eq $repo) "$($action.Execute) $($action.Arguments) in $($action.WorkingDirectory)"
    # Installed and not yet run: the status says so with the install's date; three days on, it names the night missed.
    $status = & $shell -NoProfile -File $script -Status -StateFolder $state -TaskName $taskName 2>&1 | Out-String
    Check 'a fresh install reads "no scheduled run yet" with its date' ($status -like "*No scheduled run yet, installed $(Get-Date -Format 'yyyy-MM-dd')*") $status
    $mark = Join-Path $state 'installed.json'
    $was = Get-Content -LiteralPath $mark -Raw
    Set-Content -LiteralPath $mark -Value (@{ at = (Get-Date).AddDays(-3).ToUniversalTime().ToString('o'); task = $taskName } | ConvertTo-Json -Compress)
    $night = (Get-Date).Date.AddHours(3); if ((Get-Date) -lt $night.AddHours(3)) { $night = $night.AddDays(-1) }
    $status = & $shell -NoProfile -File $script -Status -StateFolder $state -TaskName $taskName 2>&1 | Out-String
    Check 'a night with no record is named by its date' ($status -like "*No result for $($night.ToString('yyyy-MM-dd'))*") $status
    # A scheduled run started by hand after the window, on the night missed, is not that night's result.
    $history = Join-Path $state 'input-history.jsonl'
    Set-Content -LiteralPath $history -Value (@{ at = $night.Date.AddHours(9).ToUniversalTime().ToString('o'); mode = 'scheduled'; outcome = 'stopped-deadline'; reason = 'it began after its deadline, 06:00'; discovered = 0; passed = 0; failed = 0; skipped = 0; log = '' } | ConvertTo-Json -Compress)
    $status = & $shell -NoProfile -File $script -Status -StateFolder $state -TaskName $taskName 2>&1 | Out-String
    Check 'a late run on a missed night still leaves it named as missed' ($status -like "*No result for $($night.ToString('yyyy-MM-dd'))*") $status
    # Skipped nights are counted by date: a night with no result between two skipped ones ends the count.
    $skip = { param($d) (@{ at = $d.Date.AddHours(3).AddMinutes(1).ToUniversalTime().ToString('o'); mode = 'scheduled'; outcome = 'skipped-locked'; reason = 'the desktop was locked'; discovered = 0; passed = 0; failed = 0; skipped = 0; log = '' } | ConvertTo-Json -Compress) }
    Set-Content -LiteralPath $history -Value @((& $skip $night.AddDays(-3)), (& $skip $night.AddDays(-1)), (& $skip $night))
    $status = & $shell -NoProfile -File $script -Status -StateFolder $state -TaskName $taskName 2>&1 | Out-String
    Check 'a night with no result between skipped ones ends the count (2 in a row, not 3)' ($status -like '*Skipped 2 nights in a row*') $status
    Remove-Item -LiteralPath $history
    Set-Content -LiteralPath $mark -Value $was
    $before = @(if (Test-Path -LiteralPath (Join-Path $state 'input-history.jsonl')) { Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') }).Count
    Push-Location $env:TEMP
    try { Start-ScheduledTask -TaskName $taskName } finally { Pop-Location }
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and @(if (Test-Path -LiteralPath (Join-Path $state 'input-history.jsonl')) { Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') }).Count -le $before) { Start-Sleep -Milliseconds 500 }
    $entry = @(Get-Content -LiteralPath (Join-Path $state 'input-history.jsonl') -ErrorAction Ignore | ForEach-Object { $_ | ConvertFrom-Json })[-1]
    Check 'started as the task, it records a run of its own' ($entry -and $entry.invocation -eq 'task' -and $entry.mode -eq 'scheduled') ($entry | ConvertTo-Json -Compress)
  } finally {
    & $shell -NoProfile -File $script -Uninstall -StateFolder $state -TaskName $taskName *> $null
  }
  Check 'uninstalled, the task is gone' (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction Ignore))
  # A task of the same name that is not this script's is left alone, by -Install and by -Uninstall: one that runs
  # something else, and one that runs the smoke with an action of its own beside it.
  $smokeAction = New-ScheduledTaskAction -Execute $shell -Argument "-NoProfile -WindowStyle Hidden -File `"$script`" -Scheduled -Invocation task -StateFolder `"$state`""
  foreach ($case in @(
      @{ name = 'runs something else'; actions = @(New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c rem not the smoke') },
      @{ name = 'has an action beside the smoke'; actions = @($smokeAction, (New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c rem mine')) })) {
    Register-ScheduledTask -TaskName $taskName -Action $case.actions -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)) -Force | Out-Null
    try {
      $said = & $shell -NoProfile -File $script -Install -StateFolder $state -TaskName $taskName 2>&1 | Out-String
      Check "-Install leaves a task of the same name that $($case.name) (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and @((Get-ScheduledTask -TaskName $taskName).Actions).Count -eq $case.actions.Count) $said
      $said = & $shell -NoProfile -File $script -Uninstall -StateFolder $state -TaskName $taskName 2>&1 | Out-String
      Check "-Uninstall leaves it too (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and (Get-ScheduledTask -TaskName $taskName -ErrorAction Ignore)) $said
    } finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Ignore }
  }
  # Removing a task that is not there is already done; the install record goes with it.
  $said = & $shell -NoProfile -File $script -Uninstall -StateFolder $state -TaskName $taskName 2>&1 | Out-String
  Check "uninstalling again says it is removed (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and $said -like '*Removed*' -and -not (Test-Path -LiteralPath (Join-Path $state 'installed.json'))) $said

  # The help names the commands that enable, inspect and remove the schedule, and exit 4's meaning stays capture's.
  $help = Get-Help $script -Full | Out-String
  Check 'the help names -Install, -Status and -Uninstall' ($help -like '*smoke-input.ps1 -Install*' -and $help -like '*smoke-input.ps1 -Status*' -and $help -like '*smoke-input.ps1 -Uninstall*')
} finally {
  foreach ($p in $started) { if (-not $p.HasExited) { & taskkill.exe /T /F /PID $p.Id *> $null } }
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction Ignore) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
  try { Remove-Item -LiteralPath $dir -Recurse -Force } catch { "the test's folder $dir was left behind: $($_.Exception.Message)" }
}
if ($failed.Count) { "$($failed.Count) check(s) failed"; exit 1 }
'all passed'
