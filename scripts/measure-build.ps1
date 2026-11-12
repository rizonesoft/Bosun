# Measures what the native build costs on this machine: wall time and peak memory, per step.
#
#   pwsh scripts/measure-build.ps1 -Label baseline -Decision baseline -Tests
#   pwsh scripts/measure-build.ps1 -Label debug -Decision kept -BuildArgs Debug -Samples 5
#   pwsh scripts/measure-build.ps1 -Label sccache -Decision dropped -Note 'slower than the baseline' -DecideOnly
#   pwsh scripts/measure-build.ps1 -RenderOnly
#
# -BuildArgs names switches of scripts/build.ps1 without their dash (Debug), so a candidate is measured with
# exactly the switches it would run with.
#
# One untimed build first, so every timed step starts from a built tree. Then -Samples runs each of: a build with
# nothing to do, a build after src/app.cpp is touched, and a -Fresh build. -Tests adds one run of the Python suite.
# Each step runs in its own job object (scripts/lib/job-measure.ps1): the peak is the memory the step and everything
# it started had committed at one moment, and nothing a step starts outlives it. A step that fails, times out or did
# not do the work its name says stores nothing.
#
# Results are kept per label in docs/build-performance.json, and docs/build-performance.md is rendered from that
# file every time, so the two cannot disagree: never edit the document by hand. Measuring a label again replaces it.
# -RenderOnly renders the document from the stored data without measuring. -DecideOnly records the decision and note
# on a label already measured, once its numbers have been compared, without measuring it again.
#
# Two runs may finish at once (another session, another label). Each reads the stored data, changes it and writes
# it back while holding docs/build-performance.json.lock, so neither loses the other's result; a run that cannot
# get the lock within -LockSeconds changes nothing and says so.
#
# When bosun is installed the whole measurement runs through `bosun run --`, so it takes its turn with other heavy
# jobs and the wait for a slot is outside every timed step.
#
# -Dotnet measures the .NET solution (Bosun.slnx) instead, with the same storage, samples and steps:
#
#   pwsh scripts/measure-build.ps1 -Dotnet -Label dotnet-baseline -Decision baseline
#   pwsh scripts/measure-build.ps1 -Dotnet -Label dotnet-node-reuse -Decision dropped -BuildArgs nodeReuse:true
#
# -BuildArgs then names MSBuild arguments without their dash (nodeReuse:true, maxCpuCount:2,
# p:UseSharedCompilation=false); on the command line they override Directory.Build.rsp, so a trial edits no file.
# Each step is proved from the solution's output assemblies (-DotnetOutputs, the touched project's first; by default
# each project's evaluated TargetPath): nothing to do rewrites none, one file changed rewrites the first, the clean
# before a fresh build must leave none, and the fresh build must write all of them.
#
# A step's job ends what it left behind, which keeps samples independent but hides what MSBuild nodes and the
# compiler server hold after a build and what they buy the next one. So each .NET label also runs one build in a job
# this script keeps open -LingerSeconds after it returns, stores what is still running in it (count, names, private
# memory), and times a one-file build in a job of its own while those processes live: its peak is its own job's,
# with the memory left held stored beside it, and it counts as having used them only if their CPU time grew and it
# started no node or compiler server of its own. The linger build is sampled while it runs, so the label stores the
# settings in force as the build showed them (most worker nodes at once, whether the compiler server ran, whether
# nodes stayed), beside the response file and the evaluated UseSharedCompilation; a label whose settings the build
# contradicts stores nothing. A compiler server or reusable node of the same SDK running outside the measured jobs
# would take work from the measured builds unmeasured, so one found at the start is stopped with `dotnet build-server
# shutdown`, which says so, and one still running then, or found after any measured build (another build on this
# machine can start one at any time), stores nothing and says why.
param(
  [string]$Label,
  [ValidateSet('baseline', 'kept', 'dropped')][string]$Decision,
  [string]$Note,
  [switch]$Tests,
  [ValidateRange(1, 99)][int]$Samples = 3,
  [ValidateRange(1, 86400)][int]$TimeoutSeconds = 1800,
  [string[]]$BuildArgs = @(),
  [switch]$RenderOnly,
  [switch]$DecideOnly,
  [ValidateRange(1, 3600)][int]$LockSeconds = 30,
  [string]$BuildScript = (Join-Path $PSScriptRoot 'build.ps1'),
  [string]$TouchFile = (Join-Path (Split-Path $PSScriptRoot) 'src\app.cpp'),
  [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot) 'docs'),
  [switch]$Dotnet,
  [string]$DotnetCommand = 'dotnet',
  [string]$Solution = (Join-Path (Split-Path $PSScriptRoot) 'Bosun.slnx'),
  [string]$DotnetProject = (Join-Path (Split-Path $PSScriptRoot) 'src\Bosun.Core\Bosun.Core.csproj'),
  [string[]]$DotnetOutputs = @(),
  [string]$ResponseFile = (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.rsp'),
  [ValidateRange(0, 3600)][int]$LingerSeconds = 60
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$dataPath = Join-Path $OutDir 'build-performance.json'
$docPath = Join-Path $OutDir 'build-performance.md'
$utf8 = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'lib\job-measure.ps1')

# Writes through a temporary file in the same folder, then replaces, so a reader never sees half a file.
function Write-Atomic([string]$Path, [string]$Text) {
  $temp = "$Path.tmp-$PID"
  try {
    [IO.File]::WriteAllText($temp, $Text, $utf8)
    [IO.File]::Move($temp, $Path, $true)
  } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}

# The stored data, or an empty set when there is none yet. A file that cannot be read as this script's data is
# refused, never overwritten: it may be the only copy of earlier measurements.
function Read-Data {
  if (-not (Test-Path -LiteralPath $dataPath)) { return [ordered]@{ schema_version = 1; labels = @() } }
  try { $data = [IO.File]::ReadAllText($dataPath, $utf8) | ConvertFrom-Json -AsHashtable -DateKind String } catch {
    throw "$dataPath does not parse as JSON ($($_.Exception.Message)): fix or remove it, nothing was written"
  }
  if ($data -isnot [Collections.IDictionary] -or $data['schema_version'] -ne 1 -or $data['labels'] -isnot [array]) {
    throw "$dataPath is not this script's data (it needs schema_version 1 and a labels list): fix or remove it, nothing was written"
  }
  $data['labels'] = @($data['labels'])
  $data
}

function Format-Number($value) { ([double]$value).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture) }
# Whether a .NET warm build used what the build before it left running: null when nothing of that kind was left.
function Format-Used($value) { if ($null -eq $value) { 'none was left' } elseif ($value) { 'used' } else { 'not used' } }

function ConvertTo-Document($data) {
  $out = [Collections.Generic.List[string]]::new()
  $out.Add('# Build performance')
  $out.Add('')
  $out.Add('Generated by `pwsh scripts/measure-build.ps1` from `docs/build-performance.json`. Do not edit it by hand: measure again, or run the script with `-RenderOnly`.')
  $out.Add('')
  $out.Add('Wall time is the median of the samples, in seconds. Peak memory is the median, in MB, of the most memory each run and everything it started had committed at one moment. A `kept` label is a setting that stayed because it measured better than the baseline; a `dropped` one was reverted.')
  if (-not $data['labels'].Count) { $out.Add(''); $out.Add('Nothing has been measured yet.') }
  foreach ($entry in $data['labels']) {
    $out.Add('')
    $out.Add("## $($entry['label']) ($($entry['decision']))")
    $out.Add('')
    if ($entry['note']) { $out.Add([string]$entry['note']); $out.Add('') }
    $machine = $entry['machine']
    $out.Add("Measured $($entry['date']) on $($machine['name']): $($machine['processor']), $($machine['memory_gb']) GB, $($entry['samples']) samples.")
    $out.Add('')
    $tools = @($entry['tools'].GetEnumerator() | Where-Object { $null -ne $_.Value } | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', '
    $out.Add("Tools: $(if ($tools) { $tools } else { 'not recorded' }).")
    $out.Add('')
    $configuration = $entry['configuration']
    $buildArgs = @($configuration['build_args'] | ForEach-Object { "-$_" }) -join ' '
    if ($entry['kind'] -eq 'dotnet') {
      $rsp = @($configuration['response_file']) -join ' '
      $parts = @(
        "MSBuild arguments $(if ($buildArgs) { '`' + $buildArgs + '`' } else { 'none' })",
        "Directory.Build.rsp $(if ($rsp) { '`' + $rsp + '`' } else { 'empty' })",
        "in force: node reuse $(if ($configuration['node_reuse']) { 'on' } else { 'off' }), at most $($configuration['max_cpu_count']) nodes, shared compilation $(if ($configuration['use_shared_compilation']) { 'on' } else { 'off' })"
      )
      $out.Add("Configuration: $($parts -join '; ').")
      $out.Add('')
      $seen = $entry['observed']
      $left = $seen['left_running']
      $names = @($left['names']) -join ', '
      $out.Add("Observed in the linger build: at most $($seen['max_worker_nodes']) worker node(s) at once; compiler server $(if ($seen['compiler_server']) { 'ran' } else { 'did not run' }). Left running $(Format-Number $left['after_seconds']) s after it returned: $($left['processes']) process(es)$(if ($names) { " ($names)" }), $(Format-Number $left['mb']) MB private memory. In the one-file build made then, the nodes left: $(Format-Used $seen['warm_used_nodes']); the compiler server left: $(Format-Used $seen['warm_used_compiler']).")
      $out.Add('')
    } else {
    $parts = @(
      "build arguments $(if ($buildArgs) { '`' + $buildArgs + '`' } else { 'none' })",
      "compiler launcher $(if ($configuration['launcher']) { $configuration['launcher'] } else { 'none' })",
      "linker $(if ($configuration['linker']) { $configuration['linker'] } else { 'not recorded' })",
      "developer environment $(if ($configuration['env_cache']) { 'from cache' } else { 'loaded each build' })"
    )
    $out.Add("Configuration: $($parts -join '; ').")
    $out.Add('')
    }
    $out.Add('| Step | Wall time (s) | Peak memory (MB) | Samples (s) |')
    $out.Add('| ---- | ------------: | ---------------: | ----------- |')
    foreach ($step in $entry['steps']) {
      $samples = @($step['seconds'] | ForEach-Object { Format-Number $_ }) -join ', '
      $out.Add("| $($step['step']) | $(Format-Number $step['median_seconds']) | $(Format-Number $step['median_peak_mb']) | $samples |")
    }
  }
  ($out -join "`n") + "`n"
}

function Write-Document($data) {
  try { Write-Atomic $docPath (ConvertTo-Document $data) } catch {
    "could not write ${docPath}: $($_.Exception.Message)"
    "the measurements are stored in ${dataPath}: run 'pwsh scripts/measure-build.ps1 -RenderOnly' once that is fixed"
    exit 1
  }
}

# Stores the data (temporary file, replace, read back), then renders the document from what was stored.
function Save-Data($data) {
  $json = (($data | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n"
  try {
    Write-Atomic $dataPath $json
    if ([IO.File]::ReadAllText($dataPath, $utf8) -cne $json) { throw 'the file read back differs from what was written' }
  } catch { "could not store the measurement in ${dataPath}: $($_.Exception.Message)"; exit 1 }
  Write-Document (Read-Data)
}

# Runs $Change on the stored data and saves the result, all while holding the lock file, so two runs saving at once
# cannot overwrite each other. With no change it only renders the document, under the same lock, so a render can
# never put an older document over the one a save has just written. The lock is a file opened for this process alone; Windows removes it when the handle
# closes, however the script ends.
function Update-Data([scriptblock]$Change) {
  $lockPath = "$dataPath.lock"
  $deadline = [DateTime]::UtcNow.AddSeconds($LockSeconds)
  $lock = $null
  while (-not $lock) {
    try { $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose) } catch [IO.DirectoryNotFoundException] {
      "could not lock ${dataPath}: $($_.Exception.Message)"; exit 1
    } catch [IO.IOException], [UnauthorizedAccessException] {
      # Held by another run, or just released and still being removed: try again until the limit.
      if ([DateTime]::UtcNow -ge $deadline) { "could not lock $lockPath within $LockSeconds seconds ($($_.Exception.Message)): nothing was changed"; exit 1 }
      Start-Sleep -Milliseconds 200
    }
  }
  try {
    try { $current = Read-Data } catch { "$($_.Exception.Message)"; exit 1 }
    if ($Change) { Save-Data (& $Change $current) } else { Write-Document $current }
    $script:labelCount = $current['labels'].Count
  } finally { $lock.Dispose() }
}

try { $data = Read-Data } catch { "$($_.Exception.Message)"; exit 1 }
if ($RenderOnly) { Update-Data $null; "rendered $docPath from $labelCount label(s)"; exit 0 }
if (-not $Label -or -not $Decision) { 'measure-build needs -Label <name> and -Decision <baseline|kept|dropped>, or -RenderOnly'; exit 2 }
if ($Label -notmatch '^[a-z0-9][a-z0-9-]{0,39}$') { "-Label '$Label' must be lowercase letters, digits and dashes, at most 40"; exit 2 }
if ($DecideOnly) {
  Update-Data {
    param($current)
    $stored = $current['labels'] | Where-Object { $_['label'] -eq $Label } | Select-Object -First 1
    if (-not $stored) { "no label '$Label' has been measured: nothing was changed"; exit 1 }
    $stored['decision'] = $Decision
    if ($Note) { $stored['note'] = $Note }
    $current
  }
  "recorded '$Label' as $Decision"
  exit 0
}

# Take a turn in the queue, once: the wait for a slot happens here, before any step is timed.
if (-not $env:BOSUN_MEASURE_QUEUED) {
  $env:BOSUN_MEASURE_QUEUED = '1'
  if (Get-Command bosun -CommandType Application -ErrorAction Ignore) {
    # Every parameter this run was given goes to the queued one, so none is silently reset to its default there.
    $forward = foreach ($parameter in $PSBoundParameters.GetEnumerator()) {
      if ($parameter.Value -is [switch]) { if ($parameter.Value) { "-$($parameter.Key)" } }
      elseif ($parameter.Value -is [array]) { "-$($parameter.Key)"; $parameter.Value -join ',' }
      else { "-$($parameter.Key)"; [string]$parameter.Value }
    }
    & bosun run -- pwsh -NoProfile -File $PSCommandPath @forward
    exit $LASTEXITCODE
  }
  'bosun is not on PATH: measuring without the queue'
}
$BuildArgs = @($BuildArgs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$DotnetOutputs = @($DotnetOutputs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$log = Join-Path ([IO.Path]::GetTempPath()) "bosun-measure-$PID.log"
$results = [ordered]@{}
$envCache = $false

# Runs one step in its own job. Returns its result, or ends the script: a failed or timed-out step stores nothing.
function Invoke-Step([string]$Name, [string]$File, [string[]]$Arguments) {
  $result = Measure-Job -FilePath $File -ArgumentList $Arguments -WorkingDirectory $repo -TimeoutSeconds $TimeoutSeconds -OutputPath $log
  if ($result.TimedOut -or $result.ExitCode -ne 0) {
    Get-Content -LiteralPath $log -Tail 15 -ErrorAction Ignore
    if ($result.TimedOut) { "step '$Name' was stopped at its $TimeoutSeconds second limit: nothing was stored" }
    else { "step '$Name' failed with exit code $($result.ExitCode): nothing was stored" }
    Remove-Item -LiteralPath $log -Force -ErrorAction Ignore
    exit 1
  }
  $result
}

function Invoke-Build([string]$Name, [string[]]$Extra, [bool]$WantWork, [bool]$Timed) {
  $result = Invoke-Step $Name 'pwsh' (@('-NoProfile', '-File', $BuildScript) + @($BuildArgs | ForEach-Object { "-$_" }) + $Extra)
  $text = Get-Content -LiteralPath $log -Raw -ErrorAction Ignore
  $idle = $text -match 'ninja: no work to do'
  if ($Timed -and $WantWork -eq $idle) {
    if ($WantWork) { "step '$Name' built nothing, so it is not that workload: nothing was stored" }
    else { "step '$Name' compiled something, so it is not a build with nothing to do: nothing was stored" }
    Remove-Item -LiteralPath $log -Force -ErrorAction Ignore
    exit 1
  }
  if ($text -match 'developer environment: from cache') { $script:envCache = $true }
  if ($Timed) {
    if (-not $results.Contains($Name)) { $results[$Name] = @() }
    $results[$Name] += , $result
    "  $Name sample $($results[$Name].Count): $(Format-Number $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
  }
}

function Get-Median([double[]]$values) {
  $sorted = @($values | Sort-Object)
  $middle = [int][Math]::Floor($sorted.Count / 2)
  if ($sorted.Count % 2) { $sorted[$middle] } else { [Math]::Round(($sorted[$middle - 1] + $sorted[$middle]) / 2, 2) }
}

# The .NET half reads which parameters were given from here: a dot-sourced file has a $PSBoundParameters of its
# own. Its `exit` ends only that file, so its code is passed on.
if ($Dotnet) { $boundParameters = $PSBoundParameters; . (Join-Path $PSScriptRoot 'lib\measure-dotnet.ps1'); exit $LASTEXITCODE }

"measuring '$Label': one preparation build, then $Samples sample(s) of each step"
Invoke-Build 'preparation build' @() $false $false
foreach ($n in 1..$Samples) { Invoke-Build 'nothing to do' @() $false $true }
foreach ($n in 1..$Samples) {
  (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
  Invoke-Build 'one file changed' @() $true $true
}
foreach ($n in 1..$Samples) { Invoke-Build 'fresh build' @('-Fresh') $true $true }
if ($Tests) {
  $result = Invoke-Step 'python test suite' 'python' @((Join-Path $PSScriptRoot 'run-tests.py'))
  $results['python test suite'] = @($result)
  "  python test suite: $(Format-Number $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
}
Remove-Item -LiteralPath $log -Force -ErrorAction Ignore

# What was in force, read from the build tree the steps just used. A value that cannot be read is null.
function Get-CacheValue([string]$Name) {
  $cache = Join-Path $repo 'build\CMakeCache.txt'
  if (-not (Test-Path -LiteralPath $cache)) { return $null }
  $line = Get-Content -LiteralPath $cache | Where-Object { $_ -match "^$([regex]::Escape($Name)):[A-Z]+=(.*)$" } | Select-Object -First 1
  if ($line -and $line -match '=(.*)$' -and $Matches[1]) { $Matches[1] } else { $null }
}
function Get-ToolVersion([string]$Path, [string]$Pattern = '(\d+(\.\d+)+)') {
  if (-not $Path -or -not (Get-Command $Path -CommandType Application -ErrorAction Ignore)) { return $null }
  $text = (& $Path --version 2>&1 | Select-Object -First 1) -join ''
  if ($text -match $Pattern) { $Matches[1] } else { $null }
}
$launcher = Get-CacheValue 'CMAKE_CXX_COMPILER_LAUNCHER'
$linker = Get-CacheValue 'CMAKE_LINKER'
$compilerFile = Get-ChildItem -Path (Join-Path $repo 'build\CMakeFiles\*\CMakeCXXCompiler.cmake') -ErrorAction Ignore | Select-Object -First 1
$cl = if ($compilerFile -and (Get-Content -LiteralPath $compilerFile.FullName -Raw) -match 'CMAKE_CXX_COMPILER_VERSION "([^"]+)"') { $Matches[1] } else { $null }
$major = Get-CacheValue 'CMAKE_CACHE_MAJOR_VERSION'
$tools = [ordered]@{
  cmake      = if ($major) { "$major.$(Get-CacheValue 'CMAKE_CACHE_MINOR_VERSION').$(Get-CacheValue 'CMAKE_CACHE_PATCH_VERSION')" } else { $null }
  ninja      = Get-ToolVersion (Get-CacheValue 'CMAKE_MAKE_PROGRAM')
  cl         = $cl
  sccache    = if ($launcher -match 'sccache') { Get-ToolVersion $launcher } else { $null }
  'lld-link' = if ($linker -match 'lld-link') { Get-ToolVersion $linker } else { $null }
}
$system = Get-CimInstance Win32_ComputerSystem
$entry = [ordered]@{
  label         = $Label
  decision      = $Decision
  note          = if ($Note) { $Note } else { $null }
  date          = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
  machine       = [ordered]@{
    name      = $env:COMPUTERNAME
    processor = ((Get-CimInstance Win32_Processor | Select-Object -First 1).Name -replace '\s+', ' ').Trim()
    memory_gb = [int][Math]::Round($system.TotalPhysicalMemory / 1GB)
  }
  tools         = $tools
  configuration = [ordered]@{
    build_args = @($BuildArgs)
    launcher   = if ($launcher) { Split-Path $launcher -Leaf } else { $null }
    linker     = if ($linker) { Split-Path $linker -Leaf } else { $null }
    env_cache  = $envCache
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

# Other sessions may have measured meanwhile: under the lock, read again, replace this label, write and read back.
Update-Data {
  param($current)
  $current['labels'] = @($current['labels'] | Where-Object { $_['label'] -ne $Label }) + , $entry
  $current
}
"stored '$Label' ($Decision) in $dataPath and rendered $docPath"
foreach ($step in $entry['steps']) { "  $($step['step']): $(Format-Number $step['median_seconds']) s, $(Format-Number $step['median_peak_mb']) MB" }
exit 0
