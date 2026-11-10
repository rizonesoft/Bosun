# scripts/start-run.ps1: the shortcut it writes starts the run from this checkout, and a dry run changes nothing.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts\start-run.ps1'
$pwsh = (Get-Process -Id $PID).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-start-run-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$failed = 0
function Check([bool]$ok, [string]$what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
try {
  $out = & $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work
  $link = Join-Path $work 'Bosun unattended run.lnk'
  Check ($LASTEXITCODE -eq 0 -and (Test-Path $link)) "the shortcut is written where asked ($out)"
  $read = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
  Check ($read.WorkingDirectory -eq $repo) 'it starts in this checkout, wherever that is'
  Check ($read.Arguments -match '-NoExit' -and $read.Arguments.Contains("`"$script`"")) 'it runs this checkout''s own start script and keeps the window'
  Check ((Split-Path $read.TargetPath -Leaf) -match '^pwsh') 'it runs PowerShell 7'

  $state = Join-Path $work 'state.json'
  $env:RUN_GUARD_STATE = $state
  # With nothing but Windows on PATH, as on a CI runner with no Claude Code: a dry run starts nothing, so it needs none.
  $path = $env:Path
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -DryRun) -join "`n" } finally { $env:Path = $path }
  Remove-Item Env:RUN_GUARD_STATE
  Check ($LASTEXITCODE -eq 0 -and -not (Test-Path $state)) 'a dry run leaves the run state alone'
  Check ($out -match 'run-guard\.py start --session ([0-9a-f-]{36}) --permission-mode bypassPermissions' -and $out -match "claude --session-id $($Matches[1]) --dangerously-skip-permissions `"/goal ") 'the guard is told the same session id the session starts with, in bypass mode, with its goal'
  Check ($out -match '100% complete' -and $out -match 'query next') 'the goal names the finish line'
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
