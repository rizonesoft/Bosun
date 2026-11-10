# Installs Bosun for the current user: tools, PATH, the login logger and the WSL side. Needs no admin rights.
param([string]$Build = "$PSScriptRoot\..\bin", [string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'

$bin = Join-Path $env:LOCALAPPDATA 'Bosun\bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null

# A running logger locks its exe, so stop it before copying.
Get-Process bosun-logger -ErrorAction SilentlyContinue | Where-Object Path -EQ (Join-Path $bin 'bosun-logger.exe') | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item (Join-Path $Build 'bosun.exe'), (Join-Path $Build 'bosun-logger.exe') $bin -Force

# A user may have no user-level Path at all.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User') ?? ''
if (($userPath -split ';') -notcontains $bin) {
  [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $bin).TrimStart(';'), 'User')
}

$logger = Join-Path $bin 'bosun-logger.exe'
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Bosun Logger' -Value "`"$logger`""
Start-Process $logger

$stageWsl = & (Join-Path $PSScriptRoot 'stage-wsl.ps1') -Destination (Join-Path $env:LOCALAPPDATA 'Bosun\wsl-install') -Distro $Distro
$exeWsl = (wsl.exe -d $Distro -- wslpath -a ((Join-Path $bin 'bosun.exe') -replace '\\', '/')).Trim()
$logsWsl = (wsl.exe -d $Distro -- wslpath -a ((Join-Path $env:LOCALAPPDATA 'Bosun\logs') -replace '\\', '/')).Trim()
wsl.exe -d $Distro -- bash "$stageWsl/install.sh" $exeWsl $logsWsl
if ($LASTEXITCODE) { throw "WSL install failed with exit $LASTEXITCODE" }

# The global heartbeat was retired on 2026-11-10: each repo keeps its own watchdog. An install over an older one
# removes what that heartbeat left, so no task keeps ticking a script that is gone.
# That heartbeat had removed each registered repo's own watchdog task, so each Windows repo still on it gets its own
# back first, from its own run guard: otherwise the upgrade would leave its unattended run with nothing to resume it.
$registry = Join-Path $env:LOCALAPPDATA 'Bosun\heartbeat\repos.json'
if (Test-Path -LiteralPath $registry) {
  $python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
  foreach ($r in @((Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json).repos | Where-Object { $_ -and -not $_.distro })) {
    $guard = Join-Path $r.path 'scripts\run-guard.py'
    if (-not (Test-Path -LiteralPath $guard)) { "heartbeat: $($r.path) has no run guard now, so it has no watchdog to hand back"; continue }
    & $python $guard install | Out-Null
    if ($LASTEXITCODE) { throw "could not give $($r.path) its own watchdog back (python `"$guard`" install, exit $LASTEXITCODE), so the heartbeat is left in place: fix that, then run this again" }
    "heartbeat: $($r.path) has its own watchdog task again"
  }
}
if (Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object TaskName -EQ 'Bosun heartbeat') {
  Unregister-ScheduledTask -TaskName 'Bosun heartbeat' -Confirm:$false -ErrorAction Stop
}
foreach ($old in (Join-Path $bin 'heartbeat.py'), (Join-Path $bin 'bosun-heartbeat.cmd'), (Join-Path $env:LOCALAPPDATA 'Bosun\heartbeat')) {
  if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}

& (Join-Path $bin 'bosun.exe') status
