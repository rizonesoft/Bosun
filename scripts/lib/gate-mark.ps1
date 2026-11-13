# The mark a long local gate keeps while it runs (scripts/check-all.ps1, scripts/measure-build.ps1), so the run guard
# reads it as a wait (`gate_in_flight` in scripts/lib/run_guard.py) and doesn't send the session on to a next row, or
# let the heartbeat start one, while the gate is still deciding.
#
#   . scripts/lib/gate-mark.ps1
#   $mark = Enter-GateMark 'check-all'
#   try { ...the gate... } finally { Exit-GateMark $mark }
#
# A mark holds while the process that wrote it runs, judged by its pid and start time: a gate that is killed or crashes
# holds nothing, so removing the mark at the end is tidying, not what ends the wait. Marks go to build/gate-in-flight/
# in this checkout, or to BOSUN_GATE_MARK_DIR when that is set (the gates' own tests).

function Get-GateMarkDir {
  if ($env:BOSUN_GATE_MARK_DIR) { return $env:BOSUN_GATE_MARK_DIR }
  Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'build\gate-in-flight'
}

# Writes this process's mark, by a temporary file then a replace, and returns its path. A mark that can't be written
# (a full disk, a denied folder) throws, naming the folder: an unmarked gate would let the run guard send its session
# on to a next row while the gate still runs, so the gate stops before its first step instead.
function Enter-GateMark([string]$What) {
  try {
    $dir = Get-GateMarkDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "$PID.json"
    $doc = [ordered]@{
      pid     = $PID
      started = (Get-Process -Id $PID).StartTime.ToFileTimeUtc()  # as process_started reads it: a reused pid differs
      what    = $What
      since   = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    } | ConvertTo-Json -Compress
    $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, $doc, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $path, $true)
    $path
  } catch {
    if ($temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction Ignore }
    throw "the gate's mark couldn't be written to $(Get-GateMarkDir) ($($_.Exception.Message)), so the run guard couldn't tell it runs: free the space or the folder, then run this again"
  }
}

function Exit-GateMark([string]$Path) {
  if ($Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction Ignore }
}
