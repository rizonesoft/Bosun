# Holds the machine awake and its display on for as long as a campaign's writer runs (D00 T03 §28), through a Windows
# power request this process holds, which Windows ends with the process: a crash, a closed window or a kill releases
# it, and nothing persistent on the machine is changed.
#
#   pwsh scripts/lib/hold-awake.ps1 -ParentPid <pid> [-Minutes <n>] [-Record <file>]
#
# It calls SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED), checks its return
# value, writes one line, `held` or `failed: <why>`, to standard output for its launcher (scripts/start-run.ps1) and the
# same to the record (build/run-hold.json, temporary file then replace: its pid, the parent's, `acquired`, the minutes
# asked, when), then waits for the first of: the parent process ending, its standard input closing (the launcher's
# release when the writer returns) or the minutes asked running out. Exit 0 after a hold, 3 when the request failed.
#
# The request does not stop the screen saver (Microsoft: "This function does not stop the screen saver from
# executing", https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate), so a
# saver that locks on resume is the owner's setting, which scripts/campaign-preflight.ps1 checks and the campaign never
# writes. Its evidence is this holder's own: the system's execution state can be held by any process.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][int]$ParentPid,
  [ValidateRange(0, 10080)][double]$Minutes = 0,
  [string]$Record = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'build\run-hold.json')
)
$ErrorActionPreference = 'Stop'

function Save-Hold([hashtable]$fields) {
  $dir = Split-Path $Record
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
  $text = $fields | ConvertTo-Json -Compress
  $tmp = "$Record.$PID.tmp"
  # File.Move, not Move-Item -Force: that one overwrites a read-only record and moves into a directory of its name,
  # so a record it did not write would read as written. A write that fails part way (a full disk) leaves no
  # temporary file behind.
  try {
    if ($env:BOSUN_TEST_HOLD_PARTIAL) { [IO.File]::WriteAllText($tmp, '{"pid":'); throw 'test: the disk filled part way through the record' }
    [IO.File]::WriteAllText($tmp, $text, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp, $Record, $true)
  } catch { Remove-Item -LiteralPath $tmp -ErrorAction Ignore; throw }
}

if (-not ('BosunAwake.Power' -as [type])) {
  # The input is watched on a thread of its own: Console.In's ReadLineAsync blocks its caller until a line or the end
  # of input, which would keep this loop from seeing its parent end or its time run out.
  Add-Type -TypeDefinition @'
namespace BosunAwake {
  public static class Power {
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint flags);
    public static System.Threading.ManualResetEventSlim WatchInput() {
      var closed = new System.Threading.ManualResetEventSlim(false);
      var reader = new System.Threading.Thread(() => {
        // An input that cannot be read is one that has closed: the launcher is gone, so the hold is released.
        try { while (System.Console.In.ReadLine() != null) { } } catch (System.IO.IOException) { } catch (System.ObjectDisposedException) { }
        closed.Set();
      });
      reader.IsBackground = true;
      reader.Start();
      return closed;
    }
  }
}
'@
}
# ES_CONTINUOUS (0x80000000) | ES_SYSTEM_REQUIRED (0x1) | ES_DISPLAY_REQUIRED (0x2), in decimal: PowerShell reads 0x80000000 as a negative Int32.
$flags = [uint32]2147483651
# Test-only fault injection: the call fails as a denied request would.
$previous = if ($env:BOSUN_TEST_HOLD_FAIL) { [uint32]0 } else { [BosunAwake.Power]::SetThreadExecutionState($flags) }
$acquired = $previous -ne 0
$why = if ($acquired) { $null } else { "SetThreadExecutionState refused the request (error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))" }
# The flags asked for are recorded, so a test can tell a request that dropped one (Windows accepts a system-only one).
$fields = @{ flags = $flags; pid = $PID; started = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); parent = $ParentPid; acquired = $acquired; minutes = $Minutes; at = [datetime]::UtcNow.ToString('o') }
if ($why) { $fields.why = $why }
if ($env:BOSUN_TEST_HOLD_STALL) { Start-Sleep -Seconds ([int]$env:BOSUN_TEST_HOLD_STALL) }
$unrecorded = $null
try { Save-Hold $fields } catch { $unrecorded = "$($_.Exception.Message)" }
# One line for the launcher: held or failed, and a record that could not be written, since a reader (the preflight)
# would then see an older run's record.
$said = if ($acquired) { 'held' } else { "failed: $why" }
if ($unrecorded) { $said += "; but $Record could not be written ($unrecorded)" }
[Console]::Out.WriteLine($said)
[Console]::Out.Flush()

$parent = Get-Process -Id $ParentPid -ErrorAction Ignore
$deadline = if ($Minutes -gt 0) { [datetime]::UtcNow.AddMinutes($Minutes) } else { [datetime]::MaxValue }
$released = [BosunAwake.Power]::WatchInput()  # set when the launcher closes our standard input; a stray line is no release
$why = 'its parent ended'
while ($true) {
  if (-not $parent -or $parent.HasExited) { break }
  if ($released.IsSet) { $why = 'its launcher released it'; break }
  if ([datetime]::UtcNow -ge $deadline) { $why = "its $Minutes minute(s) ran out"; break }
  [void]$released.Wait(200)
}
# A failed holder waits as a held one does, so its record reads as this run's until its writer returns or its window
# closes, and is then marked released like any other.
if ($acquired) { [void][BosunAwake.Power]::SetThreadExecutionState([uint32]2147483648) }  # ES_CONTINUOUS alone: the request ends now
$fields.released = [datetime]::UtcNow.ToString('o'); $fields.released_because = $why
# The request has ended either way; a release that could not be recorded is said, since the record would still read
# as a live hold to a reader that does not check the holder's process.
try { Save-Hold $fields } catch { [Console]::Error.WriteLine("hold released ($why), but $Record could not be updated: $($_.Exception.Message)") }
exit $(if ($acquired) { 0 } else { 3 })
