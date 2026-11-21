# Keeps this machine awake and unlocked for the unattended campaign, and puts it back after.
#
#   pwsh scripts/campaign-hold.ps1                  preview: every value it would change, changing nothing
#   pwsh scripts/campaign-hold.ps1 -Apply [-Hours N] sleep, hibernate and display timeouts to never (AC and DC), and
#                                                   the screen saver lock off, after saving every prior value; the hold
#                                                   expires N hours later (1 to 168, default 24)
#   pwsh scripts/campaign-hold.ps1 -Status          when the hold began and expires, each held value now and before,
#                                                   any automatic restore tried, and the restore command
#   pwsh scripts/campaign-hold.ps1 -Restore         writes the saved values back; the record goes only once all read back
#   pwsh scripts/campaign-hold.ps1 -Restore -Auto   the run guard's restore: tried once per hold, marked in the record
#                                                   before anything changes; a second is refused with the manual command
#
# The record, %LOCALAPPDATA%\Bosun\campaign-hold.json, is written before the first change and never replaced by a
# second -Apply, so the values it holds are always the ones from before the campaign, with when the hold began
# (Applied) and when it expires (Expires). A record that is not complete is refused before anything changes. Each
# powercfg call is stopped with its tree past -ProbeSeconds (120), and this process runs in a job that ends everything
# it started when it ends, interrupted or killed included.
#
# The run guard (scripts/lib/run_guard.py, release_hold) runs `-Restore -Auto` at the first hook event or status read
# after the run that the hold was applied for ends, or after the hold expires (D00 T03 §24). BOSUN_HOLD_RECORD,
# BOSUN_HOLD_POWERCFG and BOSUN_HOLD_LOCKKEY name the record, powercfg and the lock key when no parameter does, so the
# guard and a test act on the same targets.
[CmdletBinding(DefaultParameterSetName = 'Preview')]
param(
  [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
  [Parameter(ParameterSetName = 'Apply')][ValidateRange(1, 168)][int]$Hours = 24,
  [Parameter(ParameterSetName = 'Status')][switch]$Status,
  [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
  [Parameter(ParameterSetName = 'Restore')][switch]$Auto,
  # With -Auto: the hold the run guard chose (-ForHold), by when it was applied (yyyy-MM-ddTHH:mm:ssZ); any other record is refused.
  [Parameter(ParameterSetName = 'Restore')][string]$ForHold = '',  # not $Applied: PowerShell names ignore case, and -Apply's $applied is a time
  [string]$Record = ($env:BOSUN_HOLD_RECORD ? $env:BOSUN_HOLD_RECORD : (Join-Path $env:LOCALAPPDATA 'Bosun\campaign-hold.json')),
  [string]$PowerCfg = ($env:BOSUN_HOLD_POWERCFG ? $env:BOSUN_HOLD_POWERCFG : (Join-Path $env:SystemRoot 'System32\powercfg.exe')),
  [string]$LockKey = ($env:BOSUN_HOLD_LOCKKEY ? $env:BOSUN_HOLD_LOCKKEY : 'HKCU:\Control Panel\Desktop'),
  [ValidateRange(1, 3600)][int]$ProbeSeconds = 120
)$ErrorActionPreference = 'Stop'
# A refusal or a failure is one plain line, whatever threw it: an unreadable record, a powercfg past its limit.
trap { "FAIL $($_.Exception.Message)"; exit 1 }
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')
. (Join-Path $PSScriptRoot 'lib\job-measure.ps1')
Stop-TreeWithThisWindow
$PowerCfgSeconds = $ProbeSeconds

# The saved record, checked whole, or $null when there is none; an unreadable or incomplete one throws.
function Read-HoldRecord {
  $saved = Read-Json $Record
  if ($saved) { Assert-Record $saved $Record 'hold' }
  $saved
}

# Test-only fault injection: stop after this many changes, as a crash or a killed shell would.
$failAfter = [int]($env:BOSUN_TEST_FAIL_AFTER_CHANGES ?? '0')
$changes = 0
function Changed {
  $script:changes++
  if ($failAfter -and $script:changes -ge $failAfter) { throw "test: stopped after $($script:changes) change(s)" }
}

function Show-Plan($now) {
  "scheme $($now.Scheme)"
  foreach ($p in $now.Power) {
    foreach ($line in 'AC', 'DC') { "  $($p.Sub) $($p.Setting) $line`: $($p.$line) -> 0" }
  }
  "  $LockKey ScreenSaverIsSecure: $(if ($null -eq $now.Lock) { '(absent)' } else { $now.Lock }) -> 0"
}

# The one command that restores this hold, naming the targets this run was given when they are not the defaults.
function Get-RestoreCommand {
  $parts = @('pwsh scripts/campaign-hold.ps1 -Restore')
  # Single quotes, a quote in a path doubled: pasted as printed, a path holding $ or a backtick stays the path.
  $quote = { param($v) "'" + $v.Replace("'", "''") + "'" }
  if ($script:given.ContainsKey('Record') -or $env:BOSUN_HOLD_RECORD) { $parts += "-Record $(& $quote $Record)" }
  if ($script:given.ContainsKey('PowerCfg') -or $env:BOSUN_HOLD_POWERCFG) { $parts += "-PowerCfg $(& $quote $PowerCfg)" }
  if ($script:given.ContainsKey('LockKey') -or $env:BOSUN_HOLD_LOCKKEY) { $parts += "-LockKey $(& $quote $LockKey)" }
  $parts -join ' '
}
$script:given = $PSBoundParameters  # the script's own: inside a function, $PSBoundParameters is that function's

if ($Status) {
  $saved = Read-HoldRecord
  if (-not $saved) { 'no hold'; exit 0 }
  # First: a failed query is the one FAIL line, nothing before it, with what to do about it.
  try { $now = Get-HoldState $PowerCfg $LockKey $saved.Scheme }
  catch { "FAIL the held values can't be read ($($_.Exception.Message)); check that powercfg /q $($saved.Scheme) answers, then run -Status again; the saved values are untouched in $Record"; exit 1 }
  $life = Get-HoldLife $saved $Record
  $age = [datetime]::UtcNow - $life.Applied
  "hold applied $($life.Applied.ToString('u')) ($([math]::Floor($age.TotalHours)) h $($age.Minutes) min ago), $(if ($life.Expires) { "expires $($life.Expires.ToString('u'))" } else { 'no expiry (recorded before expiries were): it goes back when its run ends' })"
  "scheme $($saved.Scheme)"
  for ($i = 0; $i -lt $saved.Power.Count; $i++) {
    foreach ($line in 'AC', 'DC') { "  $($saved.Power[$i].Sub) $($saved.Power[$i].Setting) $line`: now $($now.Power[$i].$line), before $($saved.Power[$i].$line)" }
  }
  $show = { param($v) if ($null -eq $v) { '(absent)' } else { $v } }
  "  $LockKey ScreenSaverIsSecure: now $(& $show $now.Lock), before $(& $show $saved.Lock)"
  if ($life.RestoreTried) { "an automatic restore was tried at $($life.RestoreTried.ToString('u')) and left the hold in place" }
  "restore: $(Get-RestoreCommand)"
  exit 0
}

if ($Restore) {
  $saved = Read-HoldRecord
  if (-not $saved) { "nothing to restore: no record at $Record"; exit 0 }
  if ($Auto) {
    $life = Get-HoldLife $saved $Record
    if ($ForHold -and $life.Applied.ToString('yyyy-MM-ddTHH:mm:ssZ') -ne $ForHold) { "FAIL the hold at $Record was applied $($life.Applied.ToString('u')), not at the $ForHold the run guard chose: it is a newer hold, and nothing changed"; exit 3 }
    if ($life.RestoreTried) { "FAIL an automatic restore was already tried at $($life.RestoreTried.ToString('u')); restore by hand: $(Get-RestoreCommand)"; exit 2 }
    # Marked before anything changes, so a restore that is killed or fails is never tried again by itself.
    $saved | Add-Member -NotePropertyName Applied -NotePropertyValue $life.Applied.ToString('o') -Force
    if ($life.Expires) { $saved | Add-Member -NotePropertyName Expires -NotePropertyValue $life.Expires.ToString('o') -Force }
    $saved | Add-Member -NotePropertyName RestoreTried -NotePropertyValue ([datetime]::UtcNow.ToString('o')) -Force
    Save-Json $saved $Record
    Changed
  }
  $failed = @()
  foreach ($p in $saved.Power) {
    foreach ($line in 'AC', 'DC') {
      try { Set-PowerValue $PowerCfg $saved.Scheme $p.Sub $p.Setting $line ([int]$p.$line) }
      catch { $failed += "$($p.Sub) $($p.Setting) $line ($($_.Exception.Message))" }
    }
  }
  try { Set-LockValue $LockKey $saved.Lock } catch { $failed += "ScreenSaverIsSecure ($($_.Exception.Message))" }
  # Only what reads back counts as restored.
  $now = Get-HoldState $PowerCfg $LockKey $saved.Scheme
  for ($i = 0; $i -lt $saved.Power.Count; $i++) {
    foreach ($line in 'AC', 'DC') {
      if ($now.Power[$i].$line -ne [int]$saved.Power[$i].$line) { $failed += "$($saved.Power[$i].Setting) $line reads $($now.Power[$i].$line), not $($saved.Power[$i].$line)" }
    }
  }
  if ($now.Lock -ne $saved.Lock) { $failed += "ScreenSaverIsSecure reads $($now.Lock), not $($saved.Lock)" }
  if ($failed) {
    "FAIL restore left the record at $Record for a retry. Not restored:"
    $failed | Sort-Object -Unique | ForEach-Object { "  $_" }
    exit 1
  }
  Remove-Item -LiteralPath $Record
  'restored every held value; record removed'
  exit 0
}

$before = Get-HoldState $PowerCfg $LockKey
if (-not $Apply) {
  'preview (nothing changed; -Apply to hold, -Restore to undo):'
  Show-Plan $before
  exit 0
}

$saved = Read-HoldRecord  # a corrupt or incomplete record throws here, before anything changes
if ($saved) {
  "keeping the values saved at $Record, from before the hold"
} else {
  $applied = [datetime]::UtcNow
  $before | Add-Member -NotePropertyName Applied -NotePropertyValue $applied.ToString('o')
  $before | Add-Member -NotePropertyName Expires -NotePropertyValue $applied.AddHours($Hours).ToString('o')
  Save-Json $before $Record
  "saved the prior values to $Record; the hold expires $($applied.AddHours($Hours).ToString('u'))"
}
$scheme = if ($saved) { $saved.Scheme } else { $before.Scheme }
foreach ($p in $HoldPower) {
  foreach ($line in 'AC', 'DC') { Set-PowerValue $PowerCfg $scheme $p.Sub $p.Setting $line 0; Changed }
}
Set-LockValue $LockKey '0'; Changed
$now = Get-HoldState $PowerCfg $LockKey $scheme
$wrong = @()
foreach ($p in $now.Power) { foreach ($line in 'AC', 'DC') { if ($p.$line -ne 0) { $wrong += "$($p.Setting) $line reads $($p.$line)" } } }
if ($now.Lock -ne '0') { $wrong += "ScreenSaverIsSecure reads $($now.Lock)" }
if ($wrong) { "FAIL held values didn't read back: $($wrong -join '; ')"; exit 1 }
'held: no sleep, hibernate or display timeout, and no screen saver lock'
