# Installs DevMode for the current user: tools, PATH, the login logger and the WSL side. Needs no admin rights.
param([string]$Build = "$PSScriptRoot\..\build\Release", [string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'

$bin = Join-Path $env:LOCALAPPDATA 'DevMode\bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null

# A running logger locks its exe, so stop it before copying.
Get-Process devmode-logger -ErrorAction SilentlyContinue | Where-Object Path -EQ (Join-Path $bin 'devmode-logger.exe') | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item (Join-Path $Build 'devmode.exe'), (Join-Path $Build 'devmode-logger.exe') $bin -Force

# A user may have no user-level Path at all.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User') ?? ''
if (($userPath -split ';') -notcontains $bin) {
  [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $bin).TrimStart(';'), 'User')
}

$logger = Join-Path $bin 'devmode-logger.exe'
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DevMode Logger' -Value "`"$logger`""
Start-Process $logger

$repoWsl = (wsl.exe -d $Distro -- wslpath -a ($PSScriptRoot -replace '\\', '/')).Trim()
$exeWsl = (wsl.exe -d $Distro -- wslpath -a ((Join-Path $bin 'devmode.exe') -replace '\\', '/')).Trim()
$logsWsl = (wsl.exe -d $Distro -- wslpath -a ((Join-Path $env:LOCALAPPDATA 'DevMode\logs') -replace '\\', '/')).Trim()
wsl.exe -d $Distro -- bash "$repoWsl/../wsl/install.sh" $exeWsl $logsWsl
if ($LASTEXITCODE) { throw "WSL install failed with exit $LASTEXITCODE" }

& (Join-Path $bin 'devmode.exe') status
