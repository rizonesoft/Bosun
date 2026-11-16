# Integration test for `bosun instances` (D03 T02 §1): real processes, named by their images, found in their repos,
# their state from session records and review marks, and the jobs holding slots. Everything it starts runs from copies
# of the test sleeper under this test's own folder, its records and session records in homes of its own, and every
# process it started is ended, tree and all, whatever happens.
param([string]$Build = "$PSScriptRoot\..\build\Release")
$ErrorActionPreference = 'Stop'
Remove-Item Env:BOSUN_SLOT -ErrorAction Ignore  # a suite run through `bosun run` must not pass its slot to these jobs
$bosun = (Resolve-Path (Join-Path $Build 'bosun.exe')).Path
$sleeper = (Resolve-Path (Join-Path $Build 'test_sleeper.exe')).Path
$ns = "BosunInst$PID"
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-inst-$PID"
New-Item -ItemType Directory -Path $work | Out-Null
$work = (Resolve-Path $work).Path
$env:LOCALAPPDATA = Join-Path $work 'home'      # the queue's records
$env:USERPROFILE = Join-Path $work 'profile'    # Claude Code's session records
$sessions = Join-Path $env:USERPROFILE '.claude\sessions'
New-Item -ItemType Directory -Path $sessions | Out-Null
$repo = Join-Path $work 'app'
New-Item -ItemType Directory -Path (Join-Path $repo '.git'), (Join-Path $repo 'src') | Out-Null
$loose = Join-Path $work 'loose'
New-Item -ItemType Directory -Path $loose | Out-Null
$bin = Join-Path $work 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
foreach ($name in 'claude.exe', 'codex.exe') { Copy-Item $sleeper (Join-Path $bin $name) }
$fail = 0
function Check($ok, $name) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }
function Q($s) { "`"$s`"" }
$started = [Collections.Generic.List[int]]::new()
function Start-Held([string]$exe, [string[]]$arguments, [string]$in) {
  $p = Start-Process $exe -ArgumentList $arguments -WorkingDirectory $in -NoNewWindow -PassThru
  $script:started.Add($p.Id)
  $p
}
# A process started outside this test's own tree: a test run from an agent's terminal has that agent among its
# ancestors, and a claude under a claude is part of that claude (dm::instance_roots), so the test's claude starts from
# CIM, its parent the WMI host. It is still this test's to end, by its id.
function Start-Detached([string]$exe, [string]$arguments, [string]$in) {
  $made = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "$(Q $exe) $arguments"; CurrentDirectory = $in }
  if ($made.ReturnValue -ne 0) { throw "Win32_Process.Create refused $exe ($($made.ReturnValue))" }
  $script:started.Add([int]$made.ProcessId)
  Get-Process -Id $made.ProcessId
}
function Scan([string]$space = $ns) { & $bosun instances --json --ns $space | ConvertFrom-Json -DateKind String }
function Row($doc, [int]$id) { @($doc.instances | Where-Object { $_.pid -eq $id }) | Select-Object -First 1 }
function Same-Path([string]$a, [string]$b) { $a -and $b -and ($a -replace '\\', '/').TrimEnd('/') -ieq ($b -replace '\\', '/').TrimEnd('/') }
function Wait-Until([scriptblock]$ok, [int]$ms = 10000) { foreach ($i in 1..($ms / 100)) { if (& $ok) { return $true }; Start-Sleep -Milliseconds 100 }; [bool](& $ok) }
function Session([int]$id, [long]$start, [string]$status) {
  @{ pid = $id; sessionId = 'test'; procStart = "$start"; peerFeatures = @('x'); status = $status } | ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $sessions "$id.json") -NoNewline
}
function Mark([int]$id, [double]$deadline) {
  $folder = Join-Path $repo 'build\review-in-flight'
  New-Item -ItemType Directory -Path $folder -Force | Out-Null
  @{ pid = $id; reviewer = 'astra'; started = 'x'; deadline = $deadline } | ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $folder "$id-astra.json") -NoNewline
}

try {
  # A claude in a checkout, with its repo, folder, memory and start; one outside a checkout with a folder and no repo.
  $claude = Start-Detached (Join-Path $bin 'claude.exe') "$(Q (Join-Path $work 'c.txt')) 60000" (Join-Path $repo 'src')
  $codexLoose = Start-Held (Join-Path $bin 'codex.exe') @((Q (Join-Path $work 'x.txt')), '60000') $loose
  $null = Wait-Until { (Row (Scan) $codexLoose.Id) -and (Row (Scan) $claude.Id) }
  $doc = Scan
  $row = Row $doc $claude.Id
  $startUtc = $claude.StartTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
  Check ($row -and $row.kind -eq 'claude' -and $row.side -eq 'windows' -and (Same-Path $row.repo $repo) -and (Same-Path $row.cwd (Join-Path $repo 'src'))) 'a claude is found in its checkout, by its image'
  Check ($row -and $null -ne $row.private_mb -and $row.started_at -eq $startUtc) "its memory and start are read ($($row.started_at) vs $startUtc)"
  Check ($row -and $row.state -eq 'unknown' -and $null -eq $row.evidence) 'with no evidence at all its state is unknown'
  $loose1 = Row $doc $codexLoose.Id
  Check ($loose1 -and $null -eq $loose1.repo -and (Same-Path $loose1.cwd $loose)) 'one outside a checkout has its folder and no repo'
  Check ($doc.schema_version -eq 1 -and $doc.side -eq 'windows' -and $null -eq $doc.distro -and @($doc.problems).Count -eq 0) 'the document is the shared shape, with no problems'

  # Its session record: busy is working; a record naming another start (a reused pid) is unknown, and says why.
  $filetime = $claude.StartTime.ToFileTimeUtc()
  Session $claude.Id $filetime 'busy'
  $row = Row (Scan) $claude.Id
  Check ($row.state -eq 'working' -and $row.evidence -eq 'Claude Code says busy (session record)') 'a busy session record makes it working'
  Session $claude.Id ($filetime + 1) 'busy'
  $row = Row (Scan) $claude.Id
  Check ($row.state -eq 'unknown' -and $row.evidence -like '*started at another time*') 'a record naming another start is unknown'
  Remove-Item -LiteralPath (Join-Path $sessions "$($claude.Id).json")

  # A codex under a live review's dispatcher, in its repo, is reviewing; once the deadline has passed, unknown.
  # cmd /c takes the whole command in one pair of outer quotes.
  $dispatcher = Start-Held "$env:SystemRoot\System32\cmd.exe" @('/c', "`"$(Q (Join-Path $bin 'codex.exe')) $(Q (Join-Path $work 'r.txt')) 60000`"") $repo
  $null = Wait-Until { @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($dispatcher.Id)").Count -gt 0 }
  $reviewer = (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($dispatcher.Id) AND Name='codex.exe'").ProcessId
  $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000
  Mark $dispatcher.Id ($now + 600)
  $row = Row (Scan) $reviewer
  Check ($row -and $row.state -eq 'reviewing' -and $row.evidence -eq "runs under astra's review (dispatcher pid $($dispatcher.Id))") 'a codex under a live review mark is reviewing'
  Mark $dispatcher.Id ($now - 1)
  $row = Row (Scan) $reviewer
  Check ($row -and $row.state -eq 'unknown') 'once the mark''s deadline has passed it is unknown again'

  # A job holding a slot: its slot, side, command and memory; working once its command runs.
  $job = Start-Held $bosun @('run', '--ns', $ns, '--slots', '2', '--min-avail-mb', '0', '--', (Q $sleeper), (Q (Join-Path $work 'j.txt')), '60000') $repo
  $null = Wait-Until { (Row (Scan) $job.Id).state -eq 'working' }
  $row = Row (Scan) $job.Id
  Check ($row -and $row.kind -eq 'job' -and $row.slot -eq 0 -and $row.side -eq 'windows' -and $row.name -eq 'test_sleeper.exe') "a job holding a slot is listed with its slot and side ($($row.name))"
  Check ($row -and $row.command -like '*test_sleeper.exe*' -and $null -ne $row.private_mb -and $row.state -eq 'working' -and (Same-Path $row.repo $repo)) 'with its command, memory and repo, working'

  # Problems are said by source, and every instance is still listed beside them.
  Set-Content -LiteralPath (Join-Path $sessions 'broken.json') -Value '{not json'
  $doc = Scan
  Check (@($doc.problems | Where-Object { $_.source -eq 'sessions' -and $_.why -like '*broken.json is not a session record' }).Count -eq 1) 'a malformed session record is a problem'
  Check ((Row $doc $claude.Id) -and (Row $doc $job.Id)) 'and the instances are still listed'
  $badNs = "BosunInstBad$PID"
  $nsDir = 'ns-' + (-join ($badNs.ToCharArray() | ForEach-Object { if ($_ -cmatch '[a-z0-9_-]') { $_ } else { '%{0:X2}' -f [int]$_ } }))
  $records = Join-Path $env:LOCALAPPDATA "Bosun\run\$((Get-Process -Id $PID).SessionId)\$nsDir"
  New-Item -ItemType Directory -Path (Split-Path $records) -Force | Out-Null
  Set-Content -LiteralPath $records -Value 'a file where the records folder should be'
  $doc = Scan $badNs
  Check (@($doc.problems | Where-Object { $_.source -eq 'queue-records' }).Count -eq 1 -and (Row $doc $claude.Id)) 'a records folder that can''t be read is a problem, never an empty queue'
  $text = & $bosun instances --ns $ns
  Check ($LASTEXITCODE -eq 0 -and ($text -match "^claude $($claude.Id) ")) 'the text form has a line per instance'
} finally {
  foreach ($id in $started) { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $id 2>&1 | Out-Null }
  $null = Wait-Until { -not (Get-Process -Id $started -ErrorAction Ignore) } 5000
  Remove-Item $work -Recurse -Force -ErrorAction Ignore
}
if ($fail) { "$fail failed"; exit 1 }
'all passed'
