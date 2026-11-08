# The preflight's CMake check, and the PATH script, against fake CMakes and a test variable. Nothing here touches the
# real user PATH.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-preflight-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$system = Join-Path $env:SystemRoot 'System32'

function Fake([string]$name, [string]$body) {
  $dir = Join-Path $work $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content (Join-Path $dir 'cmake.cmd') "@echo off`r`n$body" -Encoding ascii
  $dir
}
function Preflight([string]$path, [string[]]$extra = @()) {
  $saved = $env:PATH
  $env:PATH = $path
  try { $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-preflight.ps1') @extra 2>&1 }
  finally { $env:PATH = $saved }
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}

try {
  $good = Fake 'good' 'echo cmake version 3.31.6'
  $old = Fake 'old' 'echo cmake version 3.20.0'
  $broken = Fake 'broken' "echo something went wrong`r`nexit /b 1"
  $vsNone = Join-Path $work 'vs-empty'
  New-Item -ItemType Directory -Force $vsNone | Out-Null
  $vswhereNoCMake = Join-Path $work 'vswhere-nocmake.cmd'
  Set-Content $vswhereNoCMake "@echo $vsNone" -Encoding ascii

  $r = Preflight "$good;$system"
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   cmake 3\.31\.6') 'a CMake of 3.25 or later passes'
  $r = Preflight $system
  Check ($r.Code -eq 1 -and $r.Text -match 'cmake is not on PATH' -and $r.Text -match 'fix: ') 'no CMake fails with a fix'
  $r = Preflight $system @('-VsWhere', (Join-Path $work 'no-vswhere.exe'))
  Check ($r.Code -eq 1 -and $r.Text -match "vswhere isn't at") 'a missing vswhere is named'
  $r = Preflight $system @('-VsWhere', $vswhereNoCMake)
  Check ($r.Code -eq 1 -and $r.Text -match 'has no CMake') 'Visual Studio without CMake is named'
  $r = Preflight "$old;$good;$system"
  Check ($r.Code -eq 1 -and $r.Text -match 'older than the 3\.25') 'an older CMake first on PATH fails'
  $r = Preflight "$broken;$good;$system"
  Check ($r.Code -eq 1 -and $r.Text -match "doesn't run") 'a broken CMake first on PATH fails'
  $vs =& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -property installationPath 2>$null
  if ($vs) {
    $r = Preflight $system
    Check ($r.Text -match 'add-cmake-to-path\.ps1 -Apply') 'with Visual Studio CMake installed, the fix is the PATH script'
  }

  # The PATH script, on a variable of its own.
  $variable = "BOSUN_PREFLIGHT_TEST_$PID"
  $record = Join-Path $work 'path-changes.json'
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
  $key.SetValue($variable, 'C:\keep\a;%USERPROFILE%\keep b', [Microsoft.Win32.RegistryValueKind]::ExpandString)
  $read = { $key.GetValue($variable, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
  $script = Join-Path $repo 'scripts\add-cmake-to-path.ps1'
  $cmakeHome = Join-Path $work 'vs-cmake'
  New-Item -ItemType Directory -Force $cmakeHome | Out-Null
  Set-Content (Join-Path $cmakeHome 'cmake.exe') '' # a stand-in: the script only checks that it is there
  $good = $cmakeHome
  $scriptArgs = @('-Folder', $good, '-Variable', $variable, '-Record', $record)
  $preview = & $pwsh -NoProfile -File $script @scriptArgs
  Check ($preview -match 'would add' -and (& $read) -eq 'C:\keep\a;%USERPROFILE%\keep b') 'the preview changes nothing'
  & $pwsh -NoProfile -File $script -Apply @scriptArgs | Out-Null
  Check ((& $read) -eq "C:\keep\a;%USERPROFILE%\keep b;$good") 'apply adds the folder last and keeps every entry as written'
  $again = & $pwsh -NoProfile -File $script -Apply @scriptArgs
  Check ($again -match 'already' -and ((& $read) -split ';' | Where-Object { $_ -eq $good }).Count -eq 1) 'a repeat adds nothing'
  Check ((Get-Content $record -Raw | ConvertFrom-Json).$variable -contains $good) 'what it added is recorded'
  & $pwsh -NoProfile -File $script -Undo @scriptArgs | Out-Null
  # A record that can't be saved stops the change before it happens.
  $blocker = Join-Path $work 'not-a-folder'
  Set-Content $blocker 'a file where the record folder would go'
  $before = & $read
  & $pwsh -NoProfile -File $script -Apply -Folder $good -Variable $variable -Record (Join-Path $blocker 'path-changes.json') 2>&1 | Out-Null
  Check ($LASTEXITCODE -ne 0 -and (& $read) -eq $before) 'an unsaveable record stops the change before PATH moves'
  & $pwsh -NoProfile -File $script -Apply @scriptArgs | Out-Null
  & $pwsh -NoProfile -File $script -Undo @scriptArgs | Out-Null
  Check ((& $read) -eq 'C:\keep\a;%USERPROFILE%\keep b') 'undo removes only its own entry'
  Check (-not ((Get-Content $record -Raw | ConvertFrom-Json).PSObject.Properties.Name -contains $variable)) 'undo clears the record'
}
finally {
  if ($key -and $variable) { $key.DeleteValue($variable, $false) }
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
