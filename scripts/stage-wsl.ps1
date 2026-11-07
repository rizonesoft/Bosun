# Copies the WSL side to a folder WSL can read, and prints that folder's WSL path.
# WSL mounts fixed drives when it starts, so a drive added since then, such as the one holding this repo, is not visible yet.
param([Parameter(Mandatory)][string]$Destination, [string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\wsl\*') $Destination -Force
$path = (wsl.exe -d $Distro -- wslpath -a ($Destination -replace '\\', '/')).Trim()
wsl.exe -d $Distro -- test -f "$path/install.sh"
if ($LASTEXITCODE) { throw "WSL cannot read $Destination" }
$path
