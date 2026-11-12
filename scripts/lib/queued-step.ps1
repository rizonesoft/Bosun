# Runs one command and marks the moment it starts, for a step that waits its turn in the queue first.
#
#   bosun run -- pwsh -NoProfile -File scripts/lib/queued-step.ps1 <mark file> <program> [arguments...]
#
# `bosun run` holds this script back until a slot is free. Its first act is to create the mark file, which tells
# Measure-Job (-StartedPath) that the wait is over and the step's own time limit starts now. Then it runs the
# program with its arguments and exits with its exit code.
#
# If the mark cannot be written the program is not started: without the mark the step would run on the queue's
# time allowance, or on none, in place of its own limit. Exit 3 says so.
$ErrorActionPreference = 'Stop'
$mark, $program, $rest = $args
if (-not $mark -or -not $program) { 'queued-step needs a mark file and a program'; exit 2 }
try { [IO.File]::WriteAllText($mark, 'started') } catch {
  "queued-step could not write its start mark ${mark}: $($_.Exception.InnerException.Message ?? $_.Exception.Message). The step was not started."
  exit 3
}
& $program @rest
exit $LASTEXITCODE
