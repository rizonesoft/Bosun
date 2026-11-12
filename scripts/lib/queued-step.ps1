# Runs one command and marks the moment it starts, for a step that waits its turn in the queue first.
#
#   bosun run -- pwsh -NoProfile -File scripts/lib/queued-step.ps1 <mark file> <program> [arguments...]
#
# `bosun run` holds this script back until a slot is free. Its first act is to create the mark file, which tells
# Measure-Job (-StartedPath) that the wait is over and the step's own time limit starts now. Then it runs the
# program with its arguments and exits with its exit code.
$mark, $program, $rest = $args
if (-not $mark -or -not $program) { 'queued-step needs a mark file and a program'; exit 2 }
[IO.File]::WriteAllText($mark, 'started')
& $program @rest
exit $LASTEXITCODE
