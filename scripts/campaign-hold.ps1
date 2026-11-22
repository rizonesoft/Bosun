# Puts back a hold an older version of the campaign left on this machine (D00 T03 §24): the power plan's sleep,
# hibernate and display timeouts it set to never, and the screen saver lock it turned off.
#
#   pwsh scripts/campaign-hold.ps1 -Status    the record's held values, each now and before, and the restore command
#   pwsh scripts/campaign-hold.ps1 -Restore   writes the saved values back; the record goes only once all read back
#
# A campaign no longer changes these settings (D00 T03 §28): its launcher, scripts/start-run.ps1, holds the machine
# awake through a power request its own process holds, which Windows ends with the process, so nothing is left to put
# back. -Apply refuses and says so. The screen saver's lock on resume is the owner's setting, which
# `pwsh scripts/campaign-preflight.ps1` checks.
#
# The record, %LOCALAPPDATA%\Bosun\campaign-hold.json, holds the values from before that older hold. A record that is
# not complete is refused before anything changes. Each powercfg call is stopped with its tree past -ProbeSeconds
# (120), and this process runs in a job that ends everything it started when it ends, interrupted or killed included.
# BOSUN_HOLD_RECORD, BOSUN_HOLD_POWERCFG and BOSUN_HOLD_LOCKKEY name the record, powercfg and the lock key when no
# parameter does, so a test acts on its own targets.
[CmdletBinding(DefaultParameterSetName = 'Help')]
param(
  [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
  # Kept only so the old timed apply is answered with its replacement, not a parameter error.
  [Parameter(ParameterSetName = 'Apply')][int]$Hours = 0,
  [Parameter(ParameterSetName = 'Status')][switch]$Status,
  [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
  [string]$Record = ($env:BOSUN_HOLD_RECORD ? $env:BOSUN_HOLD_RECORD : (Join-Path $env:LOCALAPPDATA 'Bosun\campaign-hold.json')),
  [string]$PowerCfg = ($env:BOSUN_HOLD_POWERCFG ? $env:BOSUN_HOLD_POWERCFG : (Join-Path $env:SystemRoot 'System32\powercfg.exe')),
  [string]$LockKey = ($env:BOSUN_HOLD_LOCKKEY ? $env:BOSUN_HOLD_LOCKKEY : 'HKCU:\Control Panel\Desktop'),
  [ValidateRange(1, 3600)][int]$ProbeSeconds = 120
)
$ErrorActionPreference = 'Stop'
# A refusal or a failure is one plain line, whatever threw it: an unreadable record, a powercfg past its limit.
trap { "FAIL $($_.Exception.Message)"; exit 1 }
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')
. (Join-Path $PSScriptRoot 'lib\job-measure.ps1')
Stop-TreeWithThisWindow
$PowerCfgSeconds = $ProbeSeconds

if ($Apply) {
  $start = if ($Hours -gt 0) { "pwsh scripts/start-run.ps1 -Minutes $([math]::Min($Hours * 60, 10080))" } else { 'pwsh scripts/start-run.ps1' }
  "FAIL a campaign no longer changes the power plan or the screen saver lock: start it with ``$start``, which holds the machine awake while its writer runs (D00 T03 §28); -Status and -Restore are here for a hold an older version left"
  exit 2
}
if (-not $Status -and -not $Restore) {
  'pwsh scripts/campaign-hold.ps1 -Status | -Restore: for a hold an older version left; a campaign''s launcher (scripts/start-run.ps1) now holds the machine awake itself'
  exit 0
}

# The saved record, checked whole, or $null when there is none; an unreadable or incomplete one throws.
function Read-HoldRecord {
  $saved = Read-Json $Record
  if ($saved) { Assert-Record $saved $Record 'hold' }
  $saved
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
  "a hold an older version left: applied $($life.Applied.ToString('u')) ($([math]::Floor($age.TotalHours)) h $($age.Minutes) min ago)"
  "scheme $($saved.Scheme)"
  for ($i = 0; $i -lt $saved.Power.Count; $i++) {
    foreach ($line in 'AC', 'DC') { "  $($saved.Power[$i].Sub) $($saved.Power[$i].Setting) $line`: now $($now.Power[$i].$line), before $($saved.Power[$i].$line)" }
  }
  $show = { param($v) if ($null -eq $v) { '(absent)' } else { $v } }
  "  $LockKey ScreenSaverIsSecure: now $(& $show $now.Lock), before $(& $show $saved.Lock)"
  "restore: $(Get-RestoreCommand)"
  exit 0
}

$saved = Read-HoldRecord
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
