# Measures what the native build costs on this machine: wall time and peak memory, per step.
#
#   pwsh scripts/measure-build.ps1 -Label baseline -Decision baseline -Tests
#   pwsh scripts/measure-build.ps1 -Label debug -Decision kept -BuildArgs Debug -Samples 5 -Baseline baseline
#   pwsh scripts/measure-build.ps1 -Label sccache -Decision dropped -Note 'slower than the baseline' -DecideOnly
#   pwsh scripts/measure-build.ps1 -RenderOnly -Baseline baseline
#   pwsh scripts/measure-build.ps1 -Dotnet -Label dotnet-diag -Decision kept -Diagnose
#
# -BuildArgs names switches of scripts/build.ps1 without their dash (Debug), so a candidate is measured with
# exactly the switches it would run with. -Baseline <label> compares compatible labels (same kind and target)
# against <label>, reporting time and memory deltas, spreads, and a warning when spread is wider than change.
# -Diagnose (with -Dotnet) runs a separate untimed one-file diagnostic build after the timed samples, keeping an
# MSBuild binary log under Get-BosunScratchRoot and naming the 3 most expensive targets or tasks.
# -AllowOutsideServers (with -Dotnet, default off) skips shutting down or refusing outside build servers and
# records outside_servers_allowed in observed.
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
# before a fresh build must leave none, and the fresh build must write all of them. The linger build and the warm
# build below are one-file builds, proved the same way. A process list Windows cannot read stores nothing, since an
# empty one would pass for nothing running.
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
  [string]$Baseline,
  [ValidateRange(1, 3600)][int]$LockSeconds = 30,
  [string]$BuildScript = (Join-Path $PSScriptRoot 'build.ps1'),
  [string]$TouchFile = (Join-Path (Split-Path $PSScriptRoot) 'src\app.cpp'),
  [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot) 'docs'),
  [switch]$Dotnet,
  [switch]$DirectCmake,
  [string]$CmakeCommand = 'cmake',
  [switch]$Diagnose,
  [switch]$AllowOutsideServers,
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
. (Join-Path $PSScriptRoot 'lib\scratch.ps1')

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
function Test-NumericValue($value) {
  $null -ne $value -and ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal] -or $value -is [float])
}
function Read-Data {
  if (-not (Test-Path -LiteralPath $dataPath)) { return [ordered]@{ schema_version = 1; labels = @() } }
  try { $data = [IO.File]::ReadAllText($dataPath, $utf8) | ConvertFrom-Json -AsHashtable -DateKind String } catch {
    throw "$dataPath does not parse as JSON ($($_.Exception.Message)): fix or remove it, nothing was written"
  }
  if ($data -isnot [Collections.IDictionary] -or $data['schema_version'] -ne 1 -or $data['labels'] -isnot [array]) {
    throw "$dataPath is not this script's data (it needs schema_version 1 and a labels list): fix or remove it, nothing was written"
  }
  $data['labels'] = @($data['labels'])
  foreach ($entry in $data['labels']) {
    if ($entry -isnot [Collections.IDictionary] -or [string]::IsNullOrWhiteSpace([string]$entry['label']) -or $entry['steps'] -isnot [array]) {
      throw "$dataPath has an incomplete label entry: fix or remove it, nothing was written"
    }
    $lbl = [string]$entry['label']
    foreach ($step in $entry['steps']) {
      if ($step -isnot [Collections.IDictionary] -or [string]::IsNullOrWhiteSpace([string]$step['step'])) {
        throw "$dataPath has an incomplete step (label '$lbl', missing 'step'): fix or remove it, nothing was written"
      }
      $stp = [string]$step['step']
      foreach ($req in 'median_seconds', 'median_peak_mb') {
        if (-not $step.Contains($req) -or -not (Test-NumericValue $step[$req])) {
          throw "$dataPath has an incomplete step (label '$lbl', step '$stp', missing or invalid '$req'): fix or remove it, nothing was written"
        }
      }
      foreach ($arr in 'seconds', 'peak_mb') {
        if (-not $step.Contains($arr) -or $step[$arr] -isnot [array] -or $step[$arr].Count -eq 0 -or (@($step[$arr] | Where-Object { -not (Test-NumericValue $_) }).Count -gt 0)) {
          throw "$dataPath has an incomplete step (label '$lbl', step '$stp', missing or invalid '$arr'): fix or remove it, nothing was written"
        }
      }
    }
  }
  $data
}

function Format-Number($value) { ([double]$value).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture) }
function Format-Seconds($value) { ([double]$value).ToString('0.00', [Globalization.CultureInfo]::InvariantCulture) }
function Format-Signed([double]$value, [string]$unit, [string]$fmt = '0.0') {
  $abs = ([Math]::Abs($value)).ToString($fmt, [Globalization.CultureInfo]::InvariantCulture)
  if ($value -gt 0) { "+$abs $unit" }
  elseif ($value -lt 0) { "-$abs $unit" }
  else { "$([double]::Parse('0').ToString($fmt, [Globalization.CultureInfo]::InvariantCulture)) $unit" }
}
function Format-SignedSeconds([double]$value) { Format-Signed $value 's' '0.00' }
function Format-SignedMb([double]$value) { Format-Signed $value 'MB' '0.0' }
function Format-PsSingleQuote([string]$Text) { "'" + ($Text -replace "'", "''") + "'" }
function Write-LiveLine([string]$Line) {
  [Console]::Out.WriteLine($Line)
  [Console]::Out.Flush()
}
function Get-Spread([double[]]$values, [int]$digits) {
  if (-not $values -or $values.Count -le 1) { return [double]0 }
  $sorted = @($values | Sort-Object)
  [Math]::Round(([double]$sorted[-1] - [double]$sorted[0]), $digits)
}
function Get-StepSpreadSeconds($step) {
  if ($step.Contains('spread_seconds') -and $null -ne $step['spread_seconds']) { return [double]$step['spread_seconds'] }
  Get-Spread @($step['seconds']) 2
}
function Get-StepSpreadPeakMb($step) {
  if ($step.Contains('spread_peak_mb') -and $null -ne $step['spread_peak_mb']) { return [double]$step['spread_peak_mb'] }
  Get-Spread @($step['peak_mb']) 1
}
function Get-EntryKind($entry) {
  if ($entry.Contains('kind') -and $entry['kind']) { return [string]$entry['kind'] }
  'native'
}
function Get-EntryTarget($entry) {
  if ($entry.Contains('target') -and $entry['target']) { return [string]$entry['target'] }
  if ((Get-EntryKind $entry) -eq 'dotnet') { return 'Bosun.slnx' }
  'native'
}
function Get-BaselineComparison($data, [string]$BaselineLabel) {
  $baseEntry = $data['labels'] | Where-Object { $_['label'] -eq $BaselineLabel } | Select-Object -First 1
  if (-not $baseEntry) { return $null }
  $baseKind = Get-EntryKind $baseEntry
  $baseTarget = Get-EntryTarget $baseEntry
  $baseSteps = [ordered]@{}
  foreach ($s in $baseEntry['steps']) { $baseSteps[$s['step']] = $s }
  $comparisons = @()
  foreach ($entry in $data['labels']) {
    if ($entry['label'] -eq $BaselineLabel) { continue }
    $entryKind = Get-EntryKind $entry
    $entryTarget = Get-EntryTarget $entry
    if ($entryKind -ne $baseKind -or $entryTarget -ne $baseTarget) {
      $comparisons += [ordered]@{
        label      = $entry['label']
        compatible = $false
        reason     = "incompatible target ($entryKind/$entryTarget vs $baseKind/$baseTarget)"
        rows       = @()
      }
      continue
    }
    $rows = @()
    $seenSteps = [Collections.Generic.HashSet[string]]::new()
    foreach ($step in $entry['steps']) {
      $stepName = [string]$step['step']
      [void]$seenSteps.Add($stepName)
      if ($baseSteps.Contains($stepName)) {
        $bStep = $baseSteps[$stepName]
        $deltaSec = [Math]::Round(([double]$step['median_seconds'] - [double]$bStep['median_seconds']), 2)
        $deltaMb = [Math]::Round(([double]$step['median_peak_mb'] - [double]$bStep['median_peak_mb']), 1)
        $candSpreadSec = Get-StepSpreadSeconds $step
        $baseSpreadSec = Get-StepSpreadSeconds $bStep
        $maxSpreadSec = [Math]::Max($candSpreadSec, $baseSpreadSec)
        $timeWarn = $maxSpreadSec -gt [Math]::Abs($deltaSec)
        $candSpreadMb = Get-StepSpreadPeakMb $step
        $baseSpreadMb = Get-StepSpreadPeakMb $bStep
        $maxSpreadMb = [Math]::Max($candSpreadMb, $baseSpreadMb)
        $mbWarn = $maxSpreadMb -gt [Math]::Abs($deltaMb)
        $status = if ($timeWarn -and $mbWarn) { 'WARNING: time+mem spread' }
          elseif ($timeWarn) { 'WARNING: time spread' }
          elseif ($mbWarn) { 'WARNING: memory spread' }
          else { 'ok' }
        $rows += [ordered]@{
          step                = $stepName
          missing             = $false
          delta_seconds       = $deltaSec
          delta_peak_mb       = $deltaMb
          cand_spread_seconds = $candSpreadSec
          base_spread_seconds = $baseSpreadSec
          cand_spread_peak_mb = $candSpreadMb
          base_spread_peak_mb = $baseSpreadMb
          time_warn           = $timeWarn
          mb_warn             = $mbWarn
          status              = $status
        }
      } else {
        $rows += [ordered]@{
          step    = $stepName
          missing = $true
          status  = "not in baseline '$BaselineLabel'"
        }
      }
    }
    foreach ($bName in $baseSteps.Keys) {
      if (-not $seenSteps.Contains($bName)) {
        $rows += [ordered]@{
          step    = $bName
          missing = $true
          status  = "not in '$($entry['label'])'"
        }
      }
    }
    $comparisons += [ordered]@{
      label      = $entry['label']
      compatible = $true
      reason     = $null
      rows       = $rows
    }
  }
  [ordered]@{
    baseline    = $BaselineLabel
    kind        = $baseKind
    target      = $baseTarget
    comparisons = $comparisons
  }
}
function Write-ComparisonSummary($comparison) {
  "comparison against baseline '$($comparison['baseline'])' ($($comparison['kind'])/$($comparison['target'])):"
  if (-not $comparison['comparisons'].Count) { '  no other labels to compare'; return }
  foreach ($item in $comparison['comparisons']) {
    if (-not $item['compatible']) {
      "  $($item['label']): $($item['reason']), not compared"
      continue
    }
    "  $($item['label']):"
    foreach ($row in $item['rows']) {
      if ($row['missing']) {
        "    $($row['step']): $($row['status'])"
      } else {
        "    $($row['step']): time $(Format-SignedSeconds $row['delta_seconds']) (spread $(Format-Seconds $row['cand_spread_seconds']) / $(Format-Seconds $row['base_spread_seconds']) s), memory $(Format-SignedMb $row['delta_peak_mb']) (spread $(Format-Number $row['cand_spread_peak_mb']) / $(Format-Number $row['base_spread_peak_mb']) MB) [$($row['status'])]"
      }
    }
  }
}
# Whether a .NET warm build used what the build before it left running: null when nothing of that kind was left.
function Format-Used($value) { if ($null -eq $value) { 'none was left' } elseif ($value) { 'used' } else { 'not used' } }

function ConvertTo-Document($data, [string]$BaselineLabel = '') {
  $out = [Collections.Generic.List[string]]::new()
  $out.Add('# Build performance')
  $out.Add('')
  $out.Add('Generated by `pwsh scripts/measure-build.ps1` from `docs/build-performance.json`. Do not edit it by hand: measure again, or run the script with `-RenderOnly`.')
  $out.Add('')
  $out.Add('Wall time is the median of the samples, in seconds. Peak memory is the median, in MB, of the most memory each run and everything it started had committed at one moment. Spread is `max - min` across samples. A `kept` label is a setting that stayed because it measured better than the baseline; a `dropped` one was reverted.')
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
      if ($entry.Contains('diagnostic') -and $entry['diagnostic']) {
        $diag = $entry['diagnostic']
        $topList = @($diag['top_expensive'] | ForEach-Object { "$($_['name']) ($($_['kind'])): $($_['ms']) ms" }) -join '; '
        $out.Add("Diagnostic run ($($diag['workload'])): binary log ``$($diag['binlog'])``; top 3 expensive targets/tasks: $topList.")
        $out.Add('')
      }
    } else {
      $parts = @(
        $(if ($configuration['direct_cmake']) { 'mode `cmake --build --preset release` (direct)' } else { "build arguments $(if ($buildArgs) { '`' + $buildArgs + '`' } else { 'none' })" }),
        "compiler launcher $(if ($configuration['launcher']) { $configuration['launcher'] } else { 'none' })",
        "linker $(if ($configuration['linker']) { $configuration['linker'] } else { 'not recorded' })",
        "developer environment $(if ($configuration['direct_cmake'] -or $configuration['env_inherited']) { 'initialised shell' } elseif ($configuration['env_cache']) { 'from cache' } else { 'loaded each build' })"
      )
      $out.Add("Configuration: $($parts -join '; ').")
      $out.Add('')
    }
    $out.Add('| Step | Wall time (s) | Peak memory (MB) | Time spread (s) | Memory spread (MB) |')
    $out.Add('| ---- | ------------: | ---------------: | --------------: | -----------------: |')
    foreach ($step in $entry['steps']) {
      $spreadSec = Format-Seconds (Get-StepSpreadSeconds $step)
      $spreadMb = Format-Number (Get-StepSpreadPeakMb $step)
      $out.Add("| $($step['step']) | $(Format-Seconds $step['median_seconds']) | $(Format-Number $step['median_peak_mb']) | $spreadSec | $spreadMb |")
    }
    $out.Add('')
    foreach ($step in $entry['steps']) {
      $samplesSec = @($step['seconds'] | ForEach-Object { Format-Seconds $_ }) -join ', '
      $samplesMb = @($step['peak_mb'] | ForEach-Object { Format-Number $_ }) -join ', '
      $out.Add("- $($step['step']) samples: $samplesSec s; $samplesMb MB.")
    }
  }
  if ($BaselineLabel) {
    $comparison = Get-BaselineComparison $data $BaselineLabel
    if ($comparison) {
      $out.Add('')
      $out.Add("## Comparison against $BaselineLabel")
      $out.Add('')
      $out.Add("Baseline target: $($comparison['kind'])/$($comparison['target']). Spread columns show ``candidate / baseline``. A ``WARNING`` is shown when ``max(candidate spread, baseline spread)`` is wider than the median change.")
      if (-not $comparison['comparisons'].Count) {
        $out.Add('')
        $out.Add('No other labels to compare.')
      }
      foreach ($item in $comparison['comparisons']) {
        $out.Add('')
        $out.Add("### $($item['label']) vs $BaselineLabel")
        $out.Add('')
        if (-not $item['compatible']) {
          $out.Add("Not compared: $($item['reason']).")
          continue
        }
        $out.Add('| Step | Time | Time spread | Memory | Mem spread | Status |')
        $out.Add('| ---- | ---: | ----------: | -----: | ---------: | ------ |')
        foreach ($row in $item['rows']) {
          if ($row['missing']) {
            $out.Add("| $($row['step']) | n/a | n/a | n/a | n/a | $($row['status']) |")
          } else {
            $tSpread = "$(Format-Seconds $row['cand_spread_seconds']) / $(Format-Seconds $row['base_spread_seconds'])"
            $mSpread = "$(Format-Number $row['cand_spread_peak_mb']) / $(Format-Number $row['base_spread_peak_mb'])"
            $out.Add("| $($row['step']) | $(Format-SignedSeconds $row['delta_seconds']) | $tSpread | $(Format-SignedMb $row['delta_peak_mb']) | $mSpread | $($row['status']) |")
          }
        }
      }
    }
  }
  ($out -join "`n") + "`n"
}

function Write-Document($data, [string]$BaselineLabel = '') {
  try { Write-Atomic $docPath (ConvertTo-Document $data $BaselineLabel) } catch {
    $retry = 'pwsh scripts/measure-build.ps1 -RenderOnly'
    $defaultOut = [IO.Path]::GetFullPath((Join-Path $repo 'docs'))
    if ([IO.Path]::GetFullPath($OutDir) -ine $defaultOut) { $retry += " -OutDir $(Format-PsSingleQuote $OutDir)" }
    if ($BaselineLabel) { $retry += " -Baseline $BaselineLabel" }
    "could not write ${docPath}: $($_.Exception.Message)"
    "the measurements are stored in ${dataPath}: run $retry once that is fixed"
    exit 1
  }
}

# Stores the data (temporary file, replace, read back), then renders the document from what was stored.
function Save-Data($data, [string]$BaselineLabel = '') {
  $json = (($data | ConvertTo-Json -Depth 8) -replace "`r`n", "`n") + "`n"
  try {
    Write-Atomic $dataPath $json
    if ([IO.File]::ReadAllText($dataPath, $utf8) -cne $json) { throw 'the file read back differs from what was written' }
  } catch { "could not store the measurement in ${dataPath}: $($_.Exception.Message)"; exit 1 }
  Write-Document (Read-Data) $BaselineLabel
}

# Runs $Change on the stored data and saves the result, all while holding the lock file, so two runs saving at once
# cannot overwrite each other. With no change it only renders the document, under the same lock, so a render can
# never put an older document over the one a save has just written. The lock is a file opened for this process alone; Windows removes it when the handle
# closes, however the script ends.
function Update-Data([scriptblock]$Change, [string]$BaselineLabel = '') {
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
    if ($BaselineLabel -and -not ($current['labels'] | Where-Object { $_['label'] -eq $BaselineLabel })) {
      "no baseline label '$BaselineLabel' has been measured: nothing was changed"
      exit 1
    }
    if ($Change) {
      $updated = & $Change $current
      Save-Data $updated $BaselineLabel
      $script:lastSavedData = $updated
    } else {
      Write-Document $current $BaselineLabel
      $script:lastSavedData = $current
    }
    $script:labelCount = $script:lastSavedData['labels'].Count
  } finally { $lock.Dispose() }
}

$hasBaseline = $PSBoundParameters.ContainsKey('Baseline')
if ($RenderOnly -and $DecideOnly) { '-RenderOnly and -DecideOnly cannot be used together'; exit 2 }
if ($DirectCmake -and $Dotnet) { '-DirectCmake and -Dotnet cannot be used together'; exit 2 }
if ($DirectCmake -and ($RenderOnly -or $DecideOnly)) { '-DirectCmake cannot be used with -RenderOnly or -DecideOnly'; exit 2 }
if ($DirectCmake -and $BuildArgs.Count -gt 0) { '-DirectCmake does not accept -BuildArgs'; exit 2 }
if ($Diagnose -and -not $Dotnet) { '-Diagnose can only be used with -Dotnet'; exit 2 }
if ($Diagnose -and ($RenderOnly -or $DecideOnly)) { '-Diagnose cannot be used with -RenderOnly or -DecideOnly'; exit 2 }
if ($AllowOutsideServers -and -not $Dotnet) { '-AllowOutsideServers can only be used with -Dotnet'; exit 2 }
if ($AllowOutsideServers -and ($RenderOnly -or $DecideOnly)) { '-AllowOutsideServers cannot be used with -RenderOnly or -DecideOnly'; exit 2 }
if ($hasBaseline -and $DecideOnly) { '-Baseline cannot be used with -DecideOnly'; exit 2 }
if ($hasBaseline -and [string]::IsNullOrWhiteSpace($Baseline)) { '-Baseline needs a label name'; exit 2 }
if ($hasBaseline -and $Baseline -cnotmatch '^[a-z0-9][a-z0-9-]{0,39}$') { "-Baseline '$Baseline' must be lowercase letters, digits and dashes, at most 40"; exit 2 }
if ($hasBaseline -and -not $RenderOnly -and $Label -and $Baseline -eq $Label) { "-Baseline cannot name the same label '$Label' being measured"; exit 2 }

try { $data = Read-Data } catch { "$($_.Exception.Message)"; exit 1 }
if ($RenderOnly) {
  if ($hasBaseline -and -not ($data['labels'] | Where-Object { $_['label'] -eq $Baseline })) {
    "no baseline label '$Baseline' has been measured: nothing was changed"
    exit 1
  }
  Update-Data $null $(if ($hasBaseline) { $Baseline } else { '' })
  "rendered $docPath from $labelCount label(s)"
  if ($hasBaseline) { Write-ComparisonSummary (Get-BaselineComparison $script:lastSavedData $Baseline) }
  exit 0
}
if (-not $Label -or -not $Decision) { 'measure-build needs -Label <name> and -Decision <baseline|kept|dropped>, or -RenderOnly'; exit 2 }
if ($Label -cnotmatch '^[a-z0-9][a-z0-9-]{0,39}$') { "-Label '$Label' must be lowercase letters, digits and dashes, at most 40"; exit 2 }
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
if ($hasBaseline -and -not ($data['labels'] | Where-Object { $_['label'] -eq $Baseline })) {
  "no baseline label '$Baseline' has been measured: nothing was changed"
  exit 1
}

# From here to the end, the run guard reads this measurement as a wait, its queue wait included
# (scripts/lib/gate-mark.ps1).
. (Join-Path $PSScriptRoot 'lib\gate-mark.ps1')
# Where its output goes, for run-guard.py status (D00 T03 §32): a run that queues itself names the queued run's own
# mark, and one that measures here names its log (and the .linger and .warm logs beside it).
# Decided once, for the mark and for the run (round 2 of D00 T03 §32).
$willQueue = -not $env:BOSUN_MEASURE_QUEUED -and [bool](Get-Command bosun -CommandType Application -ErrorAction Ignore)
$log = Join-Path ([IO.Path]::GetTempPath()) "bosun-measure-$PID.log"
$markOutput = if ($willQueue) { 'queued through bosun run: the measurement it starts has a mark of its own, naming its log' } else { "$log, with its .linger, .warm-<sample> and .diagnose logs beside it" }
try { $gateMark = Enter-GateMark "measure-build $Label" $markOutput } catch { "measure-build: $($_.Exception.Message): nothing was measured"; exit 1 }
try {
# Take a turn in the queue, once: the wait for a slot happens here, before any step is timed.
if (-not $env:BOSUN_MEASURE_QUEUED) {
  $env:BOSUN_MEASURE_QUEUED = '1'
  if ($willQueue) {
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

$results = [ordered]@{}
$envCache = $false
$envInherited = [bool](Get-Command cl -CommandType Application -ErrorAction Ignore)

# Runs one step in its own job. Returns its result, or ends the script: a failed or timed-out step stores nothing.
function Invoke-Step([string]$Name, [string]$File, [string[]]$Arguments, [string]$DisplayName = '', [string]$StepLog = $log) {
  $labelName = if ($DisplayName) { $DisplayName } else { $Name }
  Write-LiveLine "  starting $labelName..."
  $progress = @{ Next = 5.0; Clock = [Diagnostics.Stopwatch]::StartNew() }
  $result = Measure-Job -FilePath $File -ArgumentList $Arguments -WorkingDirectory $repo -TimeoutSeconds $TimeoutSeconds -OutputPath $StepLog -OnTick {
    param($final)
    if (-not $final) {
      $e = $progress.Clock.Elapsed.TotalSeconds
      if ($e -ge $progress.Next) {
        $sec = [int][Math]::Floor($e)
        Write-LiveLine "  $labelName`: still running ($sec s)"
        while ($progress.Next -le $e) { $progress.Next += 5.0 }
      }
    }
  }
  if ($result.TimedOut -or $result.ExitCode -ne 0) {
    Get-Content -LiteralPath $StepLog -Tail 15 -ErrorAction Ignore
    if ($result.TimedOut) { "step '$Name' was stopped at its $TimeoutSeconds second limit: nothing was stored" }
    else { "step '$Name' failed with exit code $($result.ExitCode): nothing was stored" }
    Remove-Item -LiteralPath $StepLog -Force -ErrorAction Ignore
    exit 1
  }
  $result
}

function Invoke-Build([string]$Name, [string[]]$Extra, [bool]$WantWork, [bool]$Timed, [string]$DisplayName = '') {
  $result = Invoke-Step $Name 'pwsh' (@('-NoProfile', '-File', $BuildScript) + @($BuildArgs | ForEach-Object { "-$_" }) + $Extra) $DisplayName
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
    "  $Name sample $($results[$Name].Count): $(Format-Seconds $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
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

if ($DirectCmake) {
  $cmakePath = (Get-Command $CmakeCommand -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
  if (-not $cmakePath) { "measure-build -DirectCmake could not find '$CmakeCommand' on PATH or at that path: nothing was stored"; exit 1 }
  if (-not (Get-Command cl -CommandType Application -ErrorAction Ignore)) { 'measure-build -DirectCmake needs an initialised Visual Studio developer shell (cl is not on PATH): nothing was stored'; exit 1 }
  function Invoke-DirectCmakeBuild([string]$Name, [bool]$WantWork, [bool]$Timed, [string]$DisplayName = '') {
    $result = Invoke-Step $Name $cmakePath @('--build', '--preset', 'release') $DisplayName
    $text = Get-Content -LiteralPath $log -Raw -ErrorAction Ignore
    $idle = $text -match 'ninja: no work to do'
    if ($Timed -and $WantWork -eq $idle) {
      if ($WantWork) { "step '$Name' built nothing, so it is not that workload: nothing was stored" }
      else { "step '$Name' compiled something, so it is not a build with nothing to do: nothing was stored" }
      Remove-Item -LiteralPath $log -Force -ErrorAction Ignore
      exit 1
    }
    if ($Timed) {
      if (-not $results.Contains($Name)) { $results[$Name] = @() }
      $results[$Name] += , $result
      "  $Name sample $($results[$Name].Count): $(Format-Seconds $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
    }
  }
  "measuring '$Label' (direct cmake): untimed preparation, then $Samples sample(s) of no-op and one-file builds"
  # Direct CMake configures build/ outside scripts/build.ps1, so invalidate the wrapper's preset fingerprint first.
  $fpPath = Join-Path $repo 'build\preset-configure.sha256'
  if (Test-Path -LiteralPath $fpPath) {
    try { Remove-Item -LiteralPath $fpPath -Force -ErrorAction Stop } catch {
      "measure-build -DirectCmake could not invalidate ${fpPath} ($($_.Exception.Message)): nothing was measured"
      exit 1
    }
  }
  [void](Invoke-Step 'preparation configure' $cmakePath @('--preset', 'default') 'preparation configure')
  Invoke-DirectCmakeBuild 'preparation build' $false $false 'preparation build'
  foreach ($n in 1..$Samples) { Invoke-DirectCmakeBuild 'nothing to do' $false $true "nothing to do sample $n/$Samples" }
  foreach ($n in 1..$Samples) {
    (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
    Invoke-DirectCmakeBuild 'one file changed' $true $true "one file changed sample $n/$Samples"
  }
} else {
  "measuring '$Label': one preparation build, then $Samples sample(s) of each step"
  Invoke-Build 'preparation build' @() $false $false 'preparation build'
  foreach ($n in 1..$Samples) { Invoke-Build 'nothing to do' @() $false $true "nothing to do sample $n/$Samples" }
  foreach ($n in 1..$Samples) {
    (Get-Item -LiteralPath $TouchFile).LastWriteTimeUtc = [DateTime]::UtcNow
    Invoke-Build 'one file changed' @() $true $true "one file changed sample $n/$Samples"
  }
  foreach ($n in 1..$Samples) { Invoke-Build 'fresh build' @('-Fresh') $true $true "fresh build sample $n/$Samples" }
}
if ($Tests) {
  # Single-run exception: the Python suite runs once regardless of -Samples (spread is 0.00 s / 0.0 MB).
  $result = Invoke-Step 'python test suite' 'python' @((Join-Path $PSScriptRoot 'run-tests.py')) 'python test suite'
  $results['python test suite'] = @($result)
  "  python test suite: $(Format-Seconds $result.Seconds) s, $(Format-Number $result.PeakMB) MB"
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
  target        = 'native'
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
    build_args    = @($BuildArgs)
    direct_cmake  = [bool]$DirectCmake
    launcher      = if ($launcher) { Split-Path $launcher -Leaf } else { $null }
    linker        = if ($linker) { Split-Path $linker -Leaf } else { $null }
    env_cache     = $envCache
    env_inherited = $envInherited
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

# Other sessions may have measured meanwhile: under the lock, read again, replace this label, write and read back.
$baselineArg = if ($hasBaseline) { $Baseline } else { '' }
Update-Data {
  param($current)
  $current['labels'] = @($current['labels'] | Where-Object { $_['label'] -ne $Label }) + , $entry
  $current
} $baselineArg
"stored '$Label' ($Decision) in $dataPath and rendered $docPath"
foreach ($step in $entry['steps']) { "  $($step['step']): $(Format-Seconds $step['median_seconds']) s (spread $(Format-Seconds $step['spread_seconds']) s), $(Format-Number $step['median_peak_mb']) MB (spread $(Format-Number $step['spread_peak_mb']) MB)" }
if ($hasBaseline) { Write-ComparisonSummary (Get-BaselineComparison $script:lastSavedData $Baseline) }
exit 0
} finally { Exit-GateMark $gateMark }
