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
$failed = [Collections.Generic.List[string]]::new()
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

# --- gitleaks over the outgoing commits ---
if ($outgoing.Count) {
  $report = Join-Path ([IO.Path]::GetTempPath()) ("bosun-leak-scan-" + [guid]::NewGuid().ToString('N') + '.json')
  try {
    $logOpts = (@('--diff-merges=first-parent') + $range) -join ' '
    & gitleaks git $repoRoot --log-opts $logOpts --redact --no-banner --report-format json --report-path $report --exit-code 3 *> $null
    $code = $LASTEXITCODE
    if ($code -ne 0 -and $code -ne 3) { Stop-Scan "gitleaks could not scan the outgoing commits (exit $code)" }
    try { $findings = @(Get-Content -LiteralPath $report -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop) }
    catch { Stop-Scan 'the gitleaks report could not be read' }
    if ($code -eq 3 -and -not $findings.Count) { Stop-Scan 'gitleaks reported a finding its report does not hold' }
    foreach ($f in $findings) { $failed.Add("$("$($f.Commit)".Substring(0, [Math]::Min(12, "$($f.Commit)".Length))): $($f.File):$($f.StartLine): secret ($($f.RuleID))") }
  } finally { Remove-Item -LiteralPath $report -ErrorAction Ignore }
}

# --- the private deny list ---
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
  foreach ($entry in $deny) { if ($lower.Contains($entry)) { return $true } }
  $false
}
function Show-Path([string]$path) { if (Test-Deny $path) { 'a path' } else { $path } }
if ($deny.Count -and $outgoing.Count) {
  # Added lines, with their new line numbers, and the paths they land in.
  $commit = $null; $path = $null; $lineNo = 0
  foreach ($line in Invoke-Git (@('log', '-p', '-U0', '--no-color', '--no-ext-diff', '--diff-merges=first-parent', '--format=commit %H') + $range)) {
    if ($line -match '^commit ([0-9a-f]{40})$') { $commit = $Matches[1]; $path = $null; continue }
    if ($line.StartsWith('+++ ')) { $path = if ($line -eq '+++ /dev/null') { $null } else { $line.Substring(4) -replace '^b/', '' }; continue }
    if ($line -match '^@@ -\S+ \+(\d+)') { $lineNo = [int]$Matches[1]; continue }
    if ($line.StartsWith('+') -and $path) {
      if (Test-Deny $line.Substring(1)) { $failed.Add("$($commit.Substring(0, 12)): $(Show-Path $path):${lineNo}: deny-list entry") }
      $lineNo++
    }
  }
  # Paths added or renamed to, and commit messages.
  $commit = $null
  foreach ($line in Invoke-Git (@('log', '--no-color', '--diff-merges=first-parent', '--name-status', '--format=commit %H') + $range)) {
    if ($line -match '^commit ([0-9a-f]{40})$') { $commit = $Matches[1]; continue }
    $fields = $line -split "`t"
    if ($fields.Count -ge 2 -and $fields[0] -match '^[ACR]') {
      if (Test-Deny $fields[-1]) { $failed.Add("$($commit.Substring(0, 12)): a path: deny-list entry (in a file name)") }
    }
  }
  foreach ($c in $outgoing) {
    $message = (Invoke-Git @('log', '-1', '--format=%B', $c)) -join "`n"
    if (Test-Deny $message) { $failed.Add("$($c.Substring(0, 12)): commit message: deny-list entry") }
  }
}
if ($deny.Count) {
  foreach ($tag in $tags) {
    $type = (Invoke-Git @('cat-file', '-t', $tag.Object)) -join ''
    $annotation = if ($type -eq 'tag') { (Invoke-Git @('cat-file', '-p', $tag.Object)) -join "`n" } else { '' }
    if ((Test-Deny $tag.Name) -or (Test-Deny $annotation)) { $failed.Add("$($tag.Object.Substring(0, 12)): a tag being pushed: deny-list entry (its name or annotation)") }
  }
}

if ($failed.Count) {
  foreach ($f in $failed | Select-Object -Unique) { Say $f }
  Stop-Scan "$(@($failed | Select-Object -Unique).Count) finding(s) above; nothing was pushed. Remove them from the commits (rewrite before pushing), then push again"
}
if (-not (Test-Path -LiteralPath $denyPath)) { Say "checked $($outgoing.Count) commit(s) for secrets; no deny list (.private/deny-list.txt), so private strings were not checked" }
exit 0
