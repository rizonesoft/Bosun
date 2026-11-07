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

& (Join-Path $bin 'bosun.exe') status
