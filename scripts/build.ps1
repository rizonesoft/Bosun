# Builds the native tools with Ninja, from any shell.
#
#   pwsh scripts/build.ps1            Release: bosun.exe and bosun-logger.exe land in bin/, tests in build/Release
#   pwsh scripts/build.ps1 -Debug     Debug, into build/Debug
#   pwsh scripts/build.ps1 -Fresh     configure from nothing first
#
# Ninja starts the compiler directly, so it needs the Visual Studio compiler environment a plain shell lacks. This
# finds Visual Studio through vswhere and loads that environment into this process only, then runs the `default`
# preset in CMakePresets.json. Unlike the Visual Studio generator, Ninja writes build/compile_commands.json, which
# clangd reads for the exact flags of every file, and the preset makes every warning an error here as in CI.
#
# Loading that environment was most of a build with nothing to do, so what the load changed is kept in
# build/dev-env.json and replayed while Visual Studio is the same installation and version
# (scripts/lib/dev-env.ps1). Measured on 2026-11-12 with scripts/measure-build.ps1: nothing to do went from 3.9 s
# to 1.2 s. sccache and lld-link were measured the same day and not kept; docs/build-performance.md has the numbers.
param(
  [switch]$Debug,
  [switch]$Fresh,
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot

if (-not (Get-Command cl -CommandType Application -ErrorAction Ignore)) {
  if (-not (Test-Path -LiteralPath $VsWhere)) { "vswhere isn't at ${VsWhere}: install Visual Studio or its Build Tools with the C++ workload"; exit 1 }
  $found = & $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json | Select-Object -First 1
  $vs = $found.installationPath
  if (-not $vs) { 'no Visual Studio here has the C++ tools: add the "Desktop development with C++" workload in the Visual Studio Installer'; exit 1 }
  . (Join-Path $PSScriptRoot 'lib/dev-env.ps1')
  Import-DevEnv -Cache (Join-Path $repo 'build/dev-env.json') -Key "$vs|$($found.installationVersion)" -Load {
    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
  }
}
foreach ($tool in 'cmake', 'ninja') {
  if (-not (Get-Command $tool -CommandType Application -ErrorAction Ignore)) { "$tool is not on PATH, and Visual Studio's own wasn't found: add the 'C++ CMake tools for Windows' component"; exit 1 }
}

Push-Location -LiteralPath $repo
try {
  # A cache another generator or another checkout folder wrote can't be reused; the rest of build/ is kept.
  . (Join-Path $PSScriptRoot 'lib\build-cache.ps1')
  $other = Test-BuildCacheStale -Cache (Join-Path $repo 'build\CMakeCache.txt') -SourceDir $repo
  $configure = @('--preset', 'default') + $(if ($Fresh -or $other) { '--fresh' })
  cmake @configure
  if ($LASTEXITCODE -and -not ($Fresh -or $other)) {
    # The cache is derived data: one it cannot configure from is rebuilt once, and a real error fails again here.
    'configuring from the existing cache failed: configuring fresh once'
    cmake @configure --fresh
  }
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  cmake --build --preset $(if ($Debug) { 'debug' } else { 'release' })
  exit $LASTEXITCODE
} finally { Pop-Location }
