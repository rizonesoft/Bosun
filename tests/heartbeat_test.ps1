# The heartbeat through its real commands, on a private LOCALAPPDATA: a real fake guard, real wsl.exe and real schtasks reads.
param([switch]$SkipWsl)
$ErrorActionPreference = 'Stop'
$script = (Resolve-Path (Join-Path $PSScriptRoot '..\scripts\heartbeat.py')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-heartbeat-$PID"
New-Item -ItemType Directory -Path $work | Out-Null
$work = (Get-Item $work).FullName
$fail = 0
$savedLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $work 'local'
function Check($ok, $name) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }
function Beat { & python $script @args 2>&1 | ForEach-Object { "$_" } }
function Status { (& python $script status --json | Out-String | ConvertFrom-Json) }
function NewRepo($path, $answer) {
  New-Item -ItemType Directory -Path (Join-Path $path 'scripts') -Force | Out-Null
  # A guard that keeps to its contract: `watch` prints one line, and task commands do nothing here.
  Set-Content (Join-Path $path 'scripts\run-guard.py') "import sys`nprint('$answer' if sys.argv[1] == 'watch' else 'ok')`n"
}
try {
  $one = Join-Path $work 'repo one'
  $two = Join-Path $work 'repo two'
  NewRepo $one 'resumed, attempt 1'
  NewRepo $two 'nothing to do'

  # Add reads back, and a repo with no guard is refused with what to do.
  $none = Join-Path $work 'no guard'
  New-Item -ItemType Directory -Path $none | Out-Null
  $out = Beat add $none | Out-String
  Check ($LASTEXITCODE -eq 1 -and $out -like '*run-guard.py*') 'a repo with no guard is refused, naming the fix'
  Beat add $one | Out-Null
  Check ($LASTEXITCODE -eq 0) 'a spaced path adds'
  Beat add $two | Out-Null
  Check ((Beat list | Out-String) -like "*$two*") 'list reads the repo back'

  # One resume per pass: repo one resumes, so repo two waits for the next pass and comes first then.
  Beat tick | Out-Null
  $s = Status
  Check ($s.repos[0].result -eq 'resumed, attempt 1') "repo one ticked (got $($s.repos[0].result))"
  Check ($null -eq $s.repos[1].last_tick) 'one resume stops the pass'
  Beat tick | Out-Null
  $s = Status
  Check ($s.repos[1].result -eq 'nothing to do') 'the next pass starts from the next repo'
  Check (-not $s.stale) 'a fresh pass is not stale'
  Check ($s.task.state -in 'missing', 'installed', 'disabled') "real task state read (got $($s.task.state))"

  Beat remove $two | Out-Null
  Check ($LASTEXITCODE -eq 0 -and (Beat list | Out-String) -notlike "*$two*") 'remove takes it off'

  if ($SkipWsl) {
    'skip the WSL cases: -SkipWsl was given'
  } else {
    # A real repo in the real distro, only ever while that distro is running.
    $linux = "/tmp/bosun-heartbeat-$PID"
    wsl.exe -d Ubuntu -e bash -c "mkdir -p '$linux/scripts' && printf 'print(\`"nothing to do\`")\n' > '$linux/scripts/run-guard.py'"
    Check ($LASTEXITCODE -eq 0) 'made a repo in the distro'
    $out = Beat add '/tmp/does-not-exist' --distro Ubuntu | Out-String
    Check ($out -like '*run-guard.py*') 'a WSL path with no guard is refused'
    Beat add $linux --distro Ubuntu | Out-Null
    Check ($LASTEXITCODE -eq 0) 'a WSL repo adds'
    Beat remove $one | Out-Null
    Beat tick | Out-Null
    $s = Status
    Check ($s.repos[0].result -eq 'nothing to do') "the WSL repo ticks inside the distro (got $($s.repos[0].result); $($s.repos[0].error))"
    $out = Beat add $linux --distro NoSuchDistroHere | Out-String
    Check ($out -like "*isn't running*") 'a distro that is not running is refused'
    Beat remove $linux --distro Ubuntu | Out-Null
    Check ($LASTEXITCODE -eq 0 -and (Beat list | Out-String) -like '*no repos*') 'a WSL repo removes'
    wsl.exe -d Ubuntu -e rm -rf $linux
  }
} finally {
  $env:LOCALAPPDATA = $savedLocal
  Remove-Item $work -Recurse -Force
}
if ($fail) { "$fail failed"; exit 1 }
'all passed'
