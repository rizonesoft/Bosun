# test_core's named groups: a name runs exactly its group, a failure reaches the exit code, implemented groups make
# up the default run, and a reserved name fails until its section fills it in.
param([string]$Exe = (Join-Path $PSScriptRoot '..\build\Release\test_core.exe'))
$ErrorActionPreference = 'Stop'
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
function Run([string[]]$names, [string]$force) {
  if ($force) { $env:TEST_CORE_FORCE_FAIL = $force } else { Remove-Item Env:TEST_CORE_FORCE_FAIL -ErrorAction SilentlyContinue }
  $out = & $Exe @names
  $code = $LASTEXITCODE
  Remove-Item Env:TEST_CORE_FORCE_FAIL -ErrorAction SilentlyContinue
  [pscustomobject]@{ Code = $code; Groups = @($out | Where-Object { $_ -like 'group *' } | ForEach-Object { $_.Substring(6) }); Text = ($out -join "`n") }
}

$list = & $Exe --list
Check ($LASTEXITCODE -eq 0) '--list exits 0'
$implemented = @($list | Where-Object { $_ -like '* implemented' } | ForEach-Object { ($_ -split ' ')[0] })
$reserved = @($list | Where-Object { $_ -like '* reserved' } | ForEach-Object { ($_ -split ' ')[0] })
Check ($implemented.Count -ge 1) "some groups are implemented ($($implemented -join ', '))"
foreach ($name in 'records', 'status_json', 'report_json', 'config', 'pressure') {
  Check ($reserved -contains $name -or $implemented -contains $name) "$name is a known group"
}

$all = Run @() $null
Check ($all.Code -eq 0) 'the default run exits 0'
Check ((Compare-Object $all.Groups $implemented) -eq $null) 'the default run is exactly the implemented groups'
foreach ($name in $reserved) { Check ($all.Groups -notcontains $name) "reserved $name stays out of the default run" }

foreach ($name in $implemented) {
  $one = Run @($name) $null
  Check ($one.Code -eq 0 -and $one.Groups.Count -eq 1 -and $one.Groups[0] -eq $name) "$name runs only its own group"
}

$victim = $implemented[0]
$other = $implemented[-1]
Check ((Run @($victim) $victim).Code -eq 1) "a failure in $victim makes its run exit 1"
Check ((Run @() $victim).Code -eq 1) "a failure in $victim makes the default run exit 1"
if ($other -ne $victim) { Check ((Run @($other) $victim).Code -eq 0) "$other still passes while $victim fails" }

foreach ($name in $reserved) {
  $r = Run @($name) $null
  Check ($r.Code -ne 0 -and $r.Text -match 'reserved') "reserved $name exits nonzero and says so"
}
$unknown = Run @('no-such-test') $null
Check ($unknown.Code -ne 0 -and $unknown.Text -match 'no test group') 'an unknown name exits nonzero and says so'

if ($failed) { "$failed failed"; exit 1 }
'all passed'
