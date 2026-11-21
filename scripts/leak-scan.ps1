# Refuses a push that would publish a secret or a private string (D00 T05 §4). scripts/hooks/pre-push runs it with
# the refs git is about to push on standard input:
#
#   pwsh scripts/leak-scan.ps1 -Remote <name or URL>          the pre-push ref lines on standard input
#   pwsh scripts/leak-scan.ps1 -Remote <name or URL> -Updates '<local ref> <local sha> <remote ref> <remote sha>', ...
#
# The outgoing commits come from the destination itself: `git ls-remote` gives its current tips, and every commit
# being pushed that is not reachable from one of them is outgoing (a local tracking ref is never trusted). They are
# scanned twice:
#   - by gitleaks (8.30.1, pinned in the README), merges diffed against their first parent so a resolution is covered;
#   - against .private/deny-list.txt (the private overlay): every non-empty line not starting with # is a string,
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
  [string[]]$Updates
)
$ErrorActionPreference = 'Stop'
if (-not $Remote) { [Console]::Error.WriteLine('leak-scan: push refused: no remote was given to check against'); exit 1 }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path $PSScriptRoot
$zero = '0' * 40
$failed = [Collections.Generic.List[object]]::new()
function Say([string]$text) { [Console]::Error.WriteLine("leak-scan: $text") }
function Stop-Scan([string]$why) { Say "push refused: $why"; exit 1 }
function Invoke-Git([string[]]$arguments) {
  $out = & git -C $repoRoot @arguments 2>$null
  if ($LASTEXITCODE) { Stop-Scan "git $($arguments[0]) failed, so the outgoing commits could not be read" }
  @($out | ForEach-Object { "$_" })
}

if (-not (Get-Command gitleaks -CommandType Application -ErrorAction Ignore)) {
  $remedy = if ($IsWindows) { 'winget install --id Gitleaks.Gitleaks -e --version 8.30.1 --scope user' }
            else { 'download gitleaks_8.30.1_linux_x64.tar.gz from https://github.com/gitleaks/gitleaks/releases/tag/v8.30.1, check it against gitleaks_8.30.1_checksums.txt, and put gitleaks on PATH' }
  Stop-Scan "gitleaks is not installed, so the push cannot be checked for secrets: $remedy"
}

if ($null -eq $Updates) { $Updates = @([Console]::In.ReadToEnd() -split "`r?`n" | Where-Object { $_.Trim() }) }
$locals = [Collections.Generic.List[string]]::new()
$tags = [Collections.Generic.List[object]]::new()
$refNames = [Collections.Generic.List[string]]::new()
foreach ($line in $Updates) {
  $parts = $line.Trim() -split '\s+'
  if ($parts.Count -ne 4 -or $parts[1] -notmatch '^[0-9a-f]{40}$') { Stop-Scan 'a pushed ref could not be read' }
  if ($parts[1] -eq $zero) { continue }  # a deletion publishes nothing
  $refNames.Add($parts[2])
  # Every pushed object that is not itself a commit (a tag, or a file or tree under any ref, refs/archive/... included)
  # goes through the object pass below, whatever namespace it is pushed to.
  $type = "$(& git -C $repoRoot cat-file -t $parts[1] 2>$null)"
  if ($type -ne 'commit') { $tags.Add([pscustomobject]@{ Name = ($parts[2] -replace '^refs/(tags|heads)/', ''); Object = $parts[1] }) }
  # A tag can point at a file or a tree, not only a commit: only what peels to a commit has commits to scan.
  $commit = "$(& git -C $repoRoot rev-parse --verify --quiet "$($parts[1])^{commit}" 2>$null)"
  if ($commit) { $locals.Add($commit) }
}
if (-not $locals.Count -and -not $tags.Count) { exit 0 }

# What the destination already has, asked of the destination.
# A remote given as an address (a push to a bare URL) is named for git through its environment configuration, so the
# address, which can hold a credential, is never on a command line.
$remoteName = $Remote
if (-not ((& git -C $repoRoot remote 2>$null) -contains $Remote)) {
  $env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = 'remote.bosun-leak-scan.url'; $env:GIT_CONFIG_VALUE_0 = $Remote
  $remoteName = 'bosun-leak-scan'
}
$listed = & git -C $repoRoot ls-remote $remoteName 2>$null
if ($LASTEXITCODE) { Stop-Scan 'the remote could not be listed, so the commits it lacks are not known' }
$known = @($listed | ForEach-Object { ("$_" -split '\s+')[0] } | Where-Object { $_ -match '^[0-9a-f]{40}$' } | Sort-Object -Unique |
  Where-Object { & git -C $repoRoot cat-file -e "$_^{commit}" 2>$null; -not $LASTEXITCODE })
$range = @($locals) + $(if ($known) { @('--not') + $known } else { @() })
$outgoing = if ($locals.Count) { @(Invoke-Git (@('rev-list') + $range)) } else { @() }

# --- the private deny list, read first so no finding of either pass prints a path holding an entry ---
$denyPath = Join-Path $repoRoot '.private' 'deny-list.txt'
$deny = @()
if (Test-Path -LiteralPath $denyPath) {
  try { $text = [IO.File]::ReadAllText($denyPath, [Text.UTF8Encoding]::new($false, $true)) }
  catch { Stop-Scan 'the deny list (.private/deny-list.txt) exists but could not be read' }
  $deny = @($text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $_.ToLowerInvariant() })
}
function Test-Deny([string]$value) {
  if (-not $value) { return $false }
  $lower = $value.ToLowerInvariant()
  foreach ($entry in $deny) { if ($lower.Contains($entry, [StringComparison]::Ordinal)) { return $true } }
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
  $encodings = [Text.Encoding]::UTF8, [Text.Encoding]::GetEncoding(1252), [Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode, [Text.Encoding]::UTF32, [Text.UTF32Encoding]::new($true, $false)
  foreach ($encoding in $encodings) { $encoding.GetString($bytes).Replace([string][char]0, '').Replace([string][char]0xFEFF, '') }
}

# --- gitleaks over the outgoing commits ---
# Findings are kept as parts (commit, path, place, what) and printed only at the end, once every secret matched is
# known, so no path shown anywhere holds any of them or a deny-list entry.
$secrets = [Collections.Generic.List[string]]::new()
function Add-Finding([string]$commit, [string]$path, [string]$place, [string]$what) { $failed.Add([pscustomobject]@{ Commit = $commit; Path = $path; Place = $place; What = $what }) }
if ($outgoing.Count) {
  $logOpts = (@('--diff-merges=first-parent') + $range) -join ' '
  # The report comes back on standard output, never a file: it is not redacted, so each matched secret can be kept
  # from every path shown, and an unredacted copy never rests on disk.
  $json = & gitleaks git $repoRoot --log-opts $logOpts --no-banner --report-format json --report-path - --exit-code 3 2>$null
  $code = $LASTEXITCODE
  if ($code -ne 0 -and $code -ne 3) { Stop-Scan "gitleaks could not scan the outgoing commits (exit $code)" }
  try { $findings = @(($json -join "`n") | ConvertFrom-Json -ErrorAction Stop) }
  catch { Stop-Scan 'the gitleaks report could not be read' }
  if ($code -eq 3 -and -not $findings.Count) { Stop-Scan 'gitleaks reported a finding its report does not hold' }
  foreach ($f in $findings) {
    if ($f.Secret) { $secrets.Add("$($f.Secret)") }
    if ($f.Match) { $secrets.Add("$($f.Match)") }
    Add-Finding "$($f.Commit)" "$($f.File)" "$($f.StartLine)" "secret ($($f.RuleID))"
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
    $tokens = [Text.Encoding]::UTF8.GetString((Get-GitBytes $tree)) -split "`0"
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
      $newReadings = @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'blob', $fields[3])))
      # No prior blob for an added file, or for one that replaces a submodule entry (whose old object is a commit).
      $oldReadings = @(if ($fields[2] -match '^0+$' -or $fields[0] -eq '160000') { } else { ConvertFrom-Blob (Get-GitBytes @('cat-file', 'blob', $fields[2])) })
      # Each reading of the new file is compared with the same reading of the old one (the same decoding), never with
      # their sum, which counts one line once per reading that shows it.
      $reported = $false
      for ($k = 0; $k -lt $newReadings.Count; $k++) {
        $reading = $newReadings[$k]
        $had = @{}
        $oldReading = if ($oldReadings.Count -eq $newReadings.Count) { $oldReadings[$k] } elseif ($oldReadings.Count) { $oldReadings[0] } else { '' }
        foreach ($text in ($oldReading.ToLowerInvariant() -split "`r?`n")) { if (Test-Deny $text) { $had[$text] = 1 + [int]$had[$text] } }
        $seen = @{}
        $number = 0
        foreach ($text in ($reading.ToLowerInvariant() -split "`r?`n")) {
          $number++
          if (-not (Test-Deny $text)) { continue }
          $seen[$text] = 1 + [int]$seen[$text]
          if ($seen[$text] -gt [int]$had[$text]) { Add-Finding $c $path "$number" 'deny-list entry'; $reported = $true; break }
        }
        if ($reported) { break }
      }
    }
    # The whole commit object as published: its author and committer names and addresses as well as its message.
    # The raw commit object, read under every encoding as a file is, and    # Git itself re-encodes by the commit's encoding header (Windows-1250, Shift-JIS or any other it knows), so its UTF-8
    # rendering of the message and identities is read too.
    $asGit = [Text.Encoding]::UTF8.GetString((Get-GitBytes @('log', '-1', '--encoding=UTF-8', '--format=%an%n%ae%n%cn%n%ce%n%B', $c)))
    if ((Test-Deny $asGit) -or @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'commit', $c)) | Where-Object { Test-Deny $_ }).Count) { Add-Finding $c $null 'commit message or author' 'deny-list entry' }
  }
}
# --- gitleaks over files a tag publishes without a commit (a tag on a file or a tree) ---
foreach ($tag in $tags) {
  if ("$(& git -C $repoRoot rev-parse --verify --quiet "$($tag.Object)^{commit}" 2>$null)") { continue }
  foreach ($object in Invoke-Git (@('rev-list', '--objects', '--no-object-names', $tag.Object) + $(if ($known) { @('--not') + $known } else { @() }))) {
    if (((Invoke-Git @('cat-file', '-t', $object)) -join '') -ne 'blob') { continue }
    $start = [Diagnostics.ProcessStartInfo]::new('gitleaks')
    foreach ($a in 'stdin', '--no-banner', '--report-format', 'json', '--report-path', '-', '--exit-code', '3') { $start.ArgumentList.Add($a) }
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
    $scan = [Diagnostics.Process]::Start($start)
    $out = $scan.StandardOutput.ReadToEndAsync(); $null = $scan.StandardError.ReadToEndAsync()
    $bytes = Get-GitBytes @('cat-file', 'blob', $object)
    $scan.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $scan.StandardInput.Close()
    $scan.WaitForExit()
    if ($scan.ExitCode -ne 0 -and $scan.ExitCode -ne 3) { Stop-Scan "gitleaks could not scan a file the tag publishes (exit $($scan.ExitCode))" }
    if ($scan.ExitCode -eq 3) {
      try { $hits = @($out.Result | ConvertFrom-Json -ErrorAction Stop) } catch { Stop-Scan 'the gitleaks report could not be read' }
      foreach ($f in $hits) { if ($f.Secret) { $secrets.Add("$($f.Secret)") }; Add-Finding $tag.Object $null 'a tag or other object being pushed' "secret ($($f.RuleID)) in a file it publishes" }
    }
  }
}
if ($deny.Count) {
  # A destination name is published too: a branch or tag named with an entry is refused even with clean commits.
  foreach ($name in $refNames) { if (Test-Deny $name) { Add-Finding '' $null 'a ref being pushed' 'deny-list entry (in its name)' } }
  foreach ($tag in $tags) {
    if (Test-Deny $tag.Name) { Add-Finding $tag.Object $null 'a tag or other object being pushed' 'deny-list entry (in its name)'; continue }
    # Everything the tag sends that the destination lacks, as git itself lists it: the tag objects down its chain, and,
    # for a tag that does not peel to a commit, the trees, paths and files it publishes. Commits are checked above,
    # by what each adds, so only objects reached without a commit are read here.
    $sent = Invoke-Git (@('rev-list', '--objects', '--no-object-names', $tag.Object) + $(if ($known) { @('--not') + $known } else { @() }))
    $peelsToCommit = [bool]"$(& git -C $repoRoot rev-parse --verify --quiet "$($tag.Object)^{commit}" 2>$null)"
    $hit = $false
    foreach ($object in $sent) {
      $type = (Invoke-Git @('cat-file', '-t', $object)) -join ''
      if ($type -eq 'tag') { $hit = Test-Deny ((Invoke-Git @('cat-file', '-p', $object)) -join "`n") }
      elseif (-not $peelsToCommit -and $type -eq 'tree') {
        # Every path under the tree, whole (an entry can hold a separator), read as raw NUL-separated records.
        $hit = @(([Text.Encoding]::UTF8.GetString((Get-GitBytes @('ls-tree', '-r', '-t', '-z', '--full-tree', $object))) -split "`0") | Where-Object { $_ -and (Test-Deny ($_ -replace '^[^\t]*\t', '')) }).Count -gt 0
      }
      elseif (-not $peelsToCommit -and $type -eq 'blob') { $hit = @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'blob', $object)) | Where-Object { Test-Deny $_ }).Count -gt 0 }
      if ($hit) { break }
    }
    if ($hit) { Add-Finding $tag.Object $null 'a tag or other object being pushed' 'deny-list entry (in an annotation, or a file or path it publishes)' }
  }
}
if ($failed.Count) {
  $lines = foreach ($f in $failed) {
    $hidden = $f.Path -and ((Test-Deny $f.Path) -or @($secrets | Where-Object { $_ -and $f.Path.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count)
    # Control characters (a newline is legal in a file name) are shown escaped, so a name cannot start a line of its own.
    $where = if (-not $f.Path) { $f.Place } elseif ($hidden) { 'a path' } else { [regex]::Replace($f.Path, '[\x00-\x1F\x7F]', { param($m) '\x{0:x2}' -f [int][char]$m.Value }) }
    if ($f.Path -and $f.Place) { $where = "${where}:$($f.Place)" }
    $lead = if ($f.Commit) { "$("$($f.Commit)".Substring(0, [Math]::Min(12, "$($f.Commit)".Length))): " } else { '' }
    "${lead}${where}: $($f.What)"
  }
  $lines = @($lines | Select-Object -Unique)
  foreach ($line in $lines) { Say $line }
  Stop-Scan "$($lines.Count) finding(s) above; nothing was pushed. Remove them from the commits (rewrite before pushing), then push again"
}
if (-not (Test-Path -LiteralPath $denyPath)) { Say "checked $($outgoing.Count) commit(s) for secrets; no deny list (.private/deny-list.txt), so private strings were not checked" }
exit 0
