# Whether build/CMakeCache.txt can be reused for this checkout, used by scripts/build.ps1.
#
# CMake refuses a cache another generator wrote, and one written for another source folder (a checkout that moved,
# such as R:\GitHub\Bosun to D:\Bosun on 2026-11-10). Both need `--fresh`, which rebuilds the cache and keeps the
# rest of build/ (the run's records).
function Test-BuildCacheStale([string]$Cache, [string]$SourceDir, [string]$Generator = 'Ninja Multi-Config') {
  if (-not (Test-Path -LiteralPath $Cache)) { return $false }
  $lines = Get-Content -LiteralPath $Cache
  $gen = ($lines | Where-Object { $_ -like 'CMAKE_GENERATOR:INTERNAL=*' } | Select-Object -First 1) -replace '^[^=]*=', ''
  $built = ($lines | Where-Object { $_ -like 'CMAKE_HOME_DIRECTORY:INTERNAL=*' } | Select-Object -First 1) -replace '^[^=]*=', ''
  if ($gen -ne $Generator) { return $true }
  $want = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\', '/') -replace '\\', '/'
  return -not [string]::Equals($built.TrimEnd('/'), $want, [StringComparison]::OrdinalIgnoreCase)
}
