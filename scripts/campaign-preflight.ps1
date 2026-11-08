# One command that proves the unattended campaign can run before anyone walks away. D00 T03 §9 adds the CMake check;
# D00 T03 §10 adds the rest. Exits 1 when any check fails, each failure naming its fix.
param(
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$failed = 0
function Pass([string]$what) { "ok   $what" }
function Fail([string]$what, [string]$fix) {
  "FAIL $what"
  "     fix: $fix"
  $script:failed++
}

# The CMake Visual Studio ships, and how to get it onto PATH, or why it can't be found.
function Find-VsCMake {
  if (-not (Test-Path $VsWhere)) {
    return @{ Fix = "vswhere isn't at $VsWhere, so Visual Studio can't be found: install Visual Studio 2022 or later with the 'C++ CMake tools for Windows' component" }
  }
  # Every installed instance, newest first: the newest may lack CMake while an older one has it.
  $all = @(& $VsWhere -products '*' -sort -property installationPath 2>$null | Where-Object { $_ })
  if (-not $all) {
    return @{ Fix = "vswhere found no Visual Studio: install it with the 'C++ CMake tools for Windows' component" }
  }
  foreach ($vs in $all) {
    $folder = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
    if (Test-Path (Join-Path $folder 'cmake.exe')) {
      return @{ Folder = $folder; Fix = "add $folder to your user PATH with ``pwsh scripts/add-cmake-to-path.ps1 -Apply``, then open a new shell" }
    }
  }
  @{ Fix = "no Visual Studio here has CMake ($($all -join ', ')): add the 'C++ CMake tools for Windows' component in the Visual Studio Installer" }
}

function Test-CMake {
  $minimum = [version]'3.25'  # cmake_minimum_required in CMakeLists.txt
  $vs = Find-VsCMake
  # The cmake a new shell would run: the first on PATH, as the build and every section's checkpoint use it.
  $found = Get-Command cmake -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if (-not $found) {
    Fail 'cmake is not on PATH' $vs.Fix
    return
  }
  # All of the output first: stopping the pipeline at its first line would leave the exit code unset.
  $lines = @(& $found.Source --version 2>&1)
  $code = $LASTEXITCODE
  $text = ($lines | Select-Object -First 1) -as [string]
  if ($code -ne 0 -or $text -notmatch 'cmake version (\d+\.\d+(?:\.\d+)?)') {
    Fail "cmake at $($found.Source) doesn't run (exit $code, said `"$text`")" "remove or repair it, or put a working one first on PATH: $($vs.Fix)"
    return
  }
  $version = [version]$Matches[1]
  if ($version -lt $minimum) {
    Fail "cmake at $($found.Source) is $version, older than the $minimum CMakeLists.txt needs" "put a newer one first on PATH: $($vs.Fix)"
    return
  }
  Pass "cmake $version at $($found.Source)"
}

Test-CMake
if ($failed) {
  "$failed check(s) failed"
  exit 1
}
'preflight passed'
