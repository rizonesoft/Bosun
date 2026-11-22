# The one command that says whether this tree is fit to commit: every gate, in order, stopping at the first failure.
#
#   pwsh scripts/check-all.ps1                 run every step
#   pwsh scripts/check-all.ps1 -List           print the steps and what is left out, and run nothing
#   pwsh scripts/check-all.ps1 -Only plan,build   run only the steps named
#   pwsh scripts/check-all.ps1 -Wsl            also run the WSL side (this starts WSL if it is stopped)
#
# Steps, in order: the plan (the TODO graph validated, the generated plan in step), the weakening scan (every
# committed hunk that deletes or skips a test, drops an assertion, relaxes a warning or swallows an error has its
# answer in a commit body), the agent tiers, the native
# build, the language standard of every compile, the hardening of every built image, the C++ tests, every PowerShell
# test in tests/, and the Python tooling suite. The first step that fails is named with its command and exit code,
# and nothing after it runs: a later step's result means little once an earlier one has failed.
#
# Each step runs in its own job object (scripts/lib/job-measure.ps1) with a time limit (-StepMinutes, default 30),
# so a step and everything it started end together when it fails, passes its limit or the gate is interrupted. The
# build and the Python suite take a turn in the queue (`bosun run --`) when bosun is installed. A queued step may
# wait up to -QueueMinutes (default 120; 0 waits without limit) for its turn, and its own limit starts only when its turn comes: on a busy
# machine the wait for a slot is not the step running, and a step that has its slot gets no longer than any other.
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
  [ValidateRange(0, 1440)][double]$QueueMinutes = 120,
  # For tests/check_all_test.ps1: a JSON file of stand-in steps ([{ "name", "file", "args" }]) run in place of the real ones.
  [string]$StepFile
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/job-measure.ps1')
. (Join-Path $PSScriptRoot 'lib/scratch.ps1')

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
  $steps = @(Get-Content -LiteralPath $StepFile -Raw | ConvertFrom-Json | ForEach-Object { New-Step $_.name $_.file @($_.args) -Queue:([bool]$_.queue) })
} else {
  $steps = @(
    New-Step 'plan' 'python' @('scripts/todo-graph.py', 'plan', '--check')
    # The panel's theme is generated from design/tokens/colors.json (D02 T01 §1): a hand edit or a stale theme fails here.
    New-Step 'theme' 'python' @('scripts/build-theme.py', '--check')
    # The app icon and the tray's state icons are generated from design/icon/ and the tokens (D02 T01 §3, §5): a
    # hand-edited or stale ICO fails here.
    New-Step 'icons' 'python' @('scripts/build-icons.py', '--check')
    New-Step 'weakening' 'python' @('scripts/weakening-scan.py')
    New-Step 'tiers' 'node' @('scripts/verify-agent-tiers.mjs')
    New-Step 'build' 'pwsh' @('-NoProfile', '-File', 'scripts/build.ps1') -Queue
    New-Step 'native-std' 'python' @('scripts/native-std-check.py')
    New-Step 'native-pe' 'python' @('scripts/native-pe-check.py', 'build/Release')
    New-Step 'core-tests' (Join-Path $repo 'build/Release/test_core.exe')
    # The .NET solution: built once, then every test project run on what was built (Microsoft.Testing.Platform, as
    # global.json selects). Two nodes measured fastest and leanest (docs/build-performance.md, dotnet-cpu-2); it is
    # passed here because `dotnet build` overrides a limit in Directory.Build.rsp.
    New-Step 'dotnet-build' 'dotnet' @('build', 'Bosun.slnx', '-nologo', '-maxCpuCount:2') -Queue
    New-Step 'dotnet-tests' 'dotnet' @('test', '--solution', 'Bosun.slnx', '--no-build') -Queue
    # The end-to-end smoke sits outside the solution (it is never run here), but is built: tests/smoke_input_test.ps1
    # runs one of its fixture tests, which sends no input, under a holder's lock (D02 T02 §12).
    New-Step 'smoke-build' 'dotnet' @('build', 'tests/Bosun.Panel.E2E', '-nologo', '-maxCpuCount:2') -Queue
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
  # A queued step is marked, so what waits its turn through `bosun run --` can be seen without running it.
  foreach ($step in $steps) { "$($step.Name): $(if ($step.Queue) { '[queued] ' })$(Format-Command $step)" }
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

# Each execution creates a distinct run log under the scratch root before the gate mark and before any step runs,
# streaming all gate lines and step stdout/stderr to both console and log incrementally (D00 T07 §4).
$scratchRoot = Get-BosunScratchRoot
$logName = "bosun-check-all-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8)).log"
$logPath = Join-Path $scratchRoot $logName
try {
  $initStream = [IO.File]::Open($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
  $initStream.Dispose()
} catch {
  "check-all: could not create run log '$logPath': $($_.Exception.Message)"
  exit 1
}

$utf8 = [Text.UTF8Encoding]::new($false)
$stdout = [Console]::OpenStandardOutput()
function Write-ConsoleBytes([byte[]]$Bytes) {
  if (-not $Bytes -or $Bytes.Length -eq 0) { return }
  $stdout.Write($Bytes, 0, $Bytes.Length)
  $stdout.Flush()
}
function Write-ConsoleLine([string]$Line) {
  Write-ConsoleBytes ($utf8.GetBytes($Line + [Environment]::NewLine))
}
function Write-LogBytes([byte[]]$Bytes) {
  if (-not $Bytes -or $Bytes.Length -eq 0) { return }
  try {
    $fs = [IO.File]::Open($logPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $fs.Write($Bytes, 0, $Bytes.Length); $fs.Flush() } finally { $fs.Dispose() }
  } catch {
    $err = [InvalidOperationException]::new("check-all: failed to write run log '$logPath': $($_.Exception.Message)", $_.Exception)
    $err.Data['BosunLogWriteFailure'] = $true
    throw $err
  }
}
function Write-GateLine([string]$Line) {
  $bytes = $utf8.GetBytes($Line + [Environment]::NewLine)
  Write-ConsoleBytes $bytes
  Write-LogBytes $bytes
}

# While the steps run, the run guard reads this gate as a wait (scripts/lib/gate-mark.ps1) and surfaces $logPath.
. (Join-Path $PSScriptRoot 'lib/gate-mark.ps1')
try {
  $gateMark = Enter-GateMark 'check-all' $logPath
} catch {
  $msg = "check-all: $($_.Exception.Message)"
  try { Write-GateLine $msg } catch { Write-ConsoleLine $msg; Write-ConsoleLine $_.Exception.Message }
  Write-ConsoleLine "log: $logPath"
  exit 1
}
$savedWslenv = $env:WSLENV
$savedScratch = $env:BOSUN_SCRATCH
$wslenvChanged = $false
if ($Wsl -and -not [string]::IsNullOrWhiteSpace($env:BOSUN_SCRATCH)) {
  $trimmedScratch = $env:BOSUN_SCRATCH.Trim()
  if (Test-Path -LiteralPath $trimmedScratch -PathType Container) {
    # Forward the normalized, resolved Windows BOSUN_SCRATCH to WSL steps in Linux path form (D00 T07 §3).
    $env:BOSUN_SCRATCH = (Resolve-Path -LiteralPath $trimmedScratch).ProviderPath
    $parts = @($env:WSLENV -split ':' | Where-Object { $_ -and $_ -notmatch '^BOSUN_SCRATCH(/|$)' })
    $env:WSLENV = ($parts + 'BOSUN_SCRATCH/p') -join ':'
    $wslenvChanged = $true
  }
}
try {
$queue = [bool](Get-Command bosun -CommandType Application -ErrorAction Ignore)
$sdkPrinted = $false
$started = [Diagnostics.Stopwatch]::StartNew()
foreach ($step in $steps) {
  $file = $step.File; $arguments = $step.Arguments
  if (-not (Get-Command $file -CommandType Application -ErrorAction Ignore)) {
    Write-GateLine "FAIL $($step.Name): $file is not on PATH or does not exist, so the step could not start ($(Format-Command $step))"
    Write-ConsoleLine "log: $logPath"
    exit 1
  }
  if ($file -eq 'dotnet' -and -not $sdkPrinted) {
    # Probe the effective .NET SDK once from $repo so global.json (allowPrerelease: false) applies even when
    # check-all is launched from another working directory (D00 T07 §4).
    $sdkOut = Join-Path $scratchRoot "bosun-check-all-sdk-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8)).out"
    try {
      $sdkRes = Measure-Job -FilePath $file -ArgumentList @('--version') -WorkingDirectory $repo -TimeoutSeconds 30 -OutputPath $sdkOut
      $sdkText = if (Test-Path -LiteralPath $sdkOut) { Get-Content -LiteralPath $sdkOut -Raw -ErrorAction Ignore } else { '' }
    } finally { Remove-Item -LiteralPath $sdkOut -Force -ErrorAction Ignore }
    $sdkTrimmed = if ($sdkText) { $sdkText.Trim() } else { '' }
    if ($sdkRes.TimedOut -or $sdkRes.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sdkTrimmed)) {
      if ($sdkTrimmed) { Write-GateLine $sdkTrimmed }
      $reason = if ($sdkRes.TimedOut) { 'timed out after 30 s' } elseif ($sdkRes.ExitCode -ne 0) { "exit $($sdkRes.ExitCode)" } else { 'empty version output' }
      Write-GateLine "FAIL dotnet-sdk: could not resolve .NET SDK via 'dotnet --version' in $repo ($reason)"
      Write-ConsoleLine "log: $logPath"
      exit 1
    }
    $sdkVersion = ($sdkTrimmed -split "`r?`n" | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 1).Trim()
    Write-GateLine "dotnet-sdk: $sdkVersion"
    $sdkPrinted = $true
  }
  Write-GateLine "---- $($step.Name): $(Format-Command $step)"
  $stepOut = Join-Path $scratchRoot "bosun-check-all-step-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8)).out"
  $streamState = @{ Offset = [long]0 }
  $drainOutput = {
    if (-not (Test-Path -LiteralPath $stepOut)) { return }
    $chunks = [Collections.Generic.List[byte[]]]::new()
    $rs = [IO.File]::Open($stepOut, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
      if ($rs.Length -gt $streamState.Offset) {
        $rs.Position = $streamState.Offset
        $buf = [byte[]]::new(16384)
        while (($n = $rs.Read($buf, 0, $buf.Length)) -gt 0) {
          $slice = [byte[]]::new($n)
          [Array]::Copy($buf, 0, $slice, 0, $n)
          $chunks.Add($slice)
          $streamState.Offset += $n
        }
      }
    } finally { $rs.Dispose() }
    foreach ($slice in $chunks) {
      Write-ConsoleBytes $slice
      Write-LogBytes $slice
    }
  }
  $measure = @{
    WorkingDirectory = $repo
    TimeoutSeconds   = [int][Math]::Ceiling($StepMinutes * 60)
    OutputPath       = $stepOut
    OnTick           = $drainOutput
  }
  if ($step.Queue -and $queue) {
    # The step runs under the queue through a small wrapper that marks the moment its turn comes. Until that mark
    # it may wait -QueueMinutes; from it, -StepMinutes counts, exactly as for a step that is not queued.
    $mark = Join-Path $scratchRoot ("bosun-check-all-$PID-" + [guid]::NewGuid() + '.started')
    # The command goes as one encoded argument, so `pwsh -File` cannot split one shaped like `-name:value`.
    $command = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject (@($file) + $arguments) -Compress)))
    $arguments = @('run', '--', 'pwsh', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'lib/queued-step.ps1'), $mark, $command)
    $file = 'bosun'
    $measure.StartedPath = $mark
    $measure.QueueSeconds = [int][Math]::Ceiling($QueueMinutes * 60)
    Write-GateLine "waiting $($step.Name): queued for a build slot"
    $stepName = $step.Name
    $measure.OnStarted = {
      param([double]$waited)
      Write-GateLine "started $($stepName): got build slot after $($waited.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)) s"
    }
  }
  try { $result = Measure-Job -FilePath $file -ArgumentList $arguments @measure }
  finally {
    if ($measure.StartedPath) { Remove-Item -LiteralPath $measure.StartedPath -Force -ErrorAction Ignore }
    Remove-Item -LiteralPath $stepOut -Force -ErrorAction Ignore
  }
  if ($result.TimedOutInQueue) {
    Write-GateLine "FAIL $($step.Name): waited $QueueMinutes minutes for a queue slot and did not get one ($(Format-Command $step))"
    Write-ConsoleLine "log: $logPath"
    exit 1
  }
  if ($result.TimedOut) {
    Write-GateLine "FAIL $($step.Name): stopped at its $StepMinutes minute limit, with everything it started ($(Format-Command $step))"
    Write-ConsoleLine "log: $logPath"
    exit 1
  }
  if ($result.ExitCode -ne 0) {
    Write-GateLine "FAIL $($step.Name): exit $($result.ExitCode) from: $(Format-Command $step)"
    Write-ConsoleLine "log: $logPath"
    exit 1
  }
  Write-GateLine "ok   $($step.Name) ($($result.Seconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)) s)"
}
Write-GateLine "all $($steps.Count) steps passed in $([int]$started.Elapsed.TotalSeconds) s"
Write-ConsoleLine "log: $logPath"
exit 0
} catch {
  if ($_.Exception.Data -and $_.Exception.Data['BosunLogWriteFailure']) {
    Write-ConsoleLine "$($_.Exception.Message) (partial log: $logPath)"
    exit 1
  }
  $failMsg = "FAIL check-all: $($_.Exception.Message)"
  try { Write-GateLine $failMsg } catch { Write-ConsoleLine $failMsg; Write-ConsoleLine "$($_.Exception.Message) (partial log: $logPath)" }
  Write-ConsoleLine "log: $logPath"
  exit 1
} finally {
  if ($wslenvChanged) {
    if ($null -eq $savedWslenv) { Remove-Item Env:WSLENV -ErrorAction Ignore } else { $env:WSLENV = $savedWslenv }
    if ($null -eq $savedScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $savedScratch }
  }
  Exit-GateMark $gateMark
}
