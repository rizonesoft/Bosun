# Tests bosun report's floor (D03 T01 §6): --low-mb at the command, the built bosun.exe over log files the test writes in
# a LOCALAPPDATA of its own. Omitted it is 2048; a value is the JSON's low_mb, its minutes and the text's words; a missing
# or bad value is refused with exit 64, naming it.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\build\Release\bosun.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "build first: $exe" }
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$dir = New-BosunScratchFolder -Name 'report-test'
$logs = Join-Path $dir 'Bosun\logs'
New-Item -ItemType Directory -Path $logs | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
function Report([string[]]$arguments) {
  $saved = $env:LOCALAPPDATA
  try {
    $env:LOCALAPPDATA = $dir
    $out = & $exe report @arguments 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
  } finally { $env:LOCALAPPDATA = $saved }
}
try {
  # An hour ago: 2500 MB twice in one minute and 3500 MB in the next, in today's file, in the logger's UTC.
  $t = [DateTime]::UtcNow.AddHours(-1)
  $minute = $t.ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)
  $next = $t.AddMinutes(1).ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)
  $file = Join-Path $logs ("windows-" + $t.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) + '.csv')
  Set-Content -LiteralPath $file -Encoding ascii -Value @(
    'ts,side,kind,name,count,private_mb,rss_mb',
    "$minute`:05,win,sys,available_mb,0,2500,0", "$minute`:35,win,sys,available_mb,0,2500,0", "$next`:05,win,sys,available_mb,0,3500,0",
    "$minute`:05,win,sys,total_mb,0,32768,0")

  $default = Report @('--json', '--days', '1')
  $json = $default.Text | ConvertFrom-Json
  Check 'omitted, the floor is 2048' ($default.Code -eq 0 -and $json.low_mb -eq 2048 -and $json.windows.low_minutes -eq 0) "($($default.Code): $($default.Text))"
  Check 'the installed memory is in the report' ($json.windows.total_mb -eq 32768)

  $given = Report @('--json', '--days', '1', '--low-mb', '3000')
  $json = $given.Text | ConvertFrom-Json
  Check 'a floor of 3000 is the JSON''s low_mb' ($given.Code -eq 0 -and $json.low_mb -eq 3000)
  Check 'two samples in one minute below it are one minute' ($json.windows.low_minutes -eq 1 -and $json.windows.low_samples -eq 2) "($($json.windows | ConvertTo-Json -Compress))"

  $text = Report @('--days', '1', '--low-mb', '3072')
  Check 'the text report says the floor in its words' ($text.Code -eq 0 -and $text.Text -match 'under 3\.0 GB available: 2') "($($text.Text))"

  $missing = Report @('--json', '--low-mb')
  Check 'a missing value is refused' ($missing.Code -eq 64) "($($missing.Code))"
  $bad = Report @('--json', '--low-mb', 'lots')
  Check 'a bad value is refused, naming it' ($bad.Code -eq 64 -and $bad.Text -match "--low-mb takes a whole number of MB from 1 to 65536, not 'lots'") "($($bad.Code): $($bad.Text))"
  $zero = Report @('--json', '--low-mb', '0')
  Check 'a floor of 0 is refused' ($zero.Code -eq 64)
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
if ($failed) { exit 1 }
