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
# Measured on 2026-11-10: it is no faster than the Visual Studio generator for this small tree (nothing to do 4 s,
# one changed file 11 s, a full build 15 s), since loading the compiler environment and compiling app.cpp dominate.
param(
  [switch]$Debug,
  [switch]$Fresh,
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot

if (-not (Get-Command cl -CommandType Application -ErrorAction Ignore)) {
  if (-not (Test-Path -LiteralPath $VsWhere)) { "vswhere isn't at ${VsWhere}: install Visual Studio or its Build Tools with the C++ workload"; exit 1 }
  $vs = & $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
  if (-not $vs) { 'no Visual Studio here has the C++ tools: add the "Desktop development with C++" workload in the Visual Studio Installer'; exit 1 }
  Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
  Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
}
foreach ($tool in 'cmake', 'ninja') {
  if (-not (Get-Command $tool -CommandType Application -ErrorAction Ignore)) { "$tool is not on PATH, and Visual Studio's own wasn't found: add the 'C++ CMake tools for Windows' component"; exit 1 }
}

Push-Location -LiteralPath $repo
try {
  # A build folder another generator configured can't be reused; its other contents (the run's records) are kept.
  $cache = Join-Path $repo 'build\CMakeCache.txt'
  $other = (Test-Path -LiteralPath $cache) -and -not (Select-String -LiteralPath $cache -Pattern '^CMAKE_GENERATOR:INTERNAL=Ninja Multi-Config$' -Quiet)
  $configure = @('--preset', 'default') + $(if ($Fresh -or $other) { '--fresh' })
  cmake @configure
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  cmake --build --preset $(if ($Debug) { 'debug' } else { 'release' })
  exit $LASTEXITCODE
} finally { Pop-Location }
