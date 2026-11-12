# Tests Measure-Job (scripts/lib/job-measure.ps1) and scripts/measure-build.ps1 with fake builds: what a job reports
# for exit code, time and the memory of child processes, that nothing it started is left running, and that
# measurements are stored, refused and rendered as the script says. Every process a case starts is in a job that
# ends with the case, and the fakes write a few lines at most.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\job-measure.ps1')
$measure = Join-Path $PSScriptRoot '..\scripts\measure-build.ps1'
$dir = Join-Path ([IO.Path]::GetTempPath()) ("bosun-measure-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $dir | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
function Test-Running([int]$id) { [bool](Get-Process -Id $id -ErrorAction Ignore) }
function Wait-Gone([int]$id) {
  foreach ($n in 1..50) { if (-not (Test-Running $id)) { return $true }; Start-Sleep -Milliseconds 100 }
  $false
}

try {
  # --- Measure-Job ---
  $r = Measure-Job -FilePath cmd -ArgumentList '/c', 'exit 7'
  Check 'the exit code of the command is returned' ($r.ExitCode -eq 7 -and -not $r.TimedOut) "(got $($r.ExitCode))"

  $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 2'
  Check 'a 2 second sleep reports 2 to 10 seconds' ($r.Seconds -ge 2 -and $r.Seconds -le 10) "(got $($r.Seconds))"

  # The parent is cmd, which allocates nothing: only the child's memory can reach the bound.
  $allocate = Join-Path $dir 'allocate.ps1'
  Set-Content -LiteralPath $allocate -Value '$b = [byte[]]::new(300MB); [Array]::Fill($b, [byte]1); exit 0'
  $idle = Join-Path $dir 'idle.ps1'
  Set-Content -LiteralPath $idle -Value 'exit 0'
  $r = Measure-Job -FilePath cmd -ArgumentList '/c', 'pwsh', '-NoProfile', '-File', $allocate
  Check 'memory a child process allocates is counted' ($r.ExitCode -eq 0 -and $r.PeakMB -ge 300) "(got $($r.PeakMB) MB)"
  $r = Measure-Job -FilePath cmd -ArgumentList '/c', 'pwsh', '-NoProfile', '-File', $idle
  Check 'the same child allocating nothing stays under 150 MB' ($r.ExitCode -eq 0 -and $r.PeakMB -gt 0 -and $r.PeakMB -lt 150) "(got $($r.PeakMB) MB)"

  # A child that sleeps on, started by a parent that waits for it (timeout) or does not (parent exits first).
  $child = Join-Path $dir 'child.ps1'
  Set-Content -LiteralPath $child -Value 'param($pidFile) Set-Content -LiteralPath $pidFile -Value $PID; Start-Sleep 120'
  $parent = Join-Path $dir 'parent.ps1'
  Set-Content -LiteralPath $parent -Value @'
param($child, $pidFile, [switch]$Wait)
$p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $child, $pidFile -PassThru -NoNewWindow
foreach ($n in 1..100) { if (Test-Path -LiteralPath $pidFile) { break }; Start-Sleep -Milliseconds 100 }
if ($Wait) { $p.WaitForExit() }
exit 0
'@
  $pidFile = Join-Path $dir 'child-timeout.pid'
  $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', $parent, $child, $pidFile, '-Wait' -TimeoutSeconds 8
  $childId = if (Test-Path -LiteralPath $pidFile) { [int](Get-Content -LiteralPath $pidFile) } else { 0 }
  Check 'a command over its limit is reported as timed out' ($r.TimedOut -and $r.ExitCode -eq -1 -and $childId -gt 0) "(timed out $($r.TimedOut), child $childId)"
  Check 'a timed-out command leaves no child running' ($childId -gt 0 -and (Wait-Gone $childId))

  $pidFile = Join-Path $dir 'child-orphan.pid'
  $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', $parent, $child, $pidFile -TimeoutSeconds 60
  $childId = if (Test-Path -LiteralPath $pidFile) { [int](Get-Content -LiteralPath $pidFile) } else { 0 }
  Check 'a parent that exits before its child still returns its own result' (-not $r.TimedOut -and $r.ExitCode -eq 0 -and $childId -gt 0) "(exit $($r.ExitCode), child $childId)"
  Check 'a child left behind by its parent is ended with the job' ($childId -gt 0 -and (Wait-Gone $childId))

  # Interrupted (Ctrl+C in a console is a stopped pipeline): the job ends at once, not when the command would have.
  $pidFile = Join-Path $dir 'child-interrupted.pid'
  $library = Join-Path $PSScriptRoot '../scripts/lib/job-measure.ps1'
  $shell = [powershell]::Create()
  [void]$shell.AddScript({ param($library, $parent, $child, $pidFile) . $library; Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', $parent, $child, $pidFile, '-Wait' -TimeoutSeconds 300 }).AddArgument($library).AddArgument($parent).AddArgument($child).AddArgument($pidFile)
  $running = $shell.BeginInvoke()
  foreach ($n in 1..150) { if (Test-Path -LiteralPath $pidFile) { break }; Start-Sleep -Milliseconds 100 }
  $childId = if (Test-Path -LiteralPath $pidFile) { [int](Get-Content -LiteralPath $pidFile) } else { 0 }
  $clock = [Diagnostics.Stopwatch]::StartNew()
  $shell.Stop()
  $gone = $childId -gt 0 -and (Wait-Gone $childId)
  Check 'an interrupted command and its child are ended at once, long before its limit' ($gone -and $clock.Elapsed.TotalSeconds -lt 10 -and $running.IsCompleted) "(child $childId, gone $gone, $([int]$clock.Elapsed.TotalSeconds) s)"
  $shell.Dispose()

  $message = try { Measure-Job -FilePath (Join-Path $dir 'missing.exe') | Out-Null; '' } catch { "$_" }
  Check 'a command that cannot start is an error, not a result' ($message -match 'could not start') "(got '$message')"
  $notProgram = Join-Path $dir 'not-a-program.exe'
  Set-Content -LiteralPath $notProgram -Value 'this is not an executable'
  $message = try { Measure-Job -FilePath $notProgram | Out-Null; '' } catch { "$_" }
  Check 'a file Windows cannot run names the Win32 error' ($message -match 'could not start the command' -and $message -match 'Win32 error \d+') "(got '$message')"

  # --- measure-build.ps1, with a fake build ---
  # The fake build does what its mode file says and counts its runs. `-Fresh` and a touched file do work.
  $fake = Join-Path $dir 'fake-build.ps1'
  Set-Content -LiteralPath $fake -Value @'
param([switch]$Fresh, [switch]$Sccache)
$here = $PSScriptRoot
$mode = Get-Content -LiteralPath (Join-Path $here 'mode.txt')
Add-Content -LiteralPath (Join-Path $here 'runs.txt') -Value "fresh=$Fresh sccache=$Sccache"
$touched = (Get-Item -LiteralPath (Join-Path $here 'source.cpp')).LastWriteTimeUtc
$stamp = Join-Path $here 'built.txt'
$work = $Fresh -or -not (Test-Path -LiteralPath $stamp) -or $touched -gt (Get-Item -LiteralPath $stamp).LastWriteTimeUtc
if ($mode -eq 'fail-fresh' -and $Fresh) { 'error: it broke'; exit 3 }
if ($mode -eq 'hang-fresh' -and $Fresh) { Start-Sleep 120 }
if ($mode -eq 'always-work') { $work = $true }
if ($work) { '[1/1] Building CXX object'; Set-Content -LiteralPath $stamp -Value 'built' } else { 'ninja: no work to do.' }
exit 0
'@
  Set-Content -LiteralPath (Join-Path $dir 'source.cpp') -Value '// fake source'
  $out = Join-Path $dir 'docs'
  New-Item -ItemType Directory -Path $out | Out-Null
  $json = Join-Path $out 'build-performance.json'
  $doc = Join-Path $out 'build-performance.md'
  $env:BOSUN_MEASURE_QUEUED = '1'
  function Invoke-Measure([string]$mode, [string[]]$arguments) {
    Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value $mode
    Remove-Item -LiteralPath (Join-Path $dir 'runs.txt') -ErrorAction Ignore
    $text = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $out @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
  }

  $run = Invoke-Measure 'ok' @('-Label', 'baseline', '-Decision', 'baseline', '-Samples', '2', '-Note', 'the first measurement')
  $data = if (Test-Path -LiteralPath $json) { Get-Content -LiteralPath $json -Raw | ConvertFrom-Json -DateKind String } else { $null }
  $entry = $data.labels | Where-Object label -eq 'baseline'
  Check 'a measurement is stored under its label' ($run.Exit -eq 0 -and $entry.decision -eq 'baseline' -and $entry.samples -eq 2) "(exit $($run.Exit): $($run.Text))"
  Check 'each step has every sample and a median' (@($entry.steps).Count -eq 3 -and -not (@($entry.steps) | Where-Object { @($_.seconds).Count -ne 2 -or @($_.peak_mb).Count -ne 2 -or $_.median_seconds -le 0 -or $_.median_peak_mb -le 0 }))
  # The median is worked out here from the stored samples, not read back from what the script stored: with an even
  # count it is the mean of the middle two, with an odd count (the next label, three samples) the middle one.
  function Get-ExpectedMedian([double[]]$values) {
    $sorted = [double[]]@($values | Sort-Object)
    if ($sorted.Count % 2) { $sorted[($sorted.Count - 1) / 2] } else { [Math]::Round(($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2, 2) }
  }
  function Test-Medians($steps) {
    -not (@($steps) | Where-Object { [double]$_.median_seconds -ne (Get-ExpectedMedian $_.seconds) -or [double]$_.median_peak_mb -ne (Get-ExpectedMedian $_.peak_mb) })
  }
  Check 'the median of an even number of samples is the mean of the middle two' (Test-Medians $entry.steps)
  Check 'the steps are the three build workloads' ((@($entry.steps).step -join '|') -eq 'nothing to do|one file changed|fresh build')
  $runs = @(Get-Content -LiteralPath (Join-Path $dir 'runs.txt'))
  Check 'one untimed preparation build runs before the samples' ($runs.Count -eq 7 -and @($runs | Where-Object { $_ -like 'fresh=True*' }).Count -eq 2) "(runs: $($runs.Count))"
  Check 'the machine and date are recorded' ($entry.machine.name -eq $env:COMPUTERNAME -and $entry.machine.memory_gb -gt 0 -and $entry.date -match '^\d{4}-\d\d-\d\dT')

  $run = Invoke-Measure 'ok' @('-Label', 'candidate', '-Decision', 'dropped', '-Samples', '3', '-BuildArgs', 'Sccache', '-Note', 'slower than the baseline')
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  Check 'a second label is added beside the first' ($run.Exit -eq 0 -and (@($data.labels).label -join '|') -eq 'baseline|candidate') "(exit $($run.Exit): $($run.Text))"
  $candidateSteps = ($data.labels | Where-Object label -eq 'candidate').steps
  Check 'the median of an odd number of samples is the middle one' (@($candidateSteps[0].seconds).Count -eq 3 -and (Test-Medians $candidateSteps))
  Check 'build arguments reach the build script' (-not (@(Get-Content -LiteralPath (Join-Path $dir 'runs.txt')) | Where-Object { $_ -notlike '*sccache=True' }))
  Check 'the build arguments are recorded' ((@(($data.labels | Where-Object label -eq 'candidate').configuration.build_args) -join '|') -eq 'Sccache')

  # A decision made after comparing the numbers is recorded on the stored label, without measuring again.
  $samplesBefore = (($data.labels | Where-Object label -eq 'candidate').steps | ConvertTo-Json -Depth 5 -Compress)
  $run = Invoke-Measure 'ok' @('-Label', 'candidate', '-Decision', 'kept', '-Note', 'kept after all', '-DecideOnly')
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  $candidate = $data.labels | Where-Object label -eq 'candidate'
  Check '-DecideOnly records the decision and note and keeps the numbers' ($run.Exit -eq 0 -and $candidate.decision -eq 'kept' -and $candidate.note -eq 'kept after all' -and
    ($candidate.steps | ConvertTo-Json -Depth 5 -Compress) -eq $samplesBefore -and -not (Test-Path -LiteralPath (Join-Path $dir 'runs.txt'))) "(exit $($run.Exit): $($run.Text))"
  $run = Invoke-Measure 'ok' @('-Label', 'never-measured', '-Decision', 'kept', '-DecideOnly')
  Check '-DecideOnly on a label never measured is refused' ($run.Exit -eq 1 -and $run.Text -match "no label 'never-measured'")

  # Saves are serialised by a lock file. While another run holds it, a save waits; past its limit it changes nothing.
  $unlocked = [IO.File]::ReadAllBytes($json)
  $held = [IO.FileStream]::new("$json.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
  try {
    $run = Invoke-Measure 'ok' @('-Label', 'candidate', '-Decision', 'dropped', '-DecideOnly', '-LockSeconds', '2')
    Check 'a save that cannot get the lock exits 1 and changes nothing' ($run.Exit -eq 1 -and $run.Text -match 'nothing was changed' -and
      [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($unlocked)) "(exit $($run.Exit): $($run.Text))"
    # A render takes the same lock: while a save holds it, the render waits its turn and cannot put an older
    # document over the one that save writes.
    $docBefore = Get-Content -LiteralPath $doc -Raw
    $run = Invoke-Measure 'ok' @('-RenderOnly', '-LockSeconds', '2')
    Check 'a render that cannot get the lock exits 1 and leaves the document' ($run.Exit -eq 1 -and $run.Text -match 'nothing was changed' -and
      (Get-Content -LiteralPath $doc -Raw) -ceq $docBefore) "(exit $($run.Exit): $($run.Text))"
    $waiting = Start-Process pwsh -PassThru -NoNewWindow -ArgumentList '-NoProfile', '-File', $measure, '-OutDir', $out, '-Label', 'candidate', '-Decision', 'dropped', '-Note', 'waited-for-the-lock', '-DecideOnly', '-LockSeconds', '60' -RedirectStandardOutput (Join-Path $dir 'waiting.txt')
    Start-Sleep -Seconds 3
    $stillWaiting = -not $waiting.HasExited
  } finally { $held.Dispose() }
  $finished = $waiting.WaitForExit(60000)
  if (-not $finished) { $waiting.Kill($true) }
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  Check 'a save waits for the lock and goes through once it is free' ($stillWaiting -and $finished -and $waiting.ExitCode -eq 0 -and
    ($data.labels | Where-Object label -eq 'candidate').note -eq 'waited-for-the-lock') "(waiting $stillWaiting, finished $finished, exit $($waiting.ExitCode))"

  # Two runs saving at the same moment, on different labels, both land: neither overwrites the other.
  $lost = 0
  foreach ($round in 1..4) {
    $pair = foreach ($name in 'baseline', 'candidate') {
      Start-Process pwsh -PassThru -NoNewWindow -ArgumentList '-NoProfile', '-File', $measure, '-OutDir', $out, '-Label', $name, '-Decision', 'kept', '-Note', "round-$round", '-DecideOnly' -RedirectStandardOutput (Join-Path $dir "pair-$name.txt")
    }
    foreach ($process in $pair) { if (-not $process.WaitForExit(60000)) { $process.Kill($true) } }
    $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    if (@($data.labels | Where-Object { $_.label -in 'baseline', 'candidate' -and $_.note -eq "round-$round" }).Count -ne 2) { $lost++ }
  }
  Check 'two saves at once on different labels both land, four rounds running' ($lost -eq 0) "($lost round(s) lost a save)"
  Check 'the lock file is gone once nothing holds it' (-not (Test-Path -LiteralPath "$json.lock"))
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json

  # The document carries each label's decision, note and exact numbers, with their units.
  $text = Get-Content -LiteralPath $doc -Raw
  $missing = @()
  foreach ($label in $data.labels) {
    if ($text -notmatch [regex]::Escape("## $($label.label) ($($label.decision))")) { $missing += "heading $($label.label)" }
    if ($text -notmatch [regex]::Escape($label.note)) { $missing += "note $($label.label)" }
    foreach ($step in $label.steps) {
      $row = "| $($step.step) | $(([double]$step.median_seconds).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)) | $(([double]$step.median_peak_mb).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)) |"
      if ($text -notmatch [regex]::Escape($row)) { $missing += "row $($label.label)/$($step.step)" }
    }
  }
  Check 'the document has every label, note and row with its numbers' (-not $missing) "(missing: $($missing -join ', '))"
  Check 'the document names its units' ($text -match 'Wall time \(s\)' -and $text -match 'Peak memory \(MB\)')
  Check 'the document has no long dash' ($text -notmatch "[–—]")

  Remove-Item -LiteralPath $doc
  $run = Invoke-Measure 'ok' @('-RenderOnly')
  Check '-RenderOnly rebuilds a deleted document without measuring' ($run.Exit -eq 0 -and (Test-Path -LiteralPath $doc) -and (Get-Content -LiteralPath $doc -Raw) -ceq $text -and -not (Test-Path -LiteralPath (Join-Path $dir 'runs.txt')))

  # A document that cannot be written (a folder is in its place) is an error that names the recovery, and the
  # measurement itself is still stored.
  Remove-Item -LiteralPath $doc
  New-Item -ItemType Directory -Path $doc | Out-Null
  $run = Invoke-Measure 'ok' @('-Label', 'unrendered', '-Decision', 'kept', '-Samples', '1')
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  Check 'a document that cannot be written exits 1 naming -RenderOnly, with the data stored' ($run.Exit -eq 1 -and $run.Text -match '-RenderOnly' -and
    (@($data.labels).label -contains 'unrendered')) "(exit $($run.Exit): $($run.Text))"
  Remove-Item -LiteralPath $doc -Recurse -Force
  $run = Invoke-Measure 'ok' @('-RenderOnly')
  Check '-RenderOnly recovers the document afterwards' ($run.Exit -eq 0 -and (Get-Content -LiteralPath $doc -Raw) -match '## unrendered \(kept\)')

  # Refusals leave the stored data exactly as it was.
  $before = [IO.File]::ReadAllBytes($json)
  function Test-Unchanged { [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($before) }
  $run = Invoke-Measure 'fail-fresh' @('-Label', 'broken', '-Decision', 'kept', '-Samples', '1')
  Check 'a failing step exits 1 naming the step and its exit code' ($run.Exit -eq 1 -and $run.Text -match "step 'fresh build' failed with exit code 3") "(exit $($run.Exit): $($run.Text))"
  Check 'a failing step stores nothing' (Test-Unchanged)
  $run = Invoke-Measure 'hang-fresh' @('-Label', 'hung', '-Decision', 'kept', '-Samples', '1', '-TimeoutSeconds', '5')
  Check 'a step over the limit exits 1 naming the limit' ($run.Exit -eq 1 -and $run.Text -match "step 'fresh build' was stopped at its 5 second limit") "(exit $($run.Exit): $($run.Text))"
  Check 'a timed-out step stores nothing' (Test-Unchanged)
  $run = Invoke-Measure 'always-work' @('-Label', 'cold', '-Decision', 'kept', '-Samples', '1')
  Check 'a build that compiled is refused as a build with nothing to do' ($run.Exit -eq 1 -and $run.Text -match 'not a build with nothing to do' -and (Test-Unchanged)) "(exit $($run.Exit): $($run.Text))"
  $run = Invoke-Measure 'ok' @('-Label', 'Bad Label', '-Decision', 'kept')
  Check 'a label that is not a slug is refused' ($run.Exit -eq 2 -and (Test-Unchanged))

  foreach ($case in @(@{ name = 'malformed'; text = '{ not json' }, @{ name = 'without its schema version'; text = '{ "labels": [] }' })) {
    Set-Content -LiteralPath $json -Value $case.text
    $bad = [IO.File]::ReadAllBytes($json)
    $run = Invoke-Measure 'ok' @('-Label', 'after', '-Decision', 'kept', '-Samples', '1')
    $same = [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($bad)
    Check "a data file $($case.name) is refused before measuring and left alone" ($run.Exit -eq 1 -and $same -and -not (Test-Path -LiteralPath (Join-Path $dir 'runs.txt'))) "(exit $($run.Exit): $($run.Text))"
  }
  [IO.File]::WriteAllBytes($json, $before)

  # --- the queue: started through bosun once, and not again inside it ---
  $bin = Join-Path $dir 'bin'
  New-Item -ItemType Directory -Path $bin | Out-Null
  Set-Content -LiteralPath (Join-Path $bin 'fake-bosun.ps1') -Value @'
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'bosun-calls.txt') -Value ($args[0..1] -join ' ')
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'bosun-arguments.txt') -Value $args
$rest = @($args | Select-Object -Skip 3)
& $args[2] @rest
exit $LASTEXITCODE
'@
  Set-Content -LiteralPath (Join-Path $bin 'bosun.cmd') -Value "@pwsh -NoProfile -File `"%~dp0fake-bosun.ps1`" %*"
  $savedPath = $env:PATH
  try {
    $env:PATH = "$bin;$env:PATH"
    Remove-Item Env:BOSUN_MEASURE_QUEUED
    $run = Invoke-Measure 'ok' @('-Label', 'queued', '-Decision', 'kept', '-Samples', '1', '-Note', 'a note with spaces', '-LockSeconds', '7', '-TimeoutSeconds', '99', '-BuildArgs', 'Sccache')
  } finally { $env:PATH = $savedPath; $env:BOSUN_MEASURE_QUEUED = '1' }
  $calls = @(Get-Content -LiteralPath (Join-Path $bin 'bosun-calls.txt') -ErrorAction Ignore)
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  $queued = $data.labels | Where-Object label -eq 'queued'
  Check 'the measurement runs through bosun run, once' ($run.Exit -eq 0 -and $calls.Count -eq 1 -and $calls[0] -eq 'run --') "(exit $($run.Exit), calls: $($calls -join ';'): $($run.Text))"
  Check 'its arguments survive the queue' ($queued.decision -eq 'kept' -and $queued.note -eq 'a note with spaces' -and $queued.samples -eq 1)
  # Every parameter given is passed on by name with its value, the ones a default would otherwise replace included.
  $passed = @(Get-Content -LiteralPath (Join-Path $bin 'bosun-arguments.txt') -ErrorAction Ignore)
  $pairs = @{}
  foreach ($n in 0..($passed.Count - 1)) { if ($passed[$n] -like '-*' -and $n -gt 2) { $pairs[$passed[$n]] = $passed[$n + 1] } }
  Check 'every parameter given reaches the queued run' ($pairs['-LockSeconds'] -eq '7' -and $pairs['-TimeoutSeconds'] -eq '99' -and $pairs['-BuildArgs'] -eq 'Sccache' -and
    $pairs.ContainsKey('-BuildScript') -and $pairs.ContainsKey('-OutDir') -and @($queued.configuration.build_args) -contains 'Sccache') "($($passed -join ' '))"

  # --- measure-build.ps1 -Dotnet, with a fake dotnet ---
  # The fake writes its four outputs only when it does work (all when one is missing, the first when the touched
  # file is newer), removes them on clean, and by its mode leaves worker processes in node mode that hold about
  # 100 MB, answer a later build (it asks, they spend CPU on it) and end by themselves after two minutes.
  $dn = Join-Path $dir 'dn'
  New-Item -ItemType Directory -Path $dn | Out-Null
  $outs = @(1..4 | ForEach-Object { Join-Path $dn "out$_.dll" })
  Set-Content -LiteralPath (Join-Path $dn 'Foundation.cs') -Value '// fake source'
  Set-Content -LiteralPath (Join-Path $dn 'worker.ps1') -Value @'
param($flag, $here, $n)
$hold = [byte[]]::new(100MB); [Array]::Fill($hold, [byte]1)
Set-Content -LiteralPath (Join-Path $here "worker$n.pid") -Value $PID
$until = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $until) {
  $ask = Join-Path $here "ask$n.txt"
  if (Test-Path -LiteralPath $ask) { $end = (Get-Date).AddMilliseconds(400); while ((Get-Date) -lt $end) { $null = [Math]::Sqrt(7) }; Remove-Item -LiteralPath $ask }
  Start-Sleep -Milliseconds 100
}
'@
  Set-Content -LiteralPath (Join-Path $dn 'fake-dotnet.ps1') -Value @'
$here = $PSScriptRoot
$mode = Get-Content -LiteralPath (Join-Path $here 'mode.txt')
$outs = @(1..4 | ForEach-Object { Join-Path $here "out$_.dll" })
switch ($args[0]) {
  '--version' { '11.0.100'; exit 0 }
  'build-server' { if ($mode -eq 'shutdown-fails') { 'error: could not reach the servers'; exit 5 }; exit 0 }
  'msbuild' { if ($args -contains '-p:UseSharedCompilation=false') { 'false' } else { '' }; exit 0 }
  'clean' { foreach ($o in $outs) { if (-not ($mode -eq 'clean-leaves' -and $o -eq $outs[2])) { Remove-Item -LiteralPath $o -ErrorAction Ignore } }; Set-Content -LiteralPath (Join-Path $here 'cleaned.txt') -Value 'yes'; exit 0 }
}
function Alive($n) { $f = Join-Path $here "worker$n.pid"; (Test-Path -LiteralPath $f) -and [bool](Get-Process -Id ([int](Get-Content -LiteralPath $f)) -ErrorAction Ignore) }
# Another build on the machine: a worker of this SDK started through WMI, so it is in none of the measuring script's jobs.
if ($mode -eq 'outside' -and -not (Test-Path -LiteralPath (Join-Path $here 'worker7.pid'))) {
  $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
  $line = "`"$((Get-Process -Id $PID).Path)`" -NoProfile -File `"$(Join-Path $here 'worker.ps1')`" /nodemode:1 `"$here`" 7"
  $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $line; ProcessStartupInformation = $startup }
  foreach ($i in 1..150) { if (Alive 7) { break }; Start-Sleep -Milliseconds 100 }
}
$workers = switch ($mode) { 'worker' { 1 } 'warm-fails' { 1 } 'two-workers' { 2 } default { 0 } }
if ($workers) {
  if (Alive 1) {
    if ($mode -eq 'warm-fails') { 'error: the warm build broke'; exit 4 }
    $ask = Join-Path $here 'ask1.txt'; Set-Content -LiteralPath $ask -Value 'build'
    foreach ($i in 1..100) { if (-not (Test-Path -LiteralPath $ask)) { break }; Start-Sleep -Milliseconds 100 }
  } else {
    foreach ($n in 1..$workers) {
      Remove-Item -LiteralPath (Join-Path $here "worker$n.pid") -ErrorAction Ignore
      Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path $here 'worker.ps1'), '/nodemode:1', $here, $n -WindowStyle Hidden
    }
    foreach ($n in 1..$workers) { foreach ($i in 1..150) { if (Alive $n) { break }; Start-Sleep -Milliseconds 100 } }
    Start-Sleep -Milliseconds 600  # long enough for the measuring script to sample them while the build runs
  }
}
$touched = (Get-Item -LiteralPath (Join-Path $here 'Foundation.cs')).LastWriteTimeUtc
$missing = @($outs | Where-Object { -not (Test-Path -LiteralPath $_) })
$afterClean = Test-Path -LiteralPath (Join-Path $here 'cleaned.txt')
if ($missing) { foreach ($o in $outs) { if (-not ($mode -eq 'skip-one' -and $afterClean -and $o -eq $outs[3])) { Set-Content -LiteralPath $o -Value 'built' } } }
elseif ($touched -gt (Get-Item -LiteralPath $outs[0]).LastWriteTimeUtc) { Set-Content -LiteralPath $outs[$(if ($mode -eq 'miss-first') { 1 } else { 0 })] -Value 'built' }
elseif ($mode -eq 'noop-work') { Set-Content -LiteralPath $outs[2] -Value 'built' }
'Build succeeded.'
exit 0
'@
  $fakeDotnet = Join-Path $dn 'dotnet.cmd'
  Set-Content -LiteralPath $fakeDotnet -Value "@pwsh -NoProfile -File `"%~dp0fake-dotnet.ps1`" %*"
  $rsp = Join-Path $dn 'Directory.Build.rsp'
  Set-Content -LiteralPath $rsp -Value @('# the repository''s settings', '-nodeReuse:false', '-maxCpuCount:4')
  function Get-WorkerIds { @(Get-ChildItem -LiteralPath $dn -Filter 'worker*.pid' -ErrorAction Ignore | ForEach-Object { [int](Get-Content -LiteralPath $_.FullName) }) }
  $workerIds = [Collections.Generic.List[int]]::new()
  function Invoke-DotnetMeasure([string]$mode, [string[]]$arguments) {
    Set-Content -LiteralPath (Join-Path $dn 'mode.txt') -Value $mode
    foreach ($o in $outs + @(Join-Path $dn 'cleaned.txt')) { Remove-Item -LiteralPath $o -ErrorAction Ignore }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $text = & pwsh -NoProfile -File $measure -Dotnet -DotnetCommand $fakeDotnet -Solution (Join-Path $dn 'Fake.slnx') -DotnetProject (Join-Path $dn 'Fake.csproj') `
      -DotnetOutputs ($outs -join ',') -TouchFile (Join-Path $dn 'Foundation.cs') -ResponseFile $rsp -OutDir $out -LingerSeconds 2 -Samples 1 @arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    foreach ($id in Get-WorkerIds) { $workerIds.Add($id) }
    [pscustomobject]@{ Exit = $code; Text = $text; Seconds = $clock.Elapsed.TotalSeconds }
  }
  function Get-Label([string]$name) { (Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).labels | Where-Object label -eq $name }

  $run = Invoke-DotnetMeasure 'ok' @('-Label', 'dotnet-plain', '-Decision', 'baseline')
  $plain = Get-Label 'dotnet-plain'
  $stepNames = @($plain.steps | ForEach-Object step) -join ','
  Check '-Dotnet stores a .NET label with the three steps and the warm build' ($run.Exit -eq 0 -and $plain.kind -eq 'dotnet' -and $stepNames -eq 'nothing to do,one file changed,fresh build,one file changed, after a build') "(exit $($run.Exit), steps $stepNames): $($run.Text)"
  Check 'it stores the SDK and the settings in force, defaults included' ($plain.tools.dotnet -eq '11.0.100' -and $plain.configuration.node_reuse -eq $false -and $plain.configuration.max_cpu_count -eq [Environment]::ProcessorCount -and $plain.configuration.use_shared_compilation -eq $true -and (@($plain.configuration.response_file) -join ' ') -eq '-nodeReuse:false -maxCpuCount:4')
  Check 'a build that leaves nothing running stores an observed zero' ($plain.observed.left_running.observed -eq $true -and $plain.observed.left_running.processes -eq 0 -and $null -eq $plain.observed.warm_used_nodes -and $null -eq $plain.observed.warm_used_compiler -and $plain.observed.left_running.after_seconds -ge 2)
  Check 'the console says the same of the warm build as the document' ($run.Text -match 'nodes left: none was left, the compiler server left: none was left') "($($run.Text))"
  $rendered = Get-Content -LiteralPath $doc -Raw
  Check 'the document renders the .NET configuration and what was left running' ($rendered -match '## dotnet-plain \(baseline\)' -and $rendered -match "in force: node reuse off, at most $([Environment]::ProcessorCount) nodes, shared compilation on" -and $rendered -match 'Left running [\d.]+ s after it returned: 0 process\(es\), 0\.0 MB private memory\. In the one-file build made then, the nodes left: none was left; the compiler server left: none was left\.')

  $run = Invoke-DotnetMeasure 'worker' @('-Label', 'dotnet-reuse', '-Decision', 'dropped', '-BuildArgs', 'nodeReuse:true', '-Note', 'reuse on')
  $reuse = Get-Label 'dotnet-reuse'
  $left = $reuse.observed.left_running
  Check 'a worker left running is stored with its name, count and memory' ($run.Exit -eq 0 -and $left.nodes -eq 1 -and $left.processes -ge 1 -and @($left.names) -contains 'MSBuild node' -and $left.mb -ge 100 -and $reuse.observed.max_worker_nodes -eq 1) "(exit $($run.Exit), $($left | ConvertTo-Json -Compress)): $($run.Text)"
  Check 'it is observed no sooner than -LingerSeconds after the build returned' ($left.after_seconds -ge 2)
  $warmStep = $reuse.steps | Where-Object step -eq 'one file changed, after a build'
  Check 'the warm build is timed and found to have used the worker' ($reuse.observed.warm_used_nodes -eq $true -and $null -eq $reuse.observed.warm_used_compiler -and $warmStep.median_seconds -gt 0 -and @($warmStep.seconds).Count -eq 1)
  Check 'the arguments asked for are stored with what they put in force' ((@($reuse.configuration.build_args) -join ',') -eq 'nodeReuse:true' -and $reuse.configuration.node_reuse -eq $true)

  # A step whose evidence disagrees with its name, a clean that leaves an output, and a contradicted setting.
  $before = [IO.File]::ReadAllBytes($json)
  function Test-DataUnchanged { [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($before) }
  $run = Invoke-DotnetMeasure 'noop-work' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'unexpected work in a build with nothing to do is refused' ($run.Exit -eq 1 -and $run.Text -match "step 'nothing to do' rewrote out3\.dll" -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'miss-first' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a one-file build that does not rebuild the touched project is refused' ($run.Exit -eq 1 -and $run.Text -match "step 'one file changed' did not rewrite out1\.dll" -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'clean-leaves' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a clean that leaves an output is refused' ($run.Exit -eq 1 -and $run.Text -match 'the clean before a fresh build left out3\.dll' -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'skip-one' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a fresh build that skips an output is refused' ($run.Exit -eq 1 -and $run.Text -match "step 'fresh build' did not write out4\.dll" -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'two-workers' @('-Label', 'dotnet-bad', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true,maxCpuCount:1')
  Check 'a label whose build contradicts its arguments is refused' ($run.Exit -eq 1 -and $run.Text -match 'contradicts the settings in force \(at most 1 node\(s\) were asked for and 2 worker node\(s\) ran at once' -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'worker' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'nodes left by a build whose settings turn reuse off are refused' ($run.Exit -eq 1 -and $run.Text -match 'node reuse is off and 1 worker node\(s\) stayed' -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'outside' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a server of this SDK that starts outside the measured builds is refused' ($run.Exit -eq 1 -and $run.Text -match "step 'preparation build': 1 compiler server or MSBuild node process\(es\) of this SDK ran outside the measured builds" -and (Test-DataUnchanged)) "($($run.Text))"
  # It is still running, as another build's server would be: the next measurement finds it and its shutdown leaves it.
  $run = Invoke-DotnetMeasure 'ok' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a server the shutdown leaves running is refused before measuring' ($run.Exit -eq 1 -and $run.Text -match 'stopping them with' -and $run.Text -match 'before measuring: 1 compiler server' -and (Test-DataUnchanged)) "($($run.Text))"
  $run = Invoke-DotnetMeasure 'shutdown-fails' @('-Label', 'dotnet-bad', '-Decision', 'kept')
  Check 'a shutdown that fails is refused' ($run.Exit -eq 1 -and $run.Text -match "'dotnet build-server shutdown' failed with exit code 5 \(error: could not reach the servers\)" -and (Test-DataUnchanged)) "($($run.Text))"
  foreach ($id in Get-WorkerIds) { Stop-Process -Id $id -Force -ErrorAction Ignore }  # the outside worker is this test's own
  Remove-Item -LiteralPath (Join-Path $dn 'worker7.pid') -ErrorAction Ignore
  $run = Invoke-DotnetMeasure 'warm-fails' @('-Label', 'dotnet-bad', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true')
  Check 'a failed warm build stores nothing' ($run.Exit -eq 1 -and $run.Text -match "step 'one file changed, after a build' failed with exit code 4" -and (Test-DataUnchanged)) "($($run.Text))"

  # The stored settings and observations survive a render and a decision.
  $run = Invoke-Measure 'ok' @('-RenderOnly')
  $run = Invoke-Measure 'ok' @('-Label', 'dotnet-reuse', '-Decision', 'kept', '-Note', 'kept after all', '-DecideOnly')
  $after = Get-Label 'dotnet-reuse'
  Check 'the stored .NET settings survive -RenderOnly and -DecideOnly' ($after.decision -eq 'kept' -and ($after.configuration | ConvertTo-Json -Compress) -eq ($reuse.configuration | ConvertTo-Json -Compress) -and ($after.observed | ConvertTo-Json -Compress -Depth 5) -eq ($reuse.observed | ConvertTo-Json -Compress -Depth 5))

  $alive = @($workerIds | Select-Object -Unique | Where-Object { Get-Process -Id $_ -ErrorAction Ignore })
  Check 'no worker the fake dotnet started is left running' ($workerIds.Count -gt 0 -and -not $alive) "($($workerIds.Count) started, alive: $($alive -join ', '))"

} finally {
  # Every worker the fakes started ends with the test, whatever happened to it.
  if ($dn -and (Test-Path -LiteralPath $dn)) { foreach ($id in @($workerIds) + @(Get-WorkerIds)) { Stop-Process -Id $id -Force -ErrorAction Ignore } }
  Remove-Item Env:BOSUN_MEASURE_QUEUED -ErrorAction Ignore
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
