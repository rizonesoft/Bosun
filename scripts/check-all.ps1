# The one command that says whether this tree is fit to commit: every gate, in order, stopping at the first failure.
#
#   pwsh scripts/check-all.ps1                 run every step
#   pwsh scripts/check-all.ps1 -List           print the steps and what is left out, and run nothing
#   pwsh scripts/check-all.ps1 -Only plan,build   run only the steps named
#   pwsh scripts/check-all.ps1 -Wsl            also run the WSL side (this starts WSL if it is stopped)
#
# Steps, in order: the plan (the TODO graph validated, the generated plan in step), the agent tiers, the native
# build, the language standard of every compile, the hardening of every built image, the C++ tests, every PowerShell
# test in tests/, and the Python tooling suite. The first step that fails is named with its command and exit code,
# and nothing after it runs: a later step's result means little once an earlier one has failed.
#
# Each step runs in its own job object (scripts/lib/job-measure.ps1) with a time limit (-StepMinutes, default 30),
# so a step and everything it started end together when it fails, passes its limit or the gate is interrupted. The
# build and the Python suite take a turn in the queue (`bosun run --`) when bosun is installed.
#
# The WSL side is never run by default, because a gate must not start a stopped WSL machine unasked. -Wsl adds it.
# What a default run leaves out is listed in $LeftOut below, each with its reason and the command that runs it;
# tests/check_all_test.ps1 fails when a test in tests/ is neither run here nor listed there.
#
# Exit 0 when every step passed, 1 at the first failure, 2 for a step name -Only does not know.
param(
  [string[]]$Only = @(),
  [switch]$List,
  [switch]$Wsl,
  [ValidateRange(0.01, 720)][double]$StepMinutes = 30,
  # For tests/check_all_test.ps1: a JSON file of stand-in steps ([{ "name", "file", "args" }]) run in place of the real ones.
  [string]$StepFile
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/job-measure.ps1')

# What a default run does not cover, why, and how to run it.
$LeftOut = @(
  @{ What = 'tests/test_bosun_log.py'; Why = 'the logger it tests runs on Linux'; Run = 'pwsh scripts/check-all.ps1 -Wsl' }
  @{ What = 'tests/queue_test.ps1 (its WSL half)'; Why = 'it queues jobs inside WSL'; Run = 'pwsh scripts/check-all.ps1 -Wsl' }
  @{ What = 'wsl/*.sh (shell syntax)'; Why = 'bash is in WSL'; Run = 'pwsh scripts/check-all.ps1 -Wsl' }
  @{ What = 'tests/stage_wsl_test.ps1'; Why = 'it asks WSL to read the folder it staged'; Run = 'pwsh scripts/check-all.ps1 -Wsl' }
)
# The PowerShell tests that need WSL, so they run only with -Wsl. Each is in $LeftOut above with its reason.
$NeedsWsl = @('stage_wsl_test.ps1')

function New-Step([string]$Name, [string]$File, [string[]]$Arguments = @(), [switch]$Queue) {
  [pscustomobject]@{ Name = $Name; File = $File; Arguments = $Arguments; Queue = [bool]$Queue }
}

if ($StepFile) {
  $steps = @(Get-Content -LiteralPath $StepFile -Raw | ConvertFrom-Json | ForEach-Object { New-Step $_.name $_.file @($_.args) })
} else {
  $steps = @(
    New-Step 'plan' 'python' @('scripts/todo-graph.py', 'plan', '--check')
    New-Step 'tiers' 'node' @('scripts/verify-agent-tiers.mjs')
    New-Step 'build' 'pwsh' @('-NoProfile', '-File', 'scripts/build.ps1') -Queue
    New-Step 'native-std' 'python' @('scripts/native-std-check.py')
    New-Step 'native-pe' 'python' @('scripts/native-pe-check.py', 'build/Release')
    New-Step 'core-tests' (Join-Path $repo 'build/Release/test_core.exe')
  )
  foreach ($test in Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Filter '*_test.ps1' | Sort-Object Name) {
    if ($test.Name -in $NeedsWsl -and -not $Wsl) { continue }
    $extra = if ($test.Name -eq 'queue_test.ps1' -and -not $Wsl) { @('-SkipWsl') } else { @() }
    $steps += New-Step ("test:" + ($test.BaseName -replace '_test$', '')) 'pwsh' (@('-NoProfile', '-File', "tests/$($test.Name)") + $extra)
  }
  $steps += New-Step 'python-tests' 'python' @('scripts/run-tests.py') -Queue
  if ($Wsl) {
    $steps += New-Step 'wsl:logger-tests' 'wsl' @('--cd', $repo, 'python3', '-B', 'tests/test_bosun_log.py')
    # Every shell script the WSL side has, found here so a new one is checked without this list being edited:
    # wsl/bosun (which has no extension) and each wsl/*.sh.
    $shell = @('wsl/bosun') + @(Get-ChildItem -LiteralPath (Join-Path $repo 'wsl') -Filter '*.sh' -ErrorAction Ignore | Sort-Object Name | ForEach-Object { "wsl/$($_.Name)" })
    # One step for each, with nothing quoted: a loop passed through wsl.exe loses its quotes on the way.
    foreach ($script in $shell) { $steps += New-Step "wsl:syntax:$(Split-Path $script -Leaf)" 'wsl' @('--cd', $repo, 'bash', '-n', $script) }
  }
}

function Format-Command($step) { (@($step.File) + $step.Arguments) -join ' ' }

if ($List) {
  foreach ($step in $steps) { "$($step.Name): $(Format-Command $step)" }
  if (-not $StepFile -and -not $Wsl) {
    ''
    'left out of a default run:'
    foreach ($item in $LeftOut) { "  $($item.What): $($item.Why). Run: $($item.Run)" }
  }
  exit 0
}

$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$unknown = @($Only | Where-Object { $_ -notin $steps.Name })
if ($unknown) { "check-all: no step named $($unknown -join ', '). The steps are: $($steps.Name -join ', ')"; exit 2 }
if ($Only) { $steps = @($steps | Where-Object { $_.Name -in $Only }) }

$queue = [bool](Get-Command bosun -CommandType Application -ErrorAction Ignore)
$started = [Diagnostics.Stopwatch]::StartNew()
foreach ($step in $steps) {
  $file = $step.File; $arguments = $step.Arguments
  if (-not (Get-Command $file -CommandType Application -ErrorAction Ignore)) {
    "FAIL $($step.Name): $file is not on PATH or does not exist, so the step could not start ($(Format-Command $step))"
    exit 1
  }
  if ($step.Queue -and $queue) { $arguments = @('run', '--', $file) + $arguments; $file = 'bosun' }
  "---- $($step.Name): $(Format-Command $step)"
  $result = Measure-Job -FilePath $file -ArgumentList $arguments -WorkingDirectory $repo -TimeoutSeconds ([int][Math]::Ceiling($StepMinutes * 60))
  if ($result.TimedOut) {
    "FAIL $($step.Name): stopped at its $StepMinutes minute limit, with everything it started ($(Format-Command $step))"
    exit 1
  }
  if ($result.ExitCode -ne 0) {
    "FAIL $($step.Name): exit $($result.ExitCode) from: $(Format-Command $step)"
    exit 1
  }
  "ok   $($step.Name) ($($result.Seconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)) s)"
}
"all $($steps.Count) steps passed in $([int]$started.Elapsed.TotalSeconds) s"
exit 0
