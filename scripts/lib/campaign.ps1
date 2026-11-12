# What the campaign's machine scripts share: the PATH a run launches with, bounded probes, the held power and
# lock values, the SYSTEM task admin setup registers, and the records both keep. Dot-sourced by scripts/campaign-preflight.ps1,
# scripts/campaign-hold.ps1, scripts/campaign-admin-setup.ps1 and scripts/install-dotnet-final.ps1, which runs as
# SYSTEM under Windows PowerShell 5.1, so nothing here needs PowerShell 7.

# The PATH a process started by Task Scheduler gets: the machine's entries, then the user's, expanded. A shell that was
# open before an installer changed PATH has a stale one, so the preflight resolves tools from this instead.
# A registry value, or $null only when the key or the value is absent; any other failure, such as access denied, throws.
function Get-RegValue([string]$key, [string]$name) {
  if (-not (Test-Path -LiteralPath $key)) { return $null }
  (Get-Item -LiteralPath $key -ErrorAction Stop).GetValue($name, $null, 'DoNotExpandEnvironmentNames')
}

function Get-RegistryPath {
  $machine = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' 'Path'
  $user = Get-RegValue 'HKCU:\Environment' 'Path'
  $parts = @()
  foreach ($p in @($machine, $user)) {
    if ($p) { $parts += [Environment]::ExpandEnvironmentVariables($p) -split ';' | Where-Object { $_ } }
  }
  $parts -join ';'
}

# The first file named $name (with each PATHEXT extension) in the folders of $searchPath, or $null.
function Find-Tool([string]$name, [string]$searchPath) {
  $exts = @('')
  if (-not [IO.Path]::HasExtension($name)) {
    $exts = @(($env:PATHEXT, '.COM;.EXE;.BAT;.CMD' | Where-Object { $_ } | Select-Object -First 1) -split ';' | Where-Object { $_ })
  }
  foreach ($dir in ($searchPath -split ';' | Where-Object { $_ })) {
    if (-not [IO.Path]::IsPathRooted($dir)) { continue }  # a relative entry would resolve against wherever we stand
    foreach ($ext in $exts) {
      $candidate = Join-Path $dir ($name + $ext)
      if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
  }
  $null
}

# The folder of the dotnet.exe a run's launch environment resolves, or $null.
function Resolve-RunDotnet([string]$searchPath = (Get-RegistryPath)) {
  $found = Find-Tool 'dotnet.exe' $searchPath
  if ($found) { Split-Path $found } else { $null }
}

# Stop a process and everything it started.
function Stop-Tree([int]$processId) {
  & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $processId 2>&1 | Out-Null
}

# Run $exe with $arguments under a limit of $seconds, its PATH set to $searchPath and its stdin closed. Returns Code,
# Out (stdout and stderr together) and TimedOut. An $environment value of $null removes that variable. A probe past its limit is stopped with its whole tree.
function Invoke-Probe([string]$exe, [string[]]$arguments, [int]$seconds, [string]$searchPath, [string]$workingDirectory = '', [hashtable]$environment = @{}) {
  $info = New-Object System.Diagnostics.ProcessStartInfo
  $info.FileName = $exe
  foreach ($a in $arguments) { [void]$info.ArgumentList.Add($a) }
  $info.UseShellExecute = $false
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $info.RedirectStandardInput = $true
  $info.CreateNoWindow = $true
  if ($workingDirectory) { $info.WorkingDirectory = $workingDirectory }
  if ($searchPath) { $info.Environment['PATH'] = $searchPath }
  foreach ($k in $environment.Keys) {
    if ($null -eq $environment[$k]) { [void]$info.Environment.Remove($k) } else { $info.Environment[$k] = $environment[$k] }
  }
  try { $p = [Diagnostics.Process]::Start($info) }
  catch { return [pscustomobject]@{ Code = -1; Out = "could not start ${exe}: $($_.Exception.Message)"; TimedOut = $false } }
  # In a job of its own where the job helper is loaded (the preflight and the hold load it): a child the probe leaves,
  # holding its output open, ends with the job instead of outliving it.
  $job = [IntPtr]::Zero
  if ('Bosun.JobMeasure' -as [type]) { try { $job = [Bosun.JobMeasure]::EndTreeWithJob($p.Id) } catch { $job = [IntPtr]::Zero } }
  $p.StandardInput.Close()
  $out = $p.StandardOutput.ReadToEndAsync()
  $err = $p.StandardError.ReadToEndAsync()
  # One deadline for the probe and its output, waited for in short slices, so Ctrl+C stops the caller between them; the
  # finally then stops the probe's tree, and closing the job ends whatever is left of it.
  $settled = $false
  try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $readers = [Threading.Tasks.Task[]]@($out, $err)
    while (-not ($p.HasExited -and [Threading.Tasks.Task]::WaitAll($readers, 0))) {
      if ($clock.Elapsed.TotalSeconds -ge $seconds) {
        if (-not $p.HasExited) { Stop-Tree $p.Id; $p.WaitForExit(10000) | Out-Null }
        $settled = $true
        return [pscustomobject]@{ Code = -1; Out = ''; TimedOut = $true }
      }
      if (-not $p.HasExited) { [void]$p.WaitForExit(200) } else { [void][Threading.Tasks.Task]::WaitAll($readers, 200) }
    }
    $p.WaitForExit()
    $settled = $true
    [pscustomobject]@{ Code = $p.ExitCode; Out = (($out.Result + $err.Result) -replace "`r`n", "`n").Trim(); TimedOut = $false }
  } finally {
    if (-not $settled -and -not $p.HasExited) { Stop-Tree $p.Id }
    if ($job -ne [IntPtr]::Zero) { [Bosun.JobMeasure]::CloseJob($job) }
  }
}

# Write $object as JSON to $path by temporary file then replace, reading it back.
function Save-Json($object, [string]$path) {
  $dir = Split-Path $path
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
  $text = $object | ConvertTo-Json -Depth 8
  $tmp = "$path.tmp"
  [IO.File]::WriteAllText($tmp, $text, (New-Object Text.UTF8Encoding $false))
  Move-Item -LiteralPath $tmp -Destination $path -Force
  if ([IO.File]::ReadAllText($path) -ne $text) { throw "$path did not read back as written" }
}

# The JSON object at $path, $null only when the file doesn't exist, or a throw naming the file when it holds anything
# but one object: an empty file, `null`, `false` or a list would otherwise read as no record at all.
function Read-Json([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { return $null }
  # PowerShell 7 unrolls a top-level list unless told not to; Windows PowerShell 5.1, which runs the SDK task, never does.
  $whole = @{}
  if ($PSVersionTable.PSVersion.Major -ge 7) { $whole['NoEnumerate'] = $true }
  try { $value = [IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop @whole }
  catch { throw "$path is not a readable record ($($_.Exception.Message)); move it aside after checking what it holds" }
  # The full type name: in a type test, [pscustomobject] means [psobject], which a boxed `false` or a list also is.
  if ($value -isnot [System.Management.Automation.PSCustomObject]) { throw "$path is not a readable record (it holds $(if ($null -eq $value) { 'nothing' } else { 'no object' })); move it aside after checking what it holds" }
  $value
}

# Throws, naming $path and the first field that is wrong, unless $saved is a complete record of $kind: 'hold' (what
# scripts/campaign-hold.ps1 saves) or 'admin' (what scripts/campaign-admin-setup.ps1 saves). Parseable is not enough:
# a record missing a value would restore nothing for it, or the wrong thing.
function Assert-Record($saved, [string]$path, [string]$kind) {
  $why = $null
  $has = { param($o, $name) $null -ne $o -and $o.PSObject.Properties.Name -contains $name }
  $isInt = { param($v) $v -is [int] -or $v -is [long] }
  if ($kind -eq 'hold') {
    if (-not (& $has $saved 'Scheme') -or "$($saved.Scheme)" -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') { $why = 'Scheme is not a power plan GUID' }
    elseif (-not (& $has $saved 'Power') -or @($saved.Power).Count -ne $HoldPower.Count) { $why = "Power does not hold the $($HoldPower.Count) held settings" }
    elseif (-not (& $has $saved 'Lock') -or ($null -ne $saved.Lock -and $saved.Lock -isnot [string])) { $why = 'Lock is missing, or neither absent nor a string' }
    else {
      for ($i = 0; $i -lt $HoldPower.Count -and -not $why; $i++) {
        $e = @($saved.Power)[$i]
        if ($e.Sub -ne $HoldPower[$i].Sub -or $e.Setting -ne $HoldPower[$i].Setting) { $why = "Power[$i] is not $($HoldPower[$i].Sub) $($HoldPower[$i].Setting)" }
        elseif (-not (& $isInt $e.AC) -or -not (& $isInt $e.DC)) { $why = "Power[$i] ($($e.Setting)) has no whole AC and DC values" }
      }
    }
  } elseif ($kind -eq 'admin') {
    if (-not (& $has $saved 'PolicyKey') -or -not ($saved.PolicyKey -is [string]) -or -not $saved.PolicyKey) { $why = 'PolicyKey is missing' }
    elseif (-not (& $has $saved 'Update')) { $why = 'Update is missing' }
    else {
      foreach ($name in $UpdateValues.Keys) {
        $u = $saved.Update.$name
        if ($why) { break }
        if (-not (& $has $u 'Present') -or $u.Present -isnot [bool]) { $why = "Update.$name has no Present" }
        elseif ($u.Present -and -not (& $isInt $u.Value)) { $why = "Update.$name is present with no whole value" }
      }
      foreach ($name in 'TaskPrefix', 'BinDir') {
        if (-not $why -and (& $has $saved $name) -and -not ($saved.$name -is [string] -and $saved.$name)) { $why = "$name is empty" }
      }
      if (-not $why -and (& $has $saved 'Originals') -and $saved.Originals -notin 'known', 'unknown') { $why = 'Originals is neither known nor unknown' }
    }
  } else { throw "Assert-Record: unknown kind '$kind'" }
  if ($why) { throw "$path is not a complete $kind record ($why); move it aside after checking what it holds" }
}

# --- The hold: power timeouts and the screen saver lock ---------------------------------------------------------

# Each power value the hold sets to never (0), AC and DC.
$HoldPower = @(
  @{ Sub = 'SUB_SLEEP'; Setting = 'STANDBYIDLE' },
  @{ Sub = 'SUB_SLEEP'; Setting = 'HIBERNATEIDLE' },
  @{ Sub = 'SUB_VIDEO'; Setting = 'VIDEOIDLE' }
)

# Each powercfg call's limit in seconds: the hold and the preflight set it from their -ProbeSeconds.
$PowerCfgSeconds = 120

# One powercfg call under that limit, stopped with its tree past it, which throws.
function Invoke-PowerCfg([string]$powercfg, [string[]]$arguments) {
  $r = Invoke-Probe $powercfg $arguments $PowerCfgSeconds ''
  if ($r.TimedOut) { throw "powercfg $($arguments -join ' ') gave no answer within $PowerCfgSeconds seconds and was stopped" }
  $r
}

# The active power scheme's GUID through $powercfg.
function Get-ActiveScheme([string]$powercfg) {
  $text = (Invoke-PowerCfg $powercfg @('/getactivescheme')).Out
  if ($text -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') { return $Matches[1].ToLower() }
  throw "powercfg /getactivescheme gave no scheme: $text"
}

# @{ AC = <int>; DC = <int> } for one setting of $scheme.
function Get-PowerValue([string]$powercfg, [string]$scheme, [string]$sub, [string]$setting) {
  $text = (Invoke-PowerCfg $powercfg @('/q', $scheme, $sub, $setting)).Out
  $ac = [regex]::Match($text, 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)')
  $dc = [regex]::Match($text, 'Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)')
  if (-not $ac.Success -or -not $dc.Success) { throw "powercfg /q $scheme $sub $setting gave no values: $text" }
  @{ AC = [Convert]::ToInt32($ac.Groups[1].Value, 16); DC = [Convert]::ToInt32($dc.Groups[1].Value, 16) }
}

function Set-PowerValue([string]$powercfg, [string]$scheme, [string]$sub, [string]$setting, [string]$line, [int]$value) {
  $flag = if ($line -eq 'AC') { '/setacvalueindex' } else { '/setdcvalueindex' }
  $r = Invoke-PowerCfg $powercfg @($flag, $scheme, $sub, $setting, "$value")
  if ($r.Code -ne 0) { throw "powercfg $flag $scheme $sub $setting $value failed: $($r.Out)" }
  [void](Invoke-PowerCfg $powercfg @('/setactive', $scheme))  # applies the change to the running scheme
}

# The screen saver lock value: the string in $key, or $null when absent.
function Get-LockValue([string]$key) {
  $v = Get-RegValue $key 'ScreenSaverIsSecure'
  if ($null -eq $v) { $null } else { [string]$v }
}

function Set-LockValue([string]$key, $value) {
  if ($null -eq $value) {
    # Already absent is already restored; a removal that does fail still throws.
    if ($null -ne (Get-RegValue $key 'ScreenSaverIsSecure')) { Remove-ItemProperty -LiteralPath $key -Name ScreenSaverIsSecure -ErrorAction Stop }
  }
  else { Set-ItemProperty -LiteralPath $key -Name ScreenSaverIsSecure -Value ([string]$value) -Type String -ErrorAction Stop }
  if ($key -eq 'HKCU:\Control Panel\Desktop') {
    # The live desktop reads this through SystemParametersInfo, which rereads the value just written.
    if (-not ('Bosun.Spi' -as [type])) {
      Add-Type -Namespace Bosun -Name Spi -MemberDefinition '[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, System.IntPtr vparam, uint flags);'
    }
    $on = 0; if ($value -eq '1') { $on = 1 }
    [void][Bosun.Spi]::SystemParametersInfo(0x0077, $on, [IntPtr]::Zero, 0x3)  # SPI_SETSCREENSAVESECURE, update and broadcast
  }
}

# Every held value now: Scheme, Power (list of Sub, Setting, AC, DC) and Lock.
function Get-HoldState([string]$powercfg, [string]$key, [string]$scheme = '') {
  if (-not $scheme) { $scheme = Get-ActiveScheme $powercfg }
  $power = foreach ($h in $HoldPower) {
    $v = Get-PowerValue $powercfg $scheme $h.Sub $h.Setting
    [ordered]@{ Sub = $h.Sub; Setting = $h.Setting; AC = $v.AC; DC = $v.DC }
  }
  [ordered]@{ Scheme = $scheme; Power = @($power); Lock = (Get-LockValue $key) }
}

# --- The SYSTEM task ---------------------------------------------------------------------------------------------

$WindowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# The task bodies admin setup copies to its BinDir, as paths under scripts/: the preflight checks each against this checkout.
$AdminBodies = @('install-dotnet-final.ps1', 'lib\campaign.ps1')

# The definitions admin setup registers, in the shape Get-CampaignTask reads back.
function Get-CampaignTaskDefs([string]$binDir, [string]$dotnetDir, [string]$prefix = 'Bosun') {
  # The comma keeps a list of one a list: unrolled, `$defs[0]` would index into the definition itself.
  , @(
    [ordered]@{
      Name = "$prefix dotnet final"; Enabled = $true; Execute = $WindowsPowerShell; User = 'SYSTEM'; Triggers = @('Boot', 'Daily')
      Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$(Join-Path $binDir 'install-dotnet-final.ps1')`" -InstallDir `"$dotnetDir`" -TaskName `"$prefix dotnet final`""
    }
  )
}

# The task named $name in the root folder, or $null when there is none. Listing the folder, rather than asking for the
# name, keeps "absent" apart from a failure to read the scheduler, which throws.
function Find-ScheduledTask([string]$name) {
  Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object TaskName -EQ $name | Select-Object -First 1
}

# A task read back as Name, Enabled, Execute, Arguments, User and sorted trigger kinds, or $null when absent. With
# $store, tasks live in that JSON file instead of Task Scheduler, so tests need neither SYSTEM nor elevation.
function Get-CampaignTask([string]$name, [string]$store = '') {
  if ($store) {
    $all = Read-Json $store
    if (-not $all) { return $null }
    $t = $all.$name
    if (-not $t) { return $null }
    return [ordered]@{ Name = $name; Enabled = [bool]$t.Enabled; Execute = $t.Execute; Arguments = $t.Arguments; User = $t.User; Triggers = @($t.Triggers | Sort-Object) }
  }
  $t = Find-ScheduledTask $name
  if (-not $t) { return $null }
  $kinds = foreach ($tr in $t.Triggers) { ($tr.CimClass.CimClassName -replace '^MSFT_Task', '' -replace 'Trigger$', '') }
  $user = $t.Principal.UserId
  if ($user -in @('S-1-5-18', 'NT AUTHORITY\SYSTEM')) { $user = 'SYSTEM' }
  [ordered]@{
    Name = $name; Enabled = ($t.State -ne 'Disabled'); Execute = $t.Actions[0].Execute; Arguments = $t.Actions[0].Arguments
    User = $user; Triggers = @($kinds | Sort-Object)
  }
}

# Task Scheduler hides a SYSTEM task an elevated shell registered from the user's own, unelevated one, so the owner's
# preflight would read it as missing and couldn't start it. $readerSid (the owner) is granted read and run on it.
function Register-CampaignTask($def, [string]$store = '', [string]$readerSid = '') {
  if ($store) {
    $all = Read-Json $store
    $table = [ordered]@{}
    if ($all) { foreach ($p in $all.PSObject.Properties) { $table[$p.Name] = $p.Value } }
    $table[$def.Name] = $def
    Save-Json $table $store
    return
  }
  $action = New-ScheduledTaskAction -Execute $def.Execute -Argument $def.Arguments
  $triggers = foreach ($k in $def.Triggers) {
    if ($k -eq 'Boot') { New-ScheduledTaskTrigger -AtStartup } else { New-ScheduledTaskTrigger -Daily -At '04:00' }
  }
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
  Register-ScheduledTask -TaskName $def.Name -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
  if ($readerSid) {
    $task = Get-SchedulerTask $def.Name
    $dacl = $task.GetSecurityDescriptor(4)  # DACL_SECURITY_INFORMATION
    if (-not (Test-TaskReader $dacl $readerSid)) { $task.SetSecurityDescriptor($dacl + "(A;;GRGX;;;$readerSid)", 0) }
  }
}

# The Task Scheduler COM object for $name in the root folder.
function Get-SchedulerTask([string]$name) {
  $service = New-Object -ComObject Schedule.Service
  $service.Connect()
  $service.GetFolder('\').GetTask($name)
}

# Whether the DACL in SDDL $dacl allows $sid anything.
function Test-TaskReader([string]$dacl, [string]$sid) {
  $dacl -match "\(A;[^;]*;[^;]+;;;$([regex]::Escape($sid))\)"
}

# Whether $sid may read the task $name; in a store, always.
function Test-CampaignTaskReader([string]$name, [string]$sid, [string]$store = '') {
  if ($store) { return $true }
  Test-TaskReader ((Get-SchedulerTask $name).GetSecurityDescriptor(4)) $sid
}

function Unregister-CampaignTask([string]$name, [string]$store = '') {
  if ($store) {
    $all = Read-Json $store
    if (-not $all) { return }
    $table = [ordered]@{}
    foreach ($p in $all.PSObject.Properties) { if ($p.Name -ne $name) { $table[$p.Name] = $p.Value } }
    Save-Json $table $store
    return
  }
  if (Find-ScheduledTask $name) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop
  }
}

# How $actual differs from $expected, one phrase per difference; empty when it matches.
function Compare-CampaignTask($expected, $actual) {
  if (-not $actual) { return @('not registered') }
  $diff = @()
  if (-not $actual.Enabled) { $diff += 'disabled' }
  if ($actual.Execute -ne $expected.Execute) { $diff += "runs $($actual.Execute), not $($expected.Execute)" }
  if ($actual.Arguments -ne $expected.Arguments) { $diff += "arguments are $($actual.Arguments), not $($expected.Arguments)" }
  if ($actual.User -ne $expected.User) { $diff += "runs as $($actual.User), not $($expected.User)" }
  $want = (@($expected.Triggers | Sort-Object) -join ',')
  $have = (@($actual.Triggers | Sort-Object) -join ',')
  if ($have -ne $want) { $diff += "triggers are $have, not $want" }
  $diff
}

# A final .NET 11 SDK (11.0.1xx with no prerelease suffix) in `dotnet --list-sdks` output.
function Test-FinalSdk([string]$listing) {
  [bool]($listing -split "`n" | Where-Object { $_ -match '^\s*11\.0\.1\d\d\s+\[' })
}

# --- Windows Update values admin setup holds -----------------------------------------------------------------

$UpdateValues = [ordered]@{ NoAutoRebootWithLoggedOnUsers = 1; AUOptions = 4 }

# Each update value's state: @{ Present; Value } (DWORD).
function Get-UpdateState([string]$key) {
  $state = [ordered]@{}
  foreach ($name in $UpdateValues.Keys) {
    $v = Get-RegValue $key $name
    if ($null -ne $v) { $state[$name] = [ordered]@{ Present = $true; Value = [int]$v } }
    else { $state[$name] = [ordered]@{ Present = $false; Value = $null } }
  }
  $state
}
