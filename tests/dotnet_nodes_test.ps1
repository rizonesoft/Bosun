# Tests Directory.Build.rsp: a .NET build in this repository leaves no MSBuild worker node alive after it returns,
# where without the file the nodes stay to be reused and hold memory (measured 2026-11-10: 11 idle nodes, 1.4 GB).
#
# Each case builds a small solution in a scratch copy of this repository's .NET settings, with the rsp file or
# without it. The build runs directly, not through `bosun run --`: Bosun ends a job's whole process tree when the
# job ends, so a queued build leaves no node either way and could not show what the file does. The file is for the
# builds that are not queued, an IDE's or a plain `dotnet build`, and the fixture is three empty projects, small
# enough not to need a slot. Nodes are matched to the build that started them through the process tree, never by
# name, and every process this test started is ended when it is done, on success, failure, a timeout or an
# interruption.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$scratch = if ($env:BOSUN_SCRATCH -and (Test-Path -LiteralPath $env:BOSUN_SCRATCH -PathType Container)) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ("bosun-dotnet-nodes-test-" + [guid]::NewGuid())
$failed = 0
$started = [Collections.Generic.HashSet[int]]::new()  # every process id this test saw in a build's tree
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
    Set-Content -LiteralPath (Join-Path $p 'C.cs') -Value "namespace P$n; public static class C { public const int V = $n; }"
    "    <Project Path=`"P$n/P$n.csproj`" />"
  }
  Set-Content -LiteralPath (Join-Path $dir 'Nodes.slnx') -Value (@('<Solution>') + $projects + '</Solution>')
  $dir
}

function Get-Tree {
  @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CommandLine, Name -ErrorAction Ignore)
}

# The MSBuild worker nodes descended from the processes this test started: their parent is one of them, and they
# run in node mode. Ids recorded while the build ran keep the chain even after the build itself has exited.
function Get-OwnNodes {
  @(Get-Tree | Where-Object { $started.Contains([int]$_.ParentProcessId) -and $_.CommandLine -match '[/-]nodemode:' })
}

# Builds $dir, recording every process in the build's tree as it appears. Returns the build's exit code, or throws
# when it passes the limit (its whole tree is ended first).
function Invoke-Build([string]$dir) {
  $process = Start-Process -FilePath 'dotnet' -ArgumentList @('build', 'Nodes.slnx', '-nologo', '-v:q') -WorkingDirectory $dir -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $dir 'build.log')
  [void]$started.Add($process.Id)
  $clock = [Diagnostics.Stopwatch]::StartNew()
  while (-not $process.HasExited) {
    foreach ($item in Get-Tree) { if ($started.Contains([int]$item.ParentProcessId)) { [void]$started.Add([int]$item.ProcessId) } }
    if ($clock.Elapsed.TotalSeconds -gt $limitSeconds) {
      Stop-Started
      throw "the build in $dir passed its $limitSeconds second limit and was stopped with its whole tree"
    }
    Start-Sleep -Milliseconds 200
  }
  # Nodes started in the last moments of the build are recorded too.
  foreach ($item in Get-Tree) { if ($started.Contains([int]$item.ParentProcessId)) { [void]$started.Add([int]$item.ProcessId) } }
  $process.ExitCode
}

function Stop-Started {
  foreach ($id in @($started)) { Stop-Process -Id $id -Force -ErrorAction Ignore }
}

try {
  New-Item -ItemType Directory -Force $work | Out-Null

  # With Directory.Build.rsp, as the repository builds: no node of this build is alive 5 seconds after it returns.
  $with = New-Fixture 'with-rsp'
  $code = Invoke-Build $with
  Start-Sleep -Seconds 5
  $left = Get-OwnNodes
  Check 'a build with the repository''s Directory.Build.rsp succeeds' ($code -eq 0) "(exit ${code}: $((Get-Content -LiteralPath (Join-Path $with 'build.log') -Tail 5) -join ' / '))"
  Check 'it leaves no MSBuild node it started alive 5 seconds after it returns' ($left.Count -eq 0) "($($left.Count) left: $(($left | ForEach-Object { $_.ProcessId }) -join ', '))"
  Stop-Started
  $started.Clear()

  # Without it, MSBuild's default keeps its nodes for reuse, which is what the file is there to prevent: a test that
  # cannot see them survive would pass on a file that did nothing.
  $without = New-Fixture 'without-rsp' -WithoutRsp
  $code = Invoke-Build $without
  Start-Sleep -Seconds 5
  $left = Get-OwnNodes
  Check 'the same build without Directory.Build.rsp succeeds' ($code -eq 0)
  Check 'and leaves the nodes it started alive to be reused, so the check above can fail' ($left.Count -gt 0)
} finally {
  # Everything this test started ends here, whatever happened above.
  Stop-Started
  Start-Sleep -Milliseconds 500
  $stray = @(Get-Tree | Where-Object { $started.Contains([int]$_.ProcessId) })
  if ($stray) { "warning: $($stray.Count) process(es) this test started were still alive after cleanup" }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
$after = @(Get-Tree | Where-Object { $started.Contains([int]$_.ProcessId) })
Check 'no process this test started is alive after it' ($after.Count -eq 0) "($($after.Count) alive)"
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
