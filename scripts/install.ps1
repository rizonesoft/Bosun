# Installs Bosun for the current user: tools, PATH, the login logger and the WSL side. Needs no admin rights.
param([string]$Build = "$PSScriptRoot\..\bin", [string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'

$bin = Join-Path $env:LOCALAPPDATA 'Bosun\bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null

# Jobs other repos have running hold bosun.exe, and the logger holds its own exe: each is swapped by rename, so no
# running job is stopped. The old logger is stopped only once the new one is in place, below.
. (Join-Path $PSScriptRoot 'lib\swap-file.ps1')
foreach ($exe in 'bosun.exe', 'bosun-logger.exe') { Install-SwapFile (Join-Path $Build $exe) (Join-Path $bin $exe) }
Get-Process bosun-logger -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $bin 'bosun-logger.exe*') } | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-SwappedFile (Join-Path $bin 'bosun-logger.exe')

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

# The global heartbeat task was retired on 2026-11-10. An install over an older one removes what it left, so no task
# keeps ticking a script that is gone. Nothing is scheduled in its place, here or in any other repository: a run
# starts only when its operator starts it, and nothing resumes one (the owner's rule of 2026-11-12).
if (Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object TaskName -EQ 'Bosun heartbeat') {
  Unregister-ScheduledTask -TaskName 'Bosun heartbeat' -Confirm:$false -ErrorAction Stop
}
foreach ($old in (Join-Path $bin 'heartbeat.py'), (Join-Path $bin 'bosun-heartbeat.cmd'), (Join-Path $env:LOCALAPPDATA 'Bosun\heartbeat')) {
  if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}

& (Join-Path $bin 'bosun.exe') status
