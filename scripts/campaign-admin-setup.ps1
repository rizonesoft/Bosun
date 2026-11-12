# The campaign's one elevated step, run once with the owner present.
#
#   pwsh scripts/campaign-admin-setup.ps1            preview: every value, file and task it would change
#   pwsh scripts/campaign-admin-setup.ps1 -Apply     elevated: holds update restarts, installs the task bodies, and
#                                                    registers `Bosun dotnet final`
#   pwsh scripts/campaign-admin-setup.ps1 -Restore   elevated: puts the update values back and removes the task
#
# Windows Update keeps downloading and installing, but never restarts while the owner is signed in:
# NoAutoRebootWithLoggedOnUsers=1, with AUOptions=4, which that policy needs to apply. The prior state of both values
# (each value, or its absence) is saved to %LOCALAPPDATA%\Bosun\admin-setup.json before the first change, and a second
# -Apply keeps it, so -Restore always returns the machine to how it was before the campaign.
[CmdletBinding(DefaultParameterSetName = 'Preview')]
param(
  [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
  [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
  [string]$PolicyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU',
  [string]$Record = (Join-Path $env:LOCALAPPDATA 'Bosun\admin-setup.json'),
  [string]$BinDir = (Join-Path $env:ProgramData 'Bosun\bin'),
  [string]$DotnetDir = '',
  [string]$TaskStore = '',
  [string]$TaskPrefix = 'Bosun',
  [string]$SearchPath = ''  # the PATH a run launches with; the registry's when empty
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')

# Only preview and apply need dotnet's folder, for the SDK task; undo needs only the record, so it works after an
# uninstall.
if (-not $DotnetDir -and -not $Restore) { $DotnetDir = Resolve-RunDotnet $(if ($SearchPath) { $SearchPath } else { Get-RegistryPath }) }
if (-not $DotnetDir -and -not $Restore) {
  "FAIL no dotnet.exe on the PATH a run launches with (the machine and user PATH in the registry)"
  "     fix: install the .NET SDK (winget install Microsoft.DotNet.SDK.10), then run this again"
  exit 1
}
$defs = Get-CampaignTaskDefs $BinDir $DotnetDir $TaskPrefix
$bodies = @('install-dotnet-final.ps1', 'lib\campaign.ps1')
# What an earlier version set up and this one no longer does: undo still removes it, so a machine set up before
# 2026-11-10 isn't left with a startup task mounting a volume that is gone.
$retiredTasks = @("$TaskPrefix attach D")
$retiredBodies = @('attach-vhdx.ps1')

# Test-only fault injection: stop after this many changes, as a crash or a closed window would.
$failAfter = [int]($env:BOSUN_TEST_FAIL_AFTER_CHANGES ?? '0')
$changes = 0
function Changed {
  $script:changes++
  if ($failAfter -and $script:changes -ge $failAfter) { throw "test: stopped after $($script:changes) change(s)" }
}

function Show([object]$v) { if ($null -eq $v) { '(absent)' } else { $v } }

if (-not $Apply -and -not $Restore) {
  'preview (nothing changed; -Apply elevated to set up, -Restore elevated to undo):'
  $now = Get-UpdateState $PolicyKey
  foreach ($name in $UpdateValues.Keys) { "  $PolicyKey $name`: $(Show $now[$name].Value) -> $($UpdateValues[$name])" }
  foreach ($b in $bodies) { "  copy scripts\$b -> $(Join-Path $BinDir $b)" }
  foreach ($d in $defs) {
    "  task '$($d.Name)' as $($d.User), on $($d.Triggers -join ' and '):"
    "    $($d.Execute) $($d.Arguments)"
  }
  exit 0
}

# Only a target that needs it asks for elevation: the policy key under HKLM, and Task Scheduler's SYSTEM tasks.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin -and ($PolicyKey -like 'HKLM:*' -or -not $TaskStore)) {
  $verb = if ($Apply) { '-Apply' } else { '-Restore' }
  "FAIL this changes machine policy and SYSTEM tasks, so it needs an elevated shell"
  "     fix: Start-Process pwsh -Verb RunAs -ArgumentList '-NoExit', '-File', '$PSCommandPath', '$verb'"
  exit 1
}

if ($Restore) {
  $saved = Read-Json $Record
  if (-not $saved) { "nothing to restore: no record at $Record"; exit 0 }
  $failed = @()
  foreach ($name in $UpdateValues.Keys) {
    $s = $saved.Update.$name
    try {
      if ($s.Present) {
        if (-not (Test-Path -LiteralPath $PolicyKey)) { New-Item -Path $PolicyKey -Force | Out-Null }
        New-ItemProperty -LiteralPath $PolicyKey -Name $name -Value ([int]$s.Value) -PropertyType DWord -Force | Out-Null
      } elseif ($null -ne (Get-RegValue $PolicyKey $name)) {
        Remove-ItemProperty -LiteralPath $PolicyKey -Name $name
      }
    } catch { $failed += "$name ($($_.Exception.Message))" }
  }
  foreach ($name in @($defs | ForEach-Object { $_.Name }) + $retiredTasks) {
    try { Unregister-CampaignTask $name $TaskStore } catch { $failed += "task '$name' ($($_.Exception.Message))" }
  }
  $now = Get-UpdateState $PolicyKey
  foreach ($name in $UpdateValues.Keys) {
    $s = $saved.Update.$name
    if ([bool]$now[$name].Present -ne [bool]$s.Present -or ($s.Present -and $now[$name].Value -ne [int]$s.Value)) {
      $failed += "$name reads $(Show $now[$name].Value), not $(if ($s.Present) { $s.Value } else { '(absent)' })"
    }
  }
  foreach ($name in @($defs | ForEach-Object { $_.Name }) + $retiredTasks) {
    try { if (Get-CampaignTask $name $TaskStore) { $failed += "task '$name' is still registered" } }
    catch { $failed += "task '$name' can't be read back ($($_.Exception.Message))" }
  }
  foreach ($b in $bodies + $retiredBodies) {
    $at = Join-Path $BinDir $b
    try { if (Test-Path -LiteralPath $at) { Remove-Item -LiteralPath $at } } catch { $failed += "$at ($($_.Exception.Message))" }
  }
  if ($failed) {
    "FAIL restore left the record at $Record for a retry. Not restored:"
    $failed | Sort-Object -Unique | ForEach-Object { "  $_" }
    exit 1
  }
  Remove-Item -LiteralPath $Record
  'restored the update values and removed the task; record removed'
  exit 0
}

$saved = Read-Json $Record  # a corrupt record throws here, before anything changes
if ($saved) {
  "keeping the update values saved at $Record, from before the setup"
} else {
  Save-Json ([ordered]@{ PolicyKey = $PolicyKey; Update = (Get-UpdateState $PolicyKey) }) $Record
  "saved the prior update values to $Record"
}
if (-not (Test-Path -LiteralPath $PolicyKey)) { New-Item -Path $PolicyKey -Force | Out-Null }
foreach ($name in $UpdateValues.Keys) {
  New-ItemProperty -LiteralPath $PolicyKey -Name $name -Value $UpdateValues[$name] -PropertyType DWord -Force | Out-Null
  Changed
}
foreach ($b in $bodies) {
  $to = Join-Path $BinDir $b
  New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot $b) -Destination "$to.tmp" -Force
  Move-Item -LiteralPath "$to.tmp" -Destination $to -Force
  Changed
}
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value  # elevated, still the owner's own account
foreach ($d in $defs) { Register-CampaignTask $d $TaskStore $owner; Changed }

# Read everything back.
$wrong = @()
$now = Get-UpdateState $PolicyKey
foreach ($name in $UpdateValues.Keys) { if ($now[$name].Value -ne $UpdateValues[$name]) { $wrong += "$name reads $(Show $now[$name].Value)" } }
foreach ($b in $bodies) {
  if ((Get-FileHash (Join-Path $BinDir $b)).Hash -ne (Get-FileHash (Join-Path $PSScriptRoot $b)).Hash) { $wrong += "$b didn't copy" }
}
foreach ($d in $defs) {
  foreach ($x in (Compare-CampaignTask $d (Get-CampaignTask $d.Name $TaskStore))) { $wrong += "task '$($d.Name)' $x" }
  if (-not (Test-CampaignTaskReader $d.Name $owner $TaskStore)) { $wrong += "task '$($d.Name)' can't be read by $owner, so the preflight can't check it" }
}
if ($wrong) { "FAIL setup didn't read back: $($wrong -join '; ')"; exit 1 }
"set up: update restarts held while signed in, and task '$($defs[0].Name)' registered (SDK folder $DotnetDir)"
