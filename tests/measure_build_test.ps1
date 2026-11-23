# Tests Measure-Job (scripts/lib/job-measure.ps1) and scripts/measure-build.ps1 with fake builds: what a job reports
# for exit code, time and the memory of child processes, that nothing it started is left running, and that
# measurements are stored, refused and rendered as the script says. Every process a case starts is in a job that
# ends with the case, and the fakes write a few lines at most.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\job-measure.ps1')
$measure = Join-Path $PSScriptRoot '..\scripts\measure-build.ps1'
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$dir = New-BosunScratchFolder -Name 'measure-test'
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
# A process is its id and its start time ("<id> <start>"): an id alone is given to another process once it has exited.
function Test-Running([string]$who) {
  $id, $start = $who -split ' '
  $p = Get-Process -Id ([int]$id) -ErrorAction Ignore
  [bool]($p -and $start -and $p.StartTime.ToFileTimeUtc() -eq [long]$start)
}
function Read-Child([string]$pidFile) { if (Test-Path -LiteralPath $pidFile) { (Get-Content -LiteralPath $pidFile -Raw).Trim() } else { '' } }
function Wait-Gone([string]$who) {
  foreach ($n in 1..50) { if (-not (Test-Running $who)) { return $true }; Start-Sleep -Milliseconds 100 }
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
  Set-Content -LiteralPath $child -Value 'param($pidFile) Set-Content -LiteralPath $pidFile -Value "$PID $((Get-Process -Id $PID).StartTime.ToFileTimeUtc())"; Start-Sleep 120'
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
  $childId = Read-Child $pidFile
  Check 'a command over its limit is reported as timed out' ($r.TimedOut -and $r.ExitCode -eq -1 -and [bool]$childId) "(timed out $($r.TimedOut), child $childId)"
  Check 'a timed-out command leaves no child running' ([bool]$childId -and (Wait-Gone $childId))

  $pidFile = Join-Path $dir 'child-orphan.pid'
  $r = Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', $parent, $child, $pidFile -TimeoutSeconds 60
  $childId = Read-Child $pidFile
  Check 'a parent that exits before its child still returns its own result' (-not $r.TimedOut -and $r.ExitCode -eq 0 -and [bool]$childId) "(exit $($r.ExitCode), child $childId)"
  Check 'a child left behind by its parent is ended with the job' ([bool]$childId -and (Wait-Gone $childId))

  # Interrupted (Ctrl+C in a console is a stopped pipeline): the job ends at once, not when the command would have.
  $pidFile = Join-Path $dir 'child-interrupted.pid'
  $library = Join-Path $PSScriptRoot '../scripts/lib/job-measure.ps1'
  $shell = [powershell]::Create()
  [void]$shell.AddScript({ param($library, $parent, $child, $pidFile) . $library; Measure-Job -FilePath pwsh -ArgumentList '-NoProfile', '-File', $parent, $child, $pidFile, '-Wait' -TimeoutSeconds 300 }).AddArgument($library).AddArgument($parent).AddArgument($child).AddArgument($pidFile)
  $running = $shell.BeginInvoke()
  foreach ($n in 1..150) { if (Test-Path -LiteralPath $pidFile) { break }; Start-Sleep -Milliseconds 100 }
  $childId = Read-Child $pidFile
  $clock = [Diagnostics.Stopwatch]::StartNew()
  $shell.Stop()
  $gone = [bool]$childId -and (Wait-Gone $childId)
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
if ($env:BOSUN_GATE_MARK_DIR) { Add-Content -LiteralPath (Join-Path $here 'marks.txt') -Value @(Get-ChildItem -LiteralPath $env:BOSUN_GATE_MARK_DIR -Filter '*.json' -ErrorAction Ignore).Count }
if ($env:BOSUN_GATE_MARK_DIR) { Add-Content -LiteralPath (Join-Path $here 'mark-outputs.txt') -Value ((@(Get-ChildItem -LiteralPath $env:BOSUN_GATE_MARK_DIR -Filter '*.json' -ErrorAction Ignore) | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).output }) -join '|') }
if ($mode -eq 'fail-fresh' -and $Fresh) { 'error: it broke'; exit 3 }
if ($mode -eq 'hang-fresh' -and $Fresh) { Start-Sleep 120 }
if ($mode -eq 'slow-fresh' -and $Fresh) { Start-Sleep 12 }
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

  # The measurement's mark (D00 T03 §21): there while each build runs, gone after a pass and after a failure, the exit
  # code kept. Its labels go to a folder of their own, so the cases below start from no data.
  $marks = Join-Path $dir 'marks'
  $env:BOSUN_GATE_MARK_DIR = $marks
  $markOut = Join-Path $dir 'mark-docs'
  New-Item -ItemType Directory -Path $markOut | Out-Null
  try {
    foreach ($case in @(@{ Mode = 'ok'; Pass = $true }, @{ Mode = 'fail-fresh'; Pass = $false })) {
      Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value $case.Mode
      Remove-Item -LiteralPath (Join-Path $dir 'marks.txt'), (Join-Path $dir 'built.txt') -ErrorAction Ignore
      $text = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $markOut -Label 'mark-check' -Decision 'kept' -Samples 1 2>&1 | Out-String
      $code = $LASTEXITCODE
      $seen = @(Get-Content -LiteralPath (Join-Path $dir 'marks.txt') -ErrorAction Ignore)
# D00 T03 §32: a measurement that measures here names its log, and the .linger and .warm logs beside it.
$outputs = @(Get-Content -LiteralPath (Join-Path $dir 'mark-outputs.txt') -ErrorAction Ignore)
Check "the measurement's mark names its log" ($outputs.Count -ge 1 -and -not ($outputs | Where-Object { $_ -notmatch 'bosun-measure-\d+\.log, with its \.linger and \.warm logs beside it$' })) "($($outputs | Select-Object -First 1))"
      $left = @(Get-ChildItem -LiteralPath $marks -Filter '*.json' -ErrorAction Ignore)
      $exitKept = if ($case.Pass) { $code -eq 0 } else { $code -eq 1 -and $text -match "step 'fresh build'" }
      Check "the measurement's mark is there while each build runs and gone after a $(if ($case.Pass) { 'pass' } else { 'failure' }), its exit kept" ($exitKept -and $seen.Count -ge 2 -and -not ($seen | Where-Object { $_ -ne '1' }) -and -not $left) "(exit $code, seen: $($seen -join ','), left: $($left.Count))"
    }
    # A mark that can't be written: nothing is measured, and it says why.
    $blocked = Join-Path $dir 'not-a-folder'
    Set-Content -LiteralPath $blocked -Value 'a file where the marks folder would go'
    $env:BOSUN_GATE_MARK_DIR = $blocked
    Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value 'ok'
    Remove-Item -LiteralPath (Join-Path $dir 'runs.txt') -ErrorAction Ignore
    $text = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $markOut -Label 'mark-blocked' -Decision 'kept' -Samples 1 2>&1 | Out-String
    $code = $LASTEXITCODE
    Check "a measurement whose mark can't be written measures nothing, saying why" ($code -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $dir 'runs.txt')) -and $text -match "the gate's mark couldn't be written" -and $text -match 'nothing was measured') "(exit $code): $text"
  } finally { Remove-Item Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore }
  Remove-Item -LiteralPath (Join-Path $dir 'built.txt') -ErrorAction Ignore

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
    # The marks seen while the queued run builds: the outer one says it queued, the child's names its log (§32).
    $queuedMarks = Join-Path $dir 'queued-marks'
    New-Item -ItemType Directory -Force $queuedMarks | Out-Null
    $env:BOSUN_GATE_MARK_DIR = $queuedMarks
    Remove-Item -LiteralPath (Join-Path $dir 'mark-outputs.txt') -ErrorAction Ignore
    $run = Invoke-Measure 'ok' @('-Label', 'queued', '-Decision', 'kept', '-Samples', '1', '-Note', 'a note with spaces', '-LockSeconds', '7', '-TimeoutSeconds', '99', '-BuildArgs', 'Sccache')
  } finally { $env:PATH = $savedPath; $env:BOSUN_MEASURE_QUEUED = '1'; Remove-Item Env:BOSUN_GATE_MARK_DIR -ErrorAction Ignore }
  $queuedOutputs = @(Get-Content -LiteralPath (Join-Path $dir 'mark-outputs.txt') -ErrorAction Ignore)
  Check "a queued measurement's outer mark says it queued, and its child's names its log" ($queuedOutputs.Count -ge 1 -and -not ($queuedOutputs | Where-Object { $_ -notmatch 'queued through bosun run' -or $_ -notmatch 'bosun-measure-\d+\.log, with its \.linger and \.warm logs beside it' })) "($($queuedOutputs | Select-Object -First 1))"
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
Set-Content -LiteralPath (Join-Path $here "worker$n.pid") -Value "$PID $((Get-Process -Id $PID).StartTime.ToFileTimeUtc())"
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
function Alive($n) {
  $f = Join-Path $here "worker$n.pid"
  if (-not (Test-Path -LiteralPath $f)) { return $false }
  $id, $start = (Get-Content -LiteralPath $f -Raw).Trim() -split ' '
  $p = Get-Process -Id ([int]$id) -ErrorAction Ignore
  return [bool]($p -and $start -and $p.StartTime.ToFileTimeUtc() -eq [long]$start)
}
# Another build on the machine: a worker of this SDK started through WMI, so it is in none of the measuring script's jobs.
if ($mode -eq 'outside' -and -not (Test-Path -LiteralPath (Join-Path $here 'worker7.pid'))) {
  $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
  $line = "`"$((Get-Process -Id $PID).Path)`" -NoProfile -File `"$(Join-Path $here 'worker.ps1')`" /nodemode:1 `"$here`" 7"
  $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $line; ProcessStartupInformation = $startup }
  foreach ($i in 1..150) { if (Alive 7) { break }; Start-Sleep -Milliseconds 100 }
}
$workers = switch ($mode) { 'worker' { 1 } 'warm-fails' { 1 } 'warm-noop' { 1 } 'warm-fail-second' { 1 } 'warm-vary' { 1 } 'slow-warm' { 1 } 'two-workers' { 2 } default { 0 } }
$blIdx = [Array]::IndexOf($args, '-bl')
if ($blIdx -lt 0) { $blIdx = [Array]::IndexOf($args, '-bl:') }
if ($blIdx -ge 0 -and ($blIdx + 1) -lt $args.Count) {
  $isDiag = $true
  $blPath = $args[$blIdx + 1].Trim('"')
} else {
  $blArg = $args | Where-Object { $_ -like '-bl:*' } | Select-Object -First 1
  $isDiag = [bool]$blArg
  $blPath = if ($blArg) { ($blArg -replace '^-bl:(LogFile=)?', '').Trim('"') } else { '' }
}
if ($isDiag) {
  if ($mode -eq 'diag-fails') { 'error: diagnostic build broke'; exit 6 }
  if ($mode -ne 'diag-noop') { Set-Content -LiteralPath $outs[0] -Value 'diag-built' }
  if ($mode -ne 'diag-no-binlog') { Set-Content -LiteralPath $blPath -Value 'FAKE-BINLOG-BYTES' }
  if ($mode -ne 'diag-no-summary') {
    'Target Performance Summary:'
    '       12 ms  PrepareForBuild                            1 calls'
    '      450 ms  CoreCompile                                1 calls'
    'Task Performance Summary:'
    '       25 ms  Copy                                       2 calls'
    '      410 ms  Csc                                        1 calls'
    '      110 ms  ResolveAssemblyReference                   1 calls'
  }
  'Build succeeded.'
  exit 0
}
if ($workers) {
  if (Alive 1) {
    $warmCountFile = Join-Path $here 'warm-calls.txt'
    $warmIdx = @(Get-Content -LiteralPath $warmCountFile -ErrorAction Ignore).Count + 1
    Add-Content -LiteralPath $warmCountFile -Value "warm-$warmIdx"
    if ($mode -eq 'warm-fails') { 'error: the warm build broke'; exit 4 }
    if ($mode -eq 'warm-fail-second' -and $warmIdx -eq 2) { 'error: second warm build broke'; exit 4 }
    if ($mode -eq 'warm-noop') { 'Build succeeded.'; exit 0 }  # succeeds and compiles nothing
    if ($mode -eq 'warm-vary') { Start-Sleep -Milliseconds (300 * ($warmIdx - 1)) }
    if ($mode -eq 'slow-warm') { Start-Sleep -Seconds 6 }
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
  # Runs the measuring script with the process query failing after CIM_FAIL_AFTER answers, as when WMI is unavailable.
  Set-Content -LiteralPath (Join-Path $dn 'cim-fails.ps1') -Value @'
$script:calls = 0
function global:Get-CimInstance {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$ClassName, [string[]]$Property)
  if ($ClassName -eq 'Win32_Process' -and ++$script:calls -gt [int]$env:CIM_FAIL_AFTER) { throw 'the WMI service is not available' }
  CimCmdlets\Get-CimInstance @PSBoundParameters
}
$target, $rest = $args
& $target @rest
exit $LASTEXITCODE
'@
  $fakeDotnet = Join-Path $dn 'dotnet.cmd'
  Set-Content -LiteralPath $fakeDotnet -Value "@pwsh -NoProfile -File `"%~dp0fake-dotnet.ps1`" %*"
  $rsp = Join-Path $dn 'Directory.Build.rsp'
  Set-Content -LiteralPath $rsp -Value @('# the repository''s settings', '-nodeReuse:false', '-maxCpuCount:4')
  # A worker is its id and its start time: an id alone is given to another process once the worker has exited, and
  # was read as a worker still running (red on CI at b4298e8, pid 7132 alive 15 s after its worker had gone).
  function Get-WorkerIds {
    @(Get-ChildItem -LiteralPath $dn -Filter 'worker*.pid' -ErrorAction Ignore | ForEach-Object {
      $id, $start = (Get-Content -LiteralPath $_.FullName -Raw).Trim() -split ' '
      if ($start) { "$([int]$id) $([long]$start)" }
    })
  }
  function Test-Worker([string]$worker) {
    $id, $start = $worker -split ' '
    $p = Get-Process -Id ([int]$id) -ErrorAction Ignore
    return [bool]($p -and $p.StartTime.ToFileTimeUtc() -eq [long]$start)
  }
  function Stop-Worker([string]$worker) { if (Test-Worker $worker) { Stop-Process -Id ([int]($worker -split ' ')[0]) -Force -ErrorAction Ignore } }
  $workerIds = [Collections.Generic.List[string]]::new()
  function Invoke-DotnetMeasure([string]$mode, [string[]]$arguments, [string]$shim, [int]$SamplesCount = 1, [int]$Linger = 2) {
    Set-Content -LiteralPath (Join-Path $dn 'mode.txt') -Value $mode
    foreach ($o in $outs + @(Join-Path $dn 'cleaned.txt') + @(Join-Path $dn 'warm-calls.txt')) { Remove-Item -LiteralPath $o -ErrorAction Ignore }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $through = @(if ($shim) { $shim })
    $text = & pwsh -NoProfile -File @through $measure -Dotnet -DotnetCommand $fakeDotnet -Solution (Join-Path $dn 'Fake.slnx') -DotnetProject (Join-Path $dn 'Fake.csproj') `
      -DotnetOutputs ($outs -join ',') -TouchFile (Join-Path $dn 'Foundation.cs') -ResponseFile $rsp -OutDir $out -LingerSeconds $Linger -Samples $SamplesCount @arguments 2>&1 | Out-String
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
  foreach ($worker in Get-WorkerIds) { Stop-Worker $worker }  # the outside worker is this test's own
  Remove-Item -LiteralPath (Join-Path $dn 'worker7.pid') -ErrorAction Ignore
  $run = Invoke-DotnetMeasure 'warm-noop' @('-Label', 'dotnet-bad', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true')
  Check 'a warm build that compiles nothing is refused' ($run.Exit -eq 1 -and $run.Text -match "step 'one file changed, after a build' did not rewrite out1\.dll" -and (Test-DataUnchanged)) "($($run.Text))"
  foreach ($after in 0, 12) {
    $env:CIM_FAIL_AFTER = $after
    try { $run = Invoke-DotnetMeasure 'ok' @('-Label', 'dotnet-bad', '-Decision', 'kept') (Join-Path $dn 'cim-fails.ps1') } finally { Remove-Item Env:CIM_FAIL_AFTER }
    Check "a process list that cannot be read after $after answer(s) is refused, not read as nothing running" ($run.Exit -eq 1 -and $run.Text -match 'could not read the process list \(the WMI service is not available\)' -and (Test-DataUnchanged)) "($($run.Text))"
  }
  Remove-Item -LiteralPath (Join-Path $dn 'worker7.pid') -ErrorAction Ignore
  $run = Invoke-DotnetMeasure 'warm-fails' @('-Label', 'dotnet-bad', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true')
  Check 'a failed warm build stores nothing' ($run.Exit -eq 1 -and $run.Text -match "step 'one file changed, after a build' failed with exit code 4" -and (Test-DataUnchanged)) "($($run.Text))"

  # The stored settings and observations survive a render and a decision.
  $run = Invoke-Measure 'ok' @('-RenderOnly')
  $run = Invoke-Measure 'ok' @('-Label', 'dotnet-reuse', '-Decision', 'kept', '-Note', 'kept after all', '-DecideOnly')
  $after = Get-Label 'dotnet-reuse'
  Check 'the stored .NET settings survive -RenderOnly and -DecideOnly' ($after.decision -eq 'kept' -and ($after.configuration | ConvertTo-Json -Compress) -eq ($reuse.configuration | ConvertTo-Json -Compress) -and ($after.observed | ConvertTo-Json -Compress -Depth 5) -eq ($reuse.observed | ConvertTo-Json -Compress -Depth 5))

  # --- D00 T07 §7: spreads, multi-sample warm builds, -Baseline comparison, live progress, mode conflicts, -Diagnose ---
  function Get-ExpectedSpread([double[]]$values, [int]$digits) {
    if ($values.Count -le 1) { return [double]0 }
    $sorted = [double[]]@($values | Sort-Object)
    [Math]::Round(($sorted[-1] - $sorted[0]), $digits)
  }
  function Test-Spreads($steps) {
    -not (@($steps) | Where-Object {
        $null -eq $_.spread_seconds -or $null -eq $_.spread_peak_mb -or
        [double]$_.spread_seconds -ne (Get-ExpectedSpread $_.seconds 2) -or
        [double]$_.spread_peak_mb -ne (Get-ExpectedSpread $_.peak_mb 1)
      })
  }

  # Every stored native and .NET step (including single-sample steps) stores both spreads and renders raw MB samples.
  $data = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
  $baseEntry = $data.labels | Where-Object label -eq 'baseline'
  $candEntry = $data.labels | Where-Object label -eq 'candidate'
  $unrendEntry = $data.labels | Where-Object label -eq 'unrendered'
  $plainEntry = $data.labels | Where-Object label -eq 'dotnet-plain'
  Check 'native and .NET steps store exact spread_seconds and spread_peak_mb, including single-sample steps' (
    (Test-Spreads $baseEntry.steps) -and (Test-Spreads $candEntry.steps) -and (Test-Spreads $unrendEntry.steps) -and (Test-Spreads $plainEntry.steps) -and
    ($unrendEntry.steps[0].spread_seconds -eq 0) -and ($unrendEntry.steps[0].spread_peak_mb -eq 0) -and
    ($baseEntry.target -eq 'native') -and ($plainEntry.target -eq 'Fake.slnx')
  )
  $docText = Get-Content -LiteralPath $doc -Raw
  Check 'the document renders time spread, memory spread, and raw peak-memory samples' (
    $docText -match '\| Time spread \(s\) \| Memory spread \(MB\) \| Samples \(s\) \| Samples \(MB\) \|'
  )

  # .NET warm build sampled 3 times: 3 distinct warm invocations, varying timings, exact median & spreads, per-sample reuse.
  $run = Invoke-DotnetMeasure 'warm-vary' @('-Label', 'dotnet-warm3', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true') $null 3 2
  $warm3 = Get-Label 'dotnet-warm3'
  $warm3Step = $warm3.steps | Where-Object step -eq 'one file changed, after a build'
  $warmCalls = @(Get-Content -LiteralPath (Join-Path $dn 'warm-calls.txt') -ErrorAction Ignore)
  Check 'warm build runs 3 distinct invocations with exact median, spreads, and per-sample reuse evidence' (
    $run.Exit -eq 0 -and $warmCalls.Count -eq 3 -and @($warm3Step.seconds).Count -eq 3 -and @($warm3Step.peak_mb).Count -eq 3 -and
    (Test-Medians $warm3.steps) -and (Test-Spreads $warm3.steps) -and ($warm3Step.spread_seconds -gt 0) -and
    $warm3.observed.warm_used_nodes -eq $true -and @($warm3.observed.warm_samples_used_nodes).Count -eq 3 -and
    -not (@($warm3.observed.warm_samples_used_nodes) | Where-Object { $_ -ne $true })
  ) "(exit $($run.Exit), calls $($warmCalls.Count), secs $($warm3Step.seconds -join ','), spread $($warm3Step.spread_seconds)): $($run.Text)"

  # Failing only the 2nd warm invocation exits 1 and leaves prior label intact.
  $beforeWarmFail = [IO.File]::ReadAllBytes($json)
  $run = Invoke-DotnetMeasure 'warm-fail-second' @('-Label', 'dotnet-warm3', '-Decision', 'dropped', '-BuildArgs', 'nodeReuse:true') $null 3 2
  $warmFailCalls = @(Get-Content -LiteralPath (Join-Path $dn 'warm-calls.txt') -ErrorAction Ignore)
  Check 'a failure on the 2nd warm sample exits 1 and leaves the previous label intact' (
    $run.Exit -eq 1 -and $warmFailCalls.Count -eq 2 -and $run.Text -match "step 'one file changed, after a build' failed with exit code 4" -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($beforeWarmFail)
  ) "(exit $($run.Exit), calls $($warmFailCalls.Count)): $($run.Text)"

  # --- -Baseline comparison: historical data, mixed compatible/incompatible targets, wide spread vs equality vs clear win, missing baseline ---
  $cmpOut = Join-Path $dir 'cmp-docs'
  New-Item -ItemType Directory -Path $cmpOut | Out-Null
  $cmpJson = Join-Path $cmpOut 'build-performance.json'
  $cmpDoc = Join-Path $cmpOut 'build-performance.md'
  # Historical labels omit spread_seconds, spread_peak_mb, and target; mixed store includes native, dotnet solution, and dotnet single-project.
  $synthetic = [ordered]@{
    schema_version = 1
    labels         = @(
      [ordered]@{
        label         = 'hist-base'
        decision      = 'baseline'
        note          = 'historical native baseline'
        date          = '2026-11-12T00:00:00Z'
        machine       = [ordered]@{ name = 'TEST'; processor = 'CPU'; memory_gb = 32 }
        tools         = [ordered]@{ cmake = '4.3.1' }
        configuration = [ordered]@{ build_args = @(); launcher = $null; linker = 'link.exe'; env_cache = $false }
        samples       = 3
        steps         = @(
          [ordered]@{ step = 'nothing to do'; seconds = @(10.0, 10.2, 10.4); peak_mb = @(100.0, 101.0, 102.0); median_seconds = 10.2; median_peak_mb = 101.0 }
          [ordered]@{ step = 'one file changed'; seconds = @(20.0, 22.0, 24.0); peak_mb = @(200.0, 200.0, 200.0); median_seconds = 22.0; median_peak_mb = 200.0 }
          [ordered]@{ step = 'fresh build'; seconds = @(30.0, 31.0, 32.0); peak_mb = @(300.0, 305.0, 310.0); median_seconds = 31.0; median_peak_mb = 305.0 }
        )
      }
      [ordered]@{
        label         = 'clear-and-noisy'
        target        = 'native'
        decision      = 'kept'
        note          = 'clear win on noop, wide spread on one-file, exact equality on fresh'
        date          = '2026-11-23T00:00:00Z'
        machine       = [ordered]@{ name = 'TEST'; processor = 'CPU'; memory_gb = 32 }
        tools         = [ordered]@{ cmake = '4.3.1' }
        configuration = [ordered]@{ build_args = @(); launcher = $null; linker = 'link.exe'; env_cache = $true }
        samples       = 3
        steps         = @(
          # delta time = -3.0 s (spread 0.2 / 0.4 -> ok), delta mb = -20.0 MB (spread 1.0 / 2.0 -> ok)
          [ordered]@{ step = 'nothing to do'; seconds = @(7.1, 7.2, 7.3); peak_mb = @(80.5, 81.0, 81.5); median_seconds = 7.2; median_peak_mb = 81.0; spread_seconds = 0.2; spread_peak_mb = 1.0 }
          # delta time = -1.0 s (baseline spread 4.0 > 1.0 -> WARNING), delta mb = +2.0 MB (cand spread 6.0 > 2.0 -> WARNING)
          [ordered]@{ step = 'one file changed'; seconds = @(20.5, 21.0, 21.5); peak_mb = @(199.0, 202.0, 205.0); median_seconds = 21.0; median_peak_mb = 202.0; spread_seconds = 1.0; spread_peak_mb = 6.0 }
          # exact threshold equality: delta time = -2.0 s, max spread = 2.0 s -> NO warning; delta mb = -10.0 MB, max spread = 10.0 MB -> NO warning
          [ordered]@{ step = 'fresh build'; seconds = @(28.0, 29.0, 30.0); peak_mb = @(290.0, 295.0, 300.0); median_seconds = 29.0; median_peak_mb = 295.0; spread_seconds = 2.0; spread_peak_mb = 10.0 }
          # extra step not in baseline
          [ordered]@{ step = 'python test suite'; seconds = @(50.0); peak_mb = @(400.0); median_seconds = 50.0; median_peak_mb = 400.0; spread_seconds = 0.0; spread_peak_mb = 0.0 }
        )
      }
      [ordered]@{
        label         = 'hist-dotnet'
        kind          = 'dotnet'
        decision      = 'baseline'
        note          = 'historical dotnet solution label'
        date          = '2026-11-12T00:00:00Z'
        machine       = [ordered]@{ name = 'TEST'; processor = 'CPU'; memory_gb = 32 }
        tools         = [ordered]@{ dotnet = '11.0.100' }
        configuration = [ordered]@{ build_args = @(); response_file = @(); node_reuse = $false; max_cpu_count = 4; use_shared_compilation = $true }
        observed      = [ordered]@{ max_worker_nodes = 0; compiler_server = $false; left_running = [ordered]@{ observed = $true; after_seconds = 2.0; processes = 0; nodes = 0; names = @(); mb = 0.0 }; warm_used_nodes = $null; warm_used_compiler = $null }
        samples       = 1
        steps         = @(
          [ordered]@{ step = 'nothing to do'; seconds = @(1.0); peak_mb = @(50.0); median_seconds = 1.0; median_peak_mb = 50.0 }
        )
      }
      [ordered]@{
        label         = 'single-proj-dotnet'
        kind          = 'dotnet'
        target        = 'src/Bosun.Core/Bosun.Core.csproj'
        decision      = 'kept'
        note          = 'single project dotnet label'
        date          = '2026-11-23T00:00:00Z'
        machine       = [ordered]@{ name = 'TEST'; processor = 'CPU'; memory_gb = 32 }
        tools         = [ordered]@{ dotnet = '11.0.100' }
        configuration = [ordered]@{ build_args = @(); response_file = @(); node_reuse = $false; max_cpu_count = 4; use_shared_compilation = $true }
        observed      = [ordered]@{ max_worker_nodes = 0; compiler_server = $false; left_running = [ordered]@{ observed = $true; after_seconds = 2.0; processes = 0; nodes = 0; names = @(); mb = 0.0 }; warm_used_nodes = $null; warm_used_compiler = $null }
        samples       = 1
        steps         = @(
          [ordered]@{ step = 'nothing to do'; seconds = @(0.5); peak_mb = @(40.0); median_seconds = 0.5; median_peak_mb = 40.0; spread_seconds = 0.0; spread_peak_mb = 0.0 }
        )
      }
    )
  }
  [IO.File]::WriteAllText($cmpJson, (($synthetic | ConvertTo-Json -Depth 10) -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
  $cmpOutText = & pwsh -NoProfile -File $measure -OutDir $cmpOut -RenderOnly -Baseline 'hist-base' 2>&1 | Out-String
  $cmpExit = $LASTEXITCODE
  $cmpMd = Get-Content -LiteralPath $cmpDoc -Raw
  Check '-RenderOnly -Baseline compares historical native labels on stdout with signed deltas, spreads, warning, equality ok, missing step, and incompatible targets' (
    $cmpExit -eq 0 -and
    $cmpOutText -match "comparison against baseline 'hist-base' \(native/native\):" -and
    $cmpOutText -match 'nothing to do: time -3\.0 s \(spread 0\.2 / 0\.4 s\), memory -20\.0 MB \(spread 1\.0 / 2\.0 MB\) \[ok\]' -and
    $cmpOutText -match 'one file changed: time -1\.0 s \(spread 1\.0 / 4\.0 s\), memory \+2\.0 MB \(spread 6\.0 / 0\.0 MB\) \[WARNING: time and memory spread wider than change\]' -and
    $cmpOutText -match 'fresh build: time -2\.0 s \(spread 2\.0 / 2\.0 s\), memory -10\.0 MB \(spread 10\.0 / 10\.0 MB\) \[ok\]' -and
    $cmpOutText -match "python test suite: not in baseline 'hist-base'" -and
    $cmpOutText -match 'hist-dotnet: incompatible target \(dotnet/Bosun\.slnx vs native/native\), not compared' -and
    $cmpOutText -match 'single-proj-dotnet: incompatible target \(dotnet/src/Bosun\.Core/Bosun\.Core\.csproj vs native/native\), not compared'
  ) "(exit $cmpExit): $cmpOutText"
  Check '-RenderOnly -Baseline renders comparison table, raw samples, spreads, warning, and incompatible notice in Markdown' (
    $cmpMd -match '## Comparison against hist-base' -and
    $cmpMd -match '\| nothing to do \| -3\.0 s \| 0\.2 / 0\.4 \| -20\.0 MB \| 1\.0 / 2\.0 \| ok \|' -and
    $cmpMd -match '\| one file changed \| -1\.0 s \| 1\.0 / 4\.0 \| \+2\.0 MB \| 6\.0 / 0\.0 \| WARNING: time and memory spread wider than change \|' -and
    $cmpMd -match '\| fresh build \| -2\.0 s \| 2\.0 / 2\.0 \| -10\.0 MB \| 10\.0 / 10\.0 \| ok \|' -and
    $cmpMd -match "\| python test suite \| n/a \| n/a \| n/a \| n/a \| not in baseline 'hist-base' \|" -and
    $cmpMd -match 'Not compared: incompatible target \(dotnet/Bosun\.slnx vs native/native\)\.' -and
    $cmpMd -match '100\.0, 101\.0, 102\.0' -and
    $cmpMd -notmatch "[–—]"
  )
  # Comparing against hist-dotnet refuses numeric delta against single-proj-dotnet (different target).
  $cmpDnOut = & pwsh -NoProfile -File $measure -OutDir $cmpOut -RenderOnly -Baseline 'hist-dotnet' 2>&1 | Out-String
  Check '-Baseline refuses numeric delta between .NET solution and single-project .NET labels' (
    $LASTEXITCODE -eq 0 -and
    $cmpDnOut -match 'single-proj-dotnet: incompatible target \(dotnet/src/Bosun\.Core/Bosun\.Core\.csproj vs dotnet/Bosun\.slnx\), not compared'
  ) "(exit $LASTEXITCODE): $cmpDnOut"

  # Missing -Baseline exits 1 and leaves BOTH JSON and Markdown byte-identical (on -RenderOnly and on measurement).
  $cmpJsonBytes = [IO.File]::ReadAllBytes($cmpJson)
  $cmpDocBytes = [IO.File]::ReadAllBytes($cmpDoc)
  $missRender = & pwsh -NoProfile -File $measure -OutDir $cmpOut -RenderOnly -Baseline 'no-such-base' 2>&1 | Out-String
  Check 'missing -Baseline on -RenderOnly exits 1 and leaves JSON and Markdown byte-identical' (
    $LASTEXITCODE -eq 1 -and $missRender -match "no baseline label 'no-such-base'" -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($cmpJson)) -eq [Convert]::ToBase64String($cmpJsonBytes) -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($cmpDoc)) -eq [Convert]::ToBase64String($cmpDocBytes)
  ) "(exit $LASTEXITCODE): $missRender"
  Remove-Item -LiteralPath (Join-Path $dir 'runs.txt') -ErrorAction Ignore
  Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value 'ok'
  $missMeas = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $cmpOut -Label 'new-meas' -Decision 'kept' -Samples 1 -Baseline 'no-such-base' 2>&1 | Out-String
  Check 'missing -Baseline on measurement exits 1 before building and leaves JSON and Markdown byte-identical' (
    $LASTEXITCODE -eq 1 -and $missMeas -match "no baseline label 'no-such-base'" -and
    -not (Test-Path -LiteralPath (Join-Path $dir 'runs.txt')) -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($cmpJson)) -eq [Convert]::ToBase64String($cmpJsonBytes) -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($cmpDoc)) -eq [Convert]::ToBase64String($cmpDocBytes)
  ) "(exit $LASTEXITCODE): $missMeas"

  # Measurement run with -Baseline compares the newly stored label against the baseline on stdout and Markdown.
  $measWithBase = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $cmpOut -Label 'fresh-cand' -Decision 'kept' -Samples 1 -Baseline 'hist-base' 2>&1 | Out-String
  $measBaseExit = $LASTEXITCODE
  $cmpMdAfter = Get-Content -LiteralPath $cmpDoc -Raw
  Check 'measurement with -Baseline emits comparison on stdout and Markdown while keeping raw samples' (
    $measBaseExit -eq 0 -and
    $measWithBase -match "comparison against baseline 'hist-base'" -and
    $measWithBase -match 'fresh-cand:' -and
    $cmpMdAfter -match '### fresh-cand vs hist-base'
  ) "(exit $measBaseExit): $measWithBase"

  # --- CLI mode conflicts (validated before -RenderOnly / -DecideOnly early returns, exit 2, zero mutations) ---
  $conflictJsonBefore = [IO.File]::ReadAllBytes($cmpJson)
  $conflictCases = @(
    @{ Name = '-RenderOnly + -DecideOnly'; Args = @('-OutDir', $cmpOut, '-RenderOnly', '-DecideOnly', '-Label', 'hist-base', '-Decision', 'kept') }
    @{ Name = '-Diagnose without -Dotnet'; Args = @('-OutDir', $cmpOut, '-Label', 'x', '-Decision', 'kept', '-Diagnose') }
    @{ Name = '-Dotnet -Diagnose -RenderOnly'; Args = @('-OutDir', $cmpOut, '-Dotnet', '-Diagnose', '-RenderOnly') }
    @{ Name = '-Dotnet -Diagnose -DecideOnly'; Args = @('-OutDir', $cmpOut, '-Dotnet', '-Diagnose', '-DecideOnly', '-Label', 'hist-base', '-Decision', 'kept') }
    @{ Name = '-Baseline + -DecideOnly'; Args = @('-OutDir', $cmpOut, '-Baseline', 'hist-base', '-DecideOnly', '-Label', 'hist-base', '-Decision', 'kept') }
    @{ Name = 'empty -Baseline'; Args = @('-OutDir', $cmpOut, '-RenderOnly', '-Baseline', '') }
    @{ Name = '-AllowOutsideServers without -Dotnet'; Args = @('-OutDir', $cmpOut, '-Label', 'x', '-Decision', 'kept', '-AllowOutsideServers') }
  )
  $conflictOk = $true
  $conflictDetail = ''
  foreach ($cc in $conflictCases) {
    $outTxt = & pwsh -NoProfile -File $measure @($cc.Args) 2>&1 | Out-String
    if ($LASTEXITCODE -ne 2) { $conflictOk = $false; $conflictDetail += "$($cc.Name) exited $LASTEXITCODE ($outTxt); " }
  }
  Check 'conflicting CLI modes exit 2 before early returns and mutate nothing' (
    $conflictOk -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cmpJson)) -eq [Convert]::ToBase64String($conflictJsonBefore)
  ) $conflictDetail

  # --- Queue forwarding for -Baseline and -Diagnose ---
  $savedPath = $env:PATH
  try {
    $env:PATH = "$bin;$env:PATH"
    Remove-Item Env:BOSUN_MEASURE_QUEUED
    Remove-Item -LiteralPath (Join-Path $bin 'bosun-arguments.txt') -ErrorAction Ignore
    $qBase = & pwsh -NoProfile -File $measure -BuildScript $fake -TouchFile (Join-Path $dir 'source.cpp') -OutDir $cmpOut -Label 'queued-base' -Decision 'kept' -Samples 1 -Baseline 'hist-base' 2>&1 | Out-String
    $qBaseExit = $LASTEXITCODE
    $qBaseArgs = @(Get-Content -LiteralPath (Join-Path $bin 'bosun-arguments.txt') -ErrorAction Ignore)
    Set-Content -LiteralPath (Join-Path $dn 'mode.txt') -Value 'ok'
    foreach ($o in $outs + @(Join-Path $dn 'cleaned.txt') + @(Join-Path $dn 'warm-calls.txt')) { Remove-Item -LiteralPath $o -ErrorAction Ignore }
    $qDiag = & pwsh -NoProfile -File $measure -Dotnet -DotnetCommand $fakeDotnet -Solution (Join-Path $dn 'Fake.slnx') -DotnetProject (Join-Path $dn 'Fake.csproj') `
      -DotnetOutputs ($outs -join ',') -TouchFile (Join-Path $dn 'Foundation.cs') -ResponseFile $rsp -OutDir $out -LingerSeconds 0 -Samples 1 -Label 'queued-diag' -Decision 'kept' -Diagnose 2>&1 | Out-String
    $qDiagExit = $LASTEXITCODE
    $qDiagArgs = @(Get-Content -LiteralPath (Join-Path $bin 'bosun-arguments.txt') -ErrorAction Ignore)
    foreach ($id in Get-WorkerIds) { $workerIds.Add($id) }
  } finally { $env:PATH = $savedPath; $env:BOSUN_MEASURE_QUEUED = '1' }
  Check '-Baseline and -Diagnose reach the queued child through bosun run' (
    $qBaseExit -eq 0 -and ($qBaseArgs -contains '-Baseline') -and ($qBaseArgs -contains 'hist-base') -and
    $qDiagExit -eq 0 -and ($qDiagArgs -contains '-Diagnose')
  ) "(base exit $qBaseExit args: $($qBaseArgs -join ' '); diag exit $qDiagExit args: $($qDiagArgs -join ' '))"

  # --- Live progress announcements and 5-second ticks (read while child is still alive, native and .NET) ---
  Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value 'slow-fresh'
  $liveLog = Join-Path $dir 'live-native.stdout'
  $liveOutDir = Join-Path $dir 'live-docs'
  New-Item -ItemType Directory -Path $liveOutDir | Out-Null
  $liveProc = Start-Process pwsh -PassThru -NoNewWindow -RedirectStandardOutput $liveLog -ArgumentList @(
    '-NoProfile', '-File', $measure, '-BuildScript', $fake, '-TouchFile', (Join-Path $dir 'source.cpp'),
    '-OutDir', $liveOutDir, '-Label', 'live-native', '-Decision', 'kept', '-Samples', '1'
  )
  $sawStartLive = $false
  $liveTicks = 0
  $pollDeadline = (Get-Date).AddSeconds(60)
  while (-not $liveProc.HasExited -and (Get-Date) -lt $pollDeadline) {
    if (Test-Path -LiteralPath $liveLog) {
      $content = ''
      try {
        $fs = [IO.FileStream]::new($liveLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $sr = [IO.StreamReader]::new($fs); $content = $sr.ReadToEnd() } finally { $fs.Dispose() }
      } catch [IO.IOException] { $content = '' }
      if (-not $sawStartLive -and $content -match 'starting fresh build sample 1/1\.\.\.') { $sawStartLive = $true }
      $matchesNow = [regex]::Matches($content, 'fresh build sample 1/1: still running \(\d+ s\)')
      if ($matchesNow.Count -gt $liveTicks) { $liveTicks = $matchesNow.Count }
    }
    Start-Sleep -Milliseconds 250
  }
  if (-not $liveProc.WaitForExit(30000)) { $liveProc.Kill($true) }
  $liveJson = Get-Content -LiteralPath (Join-Path $liveOutDir 'build-performance.json') -Raw | ConvertFrom-Json
  $liveFresh = ($liveJson.labels | Where-Object label -eq 'live-native').steps | Where-Object step -eq 'fresh build'
  Check 'native 12-second step emits start line and >= 2 progress ticks live before exit with accurate duration' (
    $liveProc.ExitCode -eq 0 -and $sawStartLive -and $liveTicks -ge 2 -and $liveFresh.median_seconds -ge 11.5 -and $liveFresh.median_seconds -le 25
  ) "(exit $($liveProc.ExitCode), startLive $sawStartLive, ticks $liveTicks, sec $($liveFresh.median_seconds))"

  # .NET live progress: linger wait (6 s -> 5 s tick) and warm build (6 s -> 5 s tick), read while alive.
  Set-Content -LiteralPath (Join-Path $dn 'mode.txt') -Value 'slow-warm'
  foreach ($o in $outs + @(Join-Path $dn 'cleaned.txt') + @(Join-Path $dn 'warm-calls.txt')) { Remove-Item -LiteralPath $o -ErrorAction Ignore }
  $liveDnLog = Join-Path $dir 'live-dotnet.stdout'
  $liveDnProc = Start-Process pwsh -PassThru -NoNewWindow -RedirectStandardOutput $liveDnLog -ArgumentList @(
    '-NoProfile', '-File', $measure, '-Dotnet', '-DotnetCommand', $fakeDotnet,
    '-Solution', (Join-Path $dn 'Fake.slnx'), '-DotnetProject', (Join-Path $dn 'Fake.csproj'),
    '-DotnetOutputs', ($outs -join ','), '-TouchFile', (Join-Path $dn 'Foundation.cs'),
    '-ResponseFile', $rsp, '-OutDir', $liveOutDir, '-LingerSeconds', '6', '-Samples', '1',
    '-Label', 'live-dotnet', '-Decision', 'kept', '-BuildArgs', 'nodeReuse:true'
  )
  $sawLingerLive = $false
  $sawWarmTickLive = $false
  $pollDeadline = (Get-Date).AddSeconds(90)
  while (-not $liveDnProc.HasExited -and (Get-Date) -lt $pollDeadline) {
    if (Test-Path -LiteralPath $liveDnLog) {
      $content = ''
      try {
        $fs = [IO.FileStream]::new($liveDnLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $sr = [IO.StreamReader]::new($fs); $content = $sr.ReadToEnd() } finally { $fs.Dispose() }
      } catch [IO.IOException] { $content = '' }
      if (-not $sawLingerLive -and $content -match 'linger wait: still waiting \(5 s\)') { $sawLingerLive = $true }
      if (-not $sawWarmTickLive -and $content -match 'one file changed, after a build sample 1/1: still running \(5 s\)') { $sawWarmTickLive = $true }
    }
    Start-Sleep -Milliseconds 250
  }
  if (-not $liveDnProc.WaitForExit(30000)) { $liveDnProc.Kill($true) }
  foreach ($id in Get-WorkerIds) { $workerIds.Add($id) }
  Check '.NET linger wait and slow warm build emit progress ticks live before exit' (
    $liveDnProc.ExitCode -eq 0 -and $sawLingerLive -and $sawWarmTickLive
  ) "(exit $($liveDnProc.ExitCode), lingerLive $sawLingerLive, warmTickLive $sawWarmTickLive)"

  # --- -Diagnose failure paths (against existing label, exit 1, prior data intact, partial binlog cleaned up) ---
  $diagScratch = Join-Path $dir 'diag-scratch'
  New-Item -ItemType Directory -Path $diagScratch | Out-Null
  $savedScratch = $env:BOSUN_SCRATCH
  try {
    $env:BOSUN_SCRATCH = $diagScratch
    $beforeDiagFail = [IO.File]::ReadAllBytes($json)
    foreach ($failCase in @(
        @{ Mode = 'diag-fails'; Pattern = "step 'diagnostic build' failed with exit code 6" }
        @{ Mode = 'diag-no-binlog'; Pattern = 'did not produce a non-empty binary log' }
        @{ Mode = 'diag-no-summary'; Pattern = 'did not produce a PerformanceSummary' }
        @{ Mode = 'diag-noop'; Pattern = "step 'diagnostic build' did not rewrite out1\.dll" }
      )) {
      $run = Invoke-DotnetMeasure $failCase.Mode @('-Label', 'dotnet-plain', '-Decision', 'kept', '-Diagnose') $null 1 0
      $leftBinlogs = @(Get-ChildItem -LiteralPath $diagScratch -Filter '*.binlog' -ErrorAction Ignore)
      Check "-Diagnose refusal ($($failCase.Mode)) exits 1, keeps prior label intact, and leaves no partial binlog" (
        $run.Exit -eq 1 -and $run.Text -match $failCase.Pattern -and $leftBinlogs.Count -eq 0 -and
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($beforeDiagFail)
      ) "(exit $($run.Exit), binlogs $($leftBinlogs.Count)): $($run.Text)"
    }
    # Unwritable scratch destination (a regular file where BOSUN_SCRATCH points).
    $badScratchFile = Join-Path $dir 'not-a-scratch-dir'
    Set-Content -LiteralPath $badScratchFile -Value 'file'
    $env:BOSUN_SCRATCH = $badScratchFile
    $run = Invoke-DotnetMeasure 'ok' @('-Label', 'dotnet-plain', '-Decision', 'kept', '-Diagnose') $null 1 0
    Check '-Diagnose with unwritable scratch exits 1 and keeps prior label intact' (
      $run.Exit -eq 1 -and $run.Text -match 'diagnostic build could not write to the scratch folder' -and
      [Convert]::ToBase64String([IO.File]::ReadAllBytes($json)) -eq [Convert]::ToBase64String($beforeDiagFail)
    ) "(exit $($run.Exit)): $($run.Text)"

    # Fake -Diagnose success: verifies exact ranking (CoreCompile 450 > Csc 410 > ResolveAssemblyReference 110) and step isolation.
    $env:BOSUN_SCRATCH = $diagScratch
    $run = Invoke-DotnetMeasure 'ok' @('-Label', 'dotnet-diag-fake', '-Decision', 'kept', '-Diagnose') $null 1 0
    $fakeDiagEntry = Get-Label 'dotnet-diag-fake'
    $fakeTopNames = @($fakeDiagEntry.diagnostic.top_expensive | ForEach-Object name) -join ','
    $fakeStepNames = @($fakeDiagEntry.steps | ForEach-Object step) -join ','
    Check 'fake -Diagnose ranks top 3 targets/tasks from PerformanceSummary, retains binlog, and leaves steps unchanged' (
      $run.Exit -eq 0 -and $fakeTopNames -eq 'CoreCompile,Csc,ResolveAssemblyReference' -and
      $fakeStepNames -eq 'nothing to do,one file changed,fresh build,one file changed, after a build' -and
      (Test-Path -LiteralPath $fakeDiagEntry.diagnostic.binlog) -and
      $fakeDiagEntry.diagnostic.binlog.StartsWith($diagScratch, [StringComparison]::OrdinalIgnoreCase)
    ) "(exit $($run.Exit), top $fakeTopNames, steps $fakeStepNames): $($run.Text)"

    # --- Real dotnet -Diagnose on an isolated scratch copy of Bosun.Core (smallest project) ---
    $realProjDir = Join-Path $dir 'real-bosun-core'
    New-Item -ItemType Directory -Path $realProjDir | Out-Null
    $repoRoot = Split-Path $PSScriptRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src\Bosun.Core\Bosun.Core.csproj') -Destination $realProjDir
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src\Bosun.Core\Foundation.cs') -Destination $realProjDir
    $realCsproj = Join-Path $realProjDir 'Bosun.Core.csproj'
    $realSource = Join-Path $realProjDir 'Foundation.cs'
    $realRsp = Join-Path $realProjDir 'Directory.Build.rsp'
    Set-Content -LiteralPath $realRsp -Value @('-nodeReuse:false')
    $realScratch = Join-Path $dir 'real-diag-scratch'
    New-Item -ItemType Directory -Path $realScratch | Out-Null
    $realOutDir = Join-Path $dir 'real-diag-docs'
    New-Item -ItemType Directory -Path $realOutDir | Out-Null
    $env:BOSUN_SCRATCH = $realScratch
    $beforeRealStart = [DateTime]::UtcNow.AddSeconds(-2)
    $realDiagOut = & pwsh -NoProfile -File $measure -Dotnet -Solution $realCsproj -DotnetProject $realCsproj -TouchFile $realSource `
      -ResponseFile $realRsp -OutDir $realOutDir -LingerSeconds 0 -Samples 1 -Label 'core-diag' -Decision 'kept' -Diagnose -AllowOutsideServers 2>&1 | Out-String
    $realDiagExit = $LASTEXITCODE
    $realDiagJson = if (Test-Path -LiteralPath (Join-Path $realOutDir 'build-performance.json')) { Get-Content -LiteralPath (Join-Path $realOutDir 'build-performance.json') -Raw | ConvertFrom-Json } else { $null }
    $realEntry = if ($realDiagJson) { $realDiagJson.labels | Where-Object label -eq 'core-diag' } else { $null }
    $realBinlog = if ($realEntry -and $realEntry.diagnostic) { $realEntry.diagnostic.binlog } else { '' }
    $realBinlogItem = if ($realBinlog -and (Test-Path -LiteralPath $realBinlog)) { Get-Item -LiteralPath $realBinlog } else { $null }
    $realTop = if ($realEntry -and $realEntry.diagnostic) { @($realEntry.diagnostic.top_expensive) } else { @() }
    $realStepNames = if ($realEntry) { (@($realEntry.steps | ForEach-Object step) -join ',') } else { '' }
    Check 'real dotnet -Diagnose on isolated Bosun.Core produces a fresh binlog in scratch, 3 ranked targets/tasks, and unchanged timed steps' (
      $realDiagExit -eq 0 -and $null -ne $realBinlogItem -and $realBinlogItem.Length -gt 0 -and
      $realBinlogItem.LastWriteTimeUtc -ge $beforeRealStart -and
      $realBinlog.StartsWith($realScratch, [StringComparison]::OrdinalIgnoreCase) -and
      $realTop.Count -eq 3 -and $realTop[0].ms -ge $realTop[1].ms -and $realTop[1].ms -ge $realTop[2].ms -and
      $realDiagOut -match 'diagnostic binary log:' -and $realDiagOut -match 'top 3 expensive targets/tasks:' -and
      $realStepNames -eq 'nothing to do,one file changed,fresh build,one file changed, after a build' -and
      $realEntry.observed.outside_servers_allowed -eq $true
    ) "(exit $realDiagExit, binlog $realBinlog, top $($realTop.Count), steps $realStepNames): $realDiagOut"
  } finally {
    if ($null -eq $savedScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $savedScratch }
  }

  # A job ends its processes, and Stop-Process ends one, without waiting for them to exit, so a worker can still be on
  # its way out at this instant on a slow machine (red on CI at b9939b1, green at the same measuring code before). A
  # worker left running lives its two minutes; one on its way out is gone in moments: give the exits fifteen seconds.
  $alive = @()
  foreach ($i in 1..75) {
    $alive = @($workerIds | Select-Object -Unique | Where-Object { Test-Worker $_ })
    if (-not $alive) { break }
    Start-Sleep -Milliseconds 200
  }
  Check 'no worker the fake dotnet started is left running' ($workerIds.Count -gt 0 -and -not $alive) "($($workerIds.Count) started, alive after 15 s: $($alive -join ', '))"

} finally {
  # Every worker the fakes started ends with the test, whatever happened to it.
  if ($dn -and (Test-Path -LiteralPath $dn)) { foreach ($worker in @($workerIds) + @(Get-WorkerIds)) { Stop-Worker $worker } }
  Remove-Item Env:BOSUN_MEASURE_QUEUED -ErrorAction Ignore
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
