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

  # A record that parses but is not whole is refused the same way: restoring it would put back nothing, or the wrong thing.
  Reset '1'
  $partial = '{"Scheme": "381b4222-f694-41f0-9685-ff5bb260df2e", "Power": [], "Lock": "1"}'
  [IO.File]::WriteAllText($record, $partial)
  $r = Hold @('-Apply')
  Check ($r.Code -ne 0 -and $r.Text -match 'is not a complete hold record \(Power does not hold the 3 held settings\)' -and [IO.File]::ReadAllText($record) -eq $partial -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1') 'an incomplete record is refused with the field that is wrong, and nothing changes'
  $r = Hold @('-Restore')
  Check ($r.Code -ne 0 -and $r.Text -match 'is not a complete hold record' -and [IO.File]::ReadAllText($record) -eq $partial) 'and restore refuses it too'

  Reset '1'
  [IO.File]::WriteAllText($record, '[]')
  $r = Hold @('-Restore')
  Check ($r.Code -ne 0 -and $r.Text -match 'is not a readable record \(it holds no object\)' -and [IO.File]::ReadAllText($record) -eq '[]') 'a record holding a list is refused, never read as no record'

  # A powercfg write that hangs is stopped at -ProbeSeconds, with its tree.
  Reset '1'
  $hangPid = "$state.hang.pid"
  $env:BOSUN_FAKE_POWERCFG_HANG = 'write'
  try { $r = Hold @('-Apply', '-ProbeSeconds', '2') } finally { Remove-Item Env:BOSUN_FAKE_POWERCFG_HANG }
  $hung = if (Test-Path $hangPid) { [int](Get-Content $hangPid) } else { 0 }
  Check ($r.Code -ne 0 -and $r.Text -match 'gave no answer within 2 seconds and was stopped' -and $hung -and -not (Get-Process -Id $hung -ErrorAction Ignore)) "a hung powercfg write fails at -ProbeSeconds and is stopped (pid $hung)"
  Remove-Item -LiteralPath $hangPid -ErrorAction Ignore

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

  # The hold's life (D00 T03 §24): when it began and expires, kept whole by a repeat apply.
  Reset '1'
  $r = Hold @('-Apply')
  $rec = Get-Content $record -Raw | ConvertFrom-Json
  $applied = ([datetime]$rec.Applied).ToUniversalTime(); $expires = ([datetime]$rec.Expires).ToUniversalTime()
  Check ($r.Code -eq 0 -and [math]::Abs(([datetime]::UtcNow - $applied).TotalMinutes) -lt 5 -and ($expires - $applied).TotalHours -eq 24) 'apply records when the hold began and a 24 hour expiry'
  $saved = [IO.File]::ReadAllText($record)
  $r = Hold @('-Apply', '-Hours', '5')
  Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($record) -eq $saved) 'a repeat apply with other hours keeps the originals, the start and the expiry'
  Hold @('-Restore') | Out-Null
  foreach ($bad in 0, 169) {
    Reset '1'
    $r = Hold @('-Apply', '-Hours', "$bad")
    Check ($r.Code -ne 0 -and -not (Test-Path $record) -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1') "-Hours $bad is refused with nothing written or changed"
  }
  Reset '1'
  Hold @('-Apply') | Out-Null
  $rec = Get-Content $record -Raw | ConvertFrom-Json
  $rec.Expires = 'soon'
  $broken = $rec | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText($record, $broken)
  $r = Hold @('-Restore')
  Check ($r.Code -ne 0 -and $r.Text -match 'Expires is not a time' -and [IO.File]::ReadAllText($record) -eq $broken) 'a malformed Expires is refused by name, and the record is left'
  # A record from before these fields began when its file was written, and expires a day later.
  $rec.PSObject.Properties.Remove('Applied'); $rec.PSObject.Properties.Remove('Expires')
  [IO.File]::WriteAllText($record, ($rec | ConvertTo-Json -Depth 8))
  $then = [datetime]::new(2026, 11, 20, 8, 0, 0, [DateTimeKind]::Utc)
  (Get-Item -LiteralPath $record).LastWriteTimeUtc = $then
  $r = Hold @('-Status')
  Check ($r.Code -eq 0 -and $r.Text -match 'hold applied 2026-11-20 08:00:00Z' -and $r.Text -match 'expires 2026-11-21 08:00:00Z') "an old three-field record began when it was written ($(($r.Text -split "`n")[0]))"
  Hold @('-Restore') | Out-Null

  # -Status reads the saved plan's values now and before, after one drifts and another plan becomes active.
  Reset $null
  Hold @('-Apply') | Out-Null
  $live = Get-Content $state -Raw | ConvertFrom-Json
  $live.values.'SUB_VIDEO/VIDEOIDLE'.AC = 99
  $live | Add-Member -NotePropertyName active -NotePropertyValue '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
  [IO.File]::WriteAllText($state, ($live | ConvertTo-Json -Depth 8))
  $r = Hold @('-Status')
  Check ($r.Code -eq 0 -and $r.Text -match 'scheme 381b4222' -and $r.Text -match 'VIDEOIDLE AC: now 99, before 7200' -and $r.Text -match 'STANDBYIDLE DC: now 0, before 600' -and $r.Text -match 'ScreenSaverIsSecure: now 0, before \(absent\)') "status reads each held value now and before, an absent lock as absent"
  $command = ($r.Text -split "`n" | Where-Object { $_ -like 'restore: *' }) -replace '^restore: pwsh scripts/campaign-hold.ps1', ''
  Check ($command -match '-Record' -and $command -match '-LockKey') "status prints the restore command with this run's targets ($command)"
  $out = Invoke-Expression "& `$pwsh -NoProfile -File `$hold $command 2>&1"
  $now = (Get-Content $state -Raw | ConvertFrom-Json).values
  Check ($LASTEXITCODE -eq 0 -and $now.'SUB_VIDEO/VIDEOIDLE'.AC -eq 7200 -and $now.'SUB_SLEEP/STANDBYIDLE'.DC -eq 600 -and $null -eq (Lock) -and -not (Test-Path $record)) "the printed command, run as printed, restores every value ($($out | Select-Object -Last 1))"
  $r = Hold @('-Status')
  Check ($r.Code -eq 0 -and $r.Text -eq 'no hold') 'status with no record says no hold'
  Reset '1'
  [IO.File]::WriteAllText($record, '{ not json')
  $r = Hold @('-Status')
  Check ($r.Code -eq 1 -and $r.Text -match '^FAIL .*is not a readable record') 'status of an unreadable record is one FAIL line'
  Reset '1'
  Hold @('-Apply') | Out-Null
  $env:BOSUN_FAKE_POWERCFG_FAIL = '1'
  $live = Get-Content $state -Raw | ConvertFrom-Json
  $live.scheme = '00000000-0000-0000-0000-000000000000'
  [IO.File]::WriteAllText($state, ($live | ConvertTo-Json -Depth 8))
  $r = Hold @('-Status')
  Remove-Item Env:BOSUN_FAKE_POWERCFG_FAIL
  Check ($r.Code -eq 1 -and $r.Text -match '^FAIL ') "status with a failing power query is one FAIL line ($r.Text)"

  # -Auto: marked before anything changes, tried once, then the manual command.
  Reset '1'
  Hold @('-Apply') | Out-Null
  $env:BOSUN_TEST_FAIL_AFTER_CHANGES = '1'
  $r = Hold @('-Restore', '-Auto')
  Remove-Item Env:BOSUN_TEST_FAIL_AFTER_CHANGES
  $rec = Get-Content $record -Raw | ConvertFrom-Json
  Check ($r.Code -ne 0 -and $rec.RestoreTried -and (AllHeld)) 'an automatic restore marks the record before its first change'
  $r = Hold @('-Restore', '-Auto')
  Check ($r.Code -eq 2 -and $r.Text -match 'already tried' -and $r.Text -match 'restore by hand: pwsh scripts/campaign-hold.ps1 -Restore' -and (AllHeld)) 'a second automatic restore is refused with the manual command, changing nothing'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1' -and -not (Test-Path $record)) 'the manual restore still restores after it'

  # The targets come from BOSUN_HOLD_* when no parameter names them, as the run guard runs it.
  Reset '1'
  $env:BOSUN_HOLD_RECORD = $record; $env:BOSUN_HOLD_POWERCFG = $powercfg; $env:BOSUN_HOLD_LOCKKEY = $keyPath
  try {
    $out = & $pwsh -NoProfile -File $hold -Apply 2>&1
    Check ($LASTEXITCODE -eq 0 -and (Test-Path $record) -and (AllHeld)) "the BOSUN_HOLD_* variables name the targets ($($out | Select-Object -Last 1))"
    $out = & $pwsh -NoProfile -File $hold -Restore 2>&1
    Check ($LASTEXITCODE -eq 0 -and [IO.File]::ReadAllText($state) -eq $original -and -not (Test-Path $record)) 'and restore uses them too'
  } finally { Remove-Item Env:BOSUN_HOLD_RECORD, Env:BOSUN_HOLD_POWERCFG, Env:BOSUN_HOLD_LOCKKEY -ErrorAction Ignore }
}
finally {
  Remove-Item Env:BOSUN_FAKE_POWERCFG -ErrorAction Ignore
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
