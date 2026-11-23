# Tests Test-BuildCacheStale (scripts/lib/build-cache.ps1): build.ps1 configures fresh when the cache came from
# another generator or another checkout folder, and reuses it otherwise.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\build-cache.ps1')
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$dir = New-BosunScratchFolder -Name 'cache-test'
$failed = 0
function Case([string]$name, [string]$generator, [string]$homeDir, [string]$source, [bool]$want) {
  $cache = Join-Path $dir 'CMakeCache.txt'
  Set-Content -LiteralPath $cache -Value @("CMAKE_GENERATOR:INTERNAL=$generator", "CMAKE_HOME_DIRECTORY:INTERNAL=$homeDir")
  $got = Test-BuildCacheStale -Cache $cache -SourceDir $source
  if ($got -ne $want) { "FAIL $name (stale: got $got, want $want)"; $script:failed++ } else { "ok   $name" }
}
try {
  Case 'same generator and folder is reused' 'Ninja Multi-Config' 'D:/src/Bosun' 'D:\src\Bosun' $false
  Case 'folder compared without case' 'Ninja Multi-Config' 'd:/src/bosun' 'D:\src\Bosun\' $false
  Case 'moved checkout is configured fresh' 'Ninja Multi-Config' 'R:/src/Bosun' 'D:\src\Bosun' $true
  Case 'other generator is configured fresh' 'Visual Studio 18 2026' 'D:/src/Bosun' 'D:\src\Bosun' $true
  if (Test-BuildCacheStale -Cache (Join-Path $dir 'missing.txt') -SourceDir 'D:\src\Bosun') { 'FAIL no cache needs no fresh'; $failed++ } else { 'ok   no cache needs no fresh' }

  # Test-BuildNeedsConfigure: missing cache/manifests/fingerprint, warm skip, preset changes, user preset create/edit/delete, -Fresh.
  $proj = Join-Path $dir 'proj'
  $buildDir = Join-Path $proj 'build'
  New-Item -ItemType Directory -Path $buildDir | Out-Null
  Set-Content -LiteralPath (Join-Path $proj 'CMakePresets.json') -Value '{ "version": 6 }'
  $p1 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Release'
  if (-not $p1.Configure -or $p1.Fresh) { "FAIL missing cache triggers configure (got $($p1 | ConvertTo-Json -Compress))"; $failed++ } else { 'ok   missing cache triggers configure' }
  $normProj = [IO.Path]::GetFullPath($proj).TrimEnd('\', '/') -replace '\\', '/'
  Set-Content -LiteralPath (Join-Path $buildDir 'CMakeCache.txt') -Value @('CMAKE_GENERATOR:INTERNAL=Ninja Multi-Config', "CMAKE_HOME_DIRECTORY:INTERNAL=$normProj")
  Set-Content -LiteralPath (Join-Path $buildDir 'build.ninja') -Value '# ninja'
  Set-Content -LiteralPath (Join-Path $buildDir 'build-Release.ninja') -Value '# release'
  $p2 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Release'
  if (-not $p2.Configure -or $p2.Reason -ne 'missing preset fingerprint') { "FAIL missing fingerprint triggers upgrade configure (got $($p2.Reason))"; $failed++ } else { 'ok   missing fingerprint triggers upgrade configure' }
  Write-PresetConfigureFingerprint -Path $p2.FingerprintPath -Fingerprint $p2.Fingerprint
  $p3 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Release'
  if ($p3.Configure) { "FAIL warm Release tree skips configure (got $($p3.Reason))"; $failed++ } else { 'ok   warm Release tree skips configure' }
  $p4 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Debug'
  if (-not $p4.Configure -or $p4.Reason -ne 'missing build-Debug.ninja') { "FAIL missing Debug manifest triggers configure (got $($p4.Reason))"; $failed++ } else { 'ok   missing Debug manifest triggers configure' }
  Set-Content -LiteralPath (Join-Path $buildDir 'CMakeUserPresets.json') -Value '{ "version": 6 }'
  Remove-Item -LiteralPath (Join-Path $buildDir 'CMakeUserPresets.json') -Force
  Set-Content -LiteralPath (Join-Path $proj 'CMakeUserPresets.json') -Value '{ "version": 6 }'
  $p5 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Release'
  if (-not $p5.Configure -or $p5.Reason -ne 'preset inputs changed') { "FAIL adding CMakeUserPresets.json triggers configure (got $($p5.Reason))"; $failed++ } else { 'ok   adding CMakeUserPresets.json triggers configure' }
  Write-PresetConfigureFingerprint -Path $p5.FingerprintPath -Fingerprint $p5.Fingerprint
  Remove-Item -LiteralPath (Join-Path $proj 'CMakeUserPresets.json') -Force
  $p6 = Test-BuildNeedsConfigure -SourceDir $proj -Config 'Release'
  if (-not $p6.Configure -or $p6.Reason -ne 'preset inputs changed') { "FAIL removing CMakeUserPresets.json triggers configure (got $($p6.Reason))"; $failed++ } else { 'ok   removing CMakeUserPresets.json triggers configure' }
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
if ($failed) { exit 1 }
