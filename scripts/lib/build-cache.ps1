# Whether build/CMakeCache.txt can be reused for this checkout, used by scripts/build.ps1.
#
# CMake refuses a cache another generator wrote, and one written for another source folder (a checkout that moved,
# such as R:\src\Bosun to D:\src\Bosun on 2026-11-10). Both need `--fresh`, which rebuilds the cache and keeps the
# rest of build/ (the run's records).
function Test-BuildCacheStale([string]$Cache, [string]$SourceDir, [string]$Generator = 'Ninja Multi-Config') {
  if (-not (Test-Path -LiteralPath $Cache)) { return $false }
  $lines = Get-Content -LiteralPath $Cache
  $genLine = $lines | Where-Object { $_ -like 'CMAKE_GENERATOR:INTERNAL=*' } | Select-Object -First 1
  $builtLine = $lines | Where-Object { $_ -like 'CMAKE_HOME_DIRECTORY:INTERNAL=*' } | Select-Object -First 1
  $gen = if ($genLine) { $genLine -replace '^[^=]*=', '' } else { '' }
  $built = if ($builtLine) { $builtLine -replace '^[^=]*=', '' } else { '' }
  if ($gen -ne $Generator -or [string]::IsNullOrWhiteSpace($built)) { return $true }
  $want = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\', '/') -replace '\\', '/'
  return -not [string]::Equals($built.TrimEnd('/'), $want, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PresetConfigureFingerprint([string]$SourceDir) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try {
    $parts = foreach ($name in 'CMakePresets.json', 'CMakeUserPresets.json') {
      $path = Join-Path $SourceDir $name
      if (Test-Path -LiteralPath $path -PathType Leaf) {
        $bytes = [IO.File]::ReadAllBytes($path)
        $hash = -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
        "${name}:present:$($bytes.Length):$hash"
      } else {
        "${name}:absent"
      }
    }
    $combined = [Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))
    -join ($sha.ComputeHash($combined) | ForEach-Object { $_.ToString('x2') })
  } finally { $sha.Dispose() }
}

function Write-PresetConfigureFingerprint([string]$Path, [string]$Fingerprint) {
  $dir = Split-Path -Parent $Path
  if ($dir -and -not (Test-Path -LiteralPath $dir)) { [void][IO.Directory]::CreateDirectory($dir) }
  $temp = "$Path.tmp-$PID"
  $utf8 = [Text.UTF8Encoding]::new($false)
  try {
    [IO.File]::WriteAllText($temp, ($Fingerprint + "`n"), $utf8)
    [IO.File]::Move($temp, $Path, $true)
  } finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction Ignore }
  }
}

function Test-BuildNeedsConfigure([string]$SourceDir, [string]$Config = 'Release', [switch]$Fresh) {
  $cache = Join-Path $SourceDir 'build\CMakeCache.txt'
  $ninja = Join-Path $SourceDir 'build\build.ninja'
  $configNinja = Join-Path $SourceDir "build\build-$Config.ninja"
  $fpPath = Join-Path $SourceDir 'build\preset-configure.sha256'
  $fp = Get-PresetConfigureFingerprint $SourceDir
  $configure = $false
  $useFresh = $false
  $reason = 'up to date'
  if ($Fresh) {
    $configure = $true; $useFresh = $true; $reason = 'requested -Fresh'
  } elseif (Test-BuildCacheStale -Cache $cache -SourceDir $SourceDir) {
    $configure = $true; $useFresh = $true; $reason = 'stale cache'
  } elseif (-not (Test-Path -LiteralPath $cache -PathType Leaf)) {
    $configure = $true; $reason = 'missing CMakeCache.txt'
  } elseif (-not (Test-Path -LiteralPath $ninja -PathType Leaf)) {
    $configure = $true; $reason = 'missing build.ninja'
  } elseif (-not (Test-Path -LiteralPath $configNinja -PathType Leaf)) {
    $configure = $true; $reason = "missing build-$Config.ninja"
  } elseif (-not (Test-Path -LiteralPath $fpPath -PathType Leaf)) {
    $configure = $true; $reason = 'missing preset fingerprint'
  } else {
    $stored = ([IO.File]::ReadAllText($fpPath)).Trim()
    if ($stored -cne $fp) {
      $configure = $true; $reason = 'preset inputs changed'
    }
  }
  [pscustomobject]@{
    Configure       = $configure
    Fresh           = $useFresh
    Reason          = $reason
    Fingerprint     = $fp
    FingerprintPath = $fpPath
  }
}
