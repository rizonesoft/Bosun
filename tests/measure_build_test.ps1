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

} finally {
  Remove-Item Env:BOSUN_MEASURE_QUEUED -ErrorAction Ignore
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
