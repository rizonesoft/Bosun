# The WSL side must install from a folder WSL can read, even when it cannot see the drive holding the repo.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$work = New-BosunScratchFolder -Name 'stage-wsl'
try {
  $path = & (Join-Path $PSScriptRoot '..\scripts\stage-wsl.ps1') -Destination $work
  foreach ($name in 'bosun', 'bosun_log.py', 'install.sh') {
    wsl.exe -d Ubuntu -- test -f "$path/$name"
    if ($LASTEXITCODE) { throw "missing in WSL: $name" }
  }
  wsl.exe -d Ubuntu -- bash -n "$path/install.sh"
  if ($LASTEXITCODE) { throw 'install.sh does not parse in WSL' }
  $installer = Get-Content (Join-Path $PSScriptRoot '..\scripts\install.ps1') -Raw
  if ($installer -notmatch 'stage-wsl\.ps1') { throw 'install.ps1 does not stage the WSL side' }
  if ($installer -match '\$repoWsl') { throw 'install.ps1 still runs the WSL side from the repo folder' }
  'stage_wsl_test ok'
} finally {
  if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
