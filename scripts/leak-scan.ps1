# Refuses a push that would publish a secret or a private string (D00 T05 §4). scripts/hooks/pre-push runs it with
# the refs git is about to push on standard input:
#
#   pwsh scripts/leak-scan.ps1 -Remote <name or URL>          the pre-push ref lines on standard input
#   pwsh scripts/leak-scan.ps1 -Remote <name or URL> -Updates '<local ref> <local sha> <remote ref> <remote sha>', ...
#   pwsh scripts/leak-scan.ps1 -Range <revision range>        the commits git rev-list gives for the range
#   pwsh scripts/leak-scan.ps1 -All                           every ref's history, root included, and every annotated tag
#
# The outgoing commits come from the destination itself: `git ls-remote` gives its current tips, and every commit
# being pushed that is not reachable from one of them is outgoing (a local tracking ref is never trusted). They are
# scanned twice:
#   - by gitleaks (8.30.1, pinned in the README), merges diffed against their first parent so a resolution is covered;
#   - against .private/deny-list.txt (the private overlay): a line whose first character is # is a comment, a line of
#     only whitespace is blank, and every other line is a string (its surrounding whitespace removed),
#     matched literally and case-insensitively against the lines the commits add, the paths they add or rename to,
#     their messages, and the names and annotations of tags being pushed.
# A finding prints the commit, the place and the rule, never the matched text, the deny-list entry or a path that
# holds one. Every failure refuses: a missing gitleaks, a deny list that cannot be read, a remote that cannot be
# listed, a git command that fails, a report that cannot be read. Exit 0 when clean, 1 otherwise.
[CmdletBinding()]
param(
  # The remote by name or address; the hook passes it in BOSUN_LEAK_REMOTE, so an address with a credential in it
  # never reaches a process command line.
  [string]$Remote = $env:BOSUN_LEAK_REMOTE,
  [string[]]$Updates,
  # A scan outside a push (D00 T05 §6 to §8): the commits of a revision range, or every ref's history and annotated tag.
  # Neither reads standard input or lists a remote.
  [string]$Range,
  [switch]$All
)
$ErrorActionPreference = 'Stop'
$history = [bool]($All -or $Range)
$refusal = if ($history) { 'scan failed' } else { 'push refused' }
if ($All -and $Range) { [Console]::Error.WriteLine('leak-scan: scan failed: give -Range or -All, not both'); exit 1 }
if (-not $history -and -not $Remote) { [Console]::Error.WriteLine('leak-scan: push refused: no remote was given to check against'); exit 1 }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path $PSScriptRoot
$zero = '0' * 40
$failed = [Collections.Generic.List[object]]::new()
function Say([string]$text) { [Console]::Error.WriteLine("leak-scan: $text") }
function Stop-Scan([string]$why) {
  Say "${refusal}: $why"
  if (-not $script:cleaning -and (Get-Command Remove-ScanRefs -ErrorAction Ignore) -and $scanRefs) { $script:cleaning = $true; $null = Remove-ScanRefs $ownRefs }
  exit 1
}
function Invoke-Git([string[]]$arguments) {
  $out = & git -C $repoRoot @arguments 2>$null
  if ($LASTEXITCODE) { Stop-Scan "git $($arguments[0]) failed, so the outgoing commits could not be read" }
  @($out | ForEach-Object { "$_" })
}


# Git writes the ref lines in UTF-8; they are read as bytes, never through the console's code page (437 here), so a
# name like café reaches the checks as git publishes it.
if ($history) { $Updates = @() }
elseif ($null -eq $Updates) {
  $stdin = [IO.MemoryStream]::new()
  [Console]::OpenStandardInput().CopyTo($stdin)
  $stdinBytes = $stdin.ToArray()
  $Updates = @([Text.UTF8Encoding]::new($false).GetString($stdinBytes) -split "`r?`n" | Where-Object { $_.Trim() })

}
$locals = [Collections.Generic.List[string]]::new()
$tags = [Collections.Generic.List[object]]::new()
$refNames = [Collections.Generic.List[object]]::new()
foreach ($line in $Updates) {
  # Git separates the four fields with an ASCII space; a ref name can hold other whitespace (a no-break space).
  $parts = $line.TrimEnd("`r", "`n") -split ' ', 4
  if ($parts.Count -ne 4 -or $parts[1] -notmatch '^[0-9a-f]{40}$') { Stop-Scan 'a pushed ref could not be read' }
  if ($parts[1] -eq $zero) { continue }  # a deletion publishes nothing
  $refNames.Add([pscustomobject]@{ Name = $parts[2]; Sha = $parts[1] })
  # Every pushed object that is not itself a commit (a tag, or a file or tree under any ref, refs/archive/... included)
  # goes through the object pass below, whatever namespace it is pushed to.
  $type = "$(& git -C $repoRoot cat-file -t $parts[1] 2>$null)"
  if ($type -ne 'commit') { $tags.Add([pscustomobject]@{ Name = ($parts[2] -replace '^refs/(tags|heads)/', ''); Object = $parts[1] }) }
  # A tag can point at a file or a tree, not only a commit: only what peels to a commit has commits to scan.
  $commit = "$(& git -C $repoRoot rev-parse --verify --quiet "$($parts[1])^{commit}" 2>$null)"
  if ($commit) { $locals.Add($commit) }
}
if (-not $history -and -not $locals.Count -and -not $tags.Count) { exit 0 }  # only deletions: nothing is published
# The one gitleaks both scans below run, resolved once from PATH, so each uses the same tool; needed only once there is
# something to scan, so a push of deletions alone goes through without it.
$gitleaksExe = (Get-Command gitleaks -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
if (-not $gitleaksExe) {
  $remedy = if ($IsWindows) { 'winget install --id Gitleaks.Gitleaks -e --version 8.30.1 --scope user' }
            else { 'download gitleaks_8.30.1_linux_x64.tar.gz from https://github.com/gitleaks/gitleaks/releases/tag/v8.30.1, check it against gitleaks_8.30.1_checksums.txt, and put gitleaks on PATH' }
  Stop-Scan "gitleaks is not installed, so the commits cannot be checked for secrets: $remedy"
}


# What the destination already has, asked of the destination.
# A remote given as an address (a push to a bare URL) is named for git through its environment configuration, so the
# address, which can hold a credential, is never on a command line.
if ($history) {
  # Every commit of the range, or with -All of every ref's history (root commits included), as CI scans the public
  # repository (D00 T05 §6); nothing counts as published already. With -All every annotated tag goes through the tag
  # pass below, as a pushed one does.
  $revisions = if ($All) { @('--all') } else { @($Range) }
  $outgoing = @(Invoke-Git (@('rev-list') + $revisions))
  $known = @()
  if ($All) {
    # Every ref as a push of it would be read (D00 T05 §6): its name is published text, and an object it names that is
    # not a commit (an annotated tag, or a file or tree a lightweight tag points at) goes through the tag pass.
    foreach ($line in Invoke-Git @('for-each-ref', '--format=%(objecttype) %(objectname) %(refname)')) {
      $kind, $sha, $name = $line -split ' ', 3
      $refNames.Add([pscustomobject]@{ Name = $name; Sha = $sha })
      if ($kind -ne 'commit') { $tags.Add([pscustomobject]@{ Name = ($name -replace '^refs/(tags|heads)/', ''); Object = $sha }) }
    }
  }
}
else {
$remoteName = $Remote
if (-not ((& git -C $repoRoot remote 2>$null) -contains $Remote)) {
  # Appended after any entries the caller passed this way (a credential helper, say), never over them.
  $slot = if ("$env:GIT_CONFIG_COUNT" -match '^\d+$') { [int]$env:GIT_CONFIG_COUNT } else { 0 }
  Set-Item -LiteralPath "Env:GIT_CONFIG_KEY_$slot" -Value 'remote.bosun-leak-scan.url'
  Set-Item -LiteralPath "Env:GIT_CONFIG_VALUE_$slot" -Value $Remote
  $env:GIT_CONFIG_COUNT = "$($slot + 1)"
  $remoteName = 'bosun-leak-scan'
}
$listed = & git -C $repoRoot ls-remote $remoteName 2>$null
if ($LASTEXITCODE) { Stop-Scan 'the remote could not be listed, so the commits it lacks are not known' }
# The commits pushed and the tips the destination has go to git as temporary refs, never as hashes on a command line:
# hundreds of tags would pass Windows' 32,767-character limit and stop every push. They are named once, under
# refs/bosun-leak-scan/, and removed when the scan ends (any left by a scan that was killed are removed first).
# Each scan's refs sit under its own <process id>-<random id>, so two pushes from one checkout never touch each
# other's: a scan removes its own refs, and at start only those of a scan whose process has ended.
$scanRefs = "refs/bosun-leak-scan/$PID-$([guid]::NewGuid().ToString('N'))"
# update-ref --stdin is written through the process's own stdin with LF line ends: PowerShell's pipe to a native
# command ends each line with CRLF on Windows, and git would read every hash with a stray carriage return.
function Invoke-UpdateRefs([string[]]$commands) {
  $start = [Diagnostics.ProcessStartInfo]::new('git')
  foreach ($a in '-C', $repoRoot, 'update-ref', '--stdin') { $start.ArgumentList.Add($a) }
  $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
  $process = [Diagnostics.Process]::Start($start)
  $null = $process.StandardOutput.ReadToEndAsync(); $null = $process.StandardError.ReadToEndAsync()
  $bytes = [Text.UTF8Encoding]::new($false).GetBytes((($commands -join "`n") + "`n"))
  $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $process.StandardInput.Close()
  $process.WaitForExit()
  $process.ExitCode
}
# Removes the scan refs a filter keeps, and says whether they are gone: a listing or a deletion that fails is $false.
function Remove-ScanRefs([scriptblock]$which) {
  $listed = @(& git -C $repoRoot for-each-ref --format='%(refname)' 'refs/bosun-leak-scan/' 2>$null)
  if ($LASTEXITCODE) { return $false }
  $gone = @($listed | Where-Object { & $which $_ } | ForEach-Object { "delete $_" })
  (-not $gone) -or -not (Invoke-UpdateRefs $gone)
}
$ownRefs = { param($name) $name.StartsWith("$scanRefs/", [StringComparison]::Ordinal) }
$endedScans = {
  param($name)
  $owner = if ($name -match '^refs/bosun-leak-scan/(\d+)-') { [int]$Matches[1] } else { 0 }
  -not $owner -or -not (Get-Process -Id $owner -ErrorAction Ignore)
}
$script:cleaning = $true
if (-not (Remove-ScanRefs $endedScans)) { Stop-Scan 'the temporary refs a stopped scan left under refs/bosun-leak-scan/ could not be removed; remove them (git update-ref -d <ref>), then push again' }
$script:cleaning = $false
# A tip the destination has that this checkout lacks (the remote moved on since the last fetch) is fetched into the
# scan's own refs, so its history counts as published and is not scanned as outgoing. A fetch that fails leaves those
# commits counted as outgoing: the scan then checks more, never less.
function Test-Commit([string]$sha) { & git -C $repoRoot cat-file -e "$sha^{commit}" 2>$null; -not $LASTEXITCODE }
$tips = @($listed | ForEach-Object { $fields = "$_" -split "`t", 2; if ($fields.Count -eq 2 -and $fields[0] -match '^[0-9a-f]{40}$' -and -not $fields[1].EndsWith('^{}')) { [pscustomobject]@{ Sha = $fields[0]; Name = $fields[1] } } })
$missing = @($tips | Where-Object { -not (Test-Commit $_.Sha) })
if ($missing) {
  $start = [Diagnostics.ProcessStartInfo]::new('git')
  foreach ($a in '-C', $repoRoot, 'fetch', '--quiet', '--no-tags', '--no-write-fetch-head', '--stdin', $remoteName) { $start.ArgumentList.Add($a) }
  $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
  $process = [Diagnostics.Process]::Start($start)
  $null = $process.StandardOutput.ReadToEndAsync(); $null = $process.StandardError.ReadToEndAsync()
  $specs = for ($n = 0; $n -lt $missing.Count; $n++) { "+$($missing[$n].Name):$scanRefs/fetched/$n" }
  $bytes = [Text.UTF8Encoding]::new($false).GetBytes((($specs -join "`n") + "`n"))
  $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $process.StandardInput.Close()
  $process.WaitForExit()
}
$known = @($tips | ForEach-Object Sha | Sort-Object -Unique | Where-Object { Test-Commit $_ })
$create = @(for ($n = 0; $n -lt $locals.Count; $n++) { "create $scanRefs/push/$n $($locals[$n])" }) + @(for ($n = 0; $n -lt $known.Count; $n++) { "create $scanRefs/known/$n $($known[$n])" })
if ($create) {
  if (Invoke-UpdateRefs $create) { Stop-Scan 'the commits to scan could not be named for git' }
}
$notKnown = if ($known) { @('--not', "--glob=$scanRefs/known/*") } else { @() }
$revisions = @("--glob=$scanRefs/push/*") + $notKnown
$outgoing = if ($locals.Count) { @(Invoke-Git (@('rev-list') + $revisions)) } else { @() }
}

# --- the private deny list, read first so no finding of either pass prints a path holding an entry ---
# Every comparison folds both sides one way: NFKC (composed and decomposed accents, full-width letters alike), then
# invariant upper case, which maps final and medial sigma alike (lower case maps the capital to medial sigma only).
function Get-Folded([string]$value) {
  try { $value.Normalize([Text.NormalizationForm]::FormKC).ToUpperInvariant() } catch { $value.ToUpperInvariant() }
}
$denyPath = Join-Path $repoRoot '.private' 'deny-list.txt'
$deny = @()
if (Test-Path -LiteralPath $denyPath) {
  try { $text = [IO.File]::ReadAllText($denyPath, [Text.UTF8Encoding]::new($false, $true)) }
  catch { Stop-Scan 'the deny list (.private/deny-list.txt) exists but could not be read' }
  # A comment is a line whose first character is #, tested on the line as written, so an indented `  # name` is an
  # entry. An entry's surrounding whitespace is removed, so a stray space never narrows a match.
  $deny = @($text -split "`r?`n" | Where-Object { -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { Get-Folded $_ })
}
function Test-Deny([string]$value) {
  if (-not $value) { return $false }
  $folded = Get-Folded $value
  foreach ($entry in $deny) { if ($folded.Contains($entry, [StringComparison]::Ordinal)) { return $true } }
  $false
}

# Git's raw bytes, never its display: a path or file content reaches the checks exactly as it will be published,
# with no quoting of names and no "binary" verdict standing in for the content.
function Get-GitBytes([string[]]$arguments) {
  $start = [Diagnostics.ProcessStartInfo]::new('git')
  foreach ($a in @('-C', $repoRoot) + $arguments) { $start.ArgumentList.Add($a) }
  $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
  $process = [Diagnostics.Process]::Start($start)
  $buffer = [IO.MemoryStream]::new()
  $errors = $process.StandardError.ReadToEndAsync()
  $process.StandardOutput.BaseStream.CopyTo($buffer)
  $process.WaitForExit(); $null = $errors.Result
  if ($process.ExitCode) { Stop-Scan "git $($arguments[0]) failed, so the outgoing commits could not be read" }
  # A byte array goes back whole: the pipeline would turn an empty one (a zero-byte file, an empty commit) into nothing.
  ,$buffer.ToArray()
}
# Text as it is stored: UTF-8 (the default), or UTF-16 when it carries a byte-order mark or NUL bytes, as a report
# saved by Windows PowerShell 5.1 does.
function ConvertFrom-Blob([byte[]]$bytes) {
  # No guessing which encoding a file is in: every common one is read (UTF-8, Windows-1252 for files saved as "ANSI"
  # on Windows, UTF-16 and UTF-32 in both byte orders), each with NULs and byte-order marks taken out, and an entry
  # found in any reading counts. Encoding by encoding, the guesses kept missing a case (rounds 1, 3, 7, 12 to 14 of
  # D00 T05 §4); six readings of a pushed file cost microseconds.
  foreach ($encoding in Get-Encodings) { ConvertTo-Reading $encoding $bytes }
}
# Every code page Windows saves "ANSI" text in (1250 to 1258 by region, 874 Thai, 932, 936, 949 and 950 East Asian),
# KOI8-R and KOI8-U, then UTF-16 and UTF-32 both ways: a name saved by any regional Windows is read as it was written.
$script:encodings = @([Text.Encoding]::UTF8) + @(foreach ($page in 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258, 874, 932, 936, 949, 950, 20866, 21866) { [Text.Encoding]::GetEncoding($page) }) +
  @([Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode, [Text.Encoding]::UTF32, [Text.UTF32Encoding]::new($true, $false))
function Get-Encodings { $script:encodings }
function ConvertTo-Reading($encoding, [byte[]]$bytes) { $encoding.GetString($bytes).Replace([string][char]0, '').Replace([string][char]0xFEFF, '') }
# Names git publishes are bytes, which on Linux and WSL need not be UTF-8 (a legacy archive's file name, a ref typed
# in a Latin-1 shell). They are cut out of git's raw output at its byte separators and each name is read under every
# encoding, so a deny-list entry is found whatever the name's bytes. Only names a push publishes are passed in.
function Split-Bytes([byte[]]$bytes, [byte]$separator) {
  $parts = [Collections.Generic.List[object]]::new()
  $start = 0
  for ($n = 0; $n -le $bytes.Length; $n++) {
    if ($n -eq $bytes.Length -or $bytes[$n] -eq $separator) {
      if ($n -gt $start) { $parts.Add([byte[]]$bytes[$start..($n - 1)]) }
      $start = $n + 1
    }
  }
  ,$parts
}
function Test-DenyBytes([byte[]]$name) {
  foreach ($encoding in Get-Encodings) { if (Test-Deny (ConvertTo-Reading $encoding $name)) { return $true } }
  $false
}

# Every gitleaks result is judged by one rule (each run has --exit-code 3 and its JSON report on standard output):
# exit 0 with an empty array is clean, exit 3 with one or more entries is a finding, and anything else (another exit, a
# report that is not a JSON array, exit 3 with no entry, exit 0 with one) refuses: that answer cannot be trusted.
function Read-GitleaksReport([int]$code, [string]$text, [string]$what) {
  if ($code -ne 0 -and $code -ne 3) { Stop-Scan "gitleaks could not scan $what (exit $code)" }
  try { $parsed = ConvertFrom-Json -InputObject "$text" -NoEnumerate -ErrorAction Stop } catch { $parsed = $null }
  if ($parsed -isnot [array]) { Stop-Scan "the gitleaks report for $what could not be read" }
  if ($code -eq 3 -and -not $parsed.Count) { Stop-Scan "gitleaks reported a finding in $what that its report does not hold" }
  if ($code -eq 0 -and $parsed.Count) { Stop-Scan "gitleaks reported $what clean, but its report holds findings" }
  ,$parsed
}

# --- gitleaks over the outgoing commits ---
# Findings are kept as parts (commit, path, place, what) and printed only at the end, once every secret matched is
# known, so no path shown anywhere holds any of them or a deny-list entry.
$secrets = [Collections.Generic.List[string]]::new()
function Add-Finding([string]$commit, [string]$path, [string]$place, [string]$what, [string]$fingerprint = '') { $failed.Add([pscustomobject]@{ Commit = $commit; Path = $path; Place = $place; What = $what; Fingerprint = $fingerprint }) }
# Known false positives: one fingerprint per line of .gitleaksignore (# lines are comments). gitleaks applies them to
# the patch scan itself; the objects scanned as one stream below are matched here, by the same commit:file:rule:line
# form, the file being the object's path or, for an object with none, what it is (its place).
$ignored = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$ignoreFile = Join-Path $repoRoot '.gitleaksignore'
if (Test-Path -LiteralPath $ignoreFile -PathType Leaf) {
  foreach ($entry in [IO.File]::ReadAllLines($ignoreFile)) { if ($entry.Trim() -and -not $entry.TrimStart().StartsWith('#')) { [void]$ignored.Add($entry.Trim()) } }
}
if ($outgoing.Count) {
  $logOpts = (@('--diff-merges=first-parent') + $revisions) -join ' '
  # The report comes back on standard output, never a file: it is not redacted, so each matched secret can be kept
  # from every path shown, and an unredacted copy never rests on disk.
  $json = & $gitleaksExe git $repoRoot --log-opts $logOpts --no-banner --report-format json --report-path - --exit-code 3 2>$null
  $code = $LASTEXITCODE
  $findings = Read-GitleaksReport $code ($json -join "`n") 'the outgoing commits'
  foreach ($f in $findings) {
    if ($f.Secret) { $secrets.Add("$($f.Secret)") }
    if ($f.Match) { $secrets.Add("$($f.Match)") }
    Add-Finding "$($f.Commit)" "$($f.File)" "$($f.StartLine)" "secret ($($f.RuleID))" "$($f.Fingerprint)"
  }
}
# --- the deny list over what the outgoing commits publish ---
if ($deny.Count -and $outgoing.Count) {
  foreach ($c in $outgoing) {
    $short = $c.Substring(0, 12)
    # Every file the commit adds, changes or renames to, against its first parent (so a merge resolution is covered),
    # read as NUL-separated raw records: ":<mode> <mode> <old> <new> <status>", then one path, or two for a rename.
    $parent = & git -C $repoRoot rev-parse --verify --quiet "$c^1" 2>$null
    $tree = if ($parent) { @('diff-tree', '-z', '-r', '-M', '--raw', '--no-commit-id', "$parent", $c) } else { @('diff-tree', '-z', '-r', '-M', '--raw', '--no-commit-id', '--root', $c) }
    $treeBytes = Get-GitBytes $tree
    $tokens = [Text.Encoding]::UTF8.GetString($treeBytes) -split "`0"
    # Each published path as its own bytes: the destination of every record that is not a deletion, never a deleted path
    # or a rename's source, so a cleanup that removes a name is not refused for it.
    $records = Split-Bytes $treeBytes 0
    for ($r = 0; $r -lt $records.Count; $r++) {
      if ($records[$r][0] -ne [byte][char]':') { continue }
      $status = [Text.Encoding]::ASCII.GetString($records[$r]).Split(' ')[-1]
      $step = if ($status -match '^[RC]') { 2 } else { 1 }
      $published = if ($r + $step -lt $records.Count) { $records[$r + $step] } else { $null }
      $r += $step
      if ($status -notmatch '^D' -and $published -and (Test-DenyBytes $published)) { Add-Finding $c 'a path' '' 'deny-list entry (in a file name)'; break }
    }
    for ($i = 0; $i -lt $tokens.Count; $i++) {
      if (-not $tokens[$i].StartsWith(':')) { continue }
      $fields = $tokens[$i].Substring(1) -split ' '
      $status = $fields[4]
      $path = if ($status -match '^[RC]') { $tokens[$i + 2]; $i += 2 } else { $tokens[$i + 1]; $i += 1 }
      if ($status -match '^D') { continue }
      # The path is published even for a submodule entry (an embedded checkout added by mistake), so it is checked first.
      if (Test-Deny $path) { Add-Finding $c $path '' 'deny-list entry (in a file name)'; continue }
      if ($fields[1] -eq '160000') { continue }
      # What the commit adds: a line holding an entry counts only when the new file has more of that line than the
      # parent's version did, so an edit around an entry already published, or a cleanup removing one, is not refused.
      $newBytes = Get-GitBytes @('cat-file', 'blob', $fields[3])
      # No prior blob for an added file, or for one that replaces a submodule entry (whose old object is a commit).
      $oldBytes = if ($fields[2] -match '^0+$' -or $fields[0] -eq '160000') { ,[byte[]]@() } else { ,(Get-GitBytes @('cat-file', 'blob', $fields[2])) }
      # One encoding at a time, the new file against the same reading of its parent (never their sum, which counts one
      # line once per reading that shows it), so only two decoded copies are held at once, however large the file.
      $reported = $false
      foreach ($encoding in Get-Encodings) {
        $had = @{}
        foreach ($text in ((Get-Folded (ConvertTo-Reading $encoding $oldBytes)) -split "`r?`n")) { if (Test-Deny $text) { $had[$text] = 1 + [int]$had[$text] } }
        $seen = @{}
        $number = 0
        foreach ($text in ((Get-Folded (ConvertTo-Reading $encoding $newBytes)) -split "`r?`n")) {
          $number++
          if (-not (Test-Deny $text)) { continue }
          $seen[$text] = 1 + [int]$seen[$text]
          if ($seen[$text] -gt [int]$had[$text]) { Add-Finding $c $path "$number" 'deny-list entry'; $reported = $true; break }
        }
        if ($reported) { break }
      }
    }
    # The whole commit object as published: its author and committer names and addresses as well as its message.
    # The raw commit object, read under every encoding as a file is, and
    # Git itself re-encodes by the commit's encoding header (Windows-1250, Shift-JIS or any other it knows), so its UTF-8
    # rendering of the message and identities is read too.
    $asGit = [Text.Encoding]::UTF8.GetString((Get-GitBytes @('log', '-1', '--encoding=UTF-8', '--format=%an%n%ae%n%cn%n%ce%n%B', $c)))
    if ((Test-Deny $asGit) -or @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'commit', $c)) | Where-Object { Test-Deny $_ }).Count) { Add-Finding $c $null 'commit message or author' 'deny-list entry' }
  }
}
# --- gitleaks over bytes the patch scan above cannot see ---
# One helper for every such object: a commit's own text, a file git calls binary (its patch shows no content), the
# paths a commit adds, a ref's name, a tag's annotation and the files a tag publishes without a commit. Each is queued
# and all are scanned by one gitleaks run (Invoke-SecretScans), so a history of thousands of commits starts one
# scanner, not thousands. Each finding's secret is kept from every path shown.
$secretQueue = [Collections.Generic.List[object]]::new()
function Find-SecretsIn([byte[]]$bytes, [string]$commit, [string]$path, [string]$place, [string]$what) {
  $secretQueue.Add([pscustomobject]@{ Bytes = $bytes; Commit = $commit; Path = $path; Place = $place; What = $what })
}
# The queued objects go to gitleaks as one stream, each followed by a blank line so no line runs into the next object,
# and each finding's line number is mapped back to the object it falls in and its own line there.
function Invoke-SecretScans {
  if (-not $secretQueue.Count) { return }
  $stream = [IO.MemoryStream]::new()
  $starts = [int[]]::new($secretQueue.Count)
  $line = 1
  for ($n = 0; $n -lt $secretQueue.Count; $n++) {
    $bytes = $secretQueue[$n].Bytes
    $starts[$n] = $line
    $stream.Write($bytes, 0, $bytes.Length)
    $line += [Linq.Enumerable]::Count([byte[]]$bytes, [Func[byte, bool]] { param($b) $b -eq 10 })
    $stream.WriteByte(10); $stream.WriteByte(10)
    $line += 2
  }
  $start = [Diagnostics.ProcessStartInfo]::new($gitleaksExe)
  foreach ($a in 'stdin', '--no-banner', '--report-format', 'json', '--report-path', '-', '--exit-code', '3') { $start.ArgumentList.Add($a) }
  $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
  $scan = [Diagnostics.Process]::Start($start)
  $out = $scan.StandardOutput.ReadToEndAsync(); $null = $scan.StandardError.ReadToEndAsync()
  $stream.Position = 0; $stream.CopyTo($scan.StandardInput.BaseStream); $scan.StandardInput.Close()
  $scan.WaitForExit()
  $hits = Read-GitleaksReport $scan.ExitCode $out.Result 'the commits'' other published text'
  foreach ($f in $hits) {
    $at = [int]$f.StartLine
    $n = [Array]::BinarySearch($starts, $at); if ($n -lt 0) { $n = (-bnot $n) - 1 }
    $item = $secretQueue[[Math]::Max(0, $n)]
    if ($f.Secret) { $secrets.Add("$($f.Secret)") }
    $lineThere = $at - $starts[[Math]::Max(0, $n)] + 1
    $fingerprint = "$($item.Commit):$(if ($item.Path) { $item.Path } else { $item.Place }):$($f.RuleID):$lineThere"
    if ($ignored.Contains($fingerprint)) { continue }
    Add-Finding $item.Commit $item.Path "$(if ($item.Path) { $lineThere } else { $item.Place })" "secret ($($f.RuleID)) in $($item.What)" $fingerprint
  }
}
# Files whose content git leaves out of the patch, by git's own judgement (`diff-tree --numstat` gives "-" for them):
# its content test and attributes such as `*.png binary` alike. Those are piped to gitleaks whole.
foreach ($c in $outgoing) {
  # The commit object itself (its message, author and committer): a credential pasted into a message is in no patch.
  Find-SecretsIn (Get-GitBytes @('cat-file', 'commit', $c)) $c $null 'commit message or author' 'the commit itself'
  $parent = & git -C $repoRoot rev-parse --verify --quiet "$c^1" 2>$null
  $base = if ($parent) { @("$parent", $c) } else { @('--root', $c) }
  $numstat = [Text.Encoding]::UTF8.GetString((Get-GitBytes (@('diff-tree', '-z', '-r', '-M', '--numstat', '--no-commit-id') + $base))) -split "`0"
  $unshown = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  for ($n = 0; $n -lt $numstat.Count; $n++) {
    # Split at the first two tabs only: the rest is the path verbatim, a newline in it included.
    $cells = $numstat[$n] -split "`t", 3
    if ($cells.Count -ne 3) { continue }
    $dash = $cells[0] -eq '-'
    $name = if ($cells[2] -eq '') { $n += 2; $numstat[$n] } else { $cells[2] }
    if ($dash) { [void]$unshown.Add($name) }
  }
  $newPaths = [Collections.Generic.List[string]]::new()
  $tree = @('diff-tree', '-z', '-r', '-M', '--raw', '--no-commit-id') + $base
  $tokens = [Text.Encoding]::UTF8.GetString((Get-GitBytes $tree)) -split "`0"
  for ($i = 0; $i -lt $tokens.Count; $i++) {
    if (-not $tokens[$i].StartsWith(':')) { continue }
    $fields = $tokens[$i].Substring(1) -split ' '
    $path = if ($fields[4] -match '^[RC]') { $tokens[$i + 2]; $i += 2 } else { $tokens[$i + 1]; $i += 1 }
    if ($fields[4] -match '^D') { continue }
    [void]$newPaths.Add($path)
    if ($fields[1] -eq '160000') { continue }
    if ($unshown.Contains($path)) { Find-SecretsIn (Get-GitBytes @('cat-file', 'blob', $fields[3])) $c $path '' 'a file git does not diff' }
  }
  # The paths themselves are published text (a submodule entry's directory name included): one gitleaks pass over them.
  if ($newPaths.Count) { Find-SecretsIn ([Text.Encoding]::UTF8.GetBytes(($newPaths -join "`n"))) $c $null 'a file or folder name' 'a path it adds' }
}
# Ref names (a lightweight tag adds no object, so its name is the only thing it publishes).
foreach ($ref in $refNames) { Find-SecretsIn ([Text.Encoding]::UTF8.GetBytes($ref.Name)) $ref.Sha $null 'the name of the ref pushing this tip' 'its name' }
# What a tag's objects are walked without: in a push, everything the destination has; in a history scan, the commit
# the tag peels to, whose history the commit scan above already read, so only the tag chain and any tree or file a
# tag publishes directly are left.
function Get-TagExclusion($tag) {
  if (-not $history) { return $notKnown }
  $peeled = "$(& git -C $repoRoot rev-parse --verify --quiet "$($tag.Object)^{commit}" 2>$null)"
  if ($peeled) { @('--not', $peeled) } else { @() }
}
# Tag annotations, and the files a tag publishes without a commit.
foreach ($tag in $tags) {
  $peels = [bool]"$(& git -C $repoRoot rev-parse --verify --quiet "$($tag.Object)^{commit}" 2>$null)"
  foreach ($object in Invoke-Git (@('rev-list', '--objects', '--no-object-names', $tag.Object) + (Get-TagExclusion $tag))) {
    $type = (Invoke-Git @('cat-file', '-t', $object)) -join ''
    if ($type -eq 'tag') { Find-SecretsIn (Get-GitBytes @('cat-file', 'tag', $object)) $tag.Object $null 'a tag or other object being pushed' 'its annotation' }
    elseif ($type -eq 'blob' -and -not $peels) { Find-SecretsIn (Get-GitBytes @('cat-file', 'blob', $object)) $tag.Object $null 'a tag or other object being pushed' 'a file it publishes' }
    elseif ($type -eq 'tree' -and -not $peels) {
      # The tree's names, whole paths, are published too: the deny pass checks them below, and gitleaks here.
      $names = @(([Text.Encoding]::UTF8.GetString((Get-GitBytes @('ls-tree', '-r', '-t', '-z', '--full-tree', $object))) -split "`0") | Where-Object { $_ } | ForEach-Object { $_ -replace '^[^\t]*\t', '' })
      if ($names) { Find-SecretsIn ([Text.Encoding]::UTF8.GetBytes(($names -join "`n"))) $tag.Object $null 'a tag or other object being pushed' 'a path it publishes' }
    }
  }
}
if ($deny.Count) {
  # A destination name is published too: a branch or tag named with an entry is refused even with clean commits.
    # Each destination ref name as its own bytes (the third field of git's lines), when git gave them on standard input;
  # a deletion (an all-zero local object) publishes nothing, so removing a private name is never refused for it.
  # Each name is checked where it is cut out, never collected first: PowerShell unrolls a lone byte array, and a
  # one-ref push would then be checked byte by byte.
  # One check of every ref name, by its bytes: git's own when it gave them on standard input, else -Updates in UTF-8.
  if (-not $stdinBytes -and $Updates) { $stdinBytes = [Text.UTF8Encoding]::new($false).GetBytes(($Updates -join "`n")) }
  # A history scan has no ref lines: each ref it read (-All) is checked by its name as git lists it.
  if ($history) { foreach ($ref in $refNames) { if (Test-Deny $ref.Name) { Add-Finding $ref.Sha $null 'the name of the ref pushing this tip' 'deny-list entry (in its name)' } } }
  if ($stdinBytes) {
    foreach ($line in (Split-Bytes $stdinBytes 10)) {
      $fields = Split-Bytes $line 32
      if ($fields.Count -ge 4 -and [Text.Encoding]::ASCII.GetString($fields[1]) -ne $zero -and (Test-DenyBytes $fields[2])) { Add-Finding ([Text.Encoding]::ASCII.GetString($fields[1])) $null 'the name of the ref pushing this tip' 'deny-list entry (in its name)' }
    }
  }

  foreach ($tag in $tags) {
    if (Test-Deny $tag.Name) { Add-Finding $tag.Object $null 'a tag or other object being pushed' 'deny-list entry (in its name)'; continue }
    # Everything the tag sends that the destination lacks, as git itself lists it: the tag objects down its chain, and,
    # for a tag that does not peel to a commit, the trees, paths and files it publishes. Commits are checked above,
    # by what each adds, so only objects reached without a commit are read here.
    $sent = Invoke-Git (@('rev-list', '--objects', '--no-object-names', $tag.Object) + (Get-TagExclusion $tag))
    $peelsToCommit = [bool]"$(& git -C $repoRoot rev-parse --verify --quiet "$($tag.Object)^{commit}" 2>$null)"
    $hit = $false
    foreach ($object in $sent) {
      $type = (Invoke-Git @('cat-file', '-t', $object)) -join ''
      # The raw tag object under every encoding, as a commit's is: an annotation can be saved in Windows-1252.
      if ($type -eq 'tag') { $hit = @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'tag', $object)) | Where-Object { Test-Deny $_ }).Count -gt 0 }
      elseif (-not $peelsToCommit -and $type -eq 'tree') {
        # Every path under the tree, whole (an entry can hold a separator), read as raw NUL-separated records.
        $hit = @(foreach ($entry in (Split-Bytes (Get-GitBytes @('ls-tree', '-r', '-t', '-z', '--full-tree', $object)) 0)) { $tab = [Array]::IndexOf($entry, [byte]9); if ($tab -ge 0 -and $tab + 1 -lt $entry.Length -and (Test-DenyBytes ([byte[]]$entry[($tab + 1)..($entry.Length - 1)]))) { 1 } }).Count -gt 0
      }
      elseif (-not $peelsToCommit -and $type -eq 'blob') { $hit = @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'blob', $object)) | Where-Object { Test-Deny $_ }).Count -gt 0 }
      if ($hit) { break }
    }
    if ($hit) { Add-Finding $tag.Object $null 'a tag or other object being pushed' 'deny-list entry (in an annotation, or a file or path it publishes)' }
  }
}
Invoke-SecretScans
if ($failed.Count) {
  $lines = foreach ($f in $failed) {
    $hidden = $f.Path -and ((Test-Deny $f.Path) -or @($secrets | Where-Object { $_ -and $f.Path.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count)
    # Control characters (a newline is legal in a file name) are shown escaped, so a name cannot start a line of its own.
    $where = if (-not $f.Path) { $f.Place } elseif ($hidden) { 'a path' } else { [regex]::Replace($f.Path, '[\x00-\x1F\x7F]', { param($m) '\x{0:x2}' -f [int][char]$m.Value }) }
    if ($f.Path -and $f.Place) { $where = "${where}:$($f.Place)" }
    $lead = if ($f.Commit) { "$("$($f.Commit)".Substring(0, [Math]::Min(12, "$($f.Commit)".Length))): " } else { '' }
    $mark = if (-not $f.Fingerprint) { '' } elseif ($hidden) { ' (its .gitleaksignore fingerprint is withheld: its path is hidden)' } else { " (.gitleaksignore fingerprint: $($f.Fingerprint))" }
    "${lead}${where}: $($f.What)$mark"
  }
  $lines = @($lines | Select-Object -Unique)
  foreach ($line in $lines) { Say $line }
  Stop-Scan $(if ($history) { "$($lines.Count) finding(s) above in the scanned commits" } else { "$($lines.Count) finding(s) above; nothing was pushed. Remove them from the commits (rewrite before pushing), then push again" })
}
if (-not (Test-Path -LiteralPath $denyPath)) { Say "checked $($outgoing.Count) commit(s) for secrets; no deny list (.private/deny-list.txt), so private strings were not checked" }
# A scan outside a push names no temporary refs. One that cannot remove its own refs refuses: they would be left to
# the next scan, and a clean result is not claimed over a step that failed.
if (-not $history) {
  $script:cleaning = $true
  if (-not (Remove-ScanRefs $ownRefs)) { Stop-Scan "this scan's temporary refs under $scanRefs could not be removed; the next push removes them" }
}
exit 0
