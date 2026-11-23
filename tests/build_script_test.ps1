# Tests scripts/build.ps1 itself with fake cl, cmake and ninja on PATH: what it passes to CMake, and that a cache
# it cannot configure from is configured fresh once while a real configure error still fails. The script and its
# lib folder are copied into a temporary checkout, so the real build tree is neither read nor touched.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$dir = New-BosunScratchFolder -Name 'build-script-test'
$checkout = Join-Path $dir 'checkout'
$bin = Join-Path $dir 'bin'
New-Item -ItemType Directory -Path (Join-Path $checkout 'scripts'), $bin | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\build.ps1') -Destination (Join-Path $checkout 'scripts')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\lib') -Destination (Join-Path $checkout 'scripts') -Recurse
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# The fake cmake records each call and writes CMakeCache.txt + Ninja manifests on a successful configure.
Set-Content -LiteralPath (Join-Path $bin 'fake-cmake.ps1') -Value @'
$mode = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mode.txt')
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value ($args -join ' ')
$compiler = (Get-Command cl -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'seen.txt') -Value "cl=$compiler include=$([bool]$env:INCLUDE)"
if ($args -contains '--build') { 'ninja: no work to do.'; exit 0 }
if ($mode -eq 'broken') { 'CMake Error: the project is broken'; exit 1 }
if ($mode -eq 'bad-cache' -and $args -notcontains '--fresh') { 'CMake Error: the cache is no good'; exit 1 }
$buildDir = Join-Path (Get-Location).ProviderPath 'build'
if (-not (Test-Path -LiteralPath $buildDir)) { New-Item -ItemType Directory -Path $buildDir | Out-Null }
$normSrc = [IO.Path]::GetFullPath((Get-Location).ProviderPath).TrimEnd('\', '/') -replace '\\', '/'
Set-Content -LiteralPath (Join-Path $buildDir 'CMakeCache.txt') -Value @('CMAKE_GENERATOR:INTERNAL=Ninja Multi-Config', "CMAKE_HOME_DIRECTORY:INTERNAL=$normSrc")
Set-Content -LiteralPath (Join-Path $buildDir 'build.ninja') -Value '# ninja'
Set-Content -LiteralPath (Join-Path $buildDir 'build-Release.ninja') -Value '# release'
Set-Content -LiteralPath (Join-Path $buildDir 'build-Debug.ninja') -Value '# debug'
'-- Build files have been written'
exit 0
'@
Set-Content -LiteralPath (Join-Path $checkout 'CMakePresets.json') -Value '{ "version": 6, "stamp": 1 }'
Set-Content -LiteralPath (Join-Path $bin 'cmake.cmd') -Value "@pwsh -NoProfile -File `"%~dp0fake-cmake.ps1`" %*"
Set-Content -LiteralPath (Join-Path $bin 'cl.cmd') -Value '@exit /b 0'
Set-Content -LiteralPath (Join-Path $bin 'ninja.cmd') -Value '@exit /b 0'

$pwshDir = Split-Path (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
$savedPath = $env:PATH
function Invoke-Build([string]$mode, [string[]]$arguments = @()) {
  Set-Content -LiteralPath (Join-Path $bin 'mode.txt') -Value $mode
  Remove-Item -LiteralPath (Join-Path $bin 'calls.txt') -ErrorAction Ignore
  $text = & pwsh -NoProfile -File (Join-Path $checkout 'scripts\build.ps1') @arguments 2>&1 | Out-String
  [pscustomobject]@{
    Exit  = $LASTEXITCODE
    Text  = $text
    Calls = @(Get-Content -LiteralPath (Join-Path $bin 'calls.txt') -ErrorAction Ignore)
  }
}

try {
  # Only the fakes, PowerShell and Windows itself: no optional tool is on this PATH.
  $env:PATH = "$bin;$pwshDir;$env:SystemRoot\System32"
  $buildDir = Join-Path $checkout 'build'
  $fpPath = Join-Path $buildDir 'preset-configure.sha256'

  $r = Invoke-Build 'ok'
  Check 'first build with no cache configures once, writes preset fingerprint, then builds' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and
    $r.Calls[1] -eq '--build --preset release' -and (Test-Path -LiteralPath $fpPath)) "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'ok'
  Check 'second build skips configure and runs only cmake --build' ($r.Exit -eq 0 -and $r.Calls.Count -eq 1 -and $r.Calls[0] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'ok' @('-Debug')
  Check '-Debug skips configure when Debug manifest is present and builds the debug preset' ($r.Exit -eq 0 -and $r.Calls.Count -eq 1 -and $r.Calls[0] -eq '--build --preset debug') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # Removing only the requested config's Ninja manifest triggers configure.
  Remove-Item -LiteralPath (Join-Path $buildDir 'build-Debug.ninja') -Force
  $r = Invoke-Build 'ok' @('-Debug')
  Check 'removing build-Debug.ninja triggers configure before building Debug' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and $r.Calls[1] -eq '--build --preset debug') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  Remove-Item -LiteralPath (Join-Path $buildDir 'build-Release.ninja') -Force
  $r = Invoke-Build 'ok'
  Check 'removing build-Release.ninja triggers configure before building Release' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and $r.Calls[1] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # Removing CMakeCache.txt triggers configure.
  Remove-Item -LiteralPath (Join-Path $buildDir 'CMakeCache.txt') -Force
  $r = Invoke-Build 'ok'
  Check 'removing CMakeCache.txt triggers configure, then builds' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and $r.Calls[1] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # Removing only top-level build.ninja triggers configure.
  Remove-Item -LiteralPath (Join-Path $buildDir 'build.ninja') -Force
  $r = Invoke-Build 'ok'
  Check 'removing top-level build.ninja triggers configure, then builds' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and $r.Calls[1] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # Stale cache (moved checkout or missing CMAKE_HOME_DIRECTORY) triggers --fresh configure through the wrapper.
  Set-Content -LiteralPath (Join-Path $buildDir 'CMakeCache.txt') -Value 'CMAKE_GENERATOR:INTERNAL=Ninja Multi-Config'
  $r = Invoke-Build 'ok'
  Check 'stale cache without CMAKE_HOME_DIRECTORY triggers --fresh configure through wrapper' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default --fresh' -and $r.Calls[1] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # Upgrade path: warm tree without preset-configure.sha256 configures once, then next build skips configure.
  Remove-Item -LiteralPath $fpPath -Force
  $r = Invoke-Build 'ok'
  $r2 = Invoke-Build 'ok'
  Check 'upgrade from warm tree without fingerprint configures once then skips' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -eq '--preset default' -and $r2.Exit -eq 0 -and $r2.Calls.Count -eq 1 -and $r2.Calls[0] -eq '--build --preset release') "(first: $($r.Calls -join ' ; '), second: $($r2.Calls -join ' ; '))"

  # Changing CMakePresets.json + failed configure: exits non-zero, runs no build, keeps old fingerprint, and next successful run configures.
  $oldFp = (Get-Content -LiteralPath $fpPath -Raw).Trim()
  Set-Content -LiteralPath (Join-Path $checkout 'CMakePresets.json') -Value '{ "version": 6, "stamp": 2 }'
  $r = Invoke-Build 'broken'
  $fpAfterFail = (Get-Content -LiteralPath $fpPath -Raw).Trim()
  $rRetry = Invoke-Build 'ok'
  $fpAfterOk = (Get-Content -LiteralPath $fpPath -Raw).Trim()
  Check 'preset change with failing configure exits non-zero, skips build, keeps prior digest, and configures on next successful run' (
    $r.Exit -ne 0 -and $r.Calls.Count -eq 2 -and $r.Calls[1] -like '*--fresh*' -and -not ($r.Calls | Where-Object { $_ -like '--build*' }) -and
    $fpAfterFail -ceq $oldFp -and
    $rRetry.Exit -eq 0 -and $rRetry.Calls.Count -eq 2 -and $rRetry.Calls[0] -eq '--preset default' -and $fpAfterOk -cne $oldFp
  ) "(fail calls: $($r.Calls -join ' ; '), retry calls: $($rRetry.Calls -join ' ; '))"

  # Optional CMakeUserPresets.json create, edit, and remove each trigger configure.
  $userPresets = Join-Path $checkout 'CMakeUserPresets.json'
  Set-Content -LiteralPath $userPresets -Value '{ "version": 6, "user": 1 }'
  $rCreate = Invoke-Build 'ok'
  Set-Content -LiteralPath $userPresets -Value '{ "version": 6, "user": 2 }'
  $rEdit = Invoke-Build 'ok'
  Remove-Item -LiteralPath $userPresets -Force
  $rRemove = Invoke-Build 'ok'
  $rSteady = Invoke-Build 'ok'
  Check 'CMakeUserPresets.json create, edit, and remove each trigger configure and then settle to skip' (
    $rCreate.Calls.Count -eq 2 -and $rEdit.Calls.Count -eq 2 -and $rRemove.Calls.Count -eq 2 -and $rSteady.Calls.Count -eq 1
  ) "(counts: $($rCreate.Calls.Count), $($rEdit.Calls.Count), $($rRemove.Calls.Count), $($rSteady.Calls.Count))"

  # Bad cache recovery (trigger configure by removing fingerprint, first configure fails, --fresh succeeds).
  Remove-Item -LiteralPath $fpPath -Force
  $r = Invoke-Build 'bad-cache'
  Check 'a cache that cannot be configured from is configured fresh once' ($r.Exit -eq 0 -and $r.Calls.Count -eq 3 -and $r.Calls[0] -notlike '*--fresh*' -and
    $r.Calls[1] -like '*--fresh*' -and $r.Calls[2] -like '--build*' -and $r.Text -match 'configuring fresh once') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'broken' @('-Fresh')
  Check '-Fresh that fails is not tried again' ($r.Exit -ne 0 -and $r.Calls.Count -eq 1 -and $r.Calls[0] -like '*--fresh*') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # With no compiler on PATH the script loads the real Visual Studio environment, and replays it the next time.
  # What CMake would see is read from inside the fake cmake: the compiler it resolves and whether INCLUDE is set.
  $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
  $hasTools = (Test-Path -LiteralPath $vsWhere) -and (& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
  if (-not $hasTools) { 'skip the compiler environment and real CMake/Ninja cases: no Visual Studio with the C++ tools here' }
  else {
    Remove-Item -LiteralPath (Join-Path $bin 'cl.cmd')
    $cache = Join-Path $checkout 'build/dev-env.json'
    $r = Invoke-Build 'ok'
    $first = Get-Content -LiteralPath (Join-Path $bin 'seen.txt') -ErrorAction Ignore
    Check 'with no compiler on PATH the environment is loaded and cached' ($r.Exit -eq 0 -and $r.Text -match 'developer environment: loaded' -and
      (Test-Path -LiteralPath $cache) -and $first -match 'cl=.+cl\.exe include=True$') "(exit $($r.Exit), saw '$first': $($r.Text))"
    $r = Invoke-Build 'ok'
    $second = Get-Content -LiteralPath (Join-Path $bin 'seen.txt') -ErrorAction Ignore
    Check 'the next build replays it and CMake sees the same compiler' ($r.Exit -eq 0 -and $r.Text -match 'developer environment: from cache' -and $second -ceq $first) "(exit $($r.Exit), saw '$second': $($r.Text))"
    Set-Content -LiteralPath $cache -Value '{ not json'
    $r = Invoke-Build 'ok'
    $third = Get-Content -LiteralPath (Join-Path $bin 'seen.txt') -ErrorAction Ignore
    Check 'a damaged cache is loaded again, not trusted' ($r.Exit -eq 0 -and $r.Text -match 'developer environment: loaded' -and $third -ceq $first)

    # Real minimal CMake/Ninja scratch project: proves no configure on second run, configure on cache removal,
    # real CMakePresets.json cache-variable read-back + next-run skip, and CMakeLists.txt automatic reconfigure by Ninja.
    $env:PATH = $savedPath
    $realProj = Join-Path $dir 'real-cmake-proj'
    New-Item -ItemType Directory -Path (Join-Path $realProj 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\build.ps1') -Destination (Join-Path $realProj 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\lib') -Destination (Join-Path $realProj 'scripts') -Recurse
    Set-Content -LiteralPath (Join-Path $realProj 'main.cpp') -Value @'
#include <cstdio>
int main() { std::puts(BOSUN_MSG); return 0; }
'@
    Set-Content -LiteralPath (Join-Path $realProj 'CMakeLists.txt') -Value @'
cmake_minimum_required(VERSION 3.25)
project(BosunBuildTest LANGUAGES CXX)
add_executable(hello main.cpp)
target_compile_definitions(hello PRIVATE BOSUN_MSG="MSG_ONE")
'@
    $presetsTemplate = @'
{
  "version": 6,
  "configurePresets": [
    {
      "name": "default",
      "generator": "Ninja Multi-Config",
      "binaryDir": "${sourceDir}/build",
      "cacheVariables": {
        "CMAKE_CXX_COMPILER": "cl",
        "BOSUN_TEST_STAMP": "STAMP_VAL"
      }
    }
  ],
  "buildPresets": [
    { "name": "release", "configurePreset": "default", "configuration": "Release" },
    { "name": "debug", "configurePreset": "default", "configuration": "Debug" }
  ]
}
'@
    Set-Content -LiteralPath (Join-Path $realProj 'CMakePresets.json') -Value ($presetsTemplate -replace 'STAMP_VAL', 'v1')
    $realBuildScript = Join-Path $realProj 'scripts\build.ps1'
    $realFp = Join-Path $realProj 'build\preset-configure.sha256'
    $realCache = Join-Path $realProj 'build\CMakeCache.txt'
    $realExe = Join-Path $realProj 'build\Release\hello.exe'

    $out1 = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e1 = $LASTEXITCODE
    $exeOut1 = if (Test-Path -LiteralPath $realExe) { (& $realExe).Trim() } else { '' }
    $fpTime1 = if (Test-Path -LiteralPath $realFp) { (Get-Item -LiteralPath $realFp).LastWriteTimeUtc.Ticks } else { 0 }
    Check 'real CMake/Ninja first build configures and builds hello.exe' ($e1 -eq 0 -and $out1 -match '-- Configuring done' -and $exeOut1 -eq 'MSG_ONE' -and $fpTime1 -gt 0) "(exit $e1, exe '$exeOut1'): $out1"

    Start-Sleep -Milliseconds 50
    $out2 = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e2 = $LASTEXITCODE
    $fpTime2 = (Get-Item -LiteralPath $realFp).LastWriteTimeUtc.Ticks
    Check 'real CMake/Ninja second build skips explicit configure and prints ninja no work to do' ($e2 -eq 0 -and $out2 -match 'ninja: no work to do' -and $out2 -notmatch '-- Configuring done' -and $fpTime2 -eq $fpTime1) "(exit $e2): $out2"

    # Real CMakePresets.json change updates cache variable, then next unchanged run skips configure.
    Set-Content -LiteralPath (Join-Path $realProj 'CMakePresets.json') -Value ($presetsTemplate -replace 'STAMP_VAL', 'v2')
    $out3 = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e3 = $LASTEXITCODE
    $cacheText3 = Get-Content -LiteralPath $realCache -Raw
    $out3Next = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e3Next = $LASTEXITCODE
    Check 'real CMakePresets.json change triggers configure, updates BOSUN_TEST_STAMP in CMakeCache.txt, and next run skips configure' (
      $e3 -eq 0 -and $out3 -match '-- Configuring done' -and $cacheText3 -match 'BOSUN_TEST_STAMP:UNINITIALIZED=v2|BOSUN_TEST_STAMP:STRING=v2' -and
      $e3Next -eq 0 -and $out3Next -match 'ninja: no work to do' -and $out3Next -notmatch '-- Configuring done'
    ) "(exit $e3/$e3Next): $out3"

    # Real CMakeLists.txt change: build.ps1 does NOT run its own configure (fingerprint timestamp unchanged), while Ninja re-runs CMake automatically and rebuilds hello.exe with MSG_TWO.
    $fpTimeBeforeCmakeLists = (Get-Item -LiteralPath $realFp).LastWriteTimeUtc.Ticks
    Start-Sleep -Milliseconds 1100
    Set-Content -LiteralPath (Join-Path $realProj 'CMakeLists.txt') -Value @'
cmake_minimum_required(VERSION 3.25)
project(BosunBuildTest LANGUAGES CXX)
add_executable(hello main.cpp)
target_compile_definitions(hello PRIVATE BOSUN_MSG="MSG_TWO")
'@
    $out4 = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e4 = $LASTEXITCODE
    $fpTimeAfterCmakeLists = (Get-Item -LiteralPath $realFp).LastWriteTimeUtc.Ticks
    $exeOut4 = if (Test-Path -LiteralPath $realExe) { (& $realExe).Trim() } else { '' }
    Check 'real CMakeLists.txt change triggers Ninja automatic reconfigure without build.ps1 configuring and rebuilds MSG_TWO' (
      $e4 -eq 0 -and $fpTimeAfterCmakeLists -eq $fpTimeBeforeCmakeLists -and $out4 -match 'Re-running CMake' -and $exeOut4 -eq 'MSG_TWO'
    ) "(exit $e4, fpSame $($fpTimeAfterCmakeLists -eq $fpTimeBeforeCmakeLists), exe '$exeOut4'): $out4"

    # Removing build/CMakeCache.txt triggers configure again.
    Remove-Item -LiteralPath $realCache -Force
    $out5 = & pwsh -NoProfile -File $realBuildScript 2>&1 | Out-String
    $e5 = $LASTEXITCODE
    Check 'real CMake/Ninja build reconfigures after build/CMakeCache.txt is removed' ($e5 -eq 0 -and $out5 -match '-- Configuring done' -and (Test-Path -LiteralPath $realCache)) "(exit $e5): $out5"
  }
} finally {
  $env:PATH = $savedPath
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
