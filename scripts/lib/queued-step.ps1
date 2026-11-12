# Runs one command and marks the moment it starts, for a step that waits its turn in the queue first.
#
#   bosun run -- pwsh -NoProfile -File scripts/lib/queued-step.ps1 <mark file> <command>
#
# <command> is the program and its arguments as one JSON array, base64-encoded (UTF-8), so every argument arrives
# whole: `pwsh -File` splits an argument shaped like a parameter with a value (`-maxCpuCount:2` into
# `-maxCpuCount:` and `2`), and a program then reads the stray half as one more argument.
# `bosun run` holds this script back until a slot is free. Its first act is to create the mark file, which tells
# Measure-Job (-StartedPath) that the wait is over and the step's own time limit starts now. Then it runs the
# program with its arguments and exits with its exit code.
#
# If the mark cannot be written the program is not started: without the mark the step would run on the queue's
# time allowance, or on none, in place of its own limit. Exit 3 says so.
$ErrorActionPreference = 'Stop'
$mark, $encoded = $args[0], $args[1]
if (-not $mark -or -not $encoded -or $args.Count -ne 2) { 'queued-step needs a mark file and one encoded command'; exit 2 }
try { $command = @([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) | ConvertFrom-Json -NoEnumerate) } catch {
  "queued-step could not read its command (base64 of a JSON array): $($_.Exception.Message)"; exit 2
}
# The rest stay an array whatever their number: one argument on its own would otherwise be splatted letter by letter.
$program = [string]$command[0][0]
$rest = @($command[0] | Select-Object -Skip 1 | ForEach-Object { [string]$_ })
if (-not $program) { 'queued-step needs a program in its command'; exit 2 }
try { [IO.File]::WriteAllText($mark, 'started') } catch {
  "queued-step could not write its start mark ${mark}: $($_.Exception.InnerException.Message ?? $_.Exception.Message). The step was not started."
  exit 3
}
& $program @rest
exit $LASTEXITCODE
