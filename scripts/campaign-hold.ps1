# Keeps this machine awake and unlocked for the unattended campaign, and puts it back after.
#
#   pwsh scripts/campaign-hold.ps1            preview: every value it would change, changing nothing
#   pwsh scripts/campaign-hold.ps1 -Apply     sleep, hibernate and display timeouts to never (AC and DC), and the
#                                             screen saver lock off, after saving every prior value
#   pwsh scripts/campaign-hold.ps1 -Restore   writes the saved values back; the record goes only once all read back
#
# The record, %LOCALAPPDATA%\Bosun\campaign-hold.json, is written before the first change and never replaced by a
# second -Apply, so the values it holds are always the ones from before the campaign.
[CmdletBinding(DefaultParameterSetName = 'Preview')]
param(
  [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
  [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
  [string]$Record = (Join-Path $env:LOCALAPPDATA 'Bosun\campaign-hold.json'),
  [string]$PowerCfg = (Join-Path $env:SystemRoot 'System32\powercfg.exe'),
  [string]$LockKey = 'HKCU:\Control Panel\Desktop'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')

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

if ($Restore) {
  $saved = Read-Json $Record
  if (-not $saved) { "nothing to restore: no record at $Record"; exit 0 }
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

$saved = Read-Json $Record  # a corrupt record throws here, before anything changes
if ($saved) {
  "keeping the values saved at $Record, from before the hold"
} else {
  Save-Json $before $Record
  "saved the prior values to $Record"
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
