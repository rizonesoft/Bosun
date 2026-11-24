# Integration test for the build queue. Uses a private slot namespace, so it does not touch real builds.
param([string]$Build = "$PSScriptRoot\..\build\Release", [switch]$SkipWsl)
$ErrorActionPreference = 'Stop'
# A suite run through `bosun run` hands this test the slot marker, and a job that has it passes straight through
# without queueing, so every check of the queue would see no queue: this test starts outside any slot.
Remove-Item Env:BOSUN_SLOT -ErrorAction Ignore
$bosun = Join-Path $Build 'bosun.exe'
$sleeper = Join-Path $Build 'test_sleeper.exe'
$ns = "BosunTest$PID"
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$work = New-BosunScratchFolder -Name 'queue-test'
try {
# The slot records go to a home of the test's own (D01 T01 §1), so none of this machine's own jobs is read or touched.
$env:LOCALAPPDATA = Join-Path $work 'home'
# The namespace's folder: upper-case letters as %XX (dm::ns_dir), so names differing only in case stay apart.
$nsDir = 'ns-' + (-join ($ns.ToCharArray() | ForEach-Object { if ($_ -cmatch '[a-z0-9_-]') { $_ } else { '%{0:X2}' -f [int]$_ } }))
$recDir = Join-Path $env:LOCALAPPDATA "Bosun\run\$((Get-Process -Id $PID).SessionId)\$nsDir"
function Records { @(Get-ChildItem -LiteralPath $recDir -Filter '*.json' -ErrorAction Ignore) }
# A record is replaced whole while jobs run, and a reader that opens it in the moment of the replace is refused (a
# sharing violation): the queue's own reader skips it and reads it on its next pass (src/app.cpp, read_pass). A test
# reads as a reader should, sharing delete and asking again for up to a second; on 2026-11-16 a single Get-Content
# lost that race on CI and failed the run. A record that is gone is $null.
function Read-Text([string]$path) {
  foreach ($try in 1..20) {
    try {
      $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
      try { return ([IO.StreamReader]::new($stream)).ReadToEnd() } finally { $stream.Dispose() }
    } catch [IO.FileNotFoundException] { return $null }
    catch [IO.DirectoryNotFoundException] { return $null }
    catch [IO.IOException] { Start-Sleep -Milliseconds 50 }  # held in the moment of a replace: asked again
  }
  throw "can't read $path after a second of trying"
}
function Read-Rec([string]$name) { $text = Read-Text (Join-Path $recDir $name); if ($text) { $text | ConvertFrom-Json -DateKind String } }  # started_at stays the text written
# The job log's lines (D01 T01 §2), read back from the test's home; -DateKind String keeps the times as written.
function Job-Lines { @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Bosun\logs') -Filter 'jobs-*.jsonl' -ErrorAction Ignore | ForEach-Object { Get-Content -LiteralPath $_.FullName } | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json -DateKind String }) }
function Job-Line([string]$like) { @(Job-Lines | Where-Object { $_.command -like $like }) }
function Wait-Until([scriptblock]$ok, [int]$ms = 10000) { foreach ($i in 1..($ms / 100)) { if (& $ok) { return $true }; Start-Sleep -Milliseconds 100 }; [bool](& $ok) }
$fail = 0
# Start-Process joins its arguments without quoting, so paths are quoted here.
function Q($s) { "`"$s`"" }
function Check($ok, $name) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }
function MaxOverlap($file) {
  $events = Get-Content -LiteralPath $file | ForEach-Object { $p = $_ -split ' '; [pscustomobject]@{ T = [uint64]$p[1]; D = $(if ($p[0] -eq 'start') { 1 } else { -1 }) } }
  $cur = 0; $max = 0
  foreach ($e in ($events | Sort-Object T, D)) { $cur += $e.D; if ($cur -gt $max) { $max = $cur } }
  $max
}

# Six jobs on two slots never overlap by more than two, and all of them finish.
$f = Join-Path $work 'overlap.txt'
$jobs = 1..6 | ForEach-Object { Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q $f),'800' -PassThru -WindowStyle Hidden }
$jobs | Wait-Process -Timeout 60
$o = MaxOverlap $f
Check ($o -eq 2) "6 jobs on 2 slots overlap by exactly 2 (got $o)"
Check ((Get-Content -LiteralPath $f | Where-Object { $_ -like 'end*' }).Count -eq 6) 'all 6 jobs finished'

# The memory gate holds slot 1 shut, so jobs run one at a time.
$f = Join-Path $work 'gate.txt'
$jobs = 1..3 | ForEach-Object { Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','99999999','--',(Q $sleeper),(Q $f),'500' -PassThru -WindowStyle Hidden }
$jobs | Wait-Process -Timeout 60
$o = MaxOverlap $f
Check ($o -eq 1) "memory gate serialises jobs (got $o)"

# The exit code passes through.
& $bosun run --ns $ns -- $sleeper (Join-Path $work 'code.txt') 1 37
Check ($LASTEXITCODE -eq 37) "exit code passes through (got $LASTEXITCODE)"

# A batch file runs, and a metacharacter in an argument does not start a second command.
$bat = Join-Path $work 'echo.cmd'
$marker = Join-Path $work 'injected.txt'
# The batch quotes its own argument: an unquoted %~1 would inject inside the batch, whatever bosun does.
Set-Content -LiteralPath $bat '@echo "%~1"'
$out = & $bosun run --ns $ns -- $bat "x&echo hi>$marker" 2>&1 | Out-String
$code = $LASTEXITCODE
Check (-not (Test-Path -LiteralPath $marker)) 'batch argument cannot inject a command'
Check ($code -eq 0) "batch file runs (exit $code)"
Check ($out.Contains("x&echo hi>$marker")) "batch argument arrives intact (got $($out.Trim()))"

# A batch file in a folder with spaces launches, as npm.cmd in Program Files must.
$spaced = Join-Path $work 'dir with spaces'
New-Item -ItemType Directory -Path $spaced | Out-Null
$spacedBat = Join-Path $spaced 'echo.cmd'
Set-Content -LiteralPath $spacedBat '@echo "%~1"'
$out = & $bosun run --ns $ns -- $spacedBat 'hello world' 2>&1 | Out-String
Check (($LASTEXITCODE -eq 0) -and ($out -like '*hello world*')) "batch file in a spaced path runs (got $($out.Trim()))"

# %VAR% in an argument stays literal. The variable name ends in ^ to catch caret-style escaping too.
$percentMarker = Join-Path $work 'percent.txt'
[Environment]::SetEnvironmentVariable('DM_REVIEW^', "x&echo hi>$percentMarker", 'Process')
[Environment]::SetEnvironmentVariable('DM_REVIEW', "x&echo hi>$percentMarker", 'Process')
$out = & $bosun run --ns $ns -- $bat '%DM_REVIEW%' 2>&1 | Out-String
Check (-not (Test-Path -LiteralPath $percentMarker)) 'a percent expression cannot inject a command'
Check ($out -like '*%DM_REVIEW%*') "a percent expression stays literal (got $($out.Trim()))"
[Environment]::SetEnvironmentVariable('DM_REVIEW^', $null, 'Process')
[Environment]::SetEnvironmentVariable('DM_REVIEW', $null, 'Process')

# An embedded quote cannot end the quoting early.
$quoteMarker = Join-Path $work 'quote.txt'
$null = & $bosun run --ns $ns -- $bat "a`"&echo hi>$quoteMarker&`"b" 2>&1
Check (-not (Test-Path -LiteralPath $quoteMarker)) 'an embedded quote cannot inject a command'

# The logger attributes a process, and its child, to the git repo holding the working directory.
$repoName = "bosuntestrepo$PID"
$repo = Join-Path $work $repoName
New-Item -ItemType Directory -Path (Join-Path $repo '.git') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repo 'sub') | Out-Null
$sub = Join-Path $repo 'sub'
function Start-HiddenIn([string]$file, [string[]]$arguments, [string]$cwd) {
  $info = [Diagnostics.ProcessStartInfo]::new($file)
  $info.Arguments = $arguments -join ' '
  $info.WorkingDirectory = $cwd
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  [Diagnostics.Process]::Start($info)
}
$held = Start-HiddenIn $sleeper @((Q (Join-Path $work 'repo.txt')), '8000') $sub
Start-Sleep -Milliseconds 500
$savedLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $work 'local'
& $bosun log --once
$logFile = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Bosun\logs') -Filter 'windows-*.csv' | Select-Object -First 1
$repoRow = Get-Content -LiteralPath $logFile | Where-Object { $_ -match ",win,repo,[^,]*$repoName," }
$env:LOCALAPPDATA = $savedLocal
Stop-Process -Id $held.Id -Force
Check ([bool]$repoRow) "logger attributes a process to its git repo (row: $repoRow)"

# A missing command reports 127 rather than hanging.
& $bosun run --ns $ns -- no-such-program-bosun 2>$null
Check ($LASTEXITCODE -eq 127) "missing command exits 127 (got $LASTEXITCODE)"
Check (-not (Records)) "and a command that couldn't start leaves no record, checked at once ($((Records).Name -join ', '))"

# Killing a slot holder frees the slot and kills its job tree.
$f = Join-Path $work 'crash.txt'
$holder = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q $f),'60000' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 700
Check ((& $bosun status --ns $ns --slots 1 | Select-Object -First 1) -eq 'slots busy: 1 of 1') 'status shows the held slot'
# Filtered by name because a hidden console also gets a conhost.exe child.
$childPids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($holder.Id) AND Name='test_sleeper.exe'" | ForEach-Object ProcessId)
Check ($childPids.Count -eq 1) "the job's child was found ($($childPids.Count))"
Stop-Process -Id $holder.Id -Force
Start-Sleep -Milliseconds 300
Check (-not (Get-Process -Id $childPids[0] -ErrorAction SilentlyContinue)) 'killing the holder kills the running child'
$started = Get-Date
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 5 -- $sleeper (Join-Path $work 'after.txt') 1
Check (((Get-Date) - $started).TotalSeconds -lt 4) 'a crashed holder frees its slot at once'

# max-wait starts the job anyway instead of stalling forever.
$f = Join-Path $work 'wait.txt'
$holder = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q $f),'6000' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$started = Get-Date
& $bosun run --ns $ns --slots 1 --max-wait 1 -- $sleeper (Join-Path $work 'wait2.txt') 1 2>$null
$t = ((Get-Date) - $started).TotalSeconds
Check ($t -lt 4) "max-wait lets a job through (took $([int]$t) s)"
Stop-Process -Id $holder.Id -Force

# --or-skip never starts a job without its place or its memory, and says so with exit 75.
$holder = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'skip0.txt')),'6000' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- $sleeper (Join-Path $work 'skip1.txt') 1 2>$null
Check ($LASTEXITCODE -eq 75) "--or-skip exits 75 while the place is held (got $LASTEXITCODE)"
Check (-not (Test-Path -LiteralPath (Join-Path $work 'skip1.txt'))) 'a skipped job never starts'
Stop-Process -Id $holder.Id -Force
Start-Sleep -Milliseconds 300
& $bosun run --ns $ns --slots 1 --min-avail-mb 99999999 --max-wait 1 --or-skip -- $sleeper (Join-Path $work 'skip2.txt') 1 2>$null
Check ($LASTEXITCODE -eq 75) "--or-skip holds the memory floor for the first slot too (got $LASTEXITCODE)"
Check (-not (Test-Path -LiteralPath (Join-Path $work 'skip2.txt'))) 'a job skipped for memory never starts'
$lone = & $bosun run --ns $ns --slots 1 --min-avail-mb 99999999 --max-wait 1 -- cmd /c 'echo %BOSUN_SLOT%' 2>$null
Check ("$lone".Trim() -eq '0') "without --or-skip the first slot still admits a lone job (slot $lone)"
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- $sleeper (Join-Path $work 'skip4.txt') 1
Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $work 'skip4.txt'))) '--or-skip runs the job when its place is free'
Check (@(Records | Where-Object Name -like 'wait-*').Count -eq 0) 'a skipped job leaves no waiter record'

# A job in one namespace takes its place in another, and still passes through in its own.
$other = "${ns}Other"
$holder = Start-Process $bosun -ArgumentList 'run','--ns',$other,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'other0.txt')),'6000' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$cross = Join-Path $work 'cross.cmd'
Set-Content -LiteralPath $cross "@`"$bosun`" run --ns $other --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- `"$sleeper`" `"$work\cross.txt`" 1`r`n@exit /b %ERRORLEVEL%"
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $cross 2>$null
Check ($LASTEXITCODE -eq 75) "a job inside one namespace waits for its place in another (got $LASTEXITCODE)"
Check (-not (Test-Path -LiteralPath "$work\cross.txt")) 'the job in the other namespace did not start'
Stop-Process -Id $holder.Id -Force

# A job that timed out and started anyway holds nothing, so a job inside it that must not start without its place doesn't.
$holder = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'late0.txt')),'8000' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$inner = Join-Path $work 'inner.cmd'
Set-Content -LiteralPath $inner "@`"$bosun`" run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 --or-skip -- `"$sleeper`" `"$work\inner.txt`" 1`r`n@exit /b %ERRORLEVEL%"
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 -- $inner 2>$null
Check ($LASTEXITCODE -eq 75 -and -not (Test-Path -LiteralPath "$work\inner.txt")) "a job that started without a place lends none to a job inside it (got $LASTEXITCODE)"
Stop-Process -Id $holder.Id -Force
& $bosun run --ns 'A;B' --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'sep.txt') 1 2>$null
Check ($LASTEXITCODE -eq 64) "a namespace holding the separator is refused (got $LASTEXITCODE)"

# A nested run passes straight through instead of deadlocking on its own slot.
$nested = Join-Path $work 'nested.cmd'
Set-Content -LiteralPath $nested "@`"$bosun`" run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 0 -- `"$sleeper`" `"$work\nested.txt`" 1"
$job = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $nested) -PassThru -WindowStyle Hidden
Check ($job.WaitForExit(10000)) 'nested run does not deadlock'
Check ($job.ExitCode -eq 0) "nested run exits 0 (got $($job.ExitCode))"
Check ((Get-Content -LiteralPath "$work\nested.txt") -match '^end') 'the nested command really ran'

# A child that writes without end is stopped at the write budget while the command itself sits idle, as a test suite
# does while a fake reviewer it started floods a log: the shape that filled C: on 2026-11-08.
$flood = Join-Path $work 'flood.py'
Set-Content -LiteralPath $flood @'
import subprocess, sys, time
if sys.argv[1] == "child":
    # 4 GB at most, so a broken budget fails this test instead of filling the disk.
    with open(sys.argv[2], "wb") as out:
        for _ in range(4096):
            out.write(b"x" * 1048576)
            out.flush()
    time.sleep(120)
subprocess.Popen([sys.executable, __file__, "child", sys.argv[1]])
time.sleep(120)
'@
$python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
$started = Get-Date
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-write-gb 1 -- $python $flood (Join-Path $work 'flood.bin') 2>$null
$code = $LASTEXITCODE
$t = ((Get-Date) - $started).TotalSeconds
$size = (Get-Item (Join-Path $work 'flood.bin')).Length
Check ($code -eq 74) "a job over its write budget exits 74 (got $code)"
Check ($t -lt 60) "it is stopped well before the command's own 120 s (took $([int]$t) s)"
Check ($size -lt 3GB) "the runaway child was stopped near the budget ($([math]::Round($size / 1GB, 1)) GB written)"
Start-Sleep -Milliseconds 500
Check ((Get-Item (Join-Path $work 'flood.bin')).Length -eq $size) 'nothing is still writing after the job ends'
Remove-Item -LiteralPath (Join-Path $work 'flood.bin')

# --- The slot records (D01 T01 §1): who holds each slot and who waits, read from the real files ---
Check (-not (Records)) "every job above left no record behind, the command that couldn't start and the one the write budget stopped included ($((Records).Name -join ', '))"

# A slot above a lowered limit (D03 T02 §3): two jobs hold both slots under --slots 2; asked with --slots 1, status still
# lists the second slot, its holder named and the slot marked above the limit, and a job started under --slots 1 waits
# for a slot rather than taking the second. The holders run out by themselves, so no record is left behind.
$hi = 0..1 | ForEach-Object { Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work "high$_.txt")),'6000' -PassThru -WindowStyle Hidden }
$null = Wait-Until { @(Records | Where-Object Name -like 'slot-*').Count -eq 2 }
$st = & $bosun status --ns $ns --slots 1 --json | ConvertFrom-Json
$above = @($st.slots | Where-Object index -eq 1)
Check ($st.slots.Count -eq 2 -and -not $st.slots[0].above_limit -and $above.Count -eq 1 -and $above[0].above_limit -and $above[0].busy -and $hi.Id -contains $above[0].holder.pid) "a holder of a slot above a lowered limit is still listed, marked above it ($($st.slots.Count) slots)"
# Its record gone (a holder that could not write one, or one removed), the slot is still found by its mutex.
$highRecord = Join-Path $recDir 'slot-1.json'
Remove-Item -LiteralPath $highRecord -ErrorAction Ignore
$st = & $bosun status --ns $ns --slots 1 --json | ConvertFrom-Json
$above = @($st.slots | Where-Object index -eq 1)
Check ($st.slots.Count -eq 2 -and $above.Count -eq 1 -and $above[0].above_limit -and $above[0].busy -eq $true) "a slot above the limit is found by its mutex with its record gone ($($st.slots.Count) slots)"
$late = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'late.txt')),'100' -PassThru -WindowStyle Hidden
$null = Wait-Until { @((& $bosun status --ns $ns --slots 1 --json | ConvertFrom-Json).waiters | Where-Object pid -eq $late.Id).Count -eq 1 }
$st = & $bosun status --ns $ns --slots 1 --json | ConvertFrom-Json
$waiting = @($st.waiters | Where-Object pid -eq $late.Id)
Check ($waiting.Count -eq 1 -and $waiting[0].waiting_for -eq 'slot') "a job under the lowered limit waits for a slot, not the one above it"
$hi + $late | ForEach-Object { $null = $_.WaitForExit(20000); if (-not $_.HasExited) { & taskkill.exe /T /F /PID $_.Id *> $null } }
$null = Wait-Until { -not (Records) }

# A holder names its pid, command, repo and side while it runs; a second job waits with a record of its own; a third
# that times out runs without a slot and leaves nothing; and the waiter becomes the holder when the slot frees.
$recRepo = Join-Path $work 'recrepo'
New-Item -ItemType Directory -Path (Join-Path $recRepo '.git'), (Join-Path $recRepo 'sub') -Force | Out-Null
$h = Start-HiddenIn $bosun @('run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'rec1.txt')),'4000') (Join-Path $recRepo 'sub')
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$r = Read-Rec 'slot-0.json'
Check ($r.kind -eq 'holder' -and $r.pid -eq $h.Id -and $r.slot -eq 0 -and $r.side -eq 'windows' -and $r.ns -eq $ns -and $r.repo -eq ($recRepo -replace '\\', '/') -and $r.command -like '*test_sleeper.exe*rec1.txt* 4000' -and $r.process_start -gt 0 -and $r.started_at -match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$') "a holder's record names its pid, command, repo and side (want pid $($h.Id), repo $($recRepo -replace '\\', '/'); got $($r | ConvertTo-Json -Compress))"
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'rec2.txt')),'500' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
$waiter = @(Records | Where-Object Name -like 'wait-*')
Check ($waiter.Count -eq 1 -and (Read-Text $waiter[0].FullName | ConvertFrom-Json).kind -eq 'waiter' -and $waiter[0].Name -like "wait-$($w.Id)-*") "a job waiting for the slot has a waiter record ($($waiter.Name -join ', '))"
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 -- $sleeper (Join-Path $work 'rec3.txt') 200 2>$null
Check (@(Records | Where-Object Name -like 'wait-*').Count -eq 1 -and (Read-Rec 'slot-0.json').pid -eq $h.Id) 'a job that timed out ran without a slot and left no record, the holder''s untouched'
$took = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $w.Id } 15000
Check ($took -and -not @(Records | Where-Object Name -like 'wait-*')) 'when the slot frees, the waiter becomes the holder and its waiter record goes'
$null = $w.WaitForExit(15000)
Check ($null -ne $w.ExitCode -and -not (Records)) 'and once it ends, no record is left'

# A run inside a slot writes no record and leaves the outer holder's as it was.
$nestedRec = Join-Path $work 'nested-rec.cmd'
Set-Content -LiteralPath $nestedRec "@`"$bosun`" run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 0 -- `"$sleeper`" `"$work\nested-rec.txt`" 200`r`n@copy /y `"$($recDir -replace '%', '%%')\slot-0.json`" `"$work\outer-after-inner.json`" >nul" -Encoding ascii
$outer = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $nestedRec) -PassThru -WindowStyle Hidden
$null = $outer.WaitForExit(15000)
$kept = if (Test-Path -LiteralPath (Join-Path $work 'outer-after-inner.json')) { Get-Content -LiteralPath (Join-Path $work 'outer-after-inner.json') -Raw | ConvertFrom-Json }
Check ($kept.pid -eq $outer.Id -and -not (Records)) "a nested run leaves the outer holder's record as it was (outer $($outer.Id), read $($kept.pid))"

# Cleanup by construction: a killed holder's record is replaced by the next holder's, and a reader's pass (status)
# removes a dead waiter's record but never the new holder's or a live waiter's.
$a = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'reca.txt')),'60000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $a.Id }
Stop-Process -Id $a.Id -Force
$b = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'recb.txt')),'4000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $b.Id }
$gone = Start-Process $sleeper -ArgumentList (Q (Join-Path $work 'gone.txt')),'1' -PassThru -WindowStyle Hidden
$goneStart = $gone.StartTime.ToFileTimeUtc()
$null = $gone.WaitForExit(5000)
$liveStart = (Get-Process -Id $PID).StartTime.ToFileTimeUtc()
function Waiter-Json([int]$id, [long]$start) { "{`"schema_version`":1,`"kind`":`"waiter`",`"pid`":$id,`"process_start`":$start,`"started_at`":`"2026-11-13T00:00:00Z`",`"command`":`"x`",`"repo`":null,`"side`":`"windows`",`"session`":0,`"ns`":`"$ns`"}" }
New-Item -ItemType Directory -Force -Path $recDir | Out-Null  # so a publisher that wrote nothing still gets every check
Set-Content -LiteralPath (Join-Path $recDir "wait-$($gone.Id)-$goneStart.json") -Value (Waiter-Json $gone.Id $goneStart) -NoNewline
Set-Content -LiteralPath (Join-Path $recDir "wait-$PID-$liveStart.json") -Value (Waiter-Json $PID $liveStart) -NoNewline
& $bosun status --ns $ns --slots 1 | Out-Null
Check ((Read-Rec 'slot-0.json').pid -eq $b.Id -and -not (Test-Path -LiteralPath (Join-Path $recDir "wait-$($gone.Id)-$goneStart.json")) -and (Test-Path -LiteralPath (Join-Path $recDir "wait-$PID-$liveStart.json"))) "a reader's pass removes a dead waiter's record only: the new holder's and a live waiter's stay (b $($b.Id); files: $((Records).Name -join ', '); holder $((Read-Rec 'slot-0.json').pid))"
Remove-Item -LiteralPath (Join-Path $recDir "wait-$PID-$liveStart.json")
$null = $b.WaitForExit(15000)

# A waiter file a reader holds open (no delete sharing) as its job takes the slot, or as it times out, is not lost: the
# job retries, then a reader's pass drops a waiter its own process supersedes, and the job removes it when it ends.
function Lock-File([string]$path) { [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite') }
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'lock1.txt')),'2000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'lock2.txt')),'4000' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
$lockedPath = @(Records | Where-Object Name -like "wait-$($w.Id)-*")[0].FullName
$lock = Lock-File $lockedPath
try { $null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $w.Id } 15000 } finally { $lock.Dispose() }
& $bosun status --ns $ns --slots 1 | Out-Null
Check ((Read-Rec 'slot-0.json').pid -eq $w.Id -and -not (Test-Path -LiteralPath $lockedPath)) 'a waiter file held open as its job took the slot is dropped by the next reader, the job still holding'
$null = $w.WaitForExit(15000)
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'lock3.txt')),'6000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$t = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','2','--',(Q $sleeper),(Q (Join-Path $work 'lock4.txt')),'1500' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($t.Id)-*").Count -eq 1 }
$lockedPath = @(Records | Where-Object Name -like "wait-$($t.Id)-*")[0].FullName
$lock = Lock-File $lockedPath
try { Start-Sleep -Milliseconds 3000 } finally { $lock.Dispose() }  # past its 2 s wait: it runs unqueued, its waiter file held
$null = $t.WaitForExit(15000)
Check ($t.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $lockedPath)) 'a waiter file held open as its job timed out is removed by that job when it ends'
$null = $h.WaitForExit(15000)
Check (-not (Records)) "and nothing is left after either ($((Records).Name -join ', '))"

# Ctrl+Break (Ctrl+C can't reach another process group) ends a running holder's command, and bosun, which outlives it,
# removes its record before the slot frees.
$breakPy = Join-Path $work 'break.py'
Set-Content -LiteralPath $breakPy -Encoding ascii -Value @"
import json, os, signal, subprocess, sys, time
bosun, ns, sleeper, out, rec = sys.argv[1:6]
p = subprocess.Popen([bosun, 'run', '--ns', ns, '--slots', '1', '--min-avail-mb', '0', '--', sleeper, out, '60000'],
                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)  # sharing this console, so the break reaches it
for _ in range(100):
    try:
        if json.load(open(rec, encoding='utf-8'))['pid'] == p.pid: break
    except (OSError, ValueError, KeyError): pass
    time.sleep(0.1)
time.sleep(0.5)
held = os.path.exists(rec)
os.kill(p.pid, signal.CTRL_BREAK_EVENT)
try:
    code = p.wait(15)
except subprocess.TimeoutExpired:
    subprocess.run(['taskkill', '/T', '/F', '/PID', str(p.pid)], capture_output=True)
    code = 'hung'
print(json.dumps({'held': held, 'code': code, 'left': os.path.exists(rec)}))
"@
$python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
$broke = & $python $breakPy $bosun $ns $sleeper (Join-Path $work 'break.txt') (Join-Path $recDir 'slot-0.json') | ConvertFrom-Json
Check ($broke.held -and $broke.code -ne 'hung' -and -not $broke.left) "Ctrl+Break ends a holder's command and its record goes ($($broke | ConvertTo-Json -Compress))"

# Ctrl+Break while a job waits for a slot: it never runs its command, its waiting record goes, and it still leaves its
# line, with no slot, all of its time waiting and Windows' own exit code for an interruption (completion review).
$waitBreakPy = Join-Path $work 'waitbreak.py'
Set-Content -LiteralPath $waitBreakPy -Encoding ascii -Value @"
import glob, json, os, signal, subprocess, sys, time
bosun, ns, sleeper, out, holdout, recdir = sys.argv[1:7]
h = subprocess.Popen([bosun, 'run', '--ns', ns, '--slots', '1', '--min-avail-mb', '0', '--', sleeper, holdout, '20000'],
                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
rec = os.path.join(recdir, 'slot-0.json')
for _ in range(100):
    try:
        if json.load(open(rec, encoding='utf-8'))['pid'] == h.pid: break
    except (OSError, ValueError, KeyError): pass
    time.sleep(0.1)
p = subprocess.Popen([bosun, 'run', '--ns', ns, '--slots', '1', '--min-avail-mb', '0', '--', sleeper, out, '1'],
                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)  # sharing this console, so the break reaches it
mine = os.path.join(glob.escape(recdir), 'wait-%d-*.json' % p.pid)
for _ in range(100):
    if glob.glob(mine): break
    time.sleep(0.1)
waited = bool(glob.glob(mine))
time.sleep(1.5)
os.kill(p.pid, signal.CTRL_BREAK_EVENT)
try:
    code = p.wait(15)
except subprocess.TimeoutExpired:
    subprocess.run(['taskkill', '/T', '/F', '/PID', str(p.pid)], capture_output=True)
    code = 'hung'
left = bool(glob.glob(mine))
subprocess.run(['taskkill', '/T', '/F', '/PID', str(h.pid)], capture_output=True)
h.wait(15)
print(json.dumps({'waited': waited, 'code': code, 'left': left, 'pid': p.pid}))
"@
$wb = & $python $waitBreakPy $bosun $ns $sleeper (Join-Path $work 'waitbreak.txt') (Join-Path $work 'waitbreakhold.txt') $recDir | ConvertFrom-Json
$wl = Job-Line '*waitbreak.txt*'
Check ($wb.waited -and $wb.code -eq 3221225786 -and -not $wb.left -and -not (Test-Path -LiteralPath (Join-Path $work 'waitbreak.txt'))) "Ctrl+Break while waiting ends the job with 0xC000013A, its command never run and its waiting record gone ($($wb | ConvertTo-Json -Compress))"
Check ($wl.Count -eq 1 -and $wl[0].pid -eq $wb.pid -and $null -eq $wl[0].slot -and $wl[0].ran_s -eq 0 -and $wl[0].waited_s -ge 1.4 -and $wl[0].exit_code -eq -1073741510 -and -not $wl[0].timed_out_into_queue) "a job cancelled while waiting leaves one line: no slot, all waiting, its 0xC000013A ($($wl | ConvertTo-Json -Compress))"
Remove-Item -LiteralPath (Join-Path $recDir 'slot-0.json') -ErrorAction Ignore  # the killed holder's, which the next holder would replace

# --- The job history log (D01 T01 §2): one line per finished job, however it ended ---
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'j0.txt') 1
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'j37.txt') 1 37
$ok = Job-Line '*j0.txt*'
$bad = Job-Line '*j37.txt*'
Check ($ok.Count -eq 1 -and $ok[0].exit_code -eq 0 -and $ok[0].slot -eq 0 -and $ok[0].side -eq 'windows' -and $ok[0].ns -eq $ns -and -not $ok[0].timed_out_into_queue -and $ok[0].started_at -match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$' -and $ok[0].ended_at -match 'Z$' -and $ok[0].ran_s -ge 0 -and $ok[0].waited_s -ge 0 -and $ok[0].pid -gt 0) "a job that exits 0 leaves one line with its fields ($($ok | ConvertTo-Json -Compress))"
Check ($bad.Count -eq 1 -and $bad[0].exit_code -eq 37) "a job that exits 37 leaves its code ($($bad.exit_code))"
Check ((Job-Line '*no-such-program-bosun*').exit_code -contains 127) 'a command that could not start leaves its 127'
Check ((Job-Line '*flood.py*').exit_code -contains 74) 'a job stopped at its write budget leaves its 74'
$crash = Join-Path $work 'crash.py'
# A read of address 0 in C (ctypes would catch one and raise), so the process really dies of an access violation.
Set-Content -LiteralPath $crash -Value 'import faulthandler; faulthandler._read_null()' -Encoding ascii
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $python $crash 2>$null
Check ((Job-Line '*crash.py*').exit_code -contains -1073741819) "a command that crashes (an access violation) leaves its 0xC0000005 ($((Job-Line '*crash.py*').exit_code))"
Check ((Job-Line '*break.txt*').exit_code -contains -1073741510) "an interrupted command (Ctrl+Break) leaves its 0xC000013A ($((Job-Line '*break.txt*').exit_code))"
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'jhold.txt')),'4000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 -- $sleeper (Join-Path $work 'jtimed.txt') 1 2>$null
$timed = Job-Line '*jtimed.txt*'
Check ($timed.Count -eq 1 -and $timed[0].timed_out_into_queue -and $null -eq $timed[0].slot -and $timed[0].waited_s -ge 0.9) "a job that timed out into the queue says so, with no slot ($($timed | ConvertTo-Json -Compress))"
$null = $h.WaitForExit(15000)
Check ((Job-Line '*nested-rec.txt*').Count -eq 0 -and (Job-Line '*nested-rec.cmd*').Count -eq 1) 'a nested run leaves exactly one line, the outer job''s'

# Six jobs on six slots whose commands all wait at a barrier, released together: six whole lines, one for each.
$barrier = Join-Path $work 'barrier.py'
Set-Content -LiteralPath $barrier -Encoding ascii -Value @'
import os, sys, time
go = sys.argv[1]
for _ in range(300):
    if os.path.exists(go): break
    time.sleep(0.1)
'@
$go = Join-Path $work 'go'
$six = 1..6 | ForEach-Object { Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','6','--min-avail-mb','0','--',(Q $python),(Q $barrier),(Q $go),"six$_" -PassThru -WindowStyle Hidden }
$all = Wait-Until { @(Records | Where-Object Name -like 'slot-*').Count -eq 6 } 15000
Set-Content -LiteralPath $go -Value 'go'
$six | Wait-Process -Timeout 30 -ErrorAction Ignore
$lines = Job-Line '*barrier.py*'
$pids = @($lines | ForEach-Object pid | Sort-Object -Unique)
Check ($all -and $lines.Count -eq 6 -and $pids.Count -eq 6 -and -not (Compare-Object $pids @($six.Id | Sort-Object))) "six jobs released at once leave six whole lines, one each (held all six: $all; lines $($lines.Count), pids $($pids -join ','))"

# Job logs keep the memory logs' 14 days: bosun log --once prunes an old one and keeps today's.
$logs = Join-Path $env:LOCALAPPDATA 'Bosun\logs'
$old = Join-Path $logs "jobs-$((Get-Date).ToUniversalTime().AddDays(-20).ToString('yyyy-MM-dd')).jsonl"
Set-Content -LiteralPath $old -Value '{}'
$today = @(Get-ChildItem -LiteralPath $logs -Filter "jobs-$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')).jsonl")
& $bosun log --once
Check (-not (Test-Path -LiteralPath $old) -and $today.Count -eq 1 -and (Test-Path -LiteralPath $today[0].FullName)) 'bosun log --once prunes a job log 20 days old and keeps today''s'

# A job log that can't be written (a file where its folder would go) changes neither the exit code nor the queue.
$noLogHome = Join-Path $work 'no-log-home'
New-Item -ItemType Directory -Path (Join-Path $noLogHome 'Bosun') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $noLogHome 'Bosun\logs') -Value 'a file where the logs folder would go'
$savedHome = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $noLogHome
$out = & $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'jnolog.txt') 1 37 2>&1 | Out-String
$code = $LASTEXITCODE
$started = Get-Date
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 5 -- $sleeper (Join-Path $work 'jnolog2.txt') 1 2>$null
$next = $LASTEXITCODE
$took = ((Get-Date) - $started).TotalSeconds
$env:LOCALAPPDATA = $savedHome
Check ($code -eq 37 -and ([regex]::Matches($out, "can't add this job to the job log")).Count -eq 1 -and $next -eq 0 -and $took -lt 4) "a job log that can't be written leaves the exit code ($code), says so once, and the next job takes the slot ($next, $([int]$took) s)"

# A record that can't be written changes neither the queue nor the command's exit code, and says so once.
$blockedHome = Join-Path $work 'blocked-home'
New-Item -ItemType Directory -Path $blockedHome | Out-Null
Set-Content -LiteralPath (Join-Path $blockedHome 'Bosun') -Value 'a file where the Bosun folder would go'
$savedHome = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $blockedHome
$out = & $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'rec37.txt') 1 37 2>&1 | Out-String
$code = $LASTEXITCODE
$env:LOCALAPPDATA = $savedHome
Check ($code -eq 37 -and $out -match "can't publish this job's slot record" -and ([regex]::Matches($out, "can't publish")).Count -eq 1) "a record that can't be written leaves the exit code (got $code) and says so once"

# A records folder that can't be read (here a file where it would be) is said by status, never shown as an empty queue.
$unreadHome = Join-Path $work 'unread-home'
$unreadDir = Join-Path $unreadHome "Bosun\run\$((Get-Process -Id $PID).SessionId)"
New-Item -ItemType Directory -Path $unreadDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $unreadDir $nsDir) -Value 'a file where the records folder would go'
$env:LOCALAPPDATA = $unreadHome
$shown = & $bosun status --ns $ns --slots 1 | Out-String
$env:LOCALAPPDATA = $savedHome
Check ($shown -match 'slots busy: 0 of 1' -and $shown -match "records: unavailable \(.*can't be read\)") "status says the records can't be read ($($shown.Trim() -replace '\s+', ' '))"
$shown = & $bosun status --ns $ns --slots 1 | Out-String
Check ($shown -notmatch 'records:') "and says nothing of them when they can"

# --- status --json (D01 T01 §3): the panel's one read, parsed as the panel parses it ---
function Status-Json([string[]]$Extra = @()) {
  $text = (& $bosun status --ns $ns --json @Extra 2>$null | Out-String).Trim()
  $obj = $null
  # Text that isn't JSON leaves Obj null, which every check below reads as a failure; a refusal is checked by its Text.
  try { $obj = $text | ConvertFrom-Json -DateKind String } catch { $obj = $null }
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text; Obj = $obj }
}
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sj1.txt')),'5000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').command_started_at }
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'sj2.txt')),'200' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
Start-Sleep -Seconds 2  # so the holder has run, and the waiter waited, a measurable while
$s = Status-Json @('--slots', '1', '--min-avail-mb', '0')
$sh = $s.Obj.slots[0].holder
$sw = @($s.Obj.waiters | Where-Object pid -eq $w.Id)
$rec = Read-Rec 'slot-0.json'
Check ($s.Code -eq 0 -and $s.Obj.schema_version -eq 1 -and $s.Obj.ns -eq $ns -and @($s.Obj.slots).Count -eq 1 -and $s.Obj.slots[0].busy -eq $true) "status --json parses, versioned, with its one slot busy ($($s.Text))"
Check ($sh.pid -eq $h.Id -and $sh.phase -eq 'running' -and $sh.state -eq 'live' -and $sh.running_s -ge 1 -and $null -ne $sh.waited_s -and $sh.command -like '*sj1.txt*') "it names the holder, running for $($sh.running_s) s after waiting $($sh.waited_s) s"
Check ($rec.acquired_at -and $rec.command_started_at -and $rec.command_started_at -ge $rec.acquired_at) "the holder's record says when it took the slot and when its command started ($($rec.acquired_at), $($rec.command_started_at))"
Check ($sw.Count -eq 1 -and $sw[0].waiting_for -eq 'slot' -and $sw[0].waited_s -ge 1 -and $sw[0].min_avail_mb -eq 0 -and $sw[0].max_wait_s -eq 60 -and $sw[0].state -eq 'live') "it names the waiter, waiting for a slot ($($sw | ConvertTo-Json -Compress))"
Check ($s.Obj.memory.total_mb -gt 0 -and $s.Obj.memory.available_mb -le $s.Obj.memory.total_mb -and $s.Obj.memory.min_avail_mb -eq 0 -and $s.Obj.records.readable -eq $true -and $s.Obj.records.skipped -eq 0) "it gives this machine's memory and readable records"
$loggerRuns = [bool](Get-Process -Name 'bosun-logger' -ErrorAction Ignore)
Check ($s.Obj.logger.windows -eq $(if ($loggerRuns) { 'running' } else { 'not running' }) -and $null -eq $s.Obj.logger.wsl -and $null -eq $s.Obj.guest) "the Windows logger reads $($s.Obj.logger.windows), as Get-Process sees it, with no guest from a Windows call"
$null = $h.WaitForExit(15000); $null = $w.WaitForExit(15000)
$s = Status-Json @('--slots', '1', '--min-avail-mb', '0')
Check ($s.Obj.slots[0].busy -eq $false -and $null -eq $s.Obj.slots[0].holder -and @($s.Obj.waiters).Count -eq 0) "once they end, the slot reads free with no holder and nobody waits ($($s.Text))"

# A free slot held shut by its memory floor: the waiter says it waits for memory, under its own limits.
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sjm1.txt')),'5000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','99999999','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'sjm2.txt')),'200' -PassThru -WindowStyle Hidden
$null = Wait-Until { (@(Records | Where-Object Name -like "wait-$($w.Id)-*") | ForEach-Object { Read-Rec $_.Name }).waiting_for -eq 'memory' }
$s = Status-Json @('--slots', '2')
$sw = @($s.Obj.waiters | Where-Object pid -eq $w.Id)
Check ($sw.Count -eq 1 -and $sw[0].waiting_for -eq 'memory' -and $sw[0].min_avail_mb -eq 99999999 -and $s.Obj.slots[1].busy -eq $false) "a waiter held by slot 1's memory floor says it waits for memory ($($sw | ConvertTo-Json -Compress))"
& "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $w.Id 2>&1 | Out-Null
$null = $h.WaitForExit(15000)

# A reader holding the waiter's record open as its reason changes doesn't lose the change: the replace is retried, and
# a write still refused after that is kept and tried again until it lands (completion review), so the record is held
# open for 2.5 s, past the first second of retries. Both slots held, so it waits for a slot; slot 1's job ends while the
# record is held open, so it now waits for memory.
$h0 = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sjo0.txt')),'6000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h0.Id }
$h1 = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sjo1.txt')),'1500' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-1.json').pid -eq $h1.Id }
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','99999999','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'sjo2.txt')),'200' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
$wfile = (Records | Where-Object Name -like "wait-$($w.Id)-*" | Select-Object -First 1).FullName
$before = (Read-Rec (Split-Path -Leaf $wfile)).waiting_for
$held = [IO.File]::Open($wfile, 'Open', 'Read', 'Read')  # as .NET readers open it: no sharing for replacement
try { $null = $h1.WaitForExit(10000); Start-Sleep -Milliseconds 2500 } finally { $held.Dispose() }
$null = Wait-Until { (Read-Rec (Split-Path -Leaf $wfile)).waiting_for -eq 'memory' } 3000
$after = (Read-Rec (Split-Path -Leaf $wfile)).waiting_for
Check ($before -eq 'slot' -and $after -eq 'memory') "a waiter's reason changes from slot to memory even while a reader held its record open for 2.5 s (from '$before' to '$after')"
& "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $w.Id 2>&1 | Out-Null
$null = $h0.WaitForExit(15000)

# An unreadable records folder still gives the object, saying so, with the slots it could probe.
$env:LOCALAPPDATA = $unreadHome
$s = Status-Json @('--slots', '1')
$env:LOCALAPPDATA = $savedHome
Check ($s.Code -eq 0 -and $s.Obj.records.readable -eq $false -and @($s.Obj.slots).Count -eq 1 -and @($s.Obj.waiters).Count -eq 0) "with its records folder unreadable, status --json says so and still parses ($($s.Text))"

# Bad values are refused with exit 64 and nothing on stdout; the guest's figures come together or not at all.
$thirty = '1' * 30
foreach ($bad in @(@('--guest-avail-mb'), @('--guest-avail-mb', '-5', '--guest-total-mb', '9'), @('--guest-avail-mb', '5x', '--guest-total-mb', '9'), @('--guest-avail-mb', $thirty, '--guest-total-mb', '9'), @('--guest-avail-mb', '5'), @('--wsl-logger', 'maybe'), @('--slots', '12x'), @('--min-avail-mb', '-1'), @('--max-write-gb', '17179869184'))) {
  $s = Status-Json $bad
  Check ($s.Code -eq 64 -and $s.Text -eq '') "status --json $($bad -join ' ') is refused, exit 64 and nothing on stdout (got $($s.Code), '$($s.Text)')"
}
$s = Status-Json @('--guest-avail-mb', '5', '--guest-total-mb', '9', '--min-guest-mb', '3', '--wsl-logger', 'stopped')
Check ($s.Code -eq 0 -and $s.Obj.guest.available_mb -eq 5 -and $s.Obj.guest.total_mb -eq 9 -and $s.Obj.guest.min_guest_mb -eq 3 -and $s.Obj.logger.wsl -eq 'not running') "the WSL side's figures read back as given ($($s.Obj.guest | ConvertTo-Json -Compress), $($s.Obj.logger.wsl))"
# A limit in the environment is parsed as strictly as its flag: a bad one is said and the default kept.
$env:BOSUN_SLOTS = '3x'
$warned = & $bosun status --ns $ns --json 2>&1 | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | Out-String
$s = Status-Json
Remove-Item Env:BOSUN_SLOTS
Check (@($s.Obj.slots).Count -eq 2 -and $warned -match 'ignoring BOSUN_SLOTS=3x') "a bad BOSUN_SLOTS is said and the default of 2 slots kept ($($warned.Trim()))"

# --- report --json and the live snapshots (D01 T01 §4), from logs folders of the test's own ---
$repHome = Join-Path $work 'report-home'
$repLogs = Join-Path $repHome 'Bosun\logs'
New-Item -ItemType Directory -Path $repLogs -Force | Out-Null
$t1 = (Get-Date).ToUniversalTime().AddHours(-25)
$t2 = (Get-Date).ToUniversalTime().AddHours(-1)
$s1 = $t1.ToString('yyyy-MM-ddTHH:mm:ss'); $s2 = $t2.ToString('yyyy-MM-ddTHH:mm:ss')
$head = 'ts,side,kind,name,count,private_mb,rss_mb'
[IO.File]::WriteAllText((Join-Path $repLogs "windows-$($t1.ToString('yyyy-MM-dd')).csv"), "$head`n$s1,win,sys,available_mb,0,9000,0`n$s1,win,repo,D:/A,3,1000,800`n$s1,win,repo,D:/C,1,10,10`n")
[IO.File]::WriteAllText((Join-Path $repLogs "windows-$($t2.ToString('yyyy-MM-dd')).csv"), "$head`n$s2,win,sys,available_mb,0,1500,0`n$s2,win,repo,D:/A,4,3000,2500`n")
[IO.File]::WriteAllText((Join-Path $repLogs "wsl-$($t1.ToString('yyyy-MM-dd')).csv"), "$head`n$s1,wsl,sys,mem_available_mb,0,7000,0`n$s1,wsl,repo,/home/u/b%2Cc,2,4000,3000`n")
$jobLine = { param($end, $ran) '{"schema_version":1,"started_at":"' + $end + '","ended_at":"' + $end + '","pid":1,"command":"x","repo":null,"side":"windows","ns":"T","slot":0,"waited_s":0.0,"ran_s":' + $ran + ',"exit_code":0,"timed_out_into_queue":false}' }
$e1 = $t2.AddSeconds(10).ToString('yyyy-MM-ddTHH:mm:ssZ'); $e2 = $t2.AddSeconds(8).ToString('yyyy-MM-ddTHH:mm:ssZ')
[IO.File]::WriteAllText((Join-Path $repLogs "jobs-$($t2.ToString('yyyy-MM-dd')).jsonl"), ((& $jobLine $e1 '10.0') + "`n" + (& $jobLine $e2 '4.0') + "`n"))
function Report-Json([string]$home_) {
  $env:LOCALAPPDATA = $home_
  $text = (& $bosun report --json --days 7 2>$null | Out-String).Trim()
  $code = $LASTEXITCODE
  $env:LOCALAPPDATA = $savedHome
  $obj = $null
  # Text that isn't JSON leaves Obj null, which every check below reads as a failure.
  try { $obj = $text | ConvertFrom-Json -DateKind String } catch { $obj = $null }
  [pscustomobject]@{ Code = $code; Text = $text; Obj = $obj }
}
$rj = Report-Json $repHome
$ro = $rj.Obj
Check ($rj.Code -eq 0 -and $ro.schema_version -eq 1 -and $ro.days_covered -eq 2 -and $ro.read.complete -eq $true -and $ro.read.rejected_rows -eq 0 -and $ro.read.files -eq 4) "report --json covers two days from four files, all read ($($rj.Text.Substring(0, [Math]::Min(300, $rj.Text.Length))))"
Check ($ro.windows.min_available_mb -eq 1500 -and $ro.wsl.min_available_mb -eq 7000 -and $null -eq $ro.wsl.max_psi_full_avg10_x100 -and @($ro.series).Count -eq 2) "it gives each side's lowest memory, a null for what was never sampled, and one series entry per sampled minute"
Check ($ro.repos[0].repo -eq '/home/u/b,c' -and $ro.repos[0].peak_private_mb -eq 4000 -and $ro.repos[1].repo -eq 'D:/A' -and $ro.repos[1].peak_processes -eq 4 -and $ro.active_repos.max -eq 3 -and $ro.builds.max_overlap -eq 2 -and $ro.builds.jobs -eq 2) "it gives each repo's peaks, decoded, the repos active at once and the builds that overlapped ($($ro.repos | ConvertTo-Json -Compress), builds $($ro.builds | ConvertTo-Json -Compress))"
$env:LOCALAPPDATA = $repHome
$text = & $bosun report --days 7 | Out-String
$env:LOCALAPPDATA = $savedHome
Check ($text -match 'lowest available: 1500 MB') "the text report reads the same history"
# A line cut short by a sampler killed mid-write is counted, and a file that can't be opened makes the read incomplete.
$cut = Join-Path $repLogs "windows-$($t2.ToString('yyyy-MM-dd')).csv"
[IO.File]::AppendAllText($cut, "$s2,win,sys,avail")
$rj = Report-Json $repHome
Check ($rj.Obj.read.rejected_rows -eq 1 -and $rj.Obj.read.complete -eq $true) "a truncated last line is counted as one rejected row ($($rj.Obj.read | ConvertTo-Json -Compress))"
$lock = [IO.File]::Open($cut, 'Open', 'Read', 'None')
try { $rj = Report-Json $repHome } finally { $lock.Dispose() }
Check ($rj.Obj.read.complete -eq $false -and $rj.Obj.read.unreadable_files -eq 1 -and $rj.Obj.days_covered -eq 1) "a CSV held open with no sharing makes the read incomplete and is counted, its day left out ($($rj.Obj.read | ConvertTo-Json -Compress), $($rj.Obj.days_covered) day)"
$emptyHome = Join-Path $work 'empty-report-home'
New-Item -ItemType Directory -Path (Join-Path $emptyHome 'Bosun\logs') -Force | Out-Null
$rj = Report-Json $emptyHome
Check ($rj.Code -eq 0 -and $rj.Obj.days_covered -eq 0 -and @($rj.Obj.series).Count -eq 0 -and $null -eq $rj.Obj.windows.min_available_mb -and $rj.Obj.read.complete -eq $true) "an empty logs folder gives 0 days covered, an empty series and no made-up minimum"

# The Windows snapshot holds the same figures as its sample's CSV rows.
$snapHome = Join-Path $work 'snap-home'
$snapLogs = Join-Path $snapHome 'Bosun\logs'
$env:LOCALAPPDATA = $snapHome
& $bosun log --once --interval 7
$env:LOCALAPPDATA = $savedHome
$snapFile = Join-Path $snapLogs 'current-windows.json'
$snap = Get-Content -LiteralPath $snapFile -Raw | ConvertFrom-Json -DateKind String
$rows = @(Get-ChildItem -LiteralPath $snapLogs -Filter 'windows-*.csv' | ForEach-Object { Get-Content -LiteralPath $_.FullName } | Where-Object { $_ -like "$($snap.at.TrimEnd('Z')),*" } | ForEach-Object { , ($_ -split ',') })
$sys = @{}; foreach ($r in $rows) { if ($r[2] -eq 'sys') { $sys[$r[3]] = [int64]$r[5] } }
$memOk = $snap.memory.available_mb -eq $sys['available_mb'] -and $snap.memory.commit_mb -eq $sys['commit_mb'] -and $snap.memory.commit_limit_mb -eq $sys['commit_limit_mb'] -and $snap.memory.total_mb -gt 0 -and $(if ($sys.ContainsKey('pages_in_per_sec')) { $snap.memory.pages_in_per_sec -eq $sys['pages_in_per_sec'] } else { $null -eq $snap.memory.pages_in_per_sec })
$unname = { param($n) [regex]::Replace($n, '%([0-9A-F]{2})', { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) }) }
$repoRows = @($rows | Where-Object { $_[2] -eq 'repo' })
$repoOk = $repoRows.Count -eq @($snap.repos).Count -and -not @($repoRows | Where-Object { $n = & $unname $_[3]; $g = @($snap.repos | Where-Object repo -eq $n); $g.Count -ne 1 -or $g[0].processes -ne [int64]$_[4] -or $g[0].private_mb -ne [int64]$_[5] -or $g[0].rss_mb -ne [int64]$_[6] })
$procNames = @($rows | Where-Object { $_[2] -eq 'proc' } | ForEach-Object { & $unname $_[3] })
$procOk = ($procNames -join '|') -eq (@($snap.processes | ForEach-Object name) -join '|') -and $procNames.Count -gt 0
$vmRuns = [bool](Get-Process -Name 'vmmemWSL', 'vmmem' -ErrorAction Ignore)
Check ($snap.schema_version -eq 1 -and $snap.side -eq 'windows' -and $snap.interval_s -eq 7 -and $rows.Count -gt 0 -and $memOk) "current-windows.json gives the memory figures of its sample's CSV rows ($($snap.memory | ConvertTo-Json -Compress))"
Check ($repoOk -and $procOk) "and each repo's three numbers and the processes in the same order ($($repoRows.Count) repos, $($procNames.Count) processes)"
Check ($snap.wsl_vm.running -eq $vmRuns) "its WSL VM reads running $($snap.wsl_vm.running), as Get-Process sees vmmemWSL ($vmRuns)"
# Replaced, never written in place: a reader holding the old snapshot (sharing read and delete) still reads the old one.
$held = [IO.FileStream]::new($snapFile, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
try {
  $before = [IO.StreamReader]::new($held).ReadToEnd()
  Start-Sleep -Milliseconds 1100  # a later second, so the new snapshot's bytes differ
  $env:LOCALAPPDATA = $snapHome; & $bosun log --once; $env:LOCALAPPDATA = $savedHome
  $null = $held.Seek(0, 'Begin')
  $through = [IO.StreamReader]::new($held).ReadToEnd()
} finally { $held.Dispose() }
$after = Get-Content -LiteralPath $snapFile -Raw
Check ($before -eq $through -and $after -ne $before -and ($after | ConvertFrom-Json).side -eq 'windows') "a reader holding the old snapshot still reads the old bytes while the new one replaces it"
# Held without delete sharing, it can't be replaced: the old one stays, nothing is left behind, and the next sample lands.
$held = [IO.File]::Open($snapFile, 'Open', 'Read', 'Read')
try {
  Start-Sleep -Milliseconds 1100
  $env:LOCALAPPDATA = $snapHome; $said = & $bosun log --once 2>&1 | Out-String; $heldCode = $LASTEXITCODE; $env:LOCALAPPDATA = $savedHome
} finally { $held.Dispose() }
$kept = Get-Content -LiteralPath $snapFile -Raw
$temps = @(Get-ChildItem -LiteralPath $snapLogs -Filter '*.tmp')
Start-Sleep -Milliseconds 1100
$env:LOCALAPPDATA = $snapHome; & $bosun log --once; $env:LOCALAPPDATA = $savedHome
$next = Get-Content -LiteralPath $snapFile -Raw
Check ($heldCode -eq 0 -and $said -match "can't replace the live snapshot" -and $kept -eq $after -and $temps.Count -eq 0 -and $next -ne $kept) "a snapshot that can't be replaced keeps the old one, leaves no temporary file, and the next sample lands (exit $heldCode, $($temps.Count) left)"
Check (($next | ConvertFrom-Json).history_written -eq $true) "a sample whose CSV row was written says so in its snapshot"
# A history that can't be written (today's CSV held with no sharing) still gives a fresh snapshot that says so, the
# logger says it once and exits 0, and the next sample once released writes both (completion review).
$todayCsv = @(Get-ChildItem -LiteralPath $snapLogs -Filter 'windows-*.csv' | Sort-Object Name | Select-Object -Last 1)[0].FullName
$csvLock = [IO.File]::Open($todayCsv, 'Open', 'Read', 'None')
try {
  Start-Sleep -Milliseconds 1100
  $env:LOCALAPPDATA = $snapHome; $lost = & $bosun log --once 2>&1 | Out-String; $lostCode = $LASTEXITCODE; $env:LOCALAPPDATA = $savedHome
} finally { $csvLock.Dispose() }
$lostSnap = Get-Content -LiteralPath $snapFile -Raw | ConvertFrom-Json -DateKind String
$rowsBefore = @(Get-Content -LiteralPath $todayCsv).Count
Start-Sleep -Milliseconds 1100
$env:LOCALAPPDATA = $snapHome; & $bosun log --once; $env:LOCALAPPDATA = $savedHome
$back = Get-Content -LiteralPath $snapFile -Raw | ConvertFrom-Json -DateKind String
Check ($lostCode -eq 0 -and $lost -match 'history is being lost' -and $lostSnap.history_written -eq $false -and $lostSnap.at -ne ($next | ConvertFrom-Json -DateKind String).at -and $back.history_written -eq $true -and @(Get-Content -LiteralPath $todayCsv).Count -gt $rowsBefore) "a CSV that can't be written still gives a fresh snapshot saying history_written false, and the next sample writes both (exit $lostCode)"

# --- One config file for queue limits (D01 T01 §5) ---
function Cfg([string[]]$a) {
  $text = (& $bosun config @a 2>$null | Out-String).Trim()
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
}
function CfgJson([string[]]$a) {
  $r = Cfg $a
  $obj = $null
  # Text that isn't JSON leaves Obj null, which every check below reads as a failure.
  try { $obj = $r.Text | ConvertFrom-Json -DateKind String } catch { $obj = $null }
  [pscustomobject]@{ Code = $r.Code; Obj = $obj; Text = $r.Text }
}
# A home with no Bosun folder yet: the first save makes it.
$cfgHome = Join-Path $work 'cfg-home'
$env:LOCALAPPDATA = $cfgHome
$cfgFile = Join-Path $cfgHome 'Bosun\config.json'
$first = CfgJson @('set', 'slots', '1', '--json')
Check ($first.Code -eq 0 -and $first.Obj.schema_version -eq 1 -and $first.Obj.ok -eq $true -and $first.Obj.saved -eq 1 -and (Test-Path -LiteralPath $cfgFile)) "config set saves into a home with no Bosun folder yet ($($first.Text))"
$null = Cfg @('set', 'min_avail_mb', '1234'); $null = Cfg @('set', 'max_wait_s', '77'); $null = Cfg @('set', 'min_guest_mb', '555')
$g = (CfgJson @('get', '--json')).Obj
Check ($g.limits.slots.saved -eq 1 -and $g.limits.slots.source -eq 'file' -and $g.limits.min_avail_mb.value -eq 1234 -and $g.limits.max_wait_s.saved -eq 77 -and $g.limits.min_guest_mb.source -eq 'file' -and $g.valid -eq $true -and $g.exists -eq $true) "each key saved reads back as saved, from the file ($($g.limits | ConvertTo-Json -Compress))"
# Under an environment override, what is saved is still told apart from what is in effect.
$env:BOSUN_MIN_AVAIL_MB = '4000'
$null = Cfg @('set', 'min_avail_mb', '2222')
$e1 = (CfgJson @('get', '--json')).Obj.limits.min_avail_mb
$null = Cfg @('unset', 'min_avail_mb')
$e2 = (CfgJson @('get', '--json')).Obj.limits.min_avail_mb
Remove-Item Env:BOSUN_MIN_AVAIL_MB
$e3 = (CfgJson @('get', '--json')).Obj.limits.min_avail_mb
Check ($e1.value -eq 4000 -and $e1.source -eq 'environment' -and $e1.saved -eq 2222 -and $e2.source -eq 'environment' -and $null -eq $e2.saved -and $e3.source -eq 'default' -and $e3.value -eq 6144) "with BOSUN_MIN_AVAIL_MB set, a save and an unset change what is saved while the variable stays in effect"
# Refused values change nothing, byte for byte, and say which key.
$before = [IO.File]::ReadAllBytes($cfgFile)
foreach ($bad in @(@('slots', '0'), @('slots', '2x'), @('min_avail_mb', '-1'), @('nonsense', '1'))) {
  $r = CfgJson (@('set') + $bad + @('--json'))
  Check ($r.Code -eq 64 -and $r.Obj.schema_version -eq 1 -and $r.Obj.ok -eq $false -and $r.Obj.key -eq $bad[0] -and $r.Obj.error) "config set $($bad -join ' ') is refused with exit 64 and its key named ($($r.Text))"
}
Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($before)) "and the file is byte for byte as it was"
$null = Cfg @('unset', 'slots')
Check ((CfgJson @('get', '--json')).Obj.limits.slots.source -eq 'default') "config unset puts a key back to its default"
$lines = @((& $bosun config get max_wait_s min_guest_mb) | Where-Object { $_ })
Check ($lines.Count -eq 2 -and $lines[0] -eq 'max_wait_s=77' -and $lines[1] -eq 'min_guest_mb=555') "config get with keys prints one key=value line each, as the WSL shim reads them ($($lines -join '; '))"
# Saving safely: a file held with no sharing can't be read, so it is not written; one held sharing read only can't
# be replaced, so the old one stays with nothing left behind, and the save works once released.
$before = [IO.File]::ReadAllBytes($cfgFile)
$lock = [IO.File]::Open($cfgFile, 'Open', 'Read', 'None')
try { $r1 = CfgJson @('set', 'slots', '3', '--json') } finally { $lock.Dispose() }
$lock = [IO.File]::Open($cfgFile, 'Open', 'Read', 'Read')
try { $r2 = CfgJson @('set', 'slots', '3', '--json') } finally { $lock.Dispose() }
$leftTemps = @(Get-ChildItem -LiteralPath (Split-Path $cfgFile) -Filter '*.tmp').Count
$same = [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($before)
$r3 = CfgJson @('set', 'slots', '3', '--json')
Check ($r1.Code -eq 74 -and $r1.Obj.ok -eq $false -and $r2.Code -eq 74 -and $same -and $leftTemps -eq 0 -and $r3.Code -eq 0 -and (CfgJson @('get', '--json')).Obj.limits.slots.saved -eq 3) "a held config file is never half written: refused (exit $($r1.Code), $($r2.Code)), kept, nothing left behind, and saved once released"
# Two writers at once don't lose each other's change.
$w1 = Start-Process $bosun -ArgumentList 'config', 'set', 'max_wait_s', '11' -PassThru -WindowStyle Hidden
$w2 = Start-Process $bosun -ArgumentList 'config', 'set', 'min_guest_mb', '22' -PassThru -WindowStyle Hidden
$null = $w1.WaitForExit(15000); $null = $w2.WaitForExit(15000)
$both = (CfgJson @('get', '--json')).Obj.limits
Check ($both.max_wait_s.saved -eq 11 -and $both.min_guest_mb.saved -eq 22 -and $both.slots.saved -eq 3) "two config set at once both land ($($both.max_wait_s.saved), $($both.min_guest_mb.saved))"
# A damaged file is never overwritten, and never stops a build: the command runs on the defaults, saying so once.
Set-Content -LiteralPath $cfgFile -Value '{"schema_version":1,"slots":' -NoNewline
$damaged = [IO.File]::ReadAllBytes($cfgFile)
$r = CfgJson @('set', 'slots', '2', '--json')
$runOut = & $bosun run --ns $ns -- $sleeper (Join-Path $work 'cfgbad.txt') 1 37 2>&1 | Out-String
$runCode = $LASTEXITCODE
$g = (CfgJson @('get', '--json')).Obj
Check ($r.Code -eq 74 -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($damaged)) "config set refuses to overwrite a damaged file (exit $($r.Code)), leaving it as it was"
Check ($runCode -eq 37 -and ([regex]::Matches($runOut, 'ignoring .*config\.json')).Count -eq 1 -and $runOut -match 'slots uses 2 from the default' -and $g.valid -eq $false -and $g.limits.slots.source -eq 'default') "with a damaged config.json a build still runs on the defaults (exit $runCode), said once with what it uses"
# One refused key names what the command runs on instead (round 5).
Set-Content -LiteralPath $cfgFile -Value '{"schema_version":1,"slots":0,"max_wait_s":5}' -NoNewline
$keyOut = & $bosun status 2>&1 | Out-String
Check ($keyOut -match 'ignoring slots in .*config\.json: it must be a whole number from 1 to 64; slots uses 2 from the default' -and ([regex]::Matches($keyOut, 'ignoring')).Count -eq 1) "a refused key is said with the value and source it falls back to ($(($keyOut -split "`n")[0].Trim()))"
# Completion review: reads say what they ignored, an unknown key is refused in JSON, a file that can't be read is said
# and never overwritten, and a temporary file that can't be written changes nothing.
Set-Content -LiteralPath $cfgFile -Value '{"schema_version":1,"slots":3}' -NoNewline
$unknownKey = CfgJson @('get', 'nonsense', '--json')
$env:BOSUN_SLOTS = 'bad'
$badText = & $bosun config get slots 2>&1 | Out-String
$badJson = (CfgJson @('get', '--json')).Obj.limits.slots
Remove-Item Env:BOSUN_SLOTS
Check ($unknownKey.Code -eq 64 -and $unknownKey.Obj.schema_version -eq 1 -and $unknownKey.Obj.ok -eq $false -and $unknownKey.Obj.key -eq 'nonsense') "config get of an unknown key is refused in JSON with --json ($($unknownKey.Text))"
Check ($badText -match 'ignoring BOSUN_SLOTS=bad' -and $badText -match 'slots=3' -and $badJson.variable_error -match 'BOSUN_SLOTS=bad' -and $badJson.source -eq 'file') "config get says a bad BOSUN_SLOTS on both paths and reports the saved 3 it falls back to"
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$cfgDir = Split-Path $cfgFile
$before = [IO.File]::ReadAllBytes($cfgFile)
& icacls.exe $cfgFile /deny "${me}:(R)" | Out-Null
try {
  $unread = (CfgJson @('get', '--json')).Obj
  $unreadSave = CfgJson @('set', 'slots', '4', '--json')
} finally { & icacls.exe $cfgFile /reset | Out-Null }
Check ($unread.readable -eq $false -and $unread.limits.slots.source -eq 'default' -and $unreadSave.Code -eq 74 -and $unreadSave.Obj.error -match "can't be read" -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($before)) "a config.json that can't be read reads as unreadable, never as no settings, and is not overwritten (exit $($unreadSave.Code))"
& icacls.exe $cfgDir /deny "${me}:(AD,WD)" | Out-Null
try { $noTemp = CfgJson @('set', 'slots', '5', '--json') } finally { & icacls.exe $cfgDir /remove:d $me | Out-Null }
$tempsLeft = @(Get-ChildItem -LiteralPath $cfgDir -Filter '*.tmp').Count
$afterTemp = CfgJson @('set', 'slots', '5', '--json')
Check ($noTemp.Code -eq 74 -and $noTemp.Obj.ok -eq $false -and $noTemp.Obj.key -eq 'slots' -and $noTemp.Obj.error -match 'temporary file' -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -ne '' -and $tempsLeft -eq 0 -and $afterTemp.Code -eq 0) "a temporary file that can't be written changes nothing, leaves nothing, and the save works once allowed (exit $($noTemp.Code), $tempsLeft left)"
Check ([Convert]::ToBase64String($before) -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -and (CfgJson @('get', '--json')).Obj.limits.slots.saved -eq 5) "and the save once allowed is the one read back"

# Round 1: a file past 64 KB is refused whole without being loaded, and a save that can't own the writers' mutex
# changes nothing.
[IO.File]::WriteAllText($cfgFile, '{"schema_version":1,"slots":1,"pad":"' + ('x' * 70000) + '"}')
$bigOut = & $bosun status 2>&1 | Out-String
$g = (CfgJson @('get', '--json')).Obj
Check ($bigOut -match 'larger than 64 KB' -and $g.valid -eq $false -and $g.limits.slots.source -eq 'default') "a config.json past 64 KB is refused whole and the defaults apply"
# Round 3: a save that would write more than the reader takes is refused, so a save can never lock the limits out.
[IO.File]::WriteAllText($cfgFile, '{"schema_version":1,"slots":1,"future":"' + ('x' * 65490) + '"}')
$nearFull = [IO.File]::ReadAllBytes($cfgFile)
$over = CfgJson @('set', 'max_wait_s', '30', '--json')
$g = (CfgJson @('get', '--json')).Obj
Check ($nearFull.Length -le 65536 -and $over.Code -eq 74 -and $over.Obj.error -match '64 KB' -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($nearFull) -and $g.limits.slots.saved -eq 1) "a save that would pass 64 KB is refused and the file still reads ($($nearFull.Length) bytes, exit $($over.Code))"
Set-Content -LiteralPath $cfgFile -Value '{"schema_version":1,"slots":3}' -NoNewline
$before = [IO.File]::ReadAllBytes($cfgFile)
$holdMutex = [System.Threading.Mutex]::new($false, 'Local\Bosun.Config')
$null = $holdMutex.WaitOne()
try { $busy = CfgJson @('set', 'slots', '4', '--json') } finally { $holdMutex.ReleaseMutex(); $holdMutex.Dispose() }
Check ($busy.Code -eq 75 -and $busy.Obj.ok -eq $false -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($cfgFile)) -eq [Convert]::ToBase64String($before)) "a save that can't own the writers' mutex in 10 s changes nothing (exit $($busy.Code))"
$env:LOCALAPPDATA = $savedHome

# Saved limits govern real jobs (in the test's own home, so its records and job log are the ones read here).
$homeCfg = Join-Path $savedHome 'Bosun\config.json'
$minAvail = { (Status-Json).Obj.memory.min_avail_mb }
$d0 = & $minAvail
$null = Cfg @('set', 'min_avail_mb', '1234'); $d1 = & $minAvail
$env:BOSUN_MIN_AVAIL_MB = '4000'; $d2 = & $minAvail
$d3 = (Status-Json @('--min-avail-mb', '5000')).Obj.memory.min_avail_mb
Remove-Item Env:BOSUN_MIN_AVAIL_MB
Check ($d0 -eq 6144 -and $d1 -eq 1234 -and $d2 -eq 4000 -and $d3 -eq 5000) "the memory floor comes from the default, then the file, then the variable, then the flag ($d0, $d1, $d2, $d3)"
$null = Cfg @('unset', 'min_avail_mb')
foreach ($n in 1, 2) {
  $null = Cfg @('set', 'slots', "$n")
  $f = Join-Path $work "cfgslots$n.txt"
  $two = 1..2 | ForEach-Object { Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--min-avail-mb', '0', '--', (Q $sleeper), (Q $f), '800' -PassThru -WindowStyle Hidden }
  $two | Wait-Process -Timeout 60
  $o = MaxOverlap $f
  Check ($o -eq $n) "with slots $n saved, two jobs overlap by $n (got $o)"
}
$null = Cfg @('unset', 'slots')
# The saved wait: 2 s runs a job without a slot after about 2 s; 0 waits as long as it takes.
$h = Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--slots', '1', '--min-avail-mb', '0', '--', (Q $sleeper), (Q (Join-Path $work 'cfgw0.txt')), '6000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$null = Cfg @('set', 'max_wait_s', '2')
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'cfgw2.txt') 1 2>$null
$w2l = Job-Line '*cfgw2.txt*'
$env:BOSUN_MAX_WAIT = '1'
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'cfgwe.txt') 1 2>$null
Remove-Item Env:BOSUN_MAX_WAIT
$wel = Job-Line '*cfgwe.txt*'
Check ($w2l.Count -eq 1 -and $w2l[0].timed_out_into_queue -eq $true -and $w2l[0].waited_s -ge 1.5 -and $w2l[0].waited_s -lt 4.5 -and $wel.Count -eq 1 -and $wel[0].timed_out_into_queue -eq $true -and $wel[0].waited_s -lt 1.8) "a saved max_wait_s 2 runs a job without a slot after $($w2l.waited_s) s, and BOSUN_MAX_WAIT=1 beats it ($($wel.waited_s) s)"
$null = $h.WaitForExit(15000)
$h = Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--slots', '1', '--min-avail-mb', '0', '--', (Q $sleeper), (Q (Join-Path $work 'cfgz0.txt')), '4000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$null = Cfg @('set', 'max_wait_s', '0')
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 -- $sleeper (Join-Path $work 'cfgz.txt') 1 2>$null
$zl = Job-Line '*cfgz.txt*'
Check ($zl.Count -eq 1 -and $zl[0].timed_out_into_queue -eq $false -and $zl[0].slot -eq 0 -and $zl[0].waited_s -ge 3) "a saved max_wait_s 0 waits as long as it takes, then takes the slot (waited $($zl.waited_s) s)"
$null = $h.WaitForExit(15000)
$h = Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--slots', '1', '--min-avail-mb', '0', '--max-wait', '0', '--', (Q $sleeper), (Q (Join-Path $work 'cfgf0.txt')), '4000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 -- $sleeper (Join-Path $work 'cfgf.txt') 1 2>$null
$fl = Job-Line '*cfgf.txt*'
Check ($fl.Count -eq 1 -and $fl[0].timed_out_into_queue -eq $true -and $fl[0].waited_s -lt 1.8) "a typed --max-wait 1 beats the saved 0 ($($fl.waited_s) s)"
$null = $h.WaitForExit(15000)
# The saved memory floor holds slot 1 shut.
$null = Cfg @('set', 'max_wait_s', '60')
$null = Cfg @('set', 'min_avail_mb', '99999999')
$h = Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--slots', '2', '--', (Q $sleeper), (Q (Join-Path $work 'cfgm0.txt')), '5000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$w = Start-Process $bosun -ArgumentList 'run', '--ns', $ns, '--slots', '2', '--', (Q $sleeper), (Q (Join-Path $work 'cfgm1.txt')), '200' -PassThru -WindowStyle Hidden
$held = Wait-Until { (@(Records | Where-Object Name -like "wait-$($w.Id)-*") | ForEach-Object { Read-Rec $_.Name }).waiting_for -eq 'memory' }
Check ($held -and -not (Read-Rec 'slot-1.json')) "a saved min_avail_mb holds a second job off slot 1, waiting for memory"
& "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $w.Id 2>&1 | Out-Null
$null = $h.WaitForExit(15000)
$null = & $bosun status --ns $ns  # a reader's pass sweeps the killed waiter's record, as one does in use
Remove-Item -LiteralPath $homeCfg -ErrorAction Ignore  # the later cases set their own limits

# The nesting marker crosses into WSL, so a run on the other side does not wait on its own slot.
if ($SkipWsl) {
  'skip the nested WSL run: -SkipWsl was given'
} else {
  $shim = (wsl.exe -d Ubuntu -- wslpath -a (Join-Path $PSScriptRoot '..\wsl\bosun').Replace('\', '/')).Trim()
  $exeWsl = (wsl.exe -d Ubuntu -- wslpath -a $bosun.Replace('\', '/')).Trim()
  $job = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--','wsl.exe','-d','Ubuntu','--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','true' -PassThru -WindowStyle Hidden
  $done = $job.WaitForExit(20000)
  Check $done 'a nested run in WSL does not deadlock'
  if ($done) { Check ($job.ExitCode -eq 0) "the nested WSL run exits 0 (got $($job.ExitCode))" } else { Stop-Process -Id $job.Id -Force }

  # A top-level WSL run holds its slot through bosun.exe acquire, whose record names wsl, the Linux repo and the command.
  $linuxRepoWin = Join-Path $work "wsl-rec-$PID"
  New-Item -ItemType Directory -Path (Join-Path $linuxRepoWin '.git'), (Join-Path $linuxRepoWin 'sub') -Force | Out-Null
  $linuxRepo = (wsl.exe -d Ubuntu -- wslpath -a $linuxRepoWin.Replace('\', '/')).Trim()
  $savedWslenv = $env:WSLENV
  $env:WSLENV = (@($savedWslenv, 'LOCALAPPDATA') | Where-Object { $_ }) -join ':'  # the test's home reaches bosun.exe
  try {
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','sleep','4' -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').side -eq 'wsl' } 15000
    $r = Read-Rec 'slot-0.json'
    Check ($r.side -eq 'wsl' -and $r.repo -eq $linuxRepo -and $r.command -eq 'sleep 4' -and $r.kind -eq 'holder') "a top-level WSL run's record names wsl, the Linux repo and its command ($($r | ConvertTo-Json -Compress))"
    $done = $job.WaitForExit(20000)
    # bosun.exe acquire is a Windows process of its own, which removes the record a moment after the shim has gone.
    Check ($done -and (Wait-Until { -not (Records) } 10000)) "and it goes when the WSL job ends ($((Records).Name -join ', '))"

    # A Linux command longer than Windows allows a command line is still queued: its label is cut, never the slot. The
    # argument is built inside Linux, since Windows can't start wsl.exe with one that long either.
    $longRun = Join-Path $work 'long-run.sh'
    [IO.File]::WriteAllText($longRun, "long=`"`$(head -c 40000 /dev/zero | tr '\0' x)`"`nBOSUN_EXE=`"`$2`" exec bash `"`$1`" run --ns `"`$3`" --slots 1 --min-avail-mb 0 --max-wait 0 -- bash -c 'sleep 4' `"`$long`"`n")
    $longRunWsl = (wsl.exe -d Ubuntu -- wslpath -a $longRun.Replace('\', '/')).Trim()
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','bash',(Q $longRunWsl),(Q $shim),(Q $exeWsl),$ns -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').side -eq 'wsl' } 15000
    $r = Read-Rec 'slot-0.json'
    Check ($r.kind -eq 'holder' -and $r.command.Length -le 4096 -and $r.command.EndsWith(' [...]') -and $r.command.StartsWith('bash -c')) "a WSL command past the Windows limit still holds a slot, its label cut ($($r.command.Length) characters)"
    $wslLine = Job-Line 'sleep 4'
    Check ($wslLine.Count -eq 1 -and $wslLine[0].side -eq 'wsl' -and $wslLine[0].exit_code -eq 0 -and $wslLine[0].slot -eq 0) "a top-level WSL job leaves its line, with side wsl and its exit code ($($wslLine | ConvertTo-Json -Compress))"
    $longDone = $job.WaitForExit(20000)
    if (-not $longDone) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }  # its whole tree, so nothing outlives the test
    Check ($longDone -and (Wait-Until { -not (Records) } 10000)) "and that long job ends and its record goes ($((Records).Name -join ', '))"
    if (-not $done) { Stop-Process -Id $job.Id -Force }

    # A WSL job held for guest memory after its slot: its wait counts until the command really starts, and its run from
    # there. Slot 0 is taken, so it gets slot 1 and waits out --max-wait 3 for memory it can't have, then runs sleep 2.
    $h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'guesthold.txt')),'15000' -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--min-guest-mb','99999999','--max-wait','3','--','sleep','2' -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-1.json').side -eq 'wsl' } 15000
    # status --json from the shim, while that job waits for guest memory on slot 1 (D01 T01 §3).
    $gs = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" bash $shim status --ns $ns --slots 2 --json | Out-String) | ConvertFrom-Json -DateKind String
    $gh = $gs.slots[1].holder
    Check ($gh.side -eq 'wsl' -and $gh.phase -eq 'waiting for guest memory' -and $gh.min_guest_mb -eq 99999999 -and $null -eq $gh.running_s) "status --json shows a WSL job held for guest memory as such ($($gh | ConvertTo-Json -Compress))"
    $null = $job.WaitForExit(30000)
    $null = Wait-Until { (Job-Line 'sleep 2').Count -eq 1 } 10000
    $g = Job-Line 'sleep 2'
    Check ($g.Count -eq 1 -and $g[0].slot -eq 1 -and $g[0].waited_s -ge 2.5 -and $g[0].ran_s -ge 1.5 -and $g[0].ran_s -le 3.5) "a WSL job held for guest memory counts that as waiting, not running (waited $($g.waited_s) s, ran $($g.ran_s) s)"
    Stop-Process -Id $h.Id -Force -ErrorAction Ignore

    # The shim's status --json adds the guest's memory and its logger, and forwards the Windows floor as run does.
    $mem = @(wsl.exe -d Ubuntu -e awk '/^MemTotal:/ {t = int($2/1024)} /^MemAvailable:/ {a = int($2/1024)} END {print t; print a}' /proc/meminfo)
    $ws = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" BOSUN_MIN_AVAIL_MB=1234 bash $shim status --ns $ns --json | Out-String) | ConvertFrom-Json -DateKind String
    $total = [double]$mem[0]
    Check ($ws.guest.total_mb -gt 0 -and [math]::Abs($ws.guest.total_mb - $total) -le 0.1 * $total -and [math]::Abs($ws.guest.available_mb - [double]$mem[1]) -le 0.1 * $total) "the shim's status --json gives guest memory as /proc/meminfo does ($($ws.guest | ConvertTo-Json -Compress) against $($mem -join ', ') MB)"
    $typed = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" BOSUN_MIN_AVAIL_MB=1234 bash $shim status --ns $ns --json --min-avail-mb 5678 | Out-String) | ConvertFrom-Json -DateKind String
    Check ($ws.memory.min_avail_mb -eq 1234 -and $typed.memory.min_avail_mb -eq 5678) "it forwards BOSUN_MIN_AVAIL_MB, and a typed --min-avail-mb wins ($($ws.memory.min_avail_mb), then $($typed.memory.min_avail_mb))"
    $active = (wsl.exe -d Ubuntu -- systemctl --user is-active bosun-log.service 2>$null | Out-String).Trim()
    Check ($(if ($active -eq 'active') { $ws.logger.wsl -eq 'running' } else { $ws.logger.wsl -in 'not running', 'unknown' })) "the WSL logger reads $($ws.logger.wsl), as systemd says $active"
    # The shim reads whole numbers as bosun.exe does: leading zeros are fine, a suffix or a value past its bound is not.
    $null = wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" bash $shim run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 0000000003 -- true 2>&1
    $zeros = $LASTEXITCODE
    $null = wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" bash $shim run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 3x -- true 2>&1
    $suffix = $LASTEXITCODE
    $null = wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" bash $shim run --ns $ns --slots 1 --min-avail-mb 0 --max-write-gb 9999999999 -- true 2>&1
    $huge = $LASTEXITCODE
    Check ($zeros -eq 0 -and $suffix -eq 64 -and $huge -eq 64) "the shim takes --max-wait 0000000003 (exit $zeros) and refuses 3x (exit $suffix) and a budget past its bound (exit $huge)"

    # The WSL logger's snapshot (D01 T01 §4), read from Windows in the shared logs folder, as the panel reads it.
    $wslLogs = Join-Path $work 'wsl-logs'
    New-Item -ItemType Directory -Path $wslLogs -Force | Out-Null
    $wslLogsLinux = (wsl.exe -d Ubuntu -- wslpath -a $wslLogs.Replace('\', '/')).Trim()
    $logger = (wsl.exe -d Ubuntu -- wslpath -a (Join-Path $PSScriptRoot '..\wsl\bosun_log.py').Replace('\', '/')).Trim()
    wsl.exe -d Ubuntu -- python3 -I $logger $wslLogsLinux --once
    $cw = Read-Text (Join-Path $wslLogs 'current-wsl.json') | ConvertFrom-Json -DateKind String
    Check ($cw.schema_version -eq 1 -and $cw.side -eq 'wsl' -and [math]::Abs($cw.memory.total_mb - $total) -le 0.1 * $total -and $null -eq $cw.wsl_vm -and @(Get-ChildItem -LiteralPath $wslLogs -Filter '*.tmp').Count -eq 0) "the WSL logger's current-wsl.json reads from Windows, its total memory as /proc/meminfo gives ($($cw.memory | ConvertTo-Json -Compress))"

    # The shim takes the saved wait and guest floor (D01 T01 §5). Slot 0 held, so the job gets slot 1 and waits for guest
    # memory it can't have, for the saved 3 s rather than the shim's own 900.
    $null = & $bosun config set min_guest_mb 99999999
    $null = & $bosun config set max_wait_s 3
    $gs2 = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" bash $shim status --ns $ns --slots 2 --json | Out-String) | ConvertFrom-Json -DateKind String
    $h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'cfgwslhold.txt')),'30000' -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
    $cfgMark = "cfgwsl$PID"
    wsl.exe -d Ubuntu --cd "$linuxRepo/sub" -- env "BOSUN_EXE=$exeWsl" bash $shim run --ns $ns --slots 2 --min-avail-mb 0 -- bash -c 'exit 0' $cfgMark 2>&1 | Out-Null
    $null = Wait-Until { (Job-Line "*$cfgMark*").Count -eq 1 } 10000
    $cl = Job-Line "*$cfgMark*"
    $zeroMark = "cfgwslzero$PID"
    wsl.exe -d Ubuntu --cd "$linuxRepo/sub" -- env "BOSUN_EXE=$exeWsl" BOSUN_MIN_GUEST_MB=0 bash $shim run --ns $ns --slots 2 --min-avail-mb 0 -- bash -c 'exit 0' $zeroMark 2>&1 | Out-Null
    $null = Wait-Until { (Job-Line "*$zeroMark*").Count -eq 1 } 10000
    $zl = Job-Line "*$zeroMark*"
    $badMark = "cfgwslbad$PID"
    $badSaid = wsl.exe -d Ubuntu --cd "$linuxRepo/sub" -- env "BOSUN_EXE=$exeWsl" BOSUN_MAX_WAIT=abc bash $shim run --ns $ns --slots 2 --min-avail-mb 0 -- bash -c 'exit 0' $badMark 2>&1 | Out-String
    $null = Wait-Until { (Job-Line "*$badMark*").Count -eq 1 } 10000
    $bl = Job-Line "*$badMark*"
    Stop-Process -Id $h.Id -Force -ErrorAction Ignore
    Remove-Item -LiteralPath (Join-Path $recDir 'slot-0.json') -ErrorAction Ignore  # the stopped holder's
    Check ($gs2.guest.min_guest_mb -eq 99999999) "the shim's status --json reads the saved guest floor ($($gs2.guest.min_guest_mb))"
    # The shim's config reads see its Linux variables, as its jobs do (completion review).
    $linuxGet = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" BOSUN_SLOTS=1 BOSUN_MAX_WAIT=7 bash $shim config get slots max_wait_s min_guest_mb | Out-String).Trim() -split "`r?`n"
    $linuxJson = (wsl.exe -d Ubuntu -- env "BOSUN_EXE=$exeWsl" BOSUN_MIN_AVAIL_MB=1234 BOSUN_MIN_GUEST_MB=0 bash $shim config get --json | Out-String) | ConvertFrom-Json
    Check (($linuxGet -join ';') -eq 'slots=1;max_wait_s=7;min_guest_mb=99999999' -and $linuxJson.limits.min_avail_mb.value -eq 1234 -and $linuxJson.limits.min_avail_mb.source -eq 'environment' -and $linuxJson.limits.min_guest_mb.value -eq 0) "the shim's config get reports the Linux variables its jobs use ($($linuxGet -join '; '))"
    Check ($cl.Count -eq 1 -and $cl[0].slot -eq 1 -and $cl[0].waited_s -ge 2.5 -and $cl[0].waited_s -le 10) "a WSL job waits out the saved max_wait_s 3 for the saved guest floor (waited $($cl.waited_s) s)"
    Check ($zl.Count -eq 1 -and $zl[0].waited_s -lt 2.5) "with BOSUN_MIN_GUEST_MB=0 the same job doesn't wait (waited $($zl.waited_s) s)"
    Check ($bl.Count -eq 1 -and $badSaid -match 'ignoring BOSUN_MAX_WAIT=abc' -and $bl[0].waited_s -ge 2.5 -and $bl[0].waited_s -le 10) "a bad BOSUN_MAX_WAIT is said and skipped, so the saved wait still applies (waited $($bl.waited_s) s)"
    # A bosun.exe whose config get fails: the shim says so and runs on its own defaults.
    $fakeExe = Join-Path $work 'fake-bosun.sh'
    [IO.File]::WriteAllText($fakeExe, "#!/bin/bash`nif [ `"`$1`" = config ]; then exit 1; fi`nexec `"`$REAL_BOSUN`" `"`$@`"`n")
    $fakeWsl = (wsl.exe -d Ubuntu -- wslpath -a $fakeExe.Replace('\', '/')).Trim()
    $failSaid = wsl.exe -d Ubuntu --cd "$linuxRepo/sub" -- env "BOSUN_EXE=$fakeWsl" "REAL_BOSUN=$exeWsl" bash $shim run --ns $ns --slots 1 --min-avail-mb 0 -- true 2>&1 | Out-String
    $failCode = $LASTEXITCODE
    Check ($failCode -eq 0 -and $failSaid -match "can't read the saved limits") "when bosun.exe can't answer config get, the shim says so and runs on its defaults (exit $failCode)"
    Remove-Item -LiteralPath (Join-Path $savedHome 'Bosun\config.json') -ErrorAction Ignore

    # A WSL shim killed while its command runs: acquire still writes the line, with no exit code.
    $marker = "killmarker$PID"
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','bash','-c',(Q 'sleep 30'),$marker -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').side -eq 'wsl' } 15000
    wsl.exe -d Ubuntu -- pkill -KILL -f "wsl/bosun run .*$marker"
    $null = Wait-Until { (Job-Line "*$marker*").Count -eq 1 } 15000
    $k = Job-Line "*$marker*"
    Check ($k.Count -eq 1 -and $null -eq $k[0].exit_code -and $k[0].side -eq 'wsl') "a killed WSL shim's job still leaves its line, with no exit code ($($k | ConvertTo-Json -Compress))"
    if (-not $job.WaitForExit(10000)) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }
    wsl.exe -d Ubuntu -- pkill -KILL -f $marker

    # Its acquire stopped while the command runs: the shim lives on and keeps the command's own exit code (round 1).
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','bash','-c',(Q 'sleep 6; exit 37') -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').side -eq 'wsl' } 15000
    Stop-Process -Id (Read-Rec 'slot-0.json').pid -Force
    $done = $job.WaitForExit(30000)
    Check ($done -and $job.ExitCode -eq 37) "a WSL job whose acquire was stopped still exits with its command's 37 (got $($job.ExitCode))"
    if (-not $done) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }
    Remove-Item -LiteralPath (Join-Path $recDir 'slot-0.json') -ErrorAction Ignore  # the stopped acquire's, which the next holder would replace

    # A WSL job ended before its command started (here waiting for guest memory on slot 1): all of it was waiting.
    $h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'neverhold.txt')),'20000' -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
    $never = "nevermarker$PID"
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',(Q "$linuxRepo/sub"),'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--min-guest-mb','99999999','--max-wait','0','--','bash','-c',(Q 'exit 0'),$never -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-1.json').side -eq 'wsl' } 15000
    # The holder's record names the shim's own Linux process (D03 T02 §1), which is the `bosun run` the job started.
    $holder = Read-Rec 'slot-1.json'
    $guest = $holder.guest_pid
    $argv = if ($guest) { (wsl.exe -d Ubuntu -- cat "/proc/$guest/cmdline") -replace "`0", ' ' } else { '' }
    Check ($guest -gt 0 -and $argv -like "*bosun run*$never*" -and $holder.guest_distro -eq 'Ubuntu') "a WSL holder's record names its shim's own process and distro ($guest in $($holder.guest_distro))"
    Start-Sleep -Seconds 3
    wsl.exe -d Ubuntu -- pkill -KILL -f "wsl/bosun run .*$never"
    $null = Wait-Until { (Job-Line "*$never*").Count -eq 1 } 15000
    $n = Job-Line "*$never*"
    Check ($n.Count -eq 1 -and $n[0].ran_s -eq 0 -and $n[0].waited_s -ge 2.5 -and $null -eq $n[0].exit_code) "a WSL job ended before its command started counts it all as waiting (waited $($n.waited_s) s, ran $($n.ran_s) s)"
    if (-not $job.WaitForExit(10000)) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }
    # WORKAROUND: WSL's /init interop proxy for the killed shim's acquire stays in poll after bosun.exe has exited, so
    # the test ends every process carrying its marker, not only the shim, and leaves nothing running.
    wsl.exe -d Ubuntu -- pkill -KILL -f $never
    Stop-Process -Id $h.Id -Force -ErrorAction Ignore
  } finally {
    $env:WSLENV = $savedWslenv
    wsl.exe -d Ubuntu -- rm -rf "$linuxRepo"
  }
}

} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($fail) { "$fail failed"; exit 1 }
'all passed'
