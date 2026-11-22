# scripts/campaign-hold.ps1 against a fake powercfg (tests/fakes/powercfg.py) and a registry key of its own (D00 T03
# §28): -Apply refuses, since a campaign's launcher now holds the machine awake itself; -Status and -Restore put back a
# hold an older version left. Nothing here touches the real power scheme or desktop settings.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
. (Join-Path $repo 'scripts\lib\scratch.ps1')
$work = New-BosunScratchFolder -Name 'hold'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$hold = Join-Path $repo 'scripts\campaign-hold.ps1'
$python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
$powercfg = Join-Path $work 'powercfg.cmd'
Set-Content $powercfg "@`"$python`" `"$(Join-Path $PSScriptRoot 'fakes\powercfg.py')`" %*" -Encoding ascii
$state = Join-Path $work 'power.json'
$record = Join-Path $work 'campaign-hold.json'
$keyPath = "HKCU:\Software\BosunTest\hold-$PID"
$scheme = '381b4222-f694-41f0-9685-ff5bb260df2e'
$values = [ordered]@{ 'SUB_SLEEP/HIBERNATEIDLE' = @{ AC = 0; DC = 10800 }; 'SUB_SLEEP/STANDBYIDLE' = @{ AC = 1800; DC = 600 }; 'SUB_VIDEO/VIDEOIDLE' = @{ AC = 7200; DC = 180 } }
$original = (@{ scheme = $scheme; values = $values } | ConvertTo-Json -Depth 4) + "`n"
. (Join-Path $repo 'scripts\lib\campaign.ps1')

function Reset($lock) {  # untyped: [string] would turn an absent value into an empty one
  [IO.File]::WriteAllText($state, $original)
  Remove-Item -LiteralPath $record -ErrorAction Ignore
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  New-Item -Path $keyPath -Force | Out-Null
  if ($null -ne $lock) { Set-ItemProperty -LiteralPath $keyPath -Name ScreenSaverIsSecure -Value $lock -Type String }
}
# The machine as an older version's -Apply left it: the record of the values before, the timeouts at 0, the lock off.
function Seed($lock) {
  Reset $lock
  $saved = [ordered]@{ Scheme = $scheme; Power = @($HoldPower | ForEach-Object { [ordered]@{ Sub = $_.Sub; Setting = $_.Setting; AC = $values["$($_.Sub)/$($_.Setting)"].AC; DC = $values["$($_.Sub)/$($_.Setting)"].DC } }); Lock = $lock; Applied = [datetime]::UtcNow.ToString('o') }
  [IO.File]::WriteAllText($record, ($saved | ConvertTo-Json -Depth 5))
  $held = @{ scheme = $scheme; values = [ordered]@{} }
  foreach ($k in $values.Keys) { $held.values[$k] = @{ AC = 0; DC = 0 } }
  [IO.File]::WriteAllText($state, ($held | ConvertTo-Json -Depth 4))
  Set-ItemProperty -LiteralPath $keyPath -Name ScreenSaverIsSecure -Value '0' -Type String
}
function Hold([string[]]$extra = @()) {
  $out = & $pwsh -NoProfile -File $hold @extra -Record $record -PowerCfg $powercfg -LockKey $keyPath 2>&1
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}
function Power { Get-Content $state -Raw | ConvertFrom-Json }
function Lock { (Get-Item -LiteralPath $keyPath).GetValue('ScreenSaverIsSecure', $null) }
function Restored { $v = (Power).values; ($values.Keys | Where-Object { $v.$_.AC -ne $values[$_].AC -or $v.$_.DC -ne $values[$_].DC }).Count -eq 0 }

$env:BOSUN_FAKE_POWERCFG = $state
try {
  # -Apply refuses and changes nothing; with no switch, one line says what the script is now for.
  Reset '1'
  $r = Hold @('-Apply')
  Check ($r.Code -eq 2 -and $r.Text -match 'a campaign no longer changes the power plan' -and $r.Text -match 'scripts/start-run.ps1' -and [IO.File]::ReadAllText($state) -eq $original -and (Lock) -eq '1' -and -not (Test-Path $record)) '-Apply refuses, names the launcher and changes nothing'
  $r = Hold @('-Apply', '-Hours', '8')
  Check ($r.Code -eq 2 -and $r.Text -match 'pwsh scripts/start-run.ps1 -Minutes 480' -and -not (Test-Path $record)) 'the old timed apply names the launcher''s -Minutes for the same time'
  $r = Hold
  Check ($r.Code -eq 0 -and $r.Text -match '-Status \| -Restore: for a hold an older version left') 'with no switch, it says what it is now for'

  foreach ($lock in '1', $null) {
    $label = if ($null -eq $lock) { 'an absent lock value' } else { 'a lock value of 1' }
    Seed $lock
    $r = Hold @('-Status')
    Check ($r.Code -eq 0 -and $r.Text -match 'a hold an older version left: applied' -and $r.Text -match 'VIDEOIDLE AC: now 0, before 7200' -and $r.Text -match "ScreenSaverIsSecure: now 0, before $(if ($null -eq $lock) { '\(absent\)' } else { '1' })") "status reads each held value now and before ($label)"
    $r = Hold @('-Restore')
    Check ($r.Code -eq 0 -and (Restored) -and (Lock) -eq $lock -and -not (Test-Path $record)) "restore puts every value back and removes the record ($label)"
  }

  # The printed command, run as printed, restores; it quotes each path literally.
  Seed '1'
  $r = Hold @('-Status')
  $line = $r.Text -split "`n" | Where-Object { $_ -like 'restore: *' }
  Check ($line -match "-Record '[^']+'" -and $line -match "-LockKey 'HKCU:") "the restore command single-quotes each path ($line)"
  $command = $line -replace '^restore: pwsh scripts/campaign-hold.ps1', ''
  $out = Invoke-Expression "& `$pwsh -NoProfile -File `$hold $command 2>&1"
  Check ($LASTEXITCODE -eq 0 -and (Restored) -and (Lock) -eq '1' -and -not (Test-Path $record)) "the printed command, run as printed, restores every value ($($out | Select-Object -Last 1))"
  $r = Hold @('-Status')
  Check ($r.Code -eq 0 -and $r.Text -eq 'no hold') 'status with no record says no hold'

  # A record from before Applied was kept is read with its file's time.
  Seed '1'
  $rec = Get-Content $record -Raw | ConvertFrom-Json
  $rec.PSObject.Properties.Remove('Applied')
  [IO.File]::WriteAllText($record, ($rec | ConvertTo-Json -Depth 5))
  (Get-Item -LiteralPath $record).LastWriteTimeUtc = [datetime]::new(2026, 11, 20, 8, 0, 0, [DateTimeKind]::Utc)
  $r = Hold @('-Status')
  Check ($r.Code -eq 0 -and $r.Text -match 'applied 2026-11-20 08:00:00Z') 'a record with no Applied is read with its file''s time'

  # Records that are not whole are refused by -Status and -Restore, and left as they are.
  foreach ($case in @(@{ Text = '{ not json'; Says = 'is not a readable record' }, @{ Text = '[]'; Says = 'is not a readable record \(it holds no object\)' },
                      @{ Text = '{"Scheme": "381b4222-f694-41f0-9685-ff5bb260df2e", "Power": [], "Lock": "1"}'; Says = 'is not a complete hold record \(Power does not hold the 3 held settings\)' })) {
    Reset '1'
    [IO.File]::WriteAllText($record, $case.Text)
    foreach ($switch in '-Status', '-Restore') {
      $r = Hold @($switch)
      Check ($r.Code -ne 0 -and $r.Text -match $case.Says -and [IO.File]::ReadAllText($record) -eq $case.Text -and [IO.File]::ReadAllText($state) -eq $original) "$switch refuses a record that $($case.Says -replace '\\', '') and leaves it"
    }
  }

  # A status whose power query fails is one FAIL line with its fix.
  Seed '1'
  $live = Get-Content $state -Raw | ConvertFrom-Json
  $live.scheme = '00000000-0000-0000-0000-000000000000'
  [IO.File]::WriteAllText($state, ($live | ConvertTo-Json -Depth 8))
  $r = Hold @('-Status')
  Check ($r.Code -eq 1 -and $r.Text -match "^FAIL the held values can't be read" -and @($r.Text -split "`n").Count -eq 1) 'status with a failing power query is one FAIL line with its fix'

  # A powercfg write that hangs is stopped at -ProbeSeconds, with its tree.
  Seed '1'
  $hangPid = "$state.hang.pid"
  $env:BOSUN_FAKE_POWERCFG_HANG = 'write'
  try { $r = Hold @('-Restore', '-ProbeSeconds', '2') } finally { Remove-Item Env:BOSUN_FAKE_POWERCFG_HANG }
  $hung = if (Test-Path $hangPid) { [int](Get-Content $hangPid) } else { 0 }
  Check ($r.Code -ne 0 -and $r.Text -match 'gave no answer within 2 seconds and was stopped' -and $hung -and -not (Get-Process -Id $hung -ErrorAction Ignore) -and (Test-Path $record)) "a hung powercfg write fails at -ProbeSeconds and is stopped, the record kept (pid $hung)"
  Remove-Item -LiteralPath $hangPid -ErrorAction Ignore

  # A restore after the owner switched to another plan writes the held plan's values and leaves the owner's plan in force
  # (round 1 of D00 T03 §24).
  Seed '1'
  $live = Get-Content $state -Raw | ConvertFrom-Json
  $live | Add-Member -NotePropertyName active -NotePropertyValue '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' -Force
  [IO.File]::WriteAllText($state, ($live | ConvertTo-Json -Depth 8))
  $r = Hold @('-Restore')
  $after = Get-Content $state -Raw | ConvertFrom-Json
  Check ($r.Code -eq 0 -and $after.active -eq '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' -and $after.values.'SUB_VIDEO/VIDEOIDLE'.AC -eq 7200) "a restore leaves the plan the owner switched to in force ($($after.active))"
  # A running plan Windows will not take back is a failed restore: the record and its originals stay.
  Seed '1'
  $env:BOSUN_FAKE_POWERCFG_FAIL_ACTIVATE = '1'
  try { $r = Hold @('-Restore') } finally { Remove-Item Env:BOSUN_FAKE_POWERCFG_FAIL_ACTIVATE }
  Check ($r.Code -eq 1 -and $r.Text -match 'left the record' -and (Test-Path $record)) 'a failed /setactive keeps the record for a retry'
  # A §24 record carries Expires; one that is not a time is refused by name, the record left.
  Seed '1'
  $rec = Get-Content $record -Raw | ConvertFrom-Json
  $rec | Add-Member -NotePropertyName Expires -NotePropertyValue '2030-01-01' -Force
  [IO.File]::WriteAllText($record, ($rec | ConvertTo-Json -Depth 8))
  $r = Hold @('-Status')
  Check ($r.Code -ne 0 -and $r.Text -match 'Expires is not a time' -and (Test-Path $record)) 'a date-only Expires is refused by name, and the record is left'

  # A restore that fails keeps the record and names what it couldn't put back; a retry then succeeds.
  Seed '1'
  $env:BOSUN_FAKE_POWERCFG_FAIL = '1'
  $r = Hold @('-Restore')
  Remove-Item Env:BOSUN_FAKE_POWERCFG_FAIL
  Check ($r.Code -eq 1 -and $r.Text -match 'left the record' -and $r.Text -match 'STANDBYIDLE' -and (Test-Path $record)) 'a failed restore keeps the record and names each value'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and (Restored) -and (Lock) -eq '1' -and -not (Test-Path $record)) 'a retry then restores everything'
  $r = Hold @('-Restore')
  Check ($r.Code -eq 0 -and $r.Text -match 'nothing to restore') 'restore with no record says so'

  # The targets come from BOSUN_HOLD_* when no parameter names them.
  Seed '1'
  $env:BOSUN_HOLD_RECORD = $record; $env:BOSUN_HOLD_POWERCFG = $powercfg; $env:BOSUN_HOLD_LOCKKEY = $keyPath
  try {
    $out = & $pwsh -NoProfile -File $hold -Restore 2>&1
    Check ($LASTEXITCODE -eq 0 -and (Restored) -and -not (Test-Path $record)) "the BOSUN_HOLD_* variables name the targets ($($out | Select-Object -Last 1))"
  } finally { Remove-Item Env:BOSUN_HOLD_RECORD, Env:BOSUN_HOLD_POWERCFG, Env:BOSUN_HOLD_LOCKKEY -ErrorAction Ignore }
}
finally {
  Remove-Item Env:BOSUN_FAKE_POWERCFG -ErrorAction Ignore
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
