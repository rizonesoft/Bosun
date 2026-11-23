# The .NET half of scripts/measure-build.ps1 (-Dotnet), dot-sourced by it after its parameters, storage and queue
# are set up: it uses Invoke-Step, Update-Data, Get-Median and Format-Number from there, and the header of that
# script says what is measured and how. Its `exit` ends only this file; the script passes the code on. Which
# parameters were given comes from the script's $boundParameters, since this file has a $PSBoundParameters of its own.

$dotnetPath = (Get-Command $DotnetCommand -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
if (-not $dotnetPath) { "measure-build could not find '$DotnetCommand' on PATH or at that path: nothing was stored"; exit 1 }
if (-not $boundParameters.ContainsKey('TouchFile')) { $TouchFile = Join-Path $repo 'src\Bosun.Core\Foundation.cs' }
$msbuildArgs = @($BuildArgs | ForEach-Object { "-$_" })
$buildLine = @('build', $Solution, '-nologo') + $msbuildArgs

# One MSBuild property of $Project as the build would see it, with this label's arguments, or '' when it is unset.
function Get-DotnetProperty([string]$Project, [string]$Name) {
  $value = & $dotnetPath msbuild $Project -nologo "-getProperty:$Name" @msbuildArgs 2>&1 | Select-Object -Last 1
  if ($LASTEXITCODE -ne 0) { "could not evaluate $Name of ${Project}: $value"; exit 1 }
  "$value".Trim()
}

# The output assemblies, the touched project's first: given, or each project's evaluated TargetPath in solution order.
if (-not $DotnetOutputs) {
  if ($Solution -like '*.csproj') {
    $projects = @($Solution)
  } else {
    $projects = @(([xml](Get-Content -LiteralPath $Solution -Raw)).SelectNodes('//Project') | ForEach-Object { Join-Path (Split-Path $Solution) $_.GetAttribute('Path') })
  }
  $DotnetOutputs = @($projects | ForEach-Object { Get-DotnetProperty $_ 'TargetPath' })
}
if (-not $DotnetOutputs -or @($DotnetOutputs | Where-Object { -not $_ }).Count) { 'measure-build could not find the output assembly of every project: nothing was stored'; exit 1 }

function Get-OutputTimes {
  $times = [ordered]@{}
  foreach ($path in $DotnetOutputs) { $times[$path] = if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path).LastWriteTimeUtc.Ticks } else { $null } }
  $times
}
function Get-Names([string[]]$paths) { (@($paths | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') }

# A build is proved by what it wrote between $before and now: 'none' rewrites no output, 'first' rewrites the touched
# project's, 'all' writes every one. Evidence that disagrees ends the run with nothing stored.
function Assert-BuildEvidence([string]$Name, [string]$Expect, $before) {
  $after = Get-OutputTimes
  $rewritten = @($DotnetOutputs | Where-Object { $null -ne $after[$_] -and $after[$_] -ne $before[$_] })
  $missing = @($DotnetOutputs | Where-Object { $null -eq $after[$_] })
  $problem = switch ($Expect) {
    'none' { if ($rewritten -or $missing) { "rewrote $(Get-Names ($rewritten + $missing)), so it is not a build with nothing to do" } }
    'first' { if ($DotnetOutputs[0] -notin $rewritten -or $missing) { "did not rewrite $(Get-Names @($DotnetOutputs[0])) or left an output missing, so it is not that workload" } }
    'all' { if ($rewritten.Count -ne $DotnetOutputs.Count) { "did not write $(Get-Names @($DotnetOutputs | Where-Object { $_ -notin $rewritten })), so it is not a fresh build" } }
  }
  if ($problem) { "step '$Name' $problem`: nothing was stored"; Remove-Item -LiteralPath $log -Force -ErrorAction Ignore; exit 1 }
}

# One timed build, proved by what it wrote.
function Invoke-DotnetBuild([string]$Name, [string]$Expect, [bool]$Timed, [string]$DisplayName = '') {
  $before = Get-OutputTimes
  $result = Invoke-Step $Name $dotnetPath $buildLine $DisplayName
  Assert-NoOutsideServers "step '$Name'"
  Assert-BuildEvidence $Name $Expect $before
  if ($Timed) {
    if (-not $results.Contains($Name)) { $results[$Name] = @() }
    $results[$Name] += , $result
    "  $Name sample $($results[$Name].Count): $(Format-Number $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
  }
}

function Invoke-Clean([string]$DisplayName = 'clean') {
  [void](Invoke-Step 'clean' $dotnetPath (@('clean', $Solution, '-nologo') + $msbuildArgs) $DisplayName)
  $left = @($DotnetOutputs | Where-Object { Test-Path -LiteralPath $_ })
  if ($left) { "the clean before a fresh build left $(Get-Names $left): nothing was stored"; exit 1 }
}

# The process list now. A query that fails, or that does not even list this process, ends the run with nothing
# stored: read as an empty list it would pass for no worker, no compiler server and no outside server.
function Get-ProcessList {
  # Its callers filter what it returns, so a refusal is written to the host, where no filter drops it.
  $why = $null
  try { $list = @(Get-CimInstance Win32_Process -Property ProcessId, Name, CommandLine -ErrorAction Stop) }
  catch { $why = "could not read the process list ($($_.Exception.Message))" }
  if (-not $why -and -not ($list | Where-Object { [int]$_.ProcessId -eq $PID })) { $why = 'the process list did not include this process' }
  if ($why) { Write-Host "$why, so what the builds ran cannot be observed: nothing was stored"; Remove-Item -LiteralPath $log -Force -ErrorAction Ignore; exit 1 }
  $list
}

# The processes in a job now: worker nodes (MSBuild in node mode) and the compiler server (VBCSCompiler, which .NET
# runs as `dotnet exec VBCSCompiler.dll`) are told apart by their command lines.
function Get-JobProcesses($run) {
  $ids = [Collections.Generic.HashSet[int]]::new([int[]]@($run.ProcessIds()))
  @(Get-ProcessList | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
      $node = "$($_.CommandLine)" -match '[/-]nodemode:'
      [pscustomobject]@{ Id = [int]$_.ProcessId; Name = $_.Name; Node = $node; Compiler = -not $node -and "$($_.CommandLine)" -match 'VBCSCompiler' }
    })
}
# Each run writes a log of its own: a step's job ends its processes without waiting for them, so the shared log can
# still be open in one of them when the next run starts.
function Start-Run([string]$OutputPath) {
  $line = (@($dotnetPath) + $buildLine | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
  [Bosun.JobMeasure+Run]::new($line, $repo, $OutputPath)
}
# Waits for a build in its own job, sampling it, and returns the most worker nodes seen at once and whether the
# compiler server or any node ran. Ends the run when the build passes its limit or fails.
function Wait-Run($run, [string]$Name, [string]$OutputPath, [string]$DisplayName = '') {
  $labelName = if ($DisplayName) { $DisplayName } else { $Name }
  $nextProgress = 5.0
  $seen = [ordered]@{ max_nodes = 0; compiler = $false; started_compiler = $false }
  while ($true) {
    $done = $run.Wait(200)
    $now = Get-JobProcesses $run
    $nodes = @($now | Where-Object Node).Count
    if ($nodes -gt $seen.max_nodes) { $seen.max_nodes = $nodes }
    if ($now | Where-Object Compiler) { $seen.compiler = $true; $seen.started_compiler = $true }
    if ($done) { break }
    $elapsed = $run.ElapsedSeconds
    if ($elapsed -ge $nextProgress) {
      Write-LiveLine "  $labelName`: still running ($([int][Math]::Floor($elapsed)) s)"
      while ($nextProgress -le $elapsed) { $nextProgress += 5.0 }
    }
    if ($elapsed -gt $TimeoutSeconds) { [void]$run.Finish($true); "step '$Name' was stopped at its $TimeoutSeconds second limit: nothing was stored"; exit 1 }
  }
  $result = $run.Finish($false)
  if ($result.ExitCode -ne 0) { Get-Content -LiteralPath $OutputPath -Tail 15 -ErrorAction Ignore; "step '$Name' failed with exit code $($result.ExitCode): nothing was stored"; exit 1 }
  $seen['result'] = $result
  $seen
}
# CPU seconds of each process by id, read now; one that has exited keeps its last reading out of the sum.
function Get-CpuSeconds($processes) {
  $seconds = @{}
  foreach ($process in $processes) { $process.Refresh(); if (-not $process.HasExited) { $seconds[$process.Id] = $process.TotalProcessorTime.TotalSeconds } }
  $seconds
}
# Whether the warm build used what the linger build left of one kind: null when none of it was left, true when one
# of those processes spent CPU during the warm build and the warm build started none of that kind itself.
function Test-Used($left, $before, $after, [bool]$startedOwn) {
  if (-not $left) { return $null }
  $grew = @($left | Where-Object { $after.ContainsKey($_.Id) -and $after[$_.Id] -gt $before[$_.Id] }).Count
  [bool]($grew -and -not $startedOwn)
}

# What the response file and the arguments put in force: the last of each switch wins, as on MSBuild's command line.
# `dotnet build` puts a bare `-maxcpucount` (every CPU) on that command line itself, after the response file, so a
# CPU limit in Directory.Build.rsp never reaches it: only one in the arguments does (measured 2026-11-12, -v:diag).
$rspLines = @(if (Test-Path -LiteralPath $ResponseFile) { Get-Content -LiteralPath $ResponseFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } })
$nodeReuse = $true
$maxCpu = 1
foreach ($switch in @($rspLines + @('-maxcpucount') + $msbuildArgs)) {
  if ($switch -match '^[-/]nodeReuse:(true|false)$') { $nodeReuse = $Matches[1] -eq 'true' }
  elseif ($switch -match '^[-/](maxCpuCount|m)(:(\d+))?$') { $maxCpu = if ($Matches[3]) { [int]$Matches[3] } else { [Environment]::ProcessorCount } }
}
$sharedRaw = Get-DotnetProperty $DotnetProject 'UseSharedCompilation'
$shared = $sharedRaw -ne 'false'  # unset means the compiler's default, which is on

# A compiler server or worker node of this SDK that is not in one of the measured jobs ($own) could take a measured
# build's work without being measured: another build on this machine can start one at any time. A build hands work
# only to servers of its own SDK, which run from its folder, so only those count.
$sdkRoot = Split-Path $dotnetPath
function Get-OutsideServers($own = @()) {
  $ids = [Collections.Generic.HashSet[int]]::new()
  foreach ($run in $own) { if ($run) { foreach ($id in $run.ProcessIds()) { [void]$ids.Add([int]$id) } } }
  @(Get-ProcessList | Where-Object {
      $line = "$($_.CommandLine)"
      $line -match 'VBCSCompiler|[/-]nodemode:' -and $line.IndexOf($sdkRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and -not $ids.Contains([int]$_.ProcessId)
    })
}
# Ends the run with nothing stored while one is alive. A timed step's job ends its own processes as it returns, without
# waiting for them, so they get a few seconds to go before what is left counts as outside.
function Assert-NoOutsideServers([string]$Name, $own = @()) {
  if ($AllowOutsideServers) { return }
  $grace = [Diagnostics.Stopwatch]::StartNew()
  while (($outside = Get-OutsideServers $own) -and $grace.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 200 }
  if ($outside) {
    "$Name`: $($outside.Count) compiler server or MSBuild node process(es) of this SDK ran outside the measured builds (process $(@($outside | ForEach-Object ProcessId) -join ', '); another build on this machine?), so a measured build may have handed its work to them: nothing was stored"
    Remove-Item -LiteralPath $log -Force -ErrorAction Ignore
    exit 1
  }
}

if (-not $AllowOutsideServers) {
  if (Get-OutsideServers) {
    "found compiler server or MSBuild node process(es) of this SDK already running: stopping them with 'dotnet build-server shutdown' before measuring"
    $said = & $dotnetPath build-server shutdown 2>&1 | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { "'dotnet build-server shutdown' failed with exit code $LASTEXITCODE ($said): nothing was stored"; exit 1 }
  }
  Assert-NoOutsideServers 'before measuring'
}

function Get-DotnetTargetName([string]$Path) {
  $fullSolution = [IO.Path]::GetFullPath($Path)
  $fullRepo = [IO.Path]::GetFullPath($repo).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
  if ($fullSolution.StartsWith($fullRepo, [StringComparison]::OrdinalIgnoreCase)) {
    return ($fullSolution.Substring($fullRepo.Length) -replace '\\', '/')
  }
  $leaf = Split-Path $fullSolution -Leaf
  $sha = [Security.Cryptography.SHA256]::Create()
  try {
    $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fullSolution.ToLowerInvariant())) | Select-Object -First 4 | ForEach-Object { $_.ToString('x2') })
  } finally { $sha.Dispose() }
  "external:${leaf}:${hash}"
}

"measuring '$Label' (.NET): one preparation build, then $Samples sample(s) of each step"
Invoke-DotnetBuild 'preparation build' 'any' $false 'preparation build'
foreach ($n in 1..$Samples) { Invoke-DotnetBuild 'nothing to do' 'none' $true "nothing to do sample $n/$Samples" }
foreach ($n in 1..$Samples) {
  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  Invoke-DotnetBuild 'one file changed' 'first' $true "one file changed sample $n/$Samples"
}
foreach ($n in 1..$Samples) {
  Invoke-Clean "clean sample $n/$Samples"
  Invoke-DotnetBuild 'fresh build' 'all' $true "fresh build sample $n/$Samples"
}

# The linger build and -Samples warm builds after it, each in a job of its own, all ended however this ends.
$linger = $null
$warm = $null
$warmLogs = [Collections.Generic.List[string]]::new()
try {
  $before = Get-OutputTimes
  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  Write-LiveLine '  starting linger build...'
  $linger = Start-Run "$log.linger"
  $seen = Wait-Run $linger 'linger build' "$log.linger" 'linger build'
  Assert-NoOutsideServers "step 'linger build'" @($linger)
  Assert-BuildEvidence 'linger build' 'first' $before
  Write-LiveLine "  starting linger wait ($LingerSeconds s)..."
  $returned = [Diagnostics.Stopwatch]::StartNew()
  $nextLingerProgress = 5.0
  while ($returned.Elapsed.TotalSeconds -lt $LingerSeconds) {
    Start-Sleep -Milliseconds 200
    $le = $returned.Elapsed.TotalSeconds
    if ($le -ge $nextLingerProgress -and $le -lt $LingerSeconds) {
      Write-LiveLine "  linger wait: still waiting ($([int][Math]::Floor($le)) s)"
      while ($nextLingerProgress -le $le) { $nextLingerProgress += 5.0 }
    }
  }
  $leftInfo = Get-JobProcesses $linger
  $afterSeconds = [Math]::Round($returned.Elapsed.TotalSeconds, 1)
  $leftProcesses = @($leftInfo | ForEach-Object { Get-Process -Id $_.Id -ErrorAction Ignore })
  $leftMb = [Math]::Round((($leftProcesses | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB, 1)
  $leftNodes = @($leftInfo | Where-Object Node).Count
  $leftCompilers = @($leftInfo | Where-Object Compiler).Count

  $warmResults = @()
  $warmUsedNodesPerSample = @()
  $warmUsedCompilerPerSample = @()
  foreach ($n in 1..$Samples) {
    $warmLog = "$log.warm-$n"
    $warmLogs.Add($warmLog)
    $cpuBefore = Get-CpuSeconds $leftProcesses
    $before = Get-OutputTimes
    (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
    Write-LiveLine "  starting one file changed, after a build sample $n/$Samples..."
    try {
      $warm = Start-Run $warmLog
      $warmSeen = Wait-Run $warm 'one file changed, after a build' $warmLog "one file changed, after a build sample $n/$Samples"
      $cpuAfter = Get-CpuSeconds $leftProcesses
      Assert-NoOutsideServers "step 'one file changed, after a build'" @($linger, $warm)
      Assert-BuildEvidence 'one file changed, after a build' 'first' $before
      $sampleUsedNodes = Test-Used @($leftInfo | Where-Object Node) $cpuBefore $cpuAfter ($warmSeen.max_nodes -gt 0)
      $sampleUsedCompiler = Test-Used @($leftInfo | Where-Object Compiler) $cpuBefore $cpuAfter $warmSeen.started_compiler
      $warmUsedNodesPerSample += , $sampleUsedNodes
      $warmUsedCompilerPerSample += , $sampleUsedCompiler
      $warmResults += , $warmSeen.result
      "  one file changed, after a build sample $n/$Samples`: $(Format-Number $warmSeen.result.Seconds) s, $(Format-Number $warmSeen.result.PeakMB) MB; nodes left: $(Format-Used $sampleUsedNodes), the compiler server left: $(Format-Used $sampleUsedCompiler)"
    } finally {
      if ($warm) { $warm.Dispose(); $warm = $null }
      Remove-Item -LiteralPath $warmLog -Force -ErrorAction Ignore
    }
  }
  $usedNodes = if (-not $leftNodes) { $null } else { (@($warmUsedNodesPerSample | Where-Object { $_ -ne $true }).Count -eq 0) }
  $usedCompiler = if (-not $leftCompilers) { $null } else { (@($warmUsedCompilerPerSample | Where-Object { $_ -ne $true }).Count -eq 0) }
  $results['one file changed, after a build'] = $warmResults
  $warmMedSec = Get-Median @($warmResults | ForEach-Object Seconds)
  $warmMedMb = Get-Median @($warmResults | ForEach-Object PeakMB)
  "  one file changed, after a build: $(Format-Number $warmMedSec) s, $(Format-Number $warmMedMb) MB; $($leftProcesses.Count) process(es) left, $(Format-Number $leftMb) MB; nodes left: $(Format-Used $usedNodes), the compiler server left: $(Format-Used $usedCompiler)"
} finally {
  foreach ($run in @($warm, $linger)) { if ($run) { $run.Dispose() } }
  foreach ($path in @($log, "$log.linger") + @($warmLogs)) { Remove-Item -LiteralPath $path -Force -ErrorAction Ignore }
}

# A build that did not do what its settings say stores nothing: the label would describe a build that never ran.
$contradictions = @(
  if ($seen.max_nodes -gt $maxCpu) { "at most $maxCpu node(s) were asked for and $($seen.max_nodes) worker node(s) ran at once" }
  if (-not $nodeReuse -and $leftNodes) { "node reuse is off and $leftNodes worker node(s) stayed after the build" }
  if ($nodeReuse -and $seen.max_nodes -and -not $leftNodes) { "node reuse is on and no worker node stayed after the build" }
  if (-not $shared -and $seen.compiler) { 'shared compilation is off and the compiler server ran' }
)
if ($contradictions) { "the build contradicts the settings in force ($($contradictions -join '; ')): nothing was stored"; exit 1 }

function Get-PerformanceSummaryTop([string[]]$Lines, [int]$Count = 3) {
  $section = $null
  $items = [Collections.Generic.List[object]]::new()
  foreach ($line in $Lines) {
    if ($line -match '^\s*Target Performance Summary:\s*$') { $section = 'target'; continue }
    if ($line -match '^\s*Task Performance Summary:\s*$') { $section = 'task'; continue }
    if ($line -match '^\s*\S.*Performance Summary:\s*$' -or $line -match '^\s*Build (succeeded|FAILED)\.') { $section = $null; continue }
    if ($section -and $line -match '^\s*(\d+)\s+ms\s+(\S+)\s+(\d+)\s+calls\b') {
      $items.Add([ordered]@{
          kind  = $section
          name  = $Matches[2]
          ms    = [int]$Matches[1]
          calls = [int]$Matches[3]
        })
    }
  }
  @($items | Sort-Object @{ Expression = { $_['ms'] }; Descending = $true }, @{ Expression = { $_['name'] }; Ascending = $true }, @{ Expression = { $_['kind'] }; Ascending = $true } | Select-Object -First $Count)
}

$diagnostic = $null
if ($Diagnose) {
  try {
    $rawScratch = $env:BOSUN_SCRATCH
    if (-not [string]::IsNullOrWhiteSpace($rawScratch) -and -not (Test-Path -LiteralPath $rawScratch.Trim() -PathType Container)) {
      throw "BOSUN_SCRATCH '$($rawScratch.Trim())' is not an existing directory"
    }
    $scratchRoot = Get-BosunScratchRoot
    $probe = Join-Path $scratchRoot ".bosun-diag-probe-$PID.tmp"
    [IO.File]::WriteAllText($probe, 'ok', $utf8)
    Remove-Item -LiteralPath $probe -Force -ErrorAction Ignore
  } catch {
    "diagnostic build could not write to the scratch folder ($($_.Exception.Message)): nothing was stored"
    exit 1
  }
  $diagStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
  $binlogPath = Join-Path $scratchRoot "bosun-diagnose-$Label-$diagStamp-$PID.binlog"
  $diagLog = "$log.diagnose"
  $diagSucceeded = $false
  try {
    $before = Get-OutputTimes
    (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
    $diagArgs = @('build', $Solution, '-nologo') + $msbuildArgs + @("-bl:$binlogPath", '-clp:PerformanceSummary')
    $savedUiLang = $env:DOTNET_CLI_UI_LANGUAGE
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    try {
      [void](Invoke-Step 'diagnostic build' $dotnetPath $diagArgs 'diagnostic build' $diagLog)
    } finally {
      if ($null -eq $savedUiLang) { Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction Ignore } else { $env:DOTNET_CLI_UI_LANGUAGE = $savedUiLang }
    }
    Assert-NoOutsideServers "step 'diagnostic build'"
    Assert-BuildEvidence 'diagnostic build' 'first' $before
    if (-not (Test-Path -LiteralPath $binlogPath) -or (Get-Item -LiteralPath $binlogPath).Length -le 0) {
      "diagnostic build did not produce a non-empty binary log at ${binlogPath}: nothing was stored"
      exit 1
    }
    $diagLines = @(Get-Content -LiteralPath $diagLog -ErrorAction Ignore)
    $top3 = @(Get-PerformanceSummaryTop $diagLines 3)
    if ($top3.Count -lt 3) {
      "diagnostic build did not produce a PerformanceSummary with at least 3 targets or tasks (found $($top3.Count)): nothing was stored"
      exit 1
    }
    $diagnostic = [ordered]@{
      workload      = 'one file changed'
      binlog        = (Split-Path $binlogPath -Leaf)
      top_expensive = $top3
    }
    $diagSucceeded = $true
    "  diagnostic binary log: $binlogPath"
    '  top 3 expensive targets/tasks:'
    foreach ($item in $top3) {
      "    $($item['name']) ($($item['kind'])): $($item['ms']) ms ($($item['calls']) calls)"
    }
  } finally {
    Remove-Item -LiteralPath $diagLog -Force -ErrorAction Ignore
    if (-not $diagSucceeded) { Remove-Item -LiteralPath $binlogPath -Force -ErrorAction Ignore }
  }
}

$system = Get-CimInstance Win32_ComputerSystem
$sdk = (& $dotnetPath --version 2>&1 | Select-Object -First 1) -join ''
$entry = [ordered]@{
  label         = $Label
  kind          = 'dotnet'
  target        = Get-DotnetTargetName $Solution
  decision      = $Decision
  note          = if ($Note) { $Note } else { $null }
  date          = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
  machine       = [ordered]@{
    name      = $env:COMPUTERNAME
    processor = ((Get-CimInstance Win32_Processor | Select-Object -First 1).Name -replace '\s+', ' ').Trim()
    memory_gb = [int][Math]::Round($system.TotalPhysicalMemory / 1GB)
  }
  tools         = [ordered]@{ dotnet = if ($sdk -match '(\d+(\.\d+)+)') { $Matches[1] } else { $null } }
  configuration = [ordered]@{
    build_args             = @($BuildArgs)
    response_file          = @($rspLines)
    node_reuse             = $nodeReuse
    max_cpu_count          = $maxCpu
    use_shared_compilation = $shared
  }
  observed      = [ordered]@{
    max_worker_nodes           = $seen.max_nodes
    compiler_server            = $seen.compiler
    outside_servers_allowed    = [bool]$AllowOutsideServers
    left_running               = [ordered]@{
      observed      = $true
      after_seconds = $afterSeconds
      processes     = $leftProcesses.Count
      nodes         = $leftNodes
      names         = @($leftInfo | ForEach-Object { if ($_.Node) { 'MSBuild node' } elseif ($_.Compiler) { 'compiler server' } else { $_.Name } } | Sort-Object -Unique)
      mb            = $leftMb
    }
    warm_used_nodes            = $usedNodes
    warm_used_compiler         = $usedCompiler
    warm_samples_used_nodes    = @($warmUsedNodesPerSample)
    warm_samples_used_compiler = @($warmUsedCompilerPerSample)
  }
  samples       = $Samples
  steps         = @($results.GetEnumerator() | ForEach-Object {
      $secs = @($_.Value | ForEach-Object { $_.Seconds })
      $mbs = @($_.Value | ForEach-Object { $_.PeakMB })
      [ordered]@{
        step           = $_.Key
        seconds        = $secs
        peak_mb        = $mbs
        median_seconds = Get-Median $secs
        median_peak_mb = Get-Median $mbs
        spread_seconds = Get-Spread $secs 2
        spread_peak_mb = Get-Spread $mbs 1
      }
    })
}
if ($diagnostic) { $entry['diagnostic'] = $diagnostic }

$baselineArg = if ($hasBaseline) { $Baseline } else { '' }
Update-Data {
  param($current)
  $current['labels'] = @($current['labels'] | Where-Object { $_['label'] -ne $Label }) + , $entry
  $current
} $baselineArg
"stored '$Label' ($Decision) in $dataPath and rendered $docPath"
foreach ($step in $entry['steps']) { "  $($step['step']): $(Format-Number $step['median_seconds']) s (spread $(Format-Number $step['spread_seconds']) s), $(Format-Number $step['median_peak_mb']) MB (spread $(Format-Number $step['spread_peak_mb']) MB)" }
if ($hasBaseline) { Write-ComparisonSummary (Get-BaselineComparison $script:lastSavedData $Baseline) }
