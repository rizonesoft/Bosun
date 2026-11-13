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
    $longDone = $job.WaitForExit(20000)
    if (-not $longDone) { & "$env:SystemRoot\System32	askkill.exe" /T /F /PID $job.Id 2>&1 | Out-Null }  # its whole tree, so nothing outlives the test
    Check ($longDone -and (Wait-Until { -not (Records) } 10000)) "and that long job ends and its record goes ($((Records).Name -join ', '))"
    if (-not $done) { Stop-Process -Id $job.Id -Force }
  } finally {
    $env:WSLENV = $savedWslenv
    wsl.exe -d Ubuntu -- rm -rf "$linuxRepo"
  }
}

Remove-Item $work -Recurse -Force
if ($fail) { "$fail failed"; exit 1 }
'all passed'
