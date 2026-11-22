# scripts/lib/hold-awake.ps1 under a fake parent (D00 T03 §28): it holds, says so, and lets go within a second of its
# parent ending, of its launcher's release and of its minutes running out; a refused request says so with exit 3.
# Every process started here has no window, and each is stopped with its tree in the end.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$holder = Join-Path $repo 'scripts\lib\hold-awake.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-hold-awake-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
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

try {
  # It holds, and says so on its line and in its record.
  $parent = Start-Parent
  $record = Join-Path $work 'a.json'
  $h = Start-Holder $parent.Id $record
  $line = $h.StandardOutput.ReadLine()
  $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
  Check ($line -eq 'held' -and $saved.acquired -eq $true -and $saved.pid -eq $h.Id -and $saved.parent -eq $parent.Id) "the holder says held and records its own pid and its parent ($line)"
  # Its parent ends: it lets go within a second.
  $parent.Kill(); $parent.WaitForExit()
  Check (Gone-Within $h 1500) 'it is gone within a second of its parent ending'
  Check ((Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).released_because -eq 'its parent ended') 'and its record says why'

  # Its launcher releases it by closing its input.
  $parent = Start-Parent
  $h = Start-Holder $parent.Id (Join-Path $work 'b.json')
  $null = $h.StandardOutput.ReadLine()
  $h.StandardInput.Close()
  Check (Gone-Within $h 1500) 'it is gone within a second of its launcher closing its input'
  Check ($h.ExitCode -eq 0) 'and exits 0'

  # A timed hold: still held just before its time, gone at it.
  $h = Start-Holder $parent.Id (Join-Path $work 'c.json') @('-Minutes', '0.05')  # three seconds
  $null = $h.StandardOutput.ReadLine()
  Start-Sleep -Milliseconds 2200
  Check (-not $h.HasExited) 'a timed hold is still held just before its time'
  Check (Gone-Within $h 2500) 'and gone once its time has run out'
  Check ((Get-Content -LiteralPath (Join-Path $work 'c.json') -Raw | ConvertFrom-Json).released_because -match 'minute\(s\) ran out') 'its record says its time ran out'

  # A refused request says so, with exit 3.
  $env:BOSUN_TEST_HOLD_FAIL = '1'
  try { $h = Start-Holder $parent.Id (Join-Path $work 'd.json') } finally { Remove-Item Env:BOSUN_TEST_HOLD_FAIL }
  $line = $h.StandardOutput.ReadLine()
  $null = $h.WaitForExit(5000)
  Check ($line -match '^failed: SetThreadExecutionState refused' -and $h.ExitCode -eq 3 -and (Get-Content -LiteralPath (Join-Path $work 'd.json') -Raw | ConvertFrom-Json).acquired -eq $false) "a refused request says failed and exits 3 ($line)"
}
finally {
  foreach ($p in $started) { if (-not $p.HasExited) { & taskkill /T /F /PID $p.Id 2>&1 | Out-Null } }
  Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all passed'
