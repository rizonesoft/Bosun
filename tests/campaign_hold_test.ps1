# scripts/campaign-hold.ps1 against a fake powercfg (tests/fakes/powercfg.py) and a registry key of its own. Nothing
# here touches the real power scheme or desktop settings.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-hold-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$hold = Join-Path $repo 'scripts\campaign-hold.ps1'
$python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
$powercfg = Join-Path $work 'powercfg.cmd'
Set-Content $powercfg "@`"$python`" `"$(Join-Path $PSScriptRoot 'fakes\powercfg.py')`" %*" -Encoding ascii
$state = Join-Path $work 'power.json'
$record = Join-Path $work 'campaign-hold.json'
$keyPath = "HKCU:\Software\BosunTest\hold-$PID"
$original = @'
{
  "scheme": "381b4222-f694-41f0-9685-ff5bb260df2e",
  "values": {
    "SUB_SLEEP/HIBERNATEIDLE": {
      "AC": 0,
      "DC": 10800
    },
    "SUB_SLEEP/STANDBYIDLE": {
      "AC": 1800,
      "DC": 600
    },
    "SUB_VIDEO/VIDEOIDLE": {
      "AC": 7200,
      "DC": 180
    }
  }
}

'@ -replace "`r`n", "`n"

function Reset($lock) {  # untyped: [string] would turn an absent value into an empty one
  [IO.File]::WriteAllText($state, $original)
  Remove-Item -LiteralPath $record -ErrorAction Ignore
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  New-Item -Path $keyPath -Force | Out-Null
  if ($null -ne $lock) { Set-ItemProperty -LiteralPath $keyPath -Name ScreenSaverIsSecure -Value $lock -Type String }
}
function Hold([string[]]$extra = @()) {
  $out = & $pwsh -NoProfile -File $hold @extra -Record $record -PowerCfg $powercfg -LockKey $keyPath 2>&1
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}
function Power { Get-Content $state -Raw | ConvertFrom-Json }
function Lock { (Get-Item -LiteralPath $keyPath).GetValue('ScreenSaverIsSecure', $null) }
function AllHeld { $v = (Power).values; @($v.PSObject.Properties | Where-Object { $_.Value.AC -ne 0 -or $_.Value.DC -ne 0 }).Count -eq 0 }

$env:BOSUN_FAKE_POWERCFG = $state
try {
  foreach ($lock in '1', $null) {
    $label = if ($null -eq $lock) { 'an absent lock value' } else { 'a lock value of 1' }
    Reset $lock
    $r = Hold
    Check ($r.Code -eq 0 -and $r.Text -match 'preview' -and $r.Text -match 'VIDEOIDLE AC: 7200 -> 0' -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq $lock -and -not (Test-Path $record)) "the preview names each change and changes nothing ($label)"
    $r = Hold @('-Apply')
    Check ($r.Code -eq 0 -and (AllHeld) -and (Lock) -eq '0' -and (Test-Path $record)) "apply holds every value and reads them back ($label)"
    $saved = [IO.File]::ReadAllText($record)
    $r = Hold @('-Apply')
    Check ($r.Code -eq 0 -and $r.Text -match 'keeping the values saved' -and [IO.File]::ReadAllText($record) -eq $saved) "a repeat apply keeps the first saved values ($label)"
    $r = Hold @('-Restore')
    Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq $lock -and -not (Test-Path $record)) "restore puts every value back byte for byte and removes the record ($label)"
  }

  # An apply that stops after its first change, then a second apply: the record still holds the originals.
  Reset '1'
  $env:BOSUN_TEST_FAIL_AFTER_CHANGES = '1'
  $r = Hold @('-Apply')
  Remove-Item Env:BOSUN_TEST_FAIL_AFTER_CHANGES
  Check ($r.Code -ne 0 -and (Test-Path $record) -and [IO.File]::ReadAllText($state) -ne $original) 'an apply interrupted after its first change leaves its record behind'
  $r = Hold @('-Apply')
  Check ($r.Code -eq 0 -and $r.Text -match 'keeping the values saved' -and (AllHeld)) 'the next apply finishes the hold from the same record'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1') 'and restore returns the values from before the first apply'

  # The same with no lock value to begin with: interrupted before the lock is written, restore leaves it absent.
  Reset $null
  $env:BOSUN_TEST_FAIL_AFTER_CHANGES = '1'
  $r = Hold @('-Apply')
  Remove-Item Env:BOSUN_TEST_FAIL_AFTER_CHANGES
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and $null -eq (Lock) -and -not (Test-Path $record)) "restoring a lock value that was absent and still is succeeds ($(($r.Text -split "`n" | Select-Object -Last 1)))"

  # A corrupt record is refused, before anything changes, and is left as it was.
  Reset '1'
  [IO.File]::WriteAllText($record, '{ not json')
  $r = Hold @('-Apply')
  Check ($r.Code -ne 0 -and $r.Text.Contains($record) -and [IO.File]::ReadAllText($record) -eq '{ not json' -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1') 'a corrupt record is refused with its path, and nothing changes'
  $r = Hold @('-Restore')
  Check ($r.Code -ne 0 -and [IO.File]::ReadAllText($record) -eq '{ not json') 'and restore refuses it too'

  # A restore that fails keeps the record and names what it couldn't put back; a retry then succeeds.
  Reset '1'
  Hold @('-Apply') | Out-Null
  $env:BOSUN_FAKE_POWERCFG_FAIL = '1'
  $r = Hold @('-Restore')
  Remove-Item Env:BOSUN_FAKE_POWERCFG_FAIL
  Check ($r.Code -eq 1 -and $r.Text -match 'left the record' -and $r.Text -match 'STANDBYIDLE' -and (Test-Path $record)) 'a failed restore keeps the record and names each value'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1' -and -not (Test-Path $record)) 'a retry then restores everything'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and $r.Text -match 'nothing to restore') 'restore with no record says so'
}
finally {
  Remove-Item Env:BOSUN_FAKE_POWERCFG -ErrorAction Ignore
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
