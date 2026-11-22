# Tests Directory.Build.rsp: a .NET build in this repository leaves no MSBuild worker node alive after it returns,
# where without the file the nodes stay to be reused and hold memory (measured 2026-11-10: 11 idle nodes, 1.4 GB).
# The file governs command-line builds (`dotnet build`, `dotnet test`, `msbuild`); Visual Studio does not read
# response files, so its builds keep its own node settings.
#
# -BreakBuild makes the fixture's builds fail, to show the failure path: the test fails and keeps its logs.
#
# Each case builds a small solution in a scratch copy of this repository's .NET settings, with the rsp file or
# without it. The build runs directly, not through `bosun run --`: Bosun ends a job's whole process tree when the
# job ends, so a queued build leaves no node either way and could not show what the file does. The file is for the
# command-line builds that are not queued, a plain `dotnet build` or `dotnet test`, and the fixture is three empty projects, small
# enough not to need a slot. Each build runs in a job object (scripts/lib/job-measure.ps1) that stays open until
# its nodes have been watched: the nodes are the job's own live members, never matched by name or by a recorded id,
# and ending the job ends every process the build started, on success, failure, a timeout or an interruption.
param([switch]$BreakBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
. (Join-Path $repo 'scripts/lib/scratch.ps1')
$work = New-BosunScratchFolder -Name 'dotnet-nodes-test'
$failed = 0
$finished = $false  # set at the end of the cases: a run stopped early keeps its logs
$jobs = [Collections.Generic.List[object]]::new()  # every build's job, ended in the finally below
. (Join-Path $repo 'scripts/lib/job-measure.ps1')
$limitSeconds = 300
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# A copy of the repository's .NET settings and three small projects, so the build has work for more than one node.
function New-Fixture([string]$name, [switch]$WithoutRsp) {
  $dir = Join-Path $work $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  foreach ($file in 'global.json', 'Directory.Build.props', 'Directory.Packages.props') {
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $dir
  }
  if (-not $WithoutRsp) { Copy-Item -LiteralPath (Join-Path $repo 'Directory.Build.rsp') -Destination $dir }
  $projects = foreach ($n in 1..3) {
    $p = Join-Path $dir "P$n"
    New-Item -ItemType Directory -Force $p | Out-Null
    Set-Content -LiteralPath (Join-Path $p "P$n.csproj") -Value "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"
    $body = if ($BreakBuild) { 'this does not compile' } else { "namespace P$n; public static class C { public const int V = $n; }" }
    Set-Content -LiteralPath (Join-Path $p 'C.cs') -Value $body
    "    <Project Path=`"P$n/P$n.csproj`" />"
  }
  Set-Content -LiteralPath (Join-Path $dir 'Nodes.slnx') -Value (@('<Solution>') + $projects + '</Solution>')
  $dir
}

# Builds $dir in a job object of its own and returns the open job with the build's exit code. Every process the build
# starts is in the job from its first instruction, whenever it starts, so none can escape the cleanup; and the job
# lists only its live members, so a process id reused by an unrelated process is never mistaken for one of them.
# The job stays open after the build returns, so the nodes can be watched; disposing it ends everything in it. A
# build past the limit is ended with its whole job and throws.
function Invoke-Build([string]$dir) {
  $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
  $line = (@($dotnet, 'build', 'Nodes.slnx', '-nologo', '-v:q') | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
  $run = [Bosun.JobMeasure+Run]::new($line, $dir, (Join-Path $dir 'build.log'))
  $script:jobs.Add($run)
  # Short slices, so Ctrl+C is seen within one of them and the finally below ends the job at once.
  while (-not $run.Wait(200)) {
    if ($run.ElapsedSeconds -gt $limitSeconds) {
      [void]$run.Finish($true)
      throw "the build in $dir passed its $limitSeconds second limit and was ended with its whole job"
    }
  }
  [pscustomobject]@{ Run = $run; ExitCode = $run.Finish($false).ExitCode }
}

# The MSBuild worker nodes still alive in the build's job: its live members that run in node mode.
function Get-OwnNodes($run) {
  $ids = [Collections.Generic.HashSet[int]]::new([int[]]$run.ProcessIds())
  @(Get-CimInstance Win32_Process -Property ProcessId, CommandLine -ErrorAction Ignore |
      Where-Object { $ids.Contains([int]$_.ProcessId) -and $_.CommandLine -match '[/-]nodemode:' })
}

try {
  New-Item -ItemType Directory -Force $work | Out-Null

  # With Directory.Build.rsp, as the repository builds: no node of this build is alive 5 seconds after it returns.
  'case: with Directory.Build.rsp'
  $with = New-Fixture 'with-rsp'
  $build = Invoke-Build $with
  Start-Sleep -Seconds 5
  $left = Get-OwnNodes $build.Run
  Check 'a build with the repository''s Directory.Build.rsp succeeds' ($build.ExitCode -eq 0) "(exit $($build.ExitCode): $((Get-Content -LiteralPath (Join-Path $with 'build.log') -Tail 5) -join ' / '))"
  Check 'it leaves no MSBuild node it started alive 5 seconds after it returns' ($left.Count -eq 0) "($($left.Count) left: $(($left | ForEach-Object { $_.ProcessId }) -join ', '))"
  $build.Run.Dispose()

  # Without it, MSBuild's default keeps its nodes for reuse, which is what the file is there to prevent: a test that
  # cannot see them survive would pass on a file that did nothing.
  'case: without Directory.Build.rsp'
  $without = New-Fixture 'without-rsp' -WithoutRsp
  $build = Invoke-Build $without
  Start-Sleep -Seconds 5
  $left = Get-OwnNodes $build.Run
  Check 'the same build without Directory.Build.rsp succeeds' ($build.ExitCode -eq 0) "(exit $($build.ExitCode): $((Get-Content -LiteralPath (Join-Path $without 'build.log') -Tail 5) -join ' / '))"
  Check 'and leaves the nodes it started alive to be reused, so the check above can fail' ($left.Count -gt 0)
  # Their process objects, held before the job ends, so the check below asks about these processes and not about
  # whatever may later reuse their ids.
  $kept = @($left | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction Ignore })
  $build.Run.Dispose()
  foreach ($process in $kept) { [void]$process.WaitForExit(10000) }
  $alive = @($kept | Where-Object { -not $_.HasExited })
  # The nodes the last build kept were members of its job, so ending the job ended them.
  Check 'ending the job ends the nodes the build left' ($kept.Count -gt 0 -and $alive.Count -eq 0) "($($kept.Count) kept, $($alive.Count) alive)"
  $finished = $true
} finally {
  # Ending each job ends every process in it, whatever happened above (a failure, a timeout or Ctrl+C).
  foreach ($run in $jobs) { $run.Dispose() }
  # A run that failed or stopped early keeps its fixtures and their build logs for diagnosis; a passing one leaves
  # nothing behind.
  if ($failed -or -not $finished) {
    "kept for diagnosis: $work (each case's build.log is in its folder)"
  } else {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
  }
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
