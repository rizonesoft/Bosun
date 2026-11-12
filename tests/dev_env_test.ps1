# Tests Import-DevEnv (scripts/lib/dev-env.ps1), which scripts/build.ps1 uses to load the compiler environment once
# and replay it from build/dev-env.json after that. Each case runs in a fresh pwsh process with a fake developer
# shell, so nothing one case loaded can make the next one pass.
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot '..\scripts\lib\dev-env.ps1'
$dir = Join-Path ([IO.Path]::GetTempPath()) ("bosun-devenv-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $dir | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# The fake shell: counts its loads, creates two variables, replaces one that existed, puts one folder in front of
# PATH and one behind, and puts its library folder in front of whatever LIB the calling shell had, as Visual
# Studio's own does. The compiler folder holds a file named cl.exe.
$compilerDir = Join-Path $dir 'vc\bin'
New-Item -ItemType Directory -Path $compilerDir | Out-Null
Copy-Item -LiteralPath (Get-Command cmd -CommandType Application | Select-Object -First 1).Source -Destination (Join-Path $compilerDir 'cl.exe')
$case = Join-Path $dir 'case.ps1'
Set-Content -LiteralPath $case -Value @'
param($lib, $cache, $key, $compilerDir, $loads, $shellLib)
. $lib
$env:UNRELATED_SECRET = 'set by this shell, not by the load'
$env:CHANGED_BY_LOAD = 'before'
if ($shellLib) { $env:LIB = $shellLib } else { Remove-Item Env:LIB -ErrorAction Ignore }
$report = Import-DevEnv -Cache $cache -Key $key -Load {
  Add-Content -LiteralPath $loads -Value 'load'
  $env:VSINSTALLDIR = 'C:\FakeVS\'
  $env:INCLUDE = 'C:\FakeVS\include'
  $env:CHANGED_BY_LOAD = 'after'
  $env:LIB = (@('C:\FakeVS\lib') + @($env:LIB -split ';' | Where-Object { $_ })) -join ';'
  $env:PATH = "$compilerDir;$env:PATH;C:\FakeVS\tail"
}
$entries = $env:PATH -split ';'
[ordered]@{
  report  = @($report) -join '|'
  include = $env:INCLUDE
  changed = $env:CHANGED_BY_LOAD
  libs    = $env:LIB
  first   = $entries[0]
  last    = $entries[-1]
  cl      = (Get-Command cl -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
} | ConvertTo-Json -Compress
'@
$cache = Join-Path $dir 'build\dev-env.json'
$loads = Join-Path $dir 'loads.txt'
function Invoke-Case([string]$key = 'C:\FakeVS|18.0.1', [string]$cachePath = $cache, [string]$shellLib = 'C:\ShellOne\lib') {
  $text = & pwsh -NoProfile -File $case $lib $cachePath $key $compilerDir $loads $shellLib
  $result = ($text | Select-Object -Last 1) | ConvertFrom-Json
  $result | Add-Member -NotePropertyName loads -NotePropertyValue @(Get-Content -LiteralPath $loads -ErrorAction Ignore).Count -PassThru
}
function Test-Loaded($r, [string]$how, [string]$libs = 'C:\FakeVS\lib;C:\ShellOne\lib') {
  $r.report -eq "developer environment: $how" -and $r.include -eq 'C:\FakeVS\include' -and $r.changed -eq 'after' -and $r.libs -eq $libs -and
  $r.first -eq $compilerDir -and $r.last -eq 'C:\FakeVS\tail' -and $r.cl -eq (Join-Path $compilerDir 'cl.exe')
}

try {
  $r = Invoke-Case
  Check 'the first build loads the environment and writes the cache' ((Test-Loaded $r 'loaded') -and $r.loads -eq 1 -and (Test-Path -LiteralPath $cache)) "($($r | ConvertTo-Json -Compress))"

  $stored = Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json -AsHashtable
  Check 'only what the load changed is stored' ($stored['variables'].Contains('INCLUDE') -and $stored['variables'].Contains('CHANGED_BY_LOAD') -and
    -not $stored['variables'].Contains('UNRELATED_SECRET') -and -not $stored['variables'].Contains('PATH') -and -not $stored['variables'].Contains('LIB') -and
    -not $stored['variables'].Contains('USERNAME')) "($($stored['variables'].Keys -join ','))"
  Check 'PATH is stored as the entries the load added' ((@($stored['lists']['PATH']['prepend']) -join '|') -eq $compilerDir -and (@($stored['lists']['PATH']['append']) -join '|') -eq 'C:\FakeVS\tail')
  Check 'a list the shell already had is stored without the shell''s own entries' ((@($stored['lists']['LIB']['prepend']) -join '|') -eq 'C:\FakeVS\lib' -and
    -not @($stored['lists']['LIB']['append']).Count -and (Get-Content -LiteralPath $cache -Raw) -notmatch 'ShellOne')

  $r = Invoke-Case
  Check 'the next build replays the cache without loading' ((Test-Loaded $r 'from cache') -and $r.loads -eq 1) "($($r | ConvertTo-Json -Compress))"

  # Another shell, with a library path of its own or none at all, keeps its own and never gets the first shell's.
  $r = Invoke-Case -shellLib 'C:\ShellTwo\lib'
  Check 'a shell with another LIB keeps its own on replay' ((Test-Loaded $r 'from cache' 'C:\FakeVS\lib;C:\ShellTwo\lib') -and $r.loads -eq 1) "(LIB: $($r.libs))"
  $r = Invoke-Case -shellLib ''
  Check 'a shell with no LIB gets only what the load added' ((Test-Loaded $r 'from cache' 'C:\FakeVS\lib') -and $r.loads -eq 1) "(LIB: $($r.libs))"

  $r = Invoke-Case -key 'C:\FakeVS|18.0.2'
  Check 'a changed key loads again and rewrites the cache' ((Test-Loaded $r 'loaded') -and $r.loads -eq 2 -and (Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json).key -eq 'C:\FakeVS|18.0.2')
  $r = Invoke-Case -key 'C:\FakeVS|18.0.2'
  Check 'the rewritten cache is replayed' ((Test-Loaded $r 'from cache') -and $r.loads -eq 2)

  Set-Content -LiteralPath $cache -Value '{ not json'
  $r = Invoke-Case -key 'C:\FakeVS|18.0.2'
  Check 'a malformed cache loads again and is rewritten' ((Test-Loaded $r 'loaded') -and $r.loads -eq 3 -and (Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json).schema_version -eq 2)

  # A cache in an earlier shape (schema_version 1 kept whole values) is not trusted.
  $text = Get-Content -LiteralPath $cache -Raw
  Set-Content -LiteralPath $cache -Value ($text -replace '"schema_version": 2', '"schema_version": 1')
  $r = Invoke-Case -key 'C:\FakeVS|18.0.2'
  Check 'a cache in an earlier shape loads again' ((Test-Loaded $r 'loaded') -and $r.loads -eq 4)

  $text = Get-Content -LiteralPath $cache -Raw
  Set-Content -LiteralPath $cache -Value ($text -replace [regex]::Escape(($compilerDir -replace '\\', '\\')), (($compilerDir + '-removed') -replace '\\', '\\'))
  $r = Invoke-Case -key 'C:\FakeVS|18.0.2'
  Check 'a cache whose compiler folder is gone loads again' ((Test-Loaded $r 'loaded') -and $r.loads -eq 5)

  # A cache that cannot be written (its folder is a file) costs a notice, and the environment is still loaded.
  $blocker = Join-Path $dir 'blocked'
  Set-Content -LiteralPath $blocker -Value 'a file where the cache folder would go'
  $r = Invoke-Case -cachePath (Join-Path $blocker 'dev-env.json')
  Check 'an unwritable cache still loads the environment and says so' ($r.report -match '^developer environment: loaded\|developer environment: could not be cached' -and $r.include -eq 'C:\FakeVS\include' -and $r.loads -eq 6) "($($r.report))"

  # A folder where the cache file should be is not written into: the same notice, and the environment is loaded.
  $folder = Join-Path $dir 'cache-is-a-folder.json'
  New-Item -ItemType Directory -Path $folder | Out-Null
  $r = Invoke-Case -cachePath $folder
  Check 'a folder in the place of the cache is left alone' ($r.report -match 'could not be cached' -and $r.include -eq 'C:\FakeVS\include' -and
    -not (Get-ChildItem -LiteralPath $folder)) "($($r.report))"
} finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Ignore }
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
