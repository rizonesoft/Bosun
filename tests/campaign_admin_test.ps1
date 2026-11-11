# scripts/campaign-admin-setup.ps1 and scripts/install-dotnet-final.ps1 against a test policy key under HKCU, a JSON
# task store, and fake dotnet and installer. Nothing here needs elevation, changes machine policy or registers a task.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-admin-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$setup = Join-Path $repo 'scripts\campaign-admin-setup.ps1'
. (Join-Path $repo 'scripts\lib\campaign.ps1')
$keyPath = "HKCU:\Software\BosunTest\admin-$PID\AU"
$record = Join-Path $work 'admin-setup.json'
$store = Join-Path $work 'tasks.json'
$bin = Join-Path $work 'bin'
$dotnetDir = Join-Path $work 'dotnet'
New-Item -ItemType Directory -Force $dotnetDir | Out-Null
$sdks = Join-Path $dotnetDir 'sdks.txt'
Set-Content (Join-Path $dotnetDir 'dotnet.cmd') "@type `"$sdks`"" -Encoding ascii
$common = @('-PolicyKey', $keyPath, '-Record', $record, '-TaskStore', $store, '-BinDir', $bin, '-DotnetDir', $dotnetDir)

function Reset {
  Remove-Item -LiteralPath $keyPath -Recurse -ErrorAction Ignore
  New-Item -Path $keyPath -Force | Out-Null
  New-ItemProperty -LiteralPath $keyPath -Name AUOptions -Value 3 -PropertyType DWord | Out-Null  # NoAutoReboot... absent
  foreach ($p in $record, $store) { Remove-Item -LiteralPath $p -ErrorAction Ignore }
  Remove-Item -LiteralPath $bin -Recurse -ErrorAction Ignore
}
function Setup([string[]]$extra = @()) {
  $out = & $pwsh -NoProfile -File $setup @extra @common 2>&1
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}
function Policy { Get-UpdateState $keyPath | ConvertTo-Json -Compress }
$before = '{"NoAutoRebootWithLoggedOnUsers":{"Present":false,"Value":null},"AUOptions":{"Present":true,"Value":3}}'
$defs = Get-CampaignTaskDefs $bin $dotnetDir
function Matches { @(foreach ($d in $defs) { Compare-CampaignTask $d (Get-CampaignTask $d.Name $store) }).Count -eq 0 }

try {
  Reset
  $r = Setup
  Check ($r.Code -eq 0 -and $r.Text -match 'preview' -and $r.Text -match 'AUOptions: 3 -> 4' -and $r.Text -match "task 'Bosun dotnet final' as SYSTEM" -and (Policy) -eq $before -and -not (Test-Path $store) -and -not (Test-Path $bin) -and -not (Test-Path $record)) 'the preview names each value, file and task, and changes nothing'

  $r = Setup @('-Apply')
  $now = Get-UpdateState $keyPath
  Check ($r.Code -eq 0 -and $now.NoAutoRebootWithLoggedOnUsers.Value -eq 1 -and $now.AUOptions.Value -eq 4) 'apply sets both update values and reads them back'
  Check ((Matches) -and (Test-Path (Join-Path $bin 'install-dotnet-final.ps1')) -and (Test-Path (Join-Path $bin 'lib\campaign.ps1'))) 'apply registers the task as defined and copies its body'
  $dotnetTask = Get-CampaignTask 'Bosun dotnet final' $store
  Check ($dotnetTask.User -eq 'SYSTEM' -and $dotnetTask.Arguments.Contains("-InstallDir `"$dotnetDir`"") -and ($dotnetTask.Triggers -join ',') -eq 'Boot,Daily') 'the SDK task runs as SYSTEM daily and at startup, given the watchdog''s dotnet folder'
  $saved = [IO.File]::ReadAllText($record)
  $r = Setup @('-Apply')
  Check ($r.Code -eq 0 -and $r.Text -match 'keeping the update values saved' -and [IO.File]::ReadAllText($record) -eq $saved) 'a repeat apply keeps the first saved state'
  $r = Setup @('-Restore')
  Check ($r.Code -eq 0 -and (Policy) -eq $before -and -not (Get-CampaignTask 'Bosun dotnet final' $store) -and -not (Test-Path (Join-Path $bin 'install-dotnet-final.ps1')) -and -not (Test-Path $record)) 'restore puts each value back as it was (one absent, one 3), removes the task and its body, then the record'

  # A machine set up before 2026-11-10 also has the retired VHDX task and its body: undo removes those too.
  Reset
  Setup @('-Apply') | Out-Null
  Register-CampaignTask ([ordered]@{ Name = 'Bosun attach D'; Enabled = $true; Execute = 'powershell.exe'; Arguments = '-File attach-vhdx.ps1'; User = 'SYSTEM'; Triggers = @('Boot') }) $store
  Set-Content (Join-Path $bin 'attach-vhdx.ps1') '# retired'
  $r = Setup @('-Restore')
  Check ($r.Code -eq 0 -and -not (Get-CampaignTask 'Bosun attach D' $store) -and -not (Test-Path (Join-Path $bin 'attach-vhdx.ps1'))) 'restore also removes the retired VHDX task and its body from an older setup'

  # Interrupted after the first change: the record keeps the state from before, and a second apply finishes.
  Reset
  $env:BOSUN_TEST_FAIL_AFTER_CHANGES = '1'
  $r = Setup @('-Apply')
  Remove-Item Env:BOSUN_TEST_FAIL_AFTER_CHANGES
  Check ($r.Code -ne 0 -and (Test-Path $record) -and (Policy) -ne $before) 'an apply interrupted after its first change leaves its record'
  $r = Setup @('-Apply')
  Check ($r.Code -eq 0 -and (Matches)) 'the next apply finishes from the same record'
  $r = Setup @('-Restore')
  Check ($r.Code -eq 0 -and (Policy) -eq $before) 'and restore returns the state from before the first apply'

  # Undo needs only the record: it runs with no dotnet anywhere, as after the SDK was uninstalled.
  Reset
  $r = Setup @('-Apply')
  $empty = Join-Path $work 'no-dotnet'
  New-Item -ItemType Directory -Force $empty | Out-Null
  $noDotnet = @($common | Where-Object { $_ -ne '-DotnetDir' -and $_ -ne $dotnetDir }) + @('-SearchPath', $empty)
  $out = & $pwsh -NoProfile -File $setup -Restore @noDotnet 2>&1
  Check ($LASTEXITCODE -eq 0 -and (Policy) -eq $before -and -not (Get-CampaignTask 'Bosun dotnet final' $store)) "restore runs with no dotnet on the watchdog's PATH ($(($out | Select-Object -Last 1)))"
  $out = & $pwsh -NoProfile -File $setup -Apply @noDotnet 2>&1
  Check ($LASTEXITCODE -eq 1 -and ($out -join ' ') -match 'no dotnet.exe') 'while apply still needs it'

  # A corrupt record is refused before anything changes.
  Reset
  [IO.File]::WriteAllText($record, '{ not json')
  $r = Setup @('-Apply')
  Check ($r.Code -ne 0 -and $r.Text.Contains($record) -and (Policy) -eq $before -and -not (Test-Path $store) -and [IO.File]::ReadAllText($record) -eq '{ not json') 'a corrupt record is refused with its path, and nothing changes'

  # A restore that fails keeps the record; a retry then succeeds.
  Reset
  Setup @('-Apply') | Out-Null
  $held = [IO.File]::Open($store, 'Open', 'ReadWrite', 'None')  # another process holding the task store
  try { $r = Setup @('-Restore') } finally { $held.Close() }
  Check ($r.Code -eq 1 -and $r.Text -match 'left the record' -and $r.Text -match "task 'Bosun" -and (Test-Path $record)) 'a failed restore keeps the record and names what it could not undo'
  $r = Setup @('-Restore')
  Check ($r.Code -eq 0 -and (Policy) -eq $before -and -not (Test-Path $record)) 'a retry then restores everything'

  # Against the real policy key and Task Scheduler, an unelevated shell is refused before anything changes.
  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $admin) {
    $out = & $pwsh -NoProfile -File $setup -Apply -Record $record -BinDir $bin -DotnetDir $dotnetDir 2>&1
    Check ($LASTEXITCODE -eq 1 -and ($out -join "`n") -match 'Verb RunAs' -and -not (Test-Path $record)) 'unelevated, the real setup is refused with the command that elevates it'
  }

  # The SDK task body, under Windows PowerShell 5.1 as the task runs it.
  $installer = Join-Path $work 'fake-install.ps1'
  $installLog = Join-Path $work 'installer-calls.txt'
  Set-Content $installer @"
param([string]`$Version, [string]`$InstallDir, [switch]`$SkipNonVersionedFiles)
Add-Content '$installLog' "`$Version `$InstallDir skip=`$SkipNonVersionedFiles"
if (`$env:BOSUN_FAKE_INSTALL_FAIL) { 'download failed'; exit 1 }
Add-Content '$sdks' "`$Version [`$InstallDir\sdk]"
"@
  $metadata = Join-Path $work 'releases.json'
  $log = Join-Path $work 'dotnet-final.log'
  function Body([string]$latest, [string]$present) {
    Set-Content $metadata (@{ 'latest-sdk' = $latest } | ConvertTo-Json)
    Set-Content $sdks $present
    Remove-Item -LiteralPath $installLog -ErrorAction Ignore
    Register-CampaignTask $defs[0] $store
    $out = & $ps51 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\install-dotnet-final.ps1') -InstallDir $dotnetDir -TaskStore $store -Metadata $metadata -Installer $installer -Dotnet (Join-Path $dotnetDir 'dotnet.cmd') -Log $log 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n"); Task = [bool](Get-CampaignTask $defs[0].Name $store); Installed = (Get-Content $installLog -ErrorAction Ignore) }
  }
  $rc = "10.0.403 [$dotnetDir\sdk]`r`n11.0.100-rc.2.26504.105 [$dotnetDir\sdk]"
  $r = Body '11.0.100-rc.3.26522.148' $rc
  Check ($r.Code -eq 0 -and $r.Text -match 'prerelease, nothing changed' -and $r.Task -and -not $r.Installed) 'before release the SDK task changes nothing and keeps itself'
  $r = Body '11.0.100' $rc
  Check ($r.Code -eq 0 -and $r.Installed -eq "11.0.100 $dotnetDir skip=True" -and -not $r.Task) 'a final SDK installs into the watchdog''s folder, leaving the in-use dotnet.exe alone, then the task removes itself'
  $env:BOSUN_FAKE_INSTALL_FAIL = '1'
  $r = Body '11.0.100' $rc
  Remove-Item Env:BOSUN_FAKE_INSTALL_FAIL
  Check ($r.Code -eq 1 -and $r.Installed -and $r.Task -and $r.Text -match 'keeping') 'a failed install keeps the task for the next run'
  $r = Body '11.0.101' "$rc`r`n11.0.100 [$dotnetDir\sdk]"
  Check ($r.Code -eq 0 -and -not $r.Installed -and -not $r.Task) 'a final SDK already present removes the task with no install'
  Check ((Get-Content $log).Count -ge 4) 'each run is logged'
}
finally {
  Remove-Item Env:BOSUN_TEST_FAIL_AFTER_CHANGES, Env:BOSUN_FAKE_INSTALL_FAIL -ErrorAction Ignore
  Remove-Item -LiteralPath "HKCU:\Software\BosunTest\admin-$PID" -Recurse -ErrorAction Ignore
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
