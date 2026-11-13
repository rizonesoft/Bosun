# Integration test for the build queue. Uses a private slot namespace, so it does not touch real builds.
param([string]$Build = "$PSScriptRoot\..\build\Release", [switch]$SkipWsl)
$ErrorActionPreference = 'Stop'
# A suite run through `bosun run` hands this test the slot marker, and a job that has it passes straight through
# without queueing, so every check of the queue would see no queue: this test starts outside any slot.
Remove-Item Env:BOSUN_SLOT -ErrorAction Ignore
$bosun = Join-Path $Build 'bosun.exe'
$sleeper = Join-Path $Build 'test_sleeper.exe'
$ns = "BosunTest$PID"
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-test-$PID"
New-Item -ItemType Directory -Path $work | Out-Null
# The slot records go to a home of the test's own (D01 T01 §1), so none of this machine's own jobs is read or touched.
$env:LOCALAPPDATA = Join-Path $work 'home'
# The namespace's folder: upper-case letters as %XX (dm::ns_dir), so names differing only in case stay apart.
$nsDir = 'ns-' + (-join ($ns.ToCharArray() | ForEach-Object { if ($_ -cmatch '[a-z0-9_-]') { $_ } else { '%{0:X2}' -f [int]$_ } }))
$recDir = Join-Path $env:LOCALAPPDATA "Bosun\run\$((Get-Process -Id $PID).SessionId)\$nsDir"
function Records { @(Get-ChildItem -LiteralPath $recDir -Filter '*.json' -ErrorAction Ignore) }
function Read-Rec([string]$name) { $f = Join-Path $recDir $name; if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -Raw | ConvertFrom-Json -DateKind String } }  # started_at stays the text written
# The job log's lines (D01 T01 §2), read back from the test's home; -DateKind String keeps the times as written.
function Job-Lines { @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Bosun\logs') -Filter 'jobs-*.jsonl' -ErrorAction Ignore | ForEach-Object { Get-Content -LiteralPath $_.FullName } | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json -DateKind String }) }
function Job-Line([string]$like) { @(Job-Lines | Where-Object { $_.command -like $like }) }
function Wait-Until([scriptblock]$ok, [int]$ms = 10000) { foreach ($i in 1..($ms / 100)) { if (& $ok) { return $true }; Start-Sleep -Milliseconds 100 }; [bool](& $ok) }
$fail = 0
# Start-Process joins its arguments without quoting, so paths are quoted here.
function Q($s) { "`"$s`"" }
function Check($ok, $name) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }
function MaxOverlap($file) {
  $events = Get-Content $file | ForEach-Object { $p = $_ -split ' '; [pscustomobject]@{ T = [uint64]$p[1]; D = $(if ($p[0] -eq 'start') { 1 } else { -1 }) } }
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
Check ((Get-Content $f | Where-Object { $_ -like 'end*' }).Count -eq 6) 'all 6 jobs finished'

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
Set-Content $bat '@echo "%~1"'
$out = & $bosun run --ns $ns -- $bat "x&echo hi>$marker" 2>&1 | Out-String
$code = $LASTEXITCODE
Check (-not (Test-Path $marker)) 'batch argument cannot inject a command'
Check ($code -eq 0) "batch file runs (exit $code)"
Check ($out -like "*x&echo hi>$marker*") "batch argument arrives intact (got $($out.Trim()))"

# A batch file in a folder with spaces launches, as npm.cmd in Program Files must.
$spaced = Join-Path $work 'dir with spaces'
New-Item -ItemType Directory -Path $spaced | Out-Null
$spacedBat = Join-Path $spaced 'echo.cmd'
Set-Content $spacedBat '@echo "%~1"'
$out = & $bosun run --ns $ns -- $spacedBat 'hello world' 2>&1 | Out-String
Check (($LASTEXITCODE -eq 0) -and ($out -like '*hello world*')) "batch file in a spaced path runs (got $($out.Trim()))"

# %VAR% in an argument stays literal. The variable name ends in ^ to catch caret-style escaping too.
$percentMarker = Join-Path $work 'percent.txt'
[Environment]::SetEnvironmentVariable('DM_REVIEW^', "x&echo hi>$percentMarker", 'Process')
[Environment]::SetEnvironmentVariable('DM_REVIEW', "x&echo hi>$percentMarker", 'Process')
$out = & $bosun run --ns $ns -- $bat '%DM_REVIEW%' 2>&1 | Out-String
Check (-not (Test-Path $percentMarker)) 'a percent expression cannot inject a command'
Check ($out -like '*%DM_REVIEW%*') "a percent expression stays literal (got $($out.Trim()))"
[Environment]::SetEnvironmentVariable('DM_REVIEW^', $null, 'Process')
[Environment]::SetEnvironmentVariable('DM_REVIEW', $null, 'Process')

# An embedded quote cannot end the quoting early.
$quoteMarker = Join-Path $work 'quote.txt'
$null = & $bosun run --ns $ns -- $bat "a`"&echo hi>$quoteMarker&`"b" 2>&1
Check (-not (Test-Path $quoteMarker)) 'an embedded quote cannot inject a command'

# The logger attributes a process, and its child, to the git repo holding the working directory.
$repoName = "bosuntestrepo$PID"
$repo = Join-Path $work $repoName
New-Item -ItemType Directory -Path (Join-Path $repo '.git') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repo 'sub') | Out-Null
$sub = Join-Path $repo 'sub'
$held = Start-Process $sleeper -ArgumentList (Q (Join-Path $work 'repo.txt')),'8000' -WorkingDirectory $sub -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
$savedLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $work 'local'
& $bosun log --once
$logFile = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Bosun\logs\windows-*.csv') | Select-Object -First 1
$repoRow = Get-Content $logFile | Where-Object { $_ -match ",win,repo,[^,]*$repoName," }
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

# A nested run passes straight through instead of deadlocking on its own slot.
$nested = Join-Path $work 'nested.cmd'
Set-Content $nested "@`"$bosun`" run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 0 -- `"$sleeper`" `"$work\nested.txt`" 1"
$job = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $nested) -PassThru -WindowStyle Hidden
Check ($job.WaitForExit(10000)) 'nested run does not deadlock'
Check ($job.ExitCode -eq 0) "nested run exits 0 (got $($job.ExitCode))"
Check ((Get-Content "$work\nested.txt") -match '^end') 'the nested command really ran'

# A child that writes without end is stopped at the write budget while the command itself sits idle, as a test suite
# does while a fake reviewer it started floods a log: the shape that filled C: on 2026-11-08.
$flood = Join-Path $work 'flood.py'
Set-Content $flood @'
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
Remove-Item (Join-Path $work 'flood.bin')

# --- The slot records (D01 T01 §1): who holds each slot and who waits, read from the real files ---
Check (-not (Records)) "every job above left no record behind, the command that couldn't start and the one the write budget stopped included ($((Records).Name -join ', '))"

# A holder names its pid, command, repo and side while it runs; a second job waits with a record of its own; a third
# that times out runs without a slot and leaves nothing; and the waiter becomes the holder when the slot frees.
$recRepo = Join-Path $work 'recrepo'
New-Item -ItemType Directory -Path (Join-Path $recRepo '.git'), (Join-Path $recRepo 'sub') -Force | Out-Null
$h = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'rec1.txt')),'4000' -PassThru -WindowStyle Hidden -WorkingDirectory (Join-Path $recRepo 'sub')
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h.Id }
$r = Read-Rec 'slot-0.json'
Check ($r.kind -eq 'holder' -and $r.pid -eq $h.Id -and $r.slot -eq 0 -and $r.side -eq 'windows' -and $r.ns -eq $ns -and $r.repo -eq ($recRepo -replace '\\', '/') -and $r.command -like '*test_sleeper.exe*rec1.txt 4000' -and $r.process_start -gt 0 -and $r.started_at -match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$') "a holder's record names its pid, command, repo and side (want pid $($h.Id), repo $($recRepo -replace '\\', '/'); got $($r | ConvertTo-Json -Compress))"
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'rec2.txt')),'500' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
$waiter = @(Records | Where-Object Name -like 'wait-*')
Check ($waiter.Count -eq 1 -and (Get-Content -LiteralPath $waiter[0].FullName -Raw | ConvertFrom-Json).kind -eq 'waiter' -and $waiter[0].Name -like "wait-$($w.Id)-*") "a job waiting for the slot has a waiter record ($($waiter.Name -join ', '))"
& $bosun run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 1 -- $sleeper (Join-Path $work 'rec3.txt') 200 2>$null
Check (@(Records | Where-Object Name -like 'wait-*').Count -eq 1 -and (Read-Rec 'slot-0.json').pid -eq $h.Id) 'a job that timed out ran without a slot and left no record, the holder''s untouched'
$took = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $w.Id } 15000
Check ($took -and -not @(Records | Where-Object Name -like 'wait-*')) 'when the slot frees, the waiter becomes the holder and its waiter record goes'
$null = $w.WaitForExit(15000)
Check ($null -ne $w.ExitCode -and -not (Records)) 'and once it ends, no record is left'

# A run inside a slot writes no record and leaves the outer holder's as it was.
$nestedRec = Join-Path $work 'nested-rec.cmd'
Set-Content $nestedRec "@`"$bosun`" run --ns $ns --slots 1 --min-avail-mb 0 --max-wait 0 -- `"$sleeper`" `"$work\nested-rec.txt`" 200`r`n@copy /y `"$($recDir -replace '%', '%%')\slot-0.json`" `"$work\outer-after-inner.json`" >nul" -Encoding ascii
$outer = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--',(Q $nestedRec) -PassThru -WindowStyle Hidden
$null = $outer.WaitForExit(15000)
$kept = if (Test-Path (Join-Path $work 'outer-after-inner.json')) { Get-Content (Join-Path $work 'outer-after-inner.json') -Raw | ConvertFrom-Json }
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
Check ((Read-Rec 'slot-0.json').pid -eq $b.Id -and -not (Test-Path (Join-Path $recDir "wait-$($gone.Id)-$goneStart.json")) -and (Test-Path (Join-Path $recDir "wait-$PID-$liveStart.json"))) "a reader's pass removes a dead waiter's record only: the new holder's and a live waiter's stay (b $($b.Id); files: $((Records).Name -join ', '); holder $((Read-Rec 'slot-0.json').pid))"
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
mine = os.path.join(recdir, 'wait-%d-*.json' % p.pid)
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

# A reader holding the waiter's record open as its reason changes doesn't lose the change: the replace is retried. Both
# slots held, so it waits for a slot; slot 1's job ends while the record is held open, so it now waits for memory.
$h0 = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sjo0.txt')),'6000' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-0.json').pid -eq $h0.Id }
$h1 = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--',(Q $sleeper),(Q (Join-Path $work 'sjo1.txt')),'1500' -PassThru -WindowStyle Hidden
$null = Wait-Until { (Read-Rec 'slot-1.json').pid -eq $h1.Id }
$w = Start-Process $bosun -ArgumentList 'run','--ns',$ns,'--slots','2','--min-avail-mb','99999999','--max-wait','60','--',(Q $sleeper),(Q (Join-Path $work 'sjo2.txt')),'200' -PassThru -WindowStyle Hidden
$null = Wait-Until { @(Records | Where-Object Name -like "wait-$($w.Id)-*").Count -eq 1 }
$wfile = (Records | Where-Object Name -like "wait-$($w.Id)-*" | Select-Object -First 1).FullName
$before = (Read-Rec (Split-Path -Leaf $wfile)).waiting_for
$held = [IO.File]::Open($wfile, 'Open', 'Read', 'Read')  # as .NET readers open it: no sharing for replacement
try { $null = $h1.WaitForExit(10000); Start-Sleep -Milliseconds 300 } finally { $held.Dispose() }
$null = Wait-Until { (Read-Rec (Split-Path -Leaf $wfile)).waiting_for -eq 'memory' } 3000
$after = (Read-Rec (Split-Path -Leaf $wfile)).waiting_for
Check ($before -eq 'slot' -and $after -eq 'memory') "a waiter's reason changes from slot to memory even while a reader held its record open (from '$before' to '$after')"
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
  $linuxRepo = "/tmp/bosunrec$PID"
  wsl.exe -d Ubuntu -- mkdir -p "$linuxRepo/.git" "$linuxRepo/sub"
  $savedWslenv = $env:WSLENV
  $env:WSLENV = (@($savedWslenv, 'LOCALAPPDATA') | Where-Object { $_ }) -join ':'  # the test's home reaches bosun.exe
  try {
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','sleep','4' -PassThru -WindowStyle Hidden
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
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','bash',(Q $longRunWsl),(Q $shim),(Q $exeWsl),$ns -PassThru -WindowStyle Hidden
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
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--min-guest-mb','99999999','--max-wait','3','--','sleep','2' -PassThru -WindowStyle Hidden
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

    # A WSL shim killed while its command runs: acquire still writes the line, with no exit code.
    $marker = "killmarker$PID"
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','bash','-c',(Q 'sleep 30'),$marker -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-0.json').side -eq 'wsl' } 15000
    wsl.exe -d Ubuntu -- pkill -KILL -f "wsl/bosun run .*$marker"
    $null = Wait-Until { (Job-Line "*$marker*").Count -eq 1 } 15000
    $k = Job-Line "*$marker*"
    Check ($k.Count -eq 1 -and $null -eq $k[0].exit_code -and $k[0].side -eq 'wsl') "a killed WSL shim's job still leaves its line, with no exit code ($($k | ConvertTo-Json -Compress))"
    if (-not $job.WaitForExit(10000)) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }
    wsl.exe -d Ubuntu -- pkill -KILL -f $marker

    # Its acquire stopped while the command runs: the shim lives on and keeps the command's own exit code (round 1).
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','1','--min-avail-mb','0','--max-wait','0','--','bash','-c',(Q 'sleep 6; exit 37') -PassThru -WindowStyle Hidden
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
    $job = Start-Process wsl.exe -ArgumentList '-d','Ubuntu','--cd',"$linuxRepo/sub",'--','env',(Q "BOSUN_EXE=$exeWsl"),'bash',(Q $shim),'run','--ns',$ns,'--slots','2','--min-avail-mb','0','--min-guest-mb','99999999','--max-wait','0','--','bash','-c',(Q 'exit 0'),$never -PassThru -WindowStyle Hidden
    $null = Wait-Until { (Read-Rec 'slot-1.json').side -eq 'wsl' } 15000
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

Remove-Item $work -Recurse -Force
if ($fail) { "$fail failed"; exit 1 }
'all passed'
