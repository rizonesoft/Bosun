# Tests Test-BuildCacheStale (scripts/lib/build-cache.ps1): build.ps1 configures fresh when the cache came from
# another generator or another checkout folder, and reuses it otherwise.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\build-cache.ps1')
$dir = Join-Path ([IO.Path]::GetTempPath()) ("bosun-cache-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $dir | Out-Null
$failed = 0
function Case([string]$name, [string]$generator, [string]$homeDir, [string]$source, [bool]$want) {
  $cache = Join-Path $dir 'CMakeCache.txt'
  Set-Content -LiteralPath $cache -Value @("CMAKE_GENERATOR:INTERNAL=$generator", "CMAKE_HOME_DIRECTORY:INTERNAL=$homeDir")
  $got = Test-BuildCacheStale -Cache $cache -SourceDir $source
  if ($got -ne $want) { "FAIL $name (stale: got $got, want $want)"; $script:failed++ } else { "ok   $name" }
}
try {
  Case 'same generator and folder is reused' 'Ninja Multi-Config' 'D:/Bosun' 'D:\Bosun' $false
  Case 'folder compared without case' 'Ninja Multi-Config' 'd:/bosun' 'D:\Bosun\' $false
  Case 'moved checkout is configured fresh' 'Ninja Multi-Config' 'R:/GitHub/Bosun' 'D:\Bosun' $true
  Case 'other generator is configured fresh' 'Visual Studio 18 2026' 'D:/Bosun' 'D:\Bosun' $true
  if (Test-BuildCacheStale -Cache (Join-Path $dir 'missing.txt') -SourceDir 'D:\Bosun') { 'FAIL no cache needs no fresh'; $failed++ } else { 'ok   no cache needs no fresh' }
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
if ($failed) { exit 1 }
