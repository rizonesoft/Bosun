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
  [Parameter(Mandatory)][string]$Remote,
  [string[]]$Updates
)
$ErrorActionPreference = 'Stop'
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
foreach ($line in $Updates) {
  $parts = $line.Trim() -split '\s+'
  if ($parts.Count -ne 4 -or $parts[1] -notmatch '^[0-9a-f]{40}$') { Stop-Scan 'a pushed ref could not be read' }
  if ($parts[1] -eq $zero) { continue }  # a deletion publishes nothing
  if ($parts[2] -like 'refs/tags/*') { $tags.Add([pscustomobject]@{ Name = $parts[2].Substring(10); Object = $parts[1] }) }
  $commit = (Invoke-Git @('rev-parse', '--verify', '--quiet', "$($parts[1])^{commit}")) -join ''
  if ($commit) { $locals.Add($commit) }
}
if (-not $locals.Count -and -not $tags.Count) { exit 0 }

# What the destination already has, asked of the destination.
$listed = & git -C $repoRoot ls-remote $Remote 2>$null
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
  if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { return [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2) }
  if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) { return [Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2) }
  $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
  # No byte-order mark but NUL bytes: each reading is returned on its own, so a line is counted within the one that holds it.
  if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { return @($utf8, [Text.Encoding]::Unicode.GetString($bytes), [Text.Encoding]::BigEndianUnicode.GetString($bytes)) }
  $utf8
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
      $oldReadings = if ($fields[2] -match '^0+$' -or $fields[0] -eq '160000') { @() } else { @(ConvertFrom-Blob (Get-GitBytes @('cat-file', 'blob', $fields[2]))) }
      $had = @{}
      foreach ($reading in $oldReadings) {
        foreach ($text in ($reading.ToLowerInvariant() -split "`r?`n")) { if (Test-Deny $text) { $had[$text] = 1 + [int]$had[$text] } }
      }
      $reported = $false
      foreach ($reading in $newReadings) {
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
    $message = (Invoke-Git @('log', '-1', '--format=%B', $c)) -join "`n"
    if (Test-Deny $message) { Add-Finding $c $null 'commit message' 'deny-list entry' }
  }
}
if ($deny.Count) {
  foreach ($tag in $tags) {
    # A tag can point at another tag, which is published with it: every annotation down the chain is checked.
    $object = $tag.Object; $hit = Test-Deny $tag.Name; $depth = 0
    while (-not $hit -and $depth -lt 50 -and ((Invoke-Git @('cat-file', '-t', $object)) -join '') -eq 'tag') {
      $body = (Invoke-Git @('cat-file', '-p', $object)) -join "`n"
      $hit = Test-Deny $body
      $object = (($body -split "`n" | Where-Object { $_ -match '^object [0-9a-f]{40}$' } | Select-Object -First 1) -replace '^object ', '')
      $depth++
    }
    if ($hit) { Add-Finding $tag.Object $null 'a tag being pushed' 'deny-list entry (its name or an annotation it carries)' }
  }
}

if ($failed.Count) {
  $lines = foreach ($f in $failed) {
    $hidden = $f.Path -and ((Test-Deny $f.Path) -or @($secrets | Where-Object { $_ -and $f.Path.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count)
    $where = if (-not $f.Path) { $f.Place } elseif ($hidden) { 'a path' } else { $f.Path }
    if ($f.Path -and $f.Place) { $where = "${where}:$($f.Place)" }
    "$("$($f.Commit)".Substring(0, [Math]::Min(12, "$($f.Commit)".Length))): ${where}: $($f.What)"
  }
  $lines = @($lines | Select-Object -Unique)
  foreach ($line in $lines) { Say $line }
  Stop-Scan "$($lines.Count) finding(s) above; nothing was pushed. Remove them from the commits (rewrite before pushing), then push again"
}
if (-not (Test-Path -LiteralPath $denyPath)) { Say "checked $($outgoing.Count) commit(s) for secrets; no deny list (.private/deny-list.txt), so private strings were not checked" }
exit 0
