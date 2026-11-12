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
  $projects = @(([xml](Get-Content -LiteralPath $Solution -Raw)).SelectNodes('//Project') | ForEach-Object { Join-Path (Split-Path $Solution) $_.GetAttribute('Path') })
  $DotnetOutputs = @($projects | ForEach-Object { Get-DotnetProperty $_ 'TargetPath' })
}
if (-not $DotnetOutputs -or @($DotnetOutputs | Where-Object { -not $_ }).Count) { 'measure-build could not find the output assembly of every project: nothing was stored'; exit 1 }

function Get-OutputTimes {
  $times = [ordered]@{}
  foreach ($path in $DotnetOutputs) { $times[$path] = if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path).LastWriteTimeUtc.Ticks } else { $null } }
  $times
}
function Get-Names([string[]]$paths) { (@($paths | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') }

# One timed build, proved by what it wrote: 'none' rewrites no output, 'first' rewrites the touched project's, 'all'
# writes every one. Evidence that disagrees ends the run with nothing stored.
function Invoke-DotnetBuild([string]$Name, [string]$Expect, [bool]$Timed) {
  $before = Get-OutputTimes
  $result = Invoke-Step $Name $dotnetPath $buildLine
  $after = Get-OutputTimes
  $rewritten = @($DotnetOutputs | Where-Object { $null -ne $after[$_] -and $after[$_] -ne $before[$_] })
  $missing = @($DotnetOutputs | Where-Object { $null -eq $after[$_] })
  $problem = switch ($Expect) {
    'none' { if ($rewritten -or $missing) { "rewrote $(Get-Names ($rewritten + $missing)), so it is not a build with nothing to do" } }
    'first' { if ($DotnetOutputs[0] -notin $rewritten -or $missing) { "did not rewrite $(Get-Names @($DotnetOutputs[0])) or left an output missing, so it is not that workload" } }
    'all' { if ($rewritten.Count -ne $DotnetOutputs.Count) { "did not write $(Get-Names @($DotnetOutputs | Where-Object { $_ -notin $rewritten })), so it is not a fresh build" } }
  }
  if ($problem) { "step '$Name' $problem`: nothing was stored"; Remove-Item -LiteralPath $log -Force -ErrorAction Ignore; exit 1 }
  if ($Timed) {
    if (-not $results.Contains($Name)) { $results[$Name] = @() }
    $results[$Name] += , $result
    "  $Name sample $($results[$Name].Count): $(Format-Number $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
  }
}

function Invoke-Clean {
  [void](Invoke-Step 'clean' $dotnetPath (@('clean', $Solution, '-nologo') + $msbuildArgs))
  $left = @($DotnetOutputs | Where-Object { Test-Path -LiteralPath $_ })
  if ($left) { "the clean before a fresh build left $(Get-Names $left): nothing was stored"; exit 1 }
}

# The processes in a job now: worker nodes (MSBuild in node mode) and the compiler server (VBCSCompiler, which .NET
# runs as `dotnet exec VBCSCompiler.dll`) are told apart by their command lines.
function Get-JobProcesses($run) {
  $ids = [Collections.Generic.HashSet[int]]::new([int[]]@($run.ProcessIds()))
  @(Get-CimInstance Win32_Process -Property ProcessId, Name, CommandLine -ErrorAction Ignore | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
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
function Wait-Run($run, [string]$Name, [string]$OutputPath) {
  $seen = [ordered]@{ max_nodes = 0; compiler = $false; started_compiler = $false }
  while ($true) {
    $done = $run.Wait(200)
    $now = Get-JobProcesses $run
    $nodes = @($now | Where-Object Node).Count
    if ($nodes -gt $seen.max_nodes) { $seen.max_nodes = $nodes }
    if ($now | Where-Object Compiler) { $seen.compiler = $true; $seen.started_compiler = $true }
    if ($done) { break }
    if ($run.ElapsedSeconds -gt $TimeoutSeconds) { [void]$run.Finish($true); "step '$Name' was stopped at its $TimeoutSeconds second limit: nothing was stored"; exit 1 }
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

# Another compiler server or reusable node would take work from the measured builds without being measured.
$outside = @(Get-CimInstance Win32_Process -Property CommandLine -ErrorAction Ignore | Where-Object { "$($_.CommandLine)" -match 'VBCSCompiler|[/-]nodemode:' })
if ($outside) {
  "found $($outside.Count) compiler server or MSBuild node process(es) already running: stopping them with 'dotnet build-server shutdown' before measuring"
  & $dotnetPath build-server shutdown | Out-Null
}

"measuring '$Label' (.NET): one preparation build, then $Samples sample(s) of each step"
Invoke-DotnetBuild 'preparation build' 'any' $false
foreach ($n in 1..$Samples) { Invoke-DotnetBuild 'nothing to do' 'none' $true }
foreach ($n in 1..$Samples) {
  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  Invoke-DotnetBuild 'one file changed' 'first' $true
}
foreach ($n in 1..$Samples) { Invoke-Clean; Invoke-DotnetBuild 'fresh build' 'all' $true }

# The linger build and the warm build after it, each in a job of its own, both ended however this ends.
$linger = $null
$warm = $null
try {
  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  $linger = Start-Run "$log.linger"
  $seen = Wait-Run $linger 'linger build' "$log.linger"
  $returned = [Diagnostics.Stopwatch]::StartNew()
  while ($returned.Elapsed.TotalSeconds -lt $LingerSeconds) { Start-Sleep -Milliseconds 200 }
  $leftInfo = Get-JobProcesses $linger
  $afterSeconds = [Math]::Round($returned.Elapsed.TotalSeconds, 1)
  $leftProcesses = @($leftInfo | ForEach-Object { Get-Process -Id $_.Id -ErrorAction Ignore })
  $leftMb = [Math]::Round((($leftProcesses | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB, 1)
  $leftNodes = @($leftInfo | Where-Object Node).Count
  $cpuBefore = Get-CpuSeconds $leftProcesses

  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  $warm = Start-Run "$log.warm"
  $warmSeen = Wait-Run $warm 'one file changed, after a build' "$log.warm"
  $cpuAfter = Get-CpuSeconds $leftProcesses
  $usedNodes = Test-Used @($leftInfo | Where-Object Node) $cpuBefore $cpuAfter ($warmSeen.max_nodes -gt 0)
  $usedCompiler = Test-Used @($leftInfo | Where-Object Compiler) $cpuBefore $cpuAfter $warmSeen.started_compiler
  $results['one file changed, after a build'] = @($warmSeen.result)
  "  one file changed, after a build: $(Format-Number $warmSeen.result.Seconds) s, $(Format-Number $warmSeen.result.PeakMB) MB; $($leftProcesses.Count) process(es) left, $(Format-Number $leftMb) MB; nodes left: $(Format-Used $usedNodes), the compiler server left: $(Format-Used $usedCompiler)"
} finally {
  foreach ($run in @($warm, $linger)) { if ($run) { $run.Dispose() } }
  foreach ($path in @($log, "$log.linger", "$log.warm")) { Remove-Item -LiteralPath $path -Force -ErrorAction Ignore }
}

# A build that did not do what its settings say stores nothing: the label would describe a build that never ran.
$contradictions = @(
  if ($seen.max_nodes -gt $maxCpu) { "at most $maxCpu node(s) were asked for and $($seen.max_nodes) worker node(s) ran at once" }
  if (-not $nodeReuse -and $leftNodes) { "node reuse is off and $leftNodes worker node(s) stayed after the build" }
  if ($nodeReuse -and $seen.max_nodes -and -not $leftNodes) { "node reuse is on and no worker node stayed after the build" }
  if (-not $shared -and $seen.compiler) { 'shared compilation is off and the compiler server ran' }
)
if ($contradictions) { "the build contradicts the settings in force ($($contradictions -join '; ')): nothing was stored"; exit 1 }

$system = Get-CimInstance Win32_ComputerSystem
$sdk = (& $dotnetPath --version 2>&1 | Select-Object -First 1) -join ''
$entry = [ordered]@{
  label         = $Label
  kind          = 'dotnet'
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
    max_worker_nodes = $seen.max_nodes
    compiler_server  = $seen.compiler
    left_running     = [ordered]@{
      observed      = $true
      after_seconds = $afterSeconds
      processes     = $leftProcesses.Count
      nodes         = $leftNodes
      names         = @($leftInfo | ForEach-Object { if ($_.Node) { 'MSBuild node' } elseif ($_.Compiler) { 'compiler server' } else { $_.Name } } | Sort-Object -Unique)
      mb            = $leftMb
    }
    warm_used_nodes    = $usedNodes
    warm_used_compiler = $usedCompiler
  }
  samples       = $Samples
  steps         = @($results.GetEnumerator() | ForEach-Object {
      [ordered]@{
        step           = $_.Key
        seconds        = @($_.Value | ForEach-Object { $_.Seconds })
        peak_mb        = @($_.Value | ForEach-Object { $_.PeakMB })
        median_seconds = Get-Median @($_.Value | ForEach-Object { $_.Seconds })
        median_peak_mb = Get-Median @($_.Value | ForEach-Object { $_.PeakMB })
      }
    })
}
Update-Data {
  param($current)
  $current['labels'] = @($current['labels'] | Where-Object { $_['label'] -ne $Label }) + , $entry
  $current
}
"stored '$Label' ($Decision) in $dataPath and rendered $docPath"
foreach ($step in $entry['steps']) { "  $($step['step']): $(Format-Number $step['median_seconds']) s, $(Format-Number $step['median_peak_mb']) MB" }
