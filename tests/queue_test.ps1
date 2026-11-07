# Integration test for the build queue. Uses a private slot namespace, so it does not touch real builds.
param([string]$Build = "$PSScriptRoot\..\build\Release", [switch]$SkipWsl)
$ErrorActionPreference = 'Stop'
$bosun = Join-Path $Build 'bosun.exe'
$sleeper = Join-Path $Build 'test_sleeper.exe'
$ns = "BosunTest$PID"
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-test-$PID"
New-Item -ItemType Directory -Path $work | Out-Null
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
}

Remove-Item $work -Recurse -Force
if ($fail) { "$fail failed"; exit 1 }
'all passed'
