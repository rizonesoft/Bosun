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
# ancestors, and a claude under a claude (or a codex under a codex) is part of it (dm::instance_roots), so every fixture
# of a kind starts from CIM, its parent the WMI host (round 3: the codex fixtures failed under a Codex session). It is
# still this test's to end, tree and all, by its id.
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
  $codexLoose = Start-Detached (Join-Path $bin 'codex.exe') "$(Q (Join-Path $work 'x.txt')) 60000" $loose
  $null = Wait-Until { (Row (Scan) $codexLoose.Id) -and (Row (Scan) $claude.Id) }
  $doc = Scan
  $row = Row $doc $claude.Id
  $startUtc = $claude.StartTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
  Check ($row -and $row.kind -eq 'claude' -and $row.side -eq 'windows' -and (Same-Path $row.repo $repo) -and (Same-Path $row.cwd (Join-Path $repo 'src'))) 'a claude is found in its checkout, by its image'
  Check ($row -and $null -ne $row.private_mb -and $row.started_at -eq $startUtc) "its memory and start are read ($($row.started_at) vs $startUtc)"
  Check ($row -and $row.state -eq 'unknown' -and $null -eq $row.evidence) 'with no evidence at all its state is unknown'
  $loose1 = Row $doc $codexLoose.Id
  Check ($loose1 -and $null -eq $loose1.repo -and (Same-Path $loose1.cwd $loose)) 'one outside a checkout has its folder and no repo'
  Check ($doc.schema_version -eq 1 -and $doc.side -eq 'windows' -and $null -eq $doc.distro -and @($doc.problems | Where-Object { $_.source -notin 'editor-windows', 'editor-coverage' }).Count -eq 0) 'the document is the shared shape, with no problems (an editor running on this machine has no window record in the test''s own LOCALAPPDATA, which is said)'

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
  $dispatcher = Start-Detached "$env:SystemRoot\System32\cmd.exe" "/c `"$(Q (Join-Path $bin 'codex.exe')) $(Q (Join-Path $work 'r.txt')) 60000`"" $repo
  # Its codex.exe, not just any child: cmd's first child can be its console host (found on CI, 2026-11-16).
  $null = Wait-Until { @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($dispatcher.Id) AND Name='codex.exe'").Count -gt 0 }
  $reviewer = (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($dispatcher.Id) AND Name='codex.exe'").ProcessId
  if (-not $reviewer) { throw "the dispatcher's codex.exe never started" }
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
  Check ($row -and (Same-Path $row.cwd $repo)) "and the folder it runs in ($($row.cwd))"

  # Editors placed by their windows (D03 T02 §4). A stand-in editor is a copy of cmd.exe named Code.exe (its main
  # process) running a copy of the sleeper also named Code.exe (its extension host), as VS Code's are; the window
  # reporter's records are written as editors/bosun-windows writes them. Two checkouts named app, both outside the
  # folder the editors start in.
  $one = Join-Path $work 'one\app'; $two = Join-Path $work 'two\app'
  New-Item -ItemType Directory -Path (Join-Path $one '.git'), (Join-Path $one 'src'), (Join-Path $two '.git') | Out-Null
  $windows = Join-Path $env:LOCALAPPDATA 'Bosun\editor-windows'
  New-Item -ItemType Directory -Path $windows -Force | Out-Null
  $env:BOSUN_WSL_EXE = Join-Path $work 'no-wsl.exe'   # no real distro is asked about, or woken
  # -Windows and -Helpers start that many renderer children beside the host: a window's carries
  # --vscode-window-config, a helper's does not (D03 T02 §5), as VS Code's do.
  function Start-Editor([string]$name, [string]$tag, [int]$Windows = 0, [int]$Helpers = 0) {
    $main = Join-Path $bin "$tag\$name"; $host_ = Join-Path $bin "$tag\host\Code.exe"
    New-Item -ItemType Directory -Path (Split-Path $host_) -Force | Out-Null
    Copy-Item "$env:SystemRoot\System32\cmd.exe" $main; Copy-Item $sleeper $host_
    $side = @()
    foreach ($k in 1..$Windows) { if ($Windows) { $side += "start `"`" /b $(Q $host_) $(Q (Join-Path $work "$tag-w$k.txt")) 60000 --type=renderer --vscode-window-config=vscode:$tag$k" } }
    foreach ($k in 1..$Helpers) { if ($Helpers) { $side += "start `"`" /b $(Q $host_) $(Q (Join-Path $work "$tag-h$k.txt")) 60000 --type=renderer" } }
    $line = (@($side) + "$(Q $host_) $(Q (Join-Path $work "$tag.txt")) 60000") -join ' & '
    $editor = Start-Detached $main "/d /s /c `"$line`"" $loose
    $want = 1 + $Windows + $Helpers
    $null = Wait-Until { @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($editor.Id) AND Name='Code.exe'").Count -ge $want }
    $hostId = (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($editor.Id) AND Name='Code.exe'" | Where-Object { $_.CommandLine -notmatch '--type=' }).ProcessId
    if (-not $hostId) { throw "the stand-in editor's host never started" }
    [pscustomobject]@{ Id = $editor.Id; Host = [int]$hostId }
  }
  function Uri([string]$path) { 'file:///' + ($path.Substring(0, 1).ToLower() + '%3A' + ($path.Substring(2) -replace '\\', '/')) }
  function Window([string]$session, $editor, [string[]]$roots, [int]$hostId = $editor.Host) {
    $record = [ordered]@{ schema_version = 1; app_name = 'Visual Studio Code'; main_pid = $editor.Id; host_pid = $hostId; session_id = $session
      workspace_file = $null; roots = @($roots); written_utc = [DateTime]::UtcNow.AddSeconds(1).ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    Set-Content -LiteralPath (Join-Path $windows "$session.json") -Value ($record | ConvertTo-Json -Compress) -NoNewline
  }
  $editorA = Start-Editor 'Code.exe' 'ea'
  $editorB = Start-Editor 'Code.exe' 'eb'
  $bare = Start-Editor 'Code.exe' 'ec'
  $agentApp = Start-Editor 'Antigravity.exe' 'ag'
  Window 'sa1' $editorA @((Uri (Join-Path $one 'src')), (Uri $two))
  Window 'sb1' $editorB @('vscode-remote://wsl%2BUbuntu/home/me/app')
  Window 'sb2' $editorB @()
  Window 'sag' $agentApp @((Uri $one))
  $sshOnly = Start-Editor 'Code.exe' 'ed'
  # Its windows against its records (D03 T02 §5): three windows and a helper with one record is two not placed; two
  # windows with one is one; one window whose record has two roots is fully placed.
  $short2 = Start-Editor 'Code.exe' 'ee' -Windows 3 -Helpers 1
  $short1 = Start-Editor 'Code.exe' 'ef' -Windows 2
  $whole = Start-Editor 'Code.exe' 'eg' -Windows 1 -Helpers 2
  $noRecord = Start-Editor 'Code.exe' 'eh' -Windows 2
  Window 'se1' $short2 @((Uri $one))
  Window 'sf1' $short1 @((Uri $two))
  Window 'sg1' $whole @((Uri $one), (Uri $two))
  Window 'sd1' $sshOnly @('vscode-remote://ssh-remote%2Bbox/srv/app')
  Window 'stale' $editorA @((Uri $one)) 999999
  Set-Content -LiteralPath (Join-Path $windows 'damaged.json') -Value '{"schema_version":1,' -NoNewline
  $doc = Scan
  $rowsA = @($doc.instances | Where-Object { $_.pid -eq $editorA.Id })
  Check ($rowsA.Count -eq 2 -and (Same-Path $rowsA[0].repo $one) -and (Same-Path $rowsA[1].repo $two) -and (Same-Path $rowsA[0].cwd (Join-Path $one 'src'))) "one editor is in both same-named repos it has open, from outside both ($(($rowsA | ForEach-Object repo) -join ', '))"
  Check (@($rowsA | Where-Object { $_.placed_by -eq 'window' -and $_.location -eq 'windows' }).Count -eq 2 -and $rowsA[0].window -like 'sa1#*' -and $rowsA[1].window -like 'sa1#*' -and $rowsA[0].window -ne $rowsA[1].window) 'each root is a row of its own, with its window identity'
  Check (($rowsA | ForEach-Object { [int64]($_.private_mb ?? 0) } | Measure-Object -Sum).Sum -eq [int64]$rowsA[0].shared_mb -and $null -eq $rowsA[1].private_mb -and $null -ne $rowsA[0].shared_mb) 'its memory is on its first row once, and shared by both'
  $rowsB = @($doc.instances | Where-Object { $_.pid -eq $editorB.Id })
  $wslRow = @($rowsB | Where-Object { $_.window -like 'sb1#*' })[0]
  Check ($rowsB.Count -eq 2 -and $wslRow.location -eq 'wsl' -and $wslRow.distro -eq 'Ubuntu' -and $wslRow.cwd -eq '/home/me/app' -and $wslRow.side -eq 'windows' -and $null -eq $wslRow.repo) 'a WSL window is in its distro, the process still on Windows, and no distro is woken to find its repo'
  $empty = @($rowsB | Where-Object { $_.window -eq 'sb2#empty' })[0]
  Check ($empty -and $null -eq $empty.cwd -and $null -eq $empty.repo) 'an empty window is a row with no folder and no repo'
  Check (-not @($doc.instances | Where-Object { $_.pid -in $editorA.Id, $editorB.Id -and $_.window -like 'sb*' -and $_.pid -eq $editorA.Id }).Count) 'each editor has only its own windows'
  $missing = @($doc.problems | Where-Object { $_.source -eq 'editor-coverage' -and $_.why -like '*windows not placed*' })
  Check (@($missing | Where-Object { $_.why -like "Code.exe $($short2.Id) has 2 of its 3 windows not placed*install-editor-extension.ps1" }).Count -eq 1) 'an editor with three windows and one record says two are not placed, helpers not counted'
  Check (@($missing | Where-Object { $_.why -like "Code.exe $($short1.Id) has 1 of its 2 windows not placed*" }).Count -eq 1) 'and one with two windows and one record says one'
  Check (-not @($missing | Where-Object { $_.why -like "*$($whole.Id)*" }).Count -and @($doc.instances | Where-Object { $_.pid -eq $whole.Id }).Count -eq 2) 'a window whose record has two roots is fully placed, with no word'
  Check ($missing.Count -eq 2) "and no other editor is said to be short ($($missing.Count))"
  $none = @($doc.problems | Where-Object { $_.source -eq 'editor-coverage' -and $_.why -like "Code.exe $($noRecord.Id) has no window record for any of its 2 windows*run pwsh scripts/install-editor-extension.ps1" })
  Check ($none.Count -eq 1 -and (Row $doc $noRecord.Id).placed_by -eq 'launch') 'an editor with two windows and no record keeps its launch row and says how many windows'
  $sshRow = Row $doc $sshOnly.Id
  Check ($sshRow -and $sshRow.placed_by -eq 'window' -and $null -eq $sshRow.cwd -and $null -ne $sshRow.private_mb -and @($doc.problems | Where-Object { $_.why -like "*$($sshOnly.Id)*is a remote window that is not WSL" }).Count -eq 1) 'an editor whose only root Bosun can''t place is still listed, with its memory, and the root is said'
  $bareRow = Row $doc $bare.Id
  Check ($bareRow -and $bareRow.placed_by -eq 'launch' -and (Same-Path $bareRow.cwd $loose) -and @($doc.problems | Where-Object { $_.source -eq 'editor-coverage' -and $_.why -like "*$($bare.Id) has no window record*install-editor-extension.ps1" }).Count -eq 1) 'an editor with no window record keeps its launch folder, marked, and the problem says how to place it'
  $agRow = Row $doc $agentApp.Id
  Check ($agRow -and $null -eq $agRow.placed_by -and $null -eq $agRow.window -and (Same-Path $agRow.cwd $loose)) 'the agent app is never placed by a window record'
  Check (@($doc.problems | Where-Object { $_.source -eq 'editor-windows' -and $_.why -like '*stale.json is stale*' }).Count -eq 1 -and @($doc.problems | Where-Object { $_.why -like '*damaged.json is not a window record*' }).Count -eq 1) 'a stale and a damaged record are each said, beside the rows'
  Check (Test-Path -LiteralPath (Join-Path $windows 'stale.json')) 'and the scanner deletes no record'
  Check ($wslRow -and @($doc.problems | Where-Object { $_.why -like '*so no WSL window''s repo is looked up' }).Count -eq 1) 'a WSL that can''t be asked is said once'
  & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $editorA.Id 2>&1 | Out-Null
  $null = Wait-Until { -not (Get-Process -Id $editorA.Id, $editorA.Host -ErrorAction Ignore) } 5000
  $doc = Scan
  Check (-not @($doc.instances | Where-Object { $_.pid -eq $editorA.Id }).Count -and @($doc.problems | Where-Object { $_.why -like '*sa1.json is stale*' }).Count -eq 1) 'an editor that has exited is not listed, and its record is stale'
  Remove-Item Env:BOSUN_WSL_EXE

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
