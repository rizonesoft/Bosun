<#
.SYNOPSIS
    Runs the end-to-end smoke's real-input tests: now, or at night when nobody is at the desk (D02 T02 §12).

.DESCRIPTION
    The tests that press keys, click, drag, move the pointer or bring the panel to the front take the operator's
    mouse and keyboard while they run, so they are opt-in: the default smoke skips them (they run only with
    BOSUN_SMOKE_INPUT=1, which this script sets), and this script runs them.

    Run with no switch, it runs them now. With -Scheduled (the nightly task, 03:00 to 05:00), it runs only on a
    desktop that is unlocked and has had no input for 15 minutes, and stops at the first sign of a person: input
    Windows did not mark as the smoke's own (a watcher's low-level hooks), or the desktop locked. It never wakes the
    machine, unlocks it or takes a desktop in use. 05:00 is its deadline: a class still running then is ended, its
    whole process tree with it.

    It holds the desktop lock (Local\bosun-desktop, D02 T02 §11) from before its first class to after its last, so no
    other smoke or capture runs in between, and each class's run works under that hold.

    Every run, run or skipped, is kept in input-history.jsonl (the last 60) with its own log (the last 30) in the
    state folder, %LOCALAPPDATA%\Bosun\smoke. -Status says, in words, the last scheduled and the last on-demand
    result and how many nights were skipped. -Install and -Uninstall add and remove the nightly task.

    Exit codes: 0 passed, or skipped by its guard; 1 failed (a test failed, was missing, ran twice or was skipped, a
    build failed, or the deadline came), stopped for a person, or could not take the lock; 2 bad arguments.

.EXAMPLE
    pwsh scripts/smoke-input.ps1            # run the real-input tests now
    pwsh scripts/smoke-input.ps1 -Install   # run them nightly at 03:00, when the desk is idle
    pwsh scripts/smoke-input.ps1 -Status    # the last results in words
    pwsh scripts/smoke-input.ps1 -Uninstall # stop the nightly run
#>
param(
    [switch]$Scheduled,
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Status,
    [string]$StateFolder = (Join-Path $env:LOCALAPPDATA 'Bosun\smoke'),
    [string]$TaskName = 'Bosun real-input smoke',
    # Appended to the nightly task's own arguments (its tests point it at stand-ins and a state folder of their own).
    [string]$TaskArguments = '',
    # How the run was started, kept in its record: 'task' from the nightly task, else 'shell'.
    [ValidateSet('shell', 'task')][string]$Invocation = 'shell',
    [int]$IdleMinutes = 15,
    # Local time the scheduled run must end by; 05:00 today when not given.
    [string]$Deadline = '',
    # For this script's own tests: stand-ins for dotnet and the input watcher, and the lock and idle probes read from
    # files in the state folder (probe-locked, probe-idle) instead of the desktop.
    [string]$Dotnet = 'dotnet',
    [string]$Watcher = '',
    [switch]$TestProbes
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'tests\Bosun.Panel.E2E'
$history = Join-Path $StateFolder 'input-history.jsonl'
$installed = Join-Path $StateFolder 'installed.json'
New-Item -ItemType Directory -Force -Path $StateFolder | Out-Null

if (-not ('Bosun.Desktop.DesktopLock' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'lib\DesktopLock.cs') }
. (Join-Path $PSScriptRoot 'lib\job-measure.ps1')
if (-not ('SmokeInputDesk' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeInputDesk {
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    public static bool Locked() { return GetForegroundWindow() == IntPtr.Zero; }
    public static double IdleSeconds() {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }
}
'@
}

# ---- the history -------------------------------------------------------------------------------------------------

function Read-History {
    if (-not (Test-Path -LiteralPath $history)) { return @() }
    # A line that is not a record (a hand edit, a disk that failed mid-write before the replace) is said and passed over,
    # so one bad line never hides the rest of the history.
    $n = 0
    @(Get-Content -LiteralPath $history | ForEach-Object {
        $n++
        if (-not $_.Trim()) { return }
        try { $_ | ConvertFrom-Json } catch { Write-Warning "smoke-input: line $n of $history is not a record and is passed over: $($_.Exception.Message)" }
    })
}

# Writes the whole file anew through a temporary file and a replace, the last 60 entries kept.
# One writer at a time, across processes: runs refused the desktop lock write their record without holding it, and two
# that read the same history would each replace it with only their own entry added. The mutex is named for the file.
function Add-History($entry) {
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(([IO.Path]::GetFullPath($history)).ToLowerInvariant()))).Substring(0, 16)
    $gate = [Threading.Mutex]::new($false, "Local\bosun-smoke-history-$key")
    try {
        try { [void]$gate.WaitOne() } catch [Threading.AbandonedMutexException] { }  # a writer that died: the file was replaced whole or not at all
        $lines = @(Read-History | ForEach-Object { $_ | ConvertTo-Json -Compress }) + @($entry | ConvertTo-Json -Compress)
        $lines = @($lines | Select-Object -Last 60)
        $temporary = "$history.$PID.tmp"
        [IO.File]::WriteAllText($temporary, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $history -Force
    } finally { $gate.ReleaseMutex(); $gate.Dispose() }
}

function Format-Outcome($e) {
    $at = ([DateTime]$e.at).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
    switch ($e.outcome) {
        'passed'          { "passed $($e.passed) of $($e.discovered) at $at" }
        'failed'          { "failed at ${at}: $($e.reason)" }
        'skipped-locked'  { "skipped at ${at}: the desktop was locked" }
        'skipped-in-use'  { "skipped at ${at}: $($e.reason)" }
        'skipped-outside-window' { "skipped at ${at}: $($e.reason)" }
        'stopped-in-use'  { "stopped at ${at}: $($e.reason) ($($e.passed) of $($e.discovered) passed before it)" }
        'stopped-locked'  { "stopped at ${at}: the desktop was locked ($($e.passed) of $($e.discovered) passed before it)" }
        'stopped-deadline' { "stopped at ${at}: 05:00 came ($($e.passed) of $($e.discovered) passed before it)" }
        default           { "$($e.outcome) at $at" }
    }
}

function Show-Status {
    $all = Read-History
    $nightly = @($all | Where-Object mode -eq 'scheduled')
    $manual = @($all | Where-Object mode -eq 'manual')
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Ignore
    if (-not $task) { 'The nightly run is not installed: pwsh scripts/smoke-input.ps1 -Install' }
    elseif ($nightly.Count -eq 0) {
        $since = if (Test-Path -LiteralPath $installed) { ([DateTime](Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json).at).ToLocalTime().ToString('yyyy-MM-dd') } else { 'an unknown date' }
        "No scheduled run yet, installed $since."
    }
    if ($nightly.Count -gt 0) { "Real-input smoke, last scheduled run: $(Format-Outcome $nightly[-1])." }
    # A night's result is a scheduled run that began in that night's window, 03:00 to 05:00: a scheduled run started
    # later the same day (a missed trigger run by hand from Task Scheduler) is not the night's, and a night with none
    # has no result. The latest run of a night is its result.
    $nights = @{}
    foreach ($e in $nightly) {
        $at = ([DateTime]$e.at).ToLocalTime()
        if ($at.TimeOfDay -ge [TimeSpan]::FromHours(3) -and $at.TimeOfDay -lt [TimeSpan]::FromHours(5)) { $nights[$at.Date] = $e }
    }
    # Skipped nights in a row: consecutive trigger dates, back from the latest night with a result, each skipped. A
    # night with no result ends the count, as a night that ran does.
    $streak = 0
    if ($nights.Count -gt 0) {
        $night = ($nights.Keys | Sort-Object)[-1]
        while ($nights.ContainsKey($night) -and "$($nights[$night].outcome)".StartsWith('skipped')) { $streak++; $night = $night.AddDays(-1) }
    }
    if ($streak -gt 1) { "Skipped $streak nights in a row: the desk was locked or in use at 03:00." }
    if ($task) {
        # The last 03:00 whose window has closed since the install: a night with no result is a night the machine slept,
        # was off or nobody was logged on, when nothing could write one.
        $last = (Get-Date).Date.AddHours(3)
        if ((Get-Date) -lt $last.AddHours(2)) { $last = $last.AddDays(-1) }
        $since = if (Test-Path -LiteralPath $installed) { ([DateTime](Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json).at).ToLocalTime() } else { $last }
        if ($last -gt $since -and -not $nights.ContainsKey($last.Date)) { "No result for $($last.ToString('yyyy-MM-dd')): the machine was asleep, off or logged off at 03:00." }
    }
    if ($manual.Count -gt 0) { "Last on-demand run: $(Format-Outcome $manual[-1])." }
    elseif ($nightly.Count -eq 0) { 'No run yet: pwsh scripts/smoke-input.ps1 runs the real-input tests now.' }
}

# ---- the task ----------------------------------------------------------------------------------------------------

if ($Status) { Show-Status; exit 0 }
if ($Install) {
    $shell = (Get-Process -Id $PID).Path
    # The state folder goes in the task's own arguments, so the nightly run writes where -Status here will read.
    $arguments = "-NoProfile -WindowStyle Hidden -File $(ConvertTo-CommandLineArgument $PSCommandPath) -Scheduled -Invocation task -StateFolder $(ConvertTo-CommandLineArgument ([IO.Path]::GetFullPath($StateFolder)))"
    if ($TaskArguments) { $arguments += " $TaskArguments" }
    $action = New-ScheduledTaskAction -Execute $shell -Argument $arguments -WorkingDirectory $repo
    $trigger = New-ScheduledTaskTrigger -Daily -At '03:00'
    # Never woken, never run late for a missed night, ended after two hours whatever it is doing.
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
    $settings.WakeToRun = $false
    $settings.StartWhenAvailable = $false
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    $temporary = "$installed.$PID.tmp"
    [IO.File]::WriteAllText($temporary, (@{ at = (Get-Date).ToUniversalTime().ToString('o'); task = $TaskName } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $installed -Force
    "Installed '$TaskName': the real-input smoke runs daily at 03:00 on an idle, unlocked desktop. pwsh scripts/smoke-input.ps1 -Status says how it went."
    exit 0
}
if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Ignore
    Remove-Item -LiteralPath $installed -ErrorAction Ignore
    "Removed '$TaskName': the real-input smoke no longer runs at night."
    exit 0
}

# ---- a run -------------------------------------------------------------------------------------------------------

$mode = if ($Scheduled) { 'scheduled' } else { 'manual' }
$started = (Get-Date).ToUniversalTime()
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $StateFolder "input-$stamp.log"
for ($n = 2; Test-Path -LiteralPath $log; $n++) { $log = Join-Path $StateFolder "input-$stamp-$n.log" }
$results = Join-Path $StateFolder "results-$([IO.Path]::GetFileNameWithoutExtension($log).Substring(6))"
New-Item -ItemType Directory -Force -Path $results | Out-Null
function Say([string]$line) { $line; Add-Content -LiteralPath $log -Value "$((Get-Date).ToString('HH:mm:ss')) $line" }
$end = $null
if ($Scheduled) {
    $end = if ($Deadline) { [DateTime]::Parse($Deadline, [Globalization.CultureInfo]::InvariantCulture) } else { (Get-Date).Date.AddHours(5) }
}
# The seconds left to the deadline, for a step's timeout; 0 (none) for a run on demand.
function Remaining { if ($end) { [Math]::Max(1, [int][Math]::Ceiling(($end - (Get-Date)).TotalSeconds)) } else { 0 } }
function PastDeadline { $null -ne $end -and (Get-Date) -ge $end }
# The nightly window is the two hours before the deadline (03:00 to 05:00): a scheduled run started earlier, by hand
# from Task Scheduler or a clock that moved, is a night outside it.
function BeforeWindow { $null -ne $end -and (Get-Date) -lt $end.AddHours(-2) }
function Locked { if ($TestProbes) { Test-Path -LiteralPath (Join-Path $StateFolder 'probe-locked') } else { [SmokeInputDesk]::Locked() } }
function IdleSeconds {
    $probe = Join-Path $StateFolder 'probe-idle'
    if ($TestProbes -and (Test-Path -LiteralPath $probe)) { return [double](Get-Content -LiteralPath $probe -Raw) }
    [SmokeInputDesk]::IdleSeconds()
}

$discovered = @()
$ran = @{}
$classFailures = @()
$entry = [ordered]@{ at = $started.ToString('o'); mode = $mode; invocation = $Invocation; outcome = ''; reason = ''; discovered = 0; passed = 0; failed = 0; skipped = 0; log = $log }
function Finish([string]$outcome, [string]$reason, [int]$code) {
    $entry.outcome = $outcome
    $entry.reason = $reason
    $entry.discovered = $discovered.Count
    $entry.passed = @($ran.Values | Where-Object { $_ -eq 'Passed' }).Count
    $entry.failed = @($ran.Values | Where-Object { $_ -eq 'Failed' }).Count
    $entry.skipped = @($ran.Values | Where-Object { $_ -ne 'Passed' -and $_ -ne 'Failed' }).Count
    Say "$outcome$(if ($reason) { ": $reason" })"
    Add-History ([pscustomobject]$entry)
    # The last 30 logs and their results kept. A run's results go first and its log only once they are gone: the log
    # is what the next run finds, so results that could not be removed (a file held open) are tried again then.
    Get-ChildItem -LiteralPath $StateFolder -Filter 'input-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 30 | ForEach-Object {
        $old = Join-Path $StateFolder "results-$($_.BaseName.Substring(6))"
        try {
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction Stop }
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
        } catch { Write-Warning "smoke-input: $old could not be removed yet, and is tried again after the next run: $($_.Exception.Message)" }
    }
    $script:exitCode = $code
}

$script:exitCode = 1
$holder = $null
$watch = $null
$watchFile = Join-Path $StateFolder "watch-$stamp.txt"
$savedHeld = $env:BOSUN_DESKTOP_HELD
$savedInput = $env:BOSUN_SMOKE_INPUT
# The run is a function, so a `return` at any step ends the run, not the script: the script then exits with the code
# that step recorded (at the script's top level a `return` ended it at once, with exit 0, whatever was recorded).
function Invoke-Smoke {
try {
    Say "Real-input smoke, $mode, started"
    if (PastDeadline) { Finish 'stopped-deadline' 'it began after 05:00' 1; return }
    if (BeforeWindow) { Finish 'skipped-outside-window' "it started at $((Get-Date).ToString('HH:mm')), before the window that opens at $($end.AddHours(-2).ToString('HH:mm'))" 0; return }

    # The desktop lock, held to the end: a smoke or capture started meanwhile waits for this run, and this run's own
    # classes work under its hold (BOSUN_DESKTOP_HELD), never waiting on it.
    $settingsNow = [Bosun.Desktop.DesktopSettings]::FromEnvironment()
    $waiter = [Bosun.Desktop.DesktopWaiter]::new($settingsNow.Name, [Bosun.Desktop.DesktopLock]::RecordFolder(), 'smoke-input.ps1', $null)
    try {
        $holder = $waiter.TryTake([TimeSpan]::Zero)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $holder -and $clock.Elapsed -lt $settingsNow.Wait -and -not (PastDeadline)) { $holder = $waiter.TryTake([TimeSpan]::FromMilliseconds(250)) }
    } finally { $waiter.Dispose() }
    if (-not $holder) {
        $who = [Bosun.Desktop.DesktopLock]::Describe($settingsNow.Name, [Bosun.Desktop.DesktopLock]::RecordFolder())
        if (PastDeadline) { Finish 'stopped-deadline' "05:00 came while it waited for the desktop, in use by $who" 1; return }
        if ($Scheduled) { Finish 'skipped-in-use' "the desktop was in use by $who" 0 } else { Finish 'failed' "the desktop lock was held by $who" 1 }
        return
    }
    $env:BOSUN_DESKTOP_HELD = $holder.HeldValue

    # The watcher, from the start: a person's input from now on is told from the smoke's own by Windows' mark.
    $watcherExe = if ($Watcher) { $Watcher } else { Join-Path $repo 'build\dotnet\bin\Bosun.TestChild\debug\Bosun.TestChild.exe' }

    # A full build is a heavy job: it waits its turn in the Bosun queue when there is one, and uses the two nodes the
    # gate and CI build with (docs/build-performance.md). Its wait counts against the deadline.
    $buildArgs = @('build', $project, '-nologo', '-maxCpuCount:2')
    $queued = [bool](Get-Command bosun -CommandType Application -ErrorAction Ignore)
    $build = Measure-Job -FilePath $(if ($queued) { 'bosun' } else { $Dotnet }) -ArgumentList $(if ($queued) { @('run', '--', $Dotnet) + $buildArgs } else { $buildArgs }) -WorkingDirectory $repo -OutputPath (Join-Path $results 'build.log') -TimeoutSeconds (Remaining)
    if ($build.TimedOut) { Finish 'stopped-deadline' 'the build ran into 05:00' 1; return }
    if ($build.ExitCode -ne 0) { Finish 'failed' "the build failed (exit $($build.ExitCode)); see $(Join-Path $results 'build.log')" 1; return }

    # Which tests there are, by name: what ran is reconciled against this.
    $env:BOSUN_SMOKE_INPUT = '1'
    $listing = Join-Path $results 'tests.json'
    $list = Measure-Job -FilePath $Dotnet -ArgumentList @('test', '--project', $project, '--no-build', '--list-tests', 'json', '--filter-trait', 'Input=Real') -WorkingDirectory $repo -OutputPath $listing -TimeoutSeconds (Remaining)
    if ($list.TimedOut) { Finish 'stopped-deadline' 'finding the tests ran into 05:00' 1; return }
    if ($list.ExitCode -ne 0) { Finish 'failed' "the tests could not be listed (exit $($list.ExitCode))" 1; return }
    $text = Get-Content -LiteralPath $listing -Raw
    $json = $text.Substring([Math]::Max(0, $text.IndexOf('{')))
    $discovered = @(($json | ConvertFrom-Json).testContainers | ForEach-Object { $_.tests } | ForEach-Object { $_.displayName })
    if ($discovered.Count -eq 0) { Finish 'failed' 'no real-input test was found' 1; return }
    # A theory's cases are named with their arguments: its class is the name before the method.
    $classes = @($discovered | ForEach-Object { ($_ -replace '\(.*$', '') -replace '\.[^.]+$', '' } | Select-Object -Unique)
    Say "$($discovered.Count) real-input tests in $($classes.Count) classes"

    $watchStart = Get-Date
    if ($Scheduled) {
        # The watcher first, and ready (both hooks in, its "watching" line read) before the idle clock is read: input
        # from before then is in Windows' idle clock, input from after it in the watcher's record, so none falls between.
        # Only the nightly run watches for a person: run on demand, the operator chose to hand over the desk.
        $info = [Diagnostics.ProcessStartInfo]::new($watcherExe)
        $info.ArgumentList.Add('watch-input'); $info.ArgumentList.Add($watchFile)
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true
        $watch = [Diagnostics.Process]::Start($info)
        $watch.StandardInput.Close()
        $ready = $watch.StandardOutput.ReadLineAsync()
        if (-not $ready.Wait(10000) -or $ready.Result -ne 'watching') { Finish 'failed' "the input watcher did not start$(if ($watch.HasExited) { " (exit $($watch.ExitCode))" })" 1; return }
        $watchStart = Get-Date
        if (Locked) { Finish 'skipped-locked' 'the desktop was locked' 0; return }
        $idle = IdleSeconds
        if ($idle -lt $IdleMinutes * 60) { Finish 'skipped-in-use' "the desk was used $([int]($idle / 60)) minutes before, under the $IdleMinutes needed" 0; return }
    }
    # Why the run must stop now, or nothing: asked before each class and every 200 ms while one runs. A watcher that
    # has ended, or a record it cannot read, stops the run as a person would: the run never goes on unwatched.
    function StopReason {
        if (PastDeadline) { return 'deadline' }
        if (-not $Scheduled) { return $null }
        if (Locked) { return 'locked' }
        if ($watch.HasExited) { return 'watcher' }
        $said = $null
        for ($try = 0; $try -lt 3 -and $null -eq $said; $try++) {
            try { $said = [IO.File]::ReadAllText($watchFile).Trim() } catch { Start-Sleep -Milliseconds 50 }
        }
        if ($null -eq $said) { return 'unreadable' }
        if ($said -eq 'none') { return $null }
        $at = [DateTime]::MinValue
        if (-not [DateTime]::TryParse($said, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$at)) { return 'unreadable' }
        if ($at.ToUniversalTime() -gt $watchStart.ToUniversalTime()) { return 'person' }
        $null
    }
    function Stop-For([string]$why, [string]$when) {
        switch ($why) {
            'deadline'   { Finish 'stopped-deadline' "05:00 came $when" 1 }
            'locked'     { Finish 'stopped-locked' "the desktop was locked $when" 1 }
            'person'     { Finish 'stopped-in-use' "a person used the desk $when" 1 }
            'watcher'    { Finish 'failed' "the input watcher ended (exit $($watch.ExitCode)) $when, so the run stopped unwatched" 1 }
            'unreadable' { Finish 'failed' "the input watcher's record could not be read $when, so the run stopped unwatched" 1 }
        }
    }

    foreach ($class in $classes) {
        $why = StopReason
        if ($why) { Stop-For $why "before $class"; return }
        Say "running $class"
        $trx = "$($class.Split('.')[-1]).trx"
        $run = Measure-Job -FilePath $Dotnet -ArgumentList @('test', '--project', $project, '--no-build', '--filter-class', $class, '--filter-trait', 'Input=Real',
            '--results-directory', $results, '--report-xunit-trx', '--report-xunit-trx-filename', $trx) -WorkingDirectory $repo -OutputPath (Join-Path $results "$trx.log") -StopWhen { StopReason }
        # The class's report, read whole or not at all: one ended mid-write is not a report.
        $rows = @()
        $report = Join-Path $results $trx
        if (Test-Path -LiteralPath $report) {
            try { $rows = @(([xml](Get-Content -LiteralPath $report -Raw)).TestRun.Results.UnitTestResult | Where-Object { $_ }) }
            catch { if (-not $run.TimedOut) { Finish 'failed' "the report of $class could not be read: $($_.Exception.Message)" 1; return } }
        }
        if ($run.TimedOut) { Stop-For $(if ($run.StoppedBy) { $run.StoppedBy } else { 'deadline' }) "during $class, which was ended"; return }
        foreach ($result in $rows) {
            $name = $result.testName
            if ($ran.ContainsKey($name)) { $ran["$name (again)"] = 'Repeated' } else { $ran[$name] = $result.outcome }
        }
        # A class whose run failed fails the run, whatever its report says.
        if ($run.ExitCode -ne 0) { $classFailures += "$class (exit $($run.ExitCode))" }
    }

    # Every discovered test ran once and passed; nothing else ran.
    $missing = @($discovered | Where-Object { -not $ran.ContainsKey($_) })
    $repeated = @($ran.Keys | Where-Object { $_ -like '* (again)' })
    $notPassed = @($ran.GetEnumerator() | Where-Object { $_.Value -ne 'Passed' -and $_.Key -notlike '* (again)' } | ForEach-Object { "$($_.Key) ($($_.Value))" })
    $unexpected = @($ran.Keys | Where-Object { $_ -notlike '* (again)' -and $discovered -notcontains $_ })
    $problems = @()
    if ($missing) { $problems += "did not run: $($missing -join ', ')" }
    if ($repeated) { $problems += "ran twice: $(($repeated -replace ' \(again\)$', '') -join ', ')" }
    if ($notPassed) { $problems += "did not pass: $($notPassed -join ', ')" }
    if ($unexpected) { $problems += "ran but was not found: $($unexpected -join ', ')" }
    if ($classFailures) { $problems += "a class's run failed: $($classFailures -join ', ')" }
    if ($problems) { Finish 'failed' ($problems -join '; ') 1 } else { Finish 'passed' '' 0 }
} finally {
    if ($watch -and -not $watch.HasExited) { & taskkill.exe /T /F /PID $watch.Id *> $null }
    Remove-Item -LiteralPath $watchFile -ErrorAction Ignore
    $env:BOSUN_DESKTOP_HELD = $savedHeld
    $env:BOSUN_SMOKE_INPUT = $savedInput
    if ($holder) { $holder.Dispose() }
}
}
Invoke-Smoke
exit $script:exitCode
