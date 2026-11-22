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

# The fake cmake records each call. A configure fails when the mode says the cache is bad and the call is not
# fresh, or when the mode says the project itself is broken.
Set-Content -LiteralPath (Join-Path $bin 'fake-cmake.ps1') -Value @'
$mode = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mode.txt')
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value ($args -join ' ')
$compiler = (Get-Command cl -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'seen.txt') -Value "cl=$compiler include=$([bool]$env:INCLUDE)"
if ($args -contains '--build') { 'ninja: no work to do.'; exit 0 }
if ($mode -eq 'broken') { 'CMake Error: the project is broken'; exit 1 }
if ($mode -eq 'bad-cache' -and $args -notcontains '--fresh') { 'CMake Error: the cache is no good'; exit 1 }
'-- Build files have been written'
exit 0
'@
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

  $r = Invoke-Build 'ok'
  Check 'a build configures once from the cache, then builds' ($r.Exit -eq 0 -and $r.Calls.Count -eq 2 -and $r.Calls[0] -like '--preset default*' -and
    $r.Calls[0] -notlike '*--fresh*' -and $r.Calls[1] -eq '--build --preset release') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'ok' @('-Debug')
  Check '-Debug builds the debug preset' ($r.Exit -eq 0 -and $r.Calls[1] -eq '--build --preset debug')

  $r = Invoke-Build 'bad-cache'
  Check 'a cache that cannot be configured from is configured fresh once' ($r.Exit -eq 0 -and $r.Calls.Count -eq 3 -and $r.Calls[0] -notlike '*--fresh*' -and
    $r.Calls[1] -like '*--fresh*' -and $r.Calls[2] -like '--build*' -and $r.Text -match 'configuring fresh once') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'broken'
  Check 'a real configure error fails after the one fresh try, and nothing is built' ($r.Exit -ne 0 -and $r.Calls.Count -eq 2 -and $r.Calls[1] -like '*--fresh*' -and
    -not ($r.Calls | Where-Object { $_ -like '--build*' })) "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  $r = Invoke-Build 'broken' @('-Fresh')
  Check '-Fresh that fails is not tried again' ($r.Exit -ne 0 -and $r.Calls.Count -eq 1 -and $r.Calls[0] -like '*--fresh*') "(exit $($r.Exit), calls: $($r.Calls -join ' ; '))"

  # With no compiler on PATH the script loads the real Visual Studio environment, and replays it the next time.
  # What CMake would see is read from inside the fake cmake: the compiler it resolves and whether INCLUDE is set.
  $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
  $hasTools = (Test-Path -LiteralPath $vsWhere) -and (& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
  if (-not $hasTools) { 'skip the compiler environment cases: no Visual Studio with the C++ tools here' }
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
  }
} finally {
  $env:PATH = $savedPath
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
