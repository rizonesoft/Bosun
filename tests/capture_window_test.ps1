# Tests scripts/capture-window.ps1 (D02 T02 §9) as far as it can be tested with no desktop: its own self-test, which
# checks the pair writer, the covered-window rule, and that what a launch started is stopped as a tree while a process
# of the same name is left alone. Capturing a real window needs a desktop session: that is D02 T02 §10.
#
# Then the desktop lock (D02 T02 §11), on a lock name of its own so it never meets a real smoke or capture: a capture
# is a first owner another waits on, a holder's capture runs under its hold and nothing else inherits it, a wait that
# runs out exits 4 with nothing launched, bad settings exit 2, and -Verify, a bad argument and the self-test take no
# lock. Every helper is a pwsh child with no window, and every one this starts is ended as a tree at the end.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts/capture-window.ps1'
$lockSource = Join-Path $repo 'scripts/lib/DesktopLock.cs'
$shell = (Get-Process -Id $PID).Path
$failed = [System.Collections.Generic.List[string]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Said = '') {
  if ($Ok) { "ok   $Name" } else { "FAIL $Name"; if ($Said) { $Said.TrimEnd() -split "`r?`n" | ForEach-Object { "     | $_" } }; $failed.Add($Name) }
}

$text = (& pwsh -NoProfile -File $script -SelfTest 2>&1 | Out-String)
$code = $LASTEXITCODE
$text.TrimEnd()
if ($code -ne 0 -or $text -notmatch '(?m)^self-test: all pass\s*$') { "capture-window's self-test failed (exit $code)"; exit 1 }

Add-Type -Path $lockSource
$scratch = if ($env:BOSUN_SCRATCH -and (Test-Path -LiteralPath $env:BOSUN_SCRATCH -PathType Container)) { $env:BOSUN_SCRATCH } else { [System.IO.Path]::GetTempPath() }
# The folder's name holds an apostrophe, so a path spliced into a child's source would break it (round 1, P2).
$dir = Join-Path $scratch ("capture-lock-test-O'Brien-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $dir | Out-Null
$name = 'test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$other = 'test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$folder = [Bosun.Desktop.DesktopLock]::RecordFolder()
$record = [Bosun.Desktop.DesktopLock]::RecordPath($name, $folder)
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

# A pwsh child with no window, its output read, with these variables set (a null removes one).
function Start-Child([string[]]$Arguments, [hashtable]$Environment = @{}) {
  $info = [System.Diagnostics.ProcessStartInfo]::new($shell)
  foreach ($a in @('-NoProfile') + $Arguments) { $info.ArgumentList.Add($a) }
  $info.CreateNoWindow = $true
  $info.UseShellExecute = $false
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $info.Environment['BOSUN_DESKTOP_HELD'] = $null
  foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
  $process = [System.Diagnostics.Process]::Start($info)
  $process | Add-Member -NotePropertyName Out -NotePropertyValue $process.StandardOutput.ReadToEndAsync()
  $process | Add-Member -NotePropertyName Err -NotePropertyValue $process.StandardError.ReadToEndAsync()
  $started.Add($process)
  return $process
}
function Wait-Child($Process, [int]$Seconds = 60) {
  if (-not $Process.WaitForExit($Seconds * 1000)) { throw "a child ($($Process.Id)) didn't end within $Seconds s" }
  $Process.WaitForExit()
  return [pscustomobject]@{ Code = $Process.ExitCode; Said = $Process.Out.Result + $Process.Err.Result }
}
function Wait-Until([scriptblock]$Probe, [string]$What, [int]$Seconds = 30) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  while (-not (& $Probe)) {
    if ((Get-Date) -gt $deadline) { throw "waited $Seconds s for $What" }
    Start-Sleep -Milliseconds 100
  }
}
# A child holding the lock <Lock> until a file 'release' appears in <Folder>; when <Then> is given, it runs that
# under its hold first, with BOSUN_DESKTOP_HELD set as a holder sets it, and writes its exit code to 'then'.
function Start-Holder([string]$Lock, [string]$Folder, [string]$Then = '') {
  New-Item -ItemType Directory -Path $Folder -Force | Out-Null
  # The script it runs under its hold goes in a file of its own, so its quotes can't break the holder's.
  $thenScript = Join-Path $Folder 'then.ps1'
  if ($Then) { Set-Content -LiteralPath $thenScript -Value $Then -Encoding utf8 }
  # Every path reaches the child in its environment, never spliced into its source, so a folder named O'Brien
  # can't break the child's quoting (round 1, P2).
  $code = @'
Add-Type -Path $env:HOLD_SOURCE
$h = [Bosun.Desktop.DesktopLock]::Acquire($env:HOLD_LOCK, [Bosun.Desktop.DesktopLock]::RecordFolder(), [TimeSpan]::FromSeconds(60), $null, $null, $null)
Set-Content -LiteralPath (Join-Path $env:HOLD_FOLDER 'held') -Value $PID
$then = Join-Path $env:HOLD_FOLDER 'then.ps1'
if (Test-Path -LiteralPath $then) {
  $env:BOSUN_DESKTOP_HELD = $h.HeldValue
  & $env:HOLD_SHELL -NoProfile -File $then *> (Join-Path $env:HOLD_FOLDER 'then-said.txt')
  Set-Content -LiteralPath (Join-Path $env:HOLD_FOLDER 'then') -Value $LASTEXITCODE
}
$deadline = (Get-Date).AddSeconds(90)
while (-not (Test-Path -LiteralPath (Join-Path $env:HOLD_FOLDER 'release')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
$h.Dispose()
'@
  $child = Start-Child @('-Command', $code) @{ HOLD_SOURCE = $lockSource; HOLD_LOCK = $Lock; HOLD_FOLDER = $Folder; HOLD_SHELL = $shell }
  Wait-Until { Test-Path -LiteralPath (Join-Path $Folder 'held') } 'a holder to hold the lock'
  return $child
}
function Test-Taken([string]$Lock) {
  $m = [System.Threading.Mutex]::new($false, "Local\$Lock")
  try {
    $owned = try { $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $true }
    if ($owned) { $m.ReleaseMutex() }
    return -not $owned
  } finally { $m.Dispose() }
}
# A capture of a program that never shows a window: it holds the lock while it waits <Wait> s for one, then exits 1.
function Start-Capture([string]$Out, [hashtable]$Environment, [int]$Wait = 1, [string]$Launch = 'Start-Sleep -Seconds 30') {
  Start-Child @('-File', $script, '-Path', $shell, "-LaunchArgs:-NoProfile -Command `"$Launch`"", '-NoNewWindow', '-WaitSeconds', "$Wait", '-Out', $Out) $Environment
}
function Quote([string]$Text) { "'" + $Text.Replace("'", "''") + "'" }

try {
  $lockEnv = @{ BOSUN_DESKTOP_LOCK = $name }

  # A capture is a first owner: its record names it, another process finds the lock taken, and a second capture
  # started meanwhile says whom it waits for and gets the lock when the first has gone.
  $first = Start-Capture (Join-Path $dir 'a.png') $lockEnv 6
  Wait-Until { [Bosun.Desktop.DesktopLock]::Describe($name, $folder) -like "pid $($first.Id) *" } 'the first capture to record itself'
  Check 'a capture holds the lock while it waits for its window' (Test-Taken $name)
  $second = Start-Capture (Join-Path $dir 'b.png') ($lockEnv + @{ BOSUN_DESKTOP_WAIT_S = '40' }) 1
  $one = Wait-Child $first
  $two = Wait-Child $second
  Check "the first ends as before, no window having appeared (exit $($one.Code))" ($one.Code -eq 1 -and $one.Said -like '*no window appeared*')
  Check 'the second said whom it waited for' ($two.Said -like "*held by pid $($first.Id) (*waiting up to 40 s*")
  # By the label its holder gave, never its command line, which can carry a secret (round 1, P1).
  Check 'and named it by its label, not its command line' ($two.Said -like "*held by pid $($first.Id) (capture-window.ps1), started*" -and $two.Said -notlike '*LaunchArgs*') $two.Said
  Check "and took the lock once the first had gone (exit $($two.Code))" ($two.Code -eq 1 -and $two.Said -like '*no window appeared*')
  Check 'the lock is free after both, and their record gone' (-not (Test-Taken $name) -and -not (Test-Path -LiteralPath $record))

  # A capture killed while it holds the lock hands it to one that was waiting.
  $first = Start-Capture (Join-Path $dir 'c.png') $lockEnv 20
  Wait-Until { [Bosun.Desktop.DesktopLock]::Describe($name, $folder) -like "pid $($first.Id) *" } 'the capture to record itself'
  $second = Start-Capture (Join-Path $dir 'd.png') ($lockEnv + @{ BOSUN_DESKTOP_WAIT_S = '40' }) 1
  Start-Sleep -Seconds 3   # the second reaches its wait: pwsh's start and the lock's Add-Type take about 1.5 s
  & taskkill.exe /T /F /PID $first.Id *> $null
  $two = Wait-Child $second
  Check "a capture killed holding the lock hands it to the one waiting (exit $($two.Code))" ($two.Code -eq 1 -and $two.Said -like "*held by pid $($first.Id) (*" -and $two.Said -like '*no window appeared*')

  # A wait that runs out exits 4, naming the holder, with nothing launched.
  $holder = Start-Holder $name (Join-Path $dir 'h1')
  $marker = Join-Path $dir 'launched.txt'
  $run = Wait-Child (Start-Capture (Join-Path $dir 'e.png') ($lockEnv + @{ BOSUN_DESKTOP_WAIT_S = '1'; LAUNCH_MARKER = $marker }) 1 'Set-Content -LiteralPath $env:LAUNCH_MARKER -Value x; Start-Sleep 30')
  Check "a capture that can't have the lock within its wait exits 4 naming the holder (exit $($run.Code))" ($run.Code -eq 4 -and $run.Said -like "*held by pid $($holder.Id) (*") $run.Said
  Check 'and launched nothing' (-not (Test-Path -LiteralPath $marker))

  # The hold is inherited only by the holder's own capture: not through a live ancestor that doesn't hold it, a
  # holder that isn't an ancestor, a dead pid, or another lock's name.
  $holderStart = [Bosun.Desktop.DesktopLock]::StartOf($holder.Id)
  $myStart = [Bosun.Desktop.DesktopLock]::StartOf($PID)
  $dead = Start-Child @('-Command', 'exit 0'); $deadStart = [Bosun.Desktop.DesktopLock]::StartOf($dead.Id); $null = Wait-Child $dead
  $cases = [ordered]@{
    'a live ancestor that does not hold the lock' = "${name}:${PID}:$myStart"
    'a holder that is not an ancestor'            = "${name}:$($holder.Id):$holderStart"
    'a pid that has ended'                        = "${name}:$($dead.Id):$deadStart"
    'the right holder under another lock name'    = "${other}:$($holder.Id):$holderStart"
  }
  foreach ($case in $cases.Keys) {
    $run = Wait-Child (Start-Capture (Join-Path $dir 'f.png') ($lockEnv + @{ BOSUN_DESKTOP_WAIT_S = '0'; BOSUN_DESKTOP_HELD = $cases[$case] }) 1)
    Check "BOSUN_DESKTOP_HELD naming $case is no hold (exit $($run.Code))" ($run.Code -eq 4) $run.Said
  }
  New-Item -ItemType File -Path (Join-Path $dir 'h1\release') | Out-Null
  $null = Wait-Child $holder

  $nested = "`$env:BOSUN_DESKTOP_LOCK = $(Quote $name); `$env:BOSUN_DESKTOP_WAIT_S = '0'; & $(Quote $shell) -NoProfile -File $(Quote $script) -Path $(Quote $shell) '-LaunchArgs:-NoProfile -Command Start-Sleep 30' -NoNewWindow -WaitSeconds 1 -Out $(Quote (Join-Path $dir 'g.png')); exit `$LASTEXITCODE"
  $holder = Start-Holder $name (Join-Path $dir 'h2') $nested
  Wait-Until { Test-Path -LiteralPath (Join-Path $dir 'h2\then') } "the holder's own capture"
  $then = (Get-Content -LiteralPath (Join-Path $dir 'h2\then') -Raw).Trim()
  $thenSaid = Get-Content -LiteralPath (Join-Path $dir 'h2\then-said.txt') -Raw
  Check "the holder's own capture runs under its hold without waiting (exit $then)" ($then -eq '1' -and $thenSaid -like '*no window appeared*' -and $thenSaid -notlike '*waiting*')
  # A child of the holder that changes the lock's name takes that lock like anyone: held elsewhere, it is refused.
  $otherHolder = Start-Holder $other (Join-Path $dir 'h3')
  $renamed = $nested.Replace("`$env:BOSUN_DESKTOP_LOCK = $(Quote $name)", "`$env:BOSUN_DESKTOP_LOCK = $(Quote $other)")
  New-Item -ItemType File -Path (Join-Path $dir 'h2\release') | Out-Null
  $null = Wait-Child $holder
  $holder = Start-Holder $name (Join-Path $dir 'h4') $renamed
  Wait-Until { Test-Path -LiteralPath (Join-Path $dir 'h4\then') } "the holder's renamed capture"
  Check 'a child of the holder under another lock name waits on that lock' ((Get-Content -LiteralPath (Join-Path $dir 'h4\then') -Raw).Trim() -eq '4')
  New-Item -ItemType File -Path (Join-Path $dir 'h4\release') | Out-Null
  $null = Wait-Child $holder
  New-Item -ItemType File -Path (Join-Path $dir 'h3\release') | Out-Null
  $null = Wait-Child $otherHolder

  # Bad settings exit 2, as bad arguments do.
  $run = Wait-Child (Start-Capture (Join-Path $dir 'i.png') ($lockEnv + @{ BOSUN_DESKTOP_WAIT_S = 'x' }) 1)
  Check "BOSUN_DESKTOP_WAIT_S=x exits 2 naming it (exit $($run.Code))" ($run.Code -eq 2 -and $run.Said -like '*BOSUN_DESKTOP_WAIT_S*')
  $run = Wait-Child (Start-Capture (Join-Path $dir 'j.png') @{ BOSUN_DESKTOP_LOCK = 'Bosun.Desktop' } 1)
  Check "BOSUN_DESKTOP_LOCK=Bosun.Desktop exits 2 naming it (exit $($run.Code))" ($run.Code -eq 2 -and $run.Said -like '*BOSUN_DESKTOP_LOCK*')

  # With the lock held throughout, -Verify, a bad argument and the self-test take no lock and don't wait.
  $image = Join-Path $dir 'pair.png'
  [System.IO.File]::WriteAllBytes($image, [byte[]](1..64))
  Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($image, '.txt')) -Value "image sha256  : $((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash)"
  $busy = $lockEnv + @{ BOSUN_DESKTOP_WAIT_S = '60' }
  $clock = [System.Diagnostics.Stopwatch]::StartNew()
  $run = Wait-Child (Start-Child @('-File', $script, '-Verify', $image) $busy)
  Check "-Verify of a known pair exits 0 while the lock is held, without waiting (exit $($run.Code))" ($run.Code -eq 0 -and $clock.Elapsed.TotalSeconds -lt 20)
  $clock.Restart()
  $run = Wait-Child (Start-Child @('-File', $script, '-Path', $shell) $busy)
  Check "a capture with no -Out exits 2 while the lock is held, without waiting (exit $($run.Code))" ($run.Code -eq 2 -and $clock.Elapsed.TotalSeconds -lt 20)
  $holder = Start-Holder $name (Join-Path $dir 'h5')
  $clock.Restart()
  $run = Wait-Child (Start-Child @('-File', $script, '-SelfTest') $busy) 600
  Check "the self-test passes while a holder holds the caller's lock (exit $($run.Code), $([int]$clock.Elapsed.TotalSeconds) s)" ($run.Code -eq 0 -and $run.Said -match '(?m)^self-test: all pass\s*$')
  New-Item -ItemType File -Path (Join-Path $dir 'h5\release') | Out-Null
  $null = Wait-Child $holder

  # A capture waiting for the lock stops at once when stopped, as Ctrl+C stops it (both act only between calls), and
  # releases its pair: a capture to the same file right after isn't refused as another capture writing it (round 1).
  $holder = Start-Holder $name (Join-Path $dir 'h6')
  $saved = @{}
  foreach ($k in 'BOSUN_DESKTOP_LOCK', 'BOSUN_DESKTOP_WAIT_S') { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
  $stopOut = Join-Path $dir 'k.png'
  $ps = [PowerShell]::Create()
  try {
    $env:BOSUN_DESKTOP_LOCK = $name
    $env:BOSUN_DESKTOP_WAIT_S = '120'
    [void]$ps.AddCommand($script).AddParameter('Path', $shell).AddParameter('LaunchArgs', '-NoProfile -Command Start-Sleep 30').AddParameter('NoNewWindow').AddParameter('WaitSeconds', 1).AddParameter('Out', $stopOut)
    $async = $ps.BeginInvoke()
    Start-Sleep -Seconds 3
    $clock.Restart()
    $ps.Stop()
    Check "a capture waiting for the lock stops when stopped ($([Math]::Round($clock.Elapsed.TotalSeconds, 1)) s)" ($async.IsCompleted -and $clock.Elapsed.TotalSeconds -lt 5)
  } finally {
    $ps.Dispose()
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
  }
  New-Item -ItemType File -Path (Join-Path $dir 'h6\release') | Out-Null
  $null = Wait-Child $holder
  $run = Wait-Child (Start-Capture $stopOut $lockEnv 1)
  Check "and its pair was released: the same file captures next (exit $($run.Code))" ($run.Code -eq 1 -and $run.Said -like '*no window appeared*') $run.Said

  # The help says what the lock is, where its record is and what exit 4 means.
  $help = Get-Help $script -Full | Out-String
  Check 'the help names the record path and exit 4' ($help -like '*\Bosun\run\<session>\desktop\bosun-desktop.json*' -and $help -like '*4 the desktop lock was not free within its wait*')
} finally {
  foreach ($p in $started) { if (-not $p.HasExited) { & taskkill.exe /T /F /PID $p.Id *> $null } }
  # Every holder here released its lock, or was killed and its successor's release cleared the record: one left over
  # is a fault of the lock's, said, not tidied away.
  foreach ($n in $name, $other) {
    $left = [Bosun.Desktop.DesktopLock]::RecordPath($n, $folder)
    Check "no record is left for $n" (-not (Test-Path -LiteralPath $left))
  }
  try { Remove-Item -LiteralPath $dir -Recurse -Force } catch { "the test's folder $dir was left behind: $($_.Exception.Message)" }
}
if ($failed.Count) { "$($failed.Count) check(s) failed"; exit 1 }
'all passed'
