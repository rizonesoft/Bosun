# scripts/lib/hold-awake.ps1 under a fake parent (D00 T03 §28): it holds, says so, and lets go within a second of its
# parent ending, of its launcher's release and of its minutes running out; a refused request says so with exit 3.
# Every process started here has no window, and each is stopped with its tree in the end.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$holder = Join-Path $repo 'scripts\lib\hold-awake.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
. (Join-Path $repo 'scripts\lib\scratch.ps1')
$work = New-BosunScratchFolder -Name 'hold-awake'
$failed = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ } }
$started = [Collections.Generic.List[Diagnostics.Process]]::new()

function Start-Hidden([string]$file, [string[]]$arguments, [switch]$WithInput) {  # not $Input: an automatic variable
  $info = [Diagnostics.ProcessStartInfo]::new($file)
  foreach ($a in $arguments) { $info.ArgumentList.Add($a) }
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.RedirectStandardInput = [bool]$WithInput
  $p = [Diagnostics.Process]::Start($info); $script:started.Add($p); $p
}
function Start-Parent { Start-Hidden "$env:SystemRoot\System32\PING.EXE" @('-n', '120', '127.0.0.1') }
function Start-Holder([int]$parent, [string]$record, [string[]]$extra = @()) {
  Start-Hidden $pwsh (@('-NoProfile', '-NonInteractive', '-File', $holder, '-ParentPid', "$parent", '-Record', $record) + $extra) -WithInput
}
function Gone-Within([Diagnostics.Process]$p, [int]$ms) { $p.WaitForExit($ms) }
# The holder's one line, or a throw into the finally below that stops every process started here (round 3 of D00 T03 §28).
function Read-Answer([Diagnostics.Process]$p, [int]$seconds = 20) {
  $answer = $p.StandardOutput.ReadLineAsync()
  if (-not $answer.Wait([TimeSpan]::FromSeconds($seconds))) { throw "holder $($p.Id) gave no line within $seconds seconds" }
  $answer.Result
}

try {
  # It holds, and says so on its line and in its record.
  $parent = Start-Parent
  $record = Join-Path $work 'a.json'
  $h = Start-Holder $parent.Id $record
  $line = (Read-Answer $h)
  $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
  # ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED, all three (completion review of D00 T03 §28).
  Check ($saved.flags -eq 2147483651) "the holder asks for the continuous, system and display request ($($saved.flags))"
  Check ($line -eq 'held' -and $saved.acquired -eq $true -and $saved.pid -eq $h.Id -and $saved.parent -eq $parent.Id -and [math]::Abs((([datetime]$saved.started).ToUniversalTime() - $h.StartTime.ToUniversalTime()).TotalSeconds) -lt 1) "the holder says held and records its own pid and its parent ($line)"
  # Its parent ends: it lets go within a second.
  $parent.Kill(); $parent.WaitForExit()
  Check (Gone-Within $h 1000) 'it is gone within a second of its parent ending'
  Check ((Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).released_because -eq 'its parent ended') 'and its record says why'

  # Its launcher releases it by closing its input.
  $parent = Start-Parent
  $h = Start-Holder $parent.Id (Join-Path $work 'b.json')
  $null = (Read-Answer $h)
  $h.StandardInput.Close()
  Check (Gone-Within $h 1000) 'it is gone within a second of its launcher closing its input'
  Check ($h.ExitCode -eq 0) 'and exits 0'

  # A timed hold: still held just before its time, gone at it.
  $h = Start-Holder $parent.Id (Join-Path $work 'c.json') @('-Minutes', '0.05')  # three seconds
  $null = (Read-Answer $h)
  Start-Sleep -Milliseconds 2200
  Check (-not $h.HasExited) 'a timed hold is still held just before its time'
  Check (Gone-Within $h 2500) 'and gone once its time has run out'
  Check ((Get-Content -LiteralPath (Join-Path $work 'c.json') -Raw | ConvertFrom-Json).released_because -match 'minute\(s\) ran out') 'its record says its time ran out'

  # A refused request says so, with exit 3.
  $env:BOSUN_TEST_HOLD_FAIL = '1'
  try { $h = Start-Holder $parent.Id (Join-Path $work 'd.json') } finally { Remove-Item Env:BOSUN_TEST_HOLD_FAIL }
  $line = (Read-Answer $h)
  # It lives until released, as a held one does, so its record stays this run's while the writer runs (round 1 of
  # D00 T03 §28), and records the release it is given.
  $alive = -not $h.WaitForExit(1500)
  $h.StandardInput.Close()
  $null = $h.WaitForExit(5000)
  $saved = Get-Content -LiteralPath (Join-Path $work 'd.json') -Raw | ConvertFrom-Json
  Check ($line -match '^failed: SetThreadExecutionState refused' -and $alive -and $h.ExitCode -eq 3 -and $saved.acquired -eq $false -and $saved.released_because -eq 'its launcher released it') "a refused request says failed, lives until released, records it and exits 3 ($line)"
  # A record it cannot write is said on its line, since the preflight would read an older run's record instead.
  $locked = Join-Path $work 'e.json'
  New-Item -ItemType Directory -Path $locked | Out-Null  # a path the replace cannot take
  $h = Start-Holder $parent.Id $locked
  $line = (Read-Answer $h)
  $h.StandardInput.Close(); $null = $h.WaitForExit(5000)
  Check ($line -match "^held; but .*e\.json could not be written") "a record that cannot be written is said on the holder's line ($line)"
  # A write that fails part way (a full disk) is said and leaves no temporary file behind.
  $partial = Join-Path $work 'g.json'
  $env:BOSUN_TEST_HOLD_PARTIAL = '1'
  try { $h = Start-Holder $parent.Id $partial } finally { Remove-Item Env:BOSUN_TEST_HOLD_PARTIAL }
  $line = (Read-Answer $h)
  $h.StandardInput.Close(); $null = $h.WaitForExit(5000)
  Check ($line -match '^held; but .*g\.json could not be written' -and -not (Get-ChildItem -LiteralPath $work -Filter 'g.json.*.tmp')) "a record write that fails part way leaves no temporary file ($line)"
  # The case round 1 named: an older run's released record left read-only.
  $readOnly = Join-Path $work 'f.json'
  [IO.File]::WriteAllText($readOnly, '{"pid": 1, "released": "2026-11-21T00:00:00Z"}')
  (Get-Item -LiteralPath $readOnly).IsReadOnly = $true
  $h = Start-Holder $parent.Id $readOnly
  $line = (Read-Answer $h)
  $h.StandardInput.Close(); $null = $h.WaitForExit(5000)
  $kept = [IO.File]::ReadAllText($readOnly)
  (Get-Item -LiteralPath $readOnly).IsReadOnly = $false
  Check ($line -match '^held; but .*f\.json could not be written' -and $kept -match '2026-11-21') "a read-only older record is said, not silently replaced ($line)"
}
finally {
  foreach ($p in $started) { if (-not $p.HasExited) { & taskkill /T /F /PID $p.Id 2>&1 | Out-Null } }
  Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all passed'
