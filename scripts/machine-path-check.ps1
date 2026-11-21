# Finds the owner's machine paths in the public files of a commit (D00 T05 §8): paths that show the owner's own
# machine, not the generic sample paths the app's tests need. Checked:
#   - the folder the owner's checkouts live in, as a Windows path, a WSL /mnt/<drive>/ path or a percent-encoded
#     file: URI, the folder alone or anything under it (<drive>:\GitHub, /mnt/<drive>/GitHub, <drive>%3A/GitHub);
#   - a checkout kept directly under a drive root (<drive>:\Bosun, <drive>:\DevMode, and their WSL spellings);
#   - the scratch drives' folder (<drive>:\scratch, /mnt/<drive>/scratch);
#   - the owner's Windows user-profile name and WSL home, as a whole word: `profile_name`, read through
#     Get-BosunOverlayValue (BOSUN_PROFILE_NAME, then the private overlay), never written into a public file. Without it
#     (a contributor's checkout) the path forms are still checked and the output says the name was not.
# What is read is what git publishes: every blob of the commit (-Commit, HEAD by default; scripts/leak-scan.ps1 passes
# each tip it pushes), a symbolic link as its target text, never the working tree. docs/reviews/ and docs/phase-runs/
# are history and skipped, and so is a stamped section of a todo/ file (one carrying a Verified: line), whole.
# Each finding prints the file and line numbers, never the matched text; a path that itself holds a match is "a path".
#
#   pwsh scripts/machine-path-check.ps1 [-Repo <checkout>] [-Commit <commit>]     every file of one commit
#   pwsh scripts/machine-path-check.ps1 -CommitsFile <file>                    the files a push's commits add or change
#
# Exit 0 when clean, 1 with findings, 2 when the check is incomplete (the commit or a blob cannot be read): an
# incomplete check is never reported clean.
[CmdletBinding()]
param(
  [string]$Repo = (Split-Path $PSScriptRoot),
  [string]$Commit = 'HEAD',
  # A push's commits and annotated tags, one per line in a file (scripts/leak-scan.ps1 writes it): each one's own text
  # (a message, an annotation) and the files each commit adds or changes are read, which is everything the push introduces, so a path added in one commit and removed in the next is
  # still found. The file keeps a long list off the command line.
  [string]$CommitsFile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')
$forms = @(
  # A drive letter stands alone (the "s:" of "https://" is no drive), and a folder name ends at a separator, a quote,
  # a sentence's full stop or the end, never at a dot that a name goes on after (github.com is a site).
  '(?<![A-Za-z0-9])[A-Za-z]:[\\/]+GitHub(?![\w-]|\.\w)', '(?<![\w.-])/mnt/[a-z]/GitHub(?![\w-]|\.\w)', '(?<![A-Za-z0-9])[A-Za-z]%3[Aa][\\/]+GitHub(?![\w-]|\.\w)',
  '(?<![A-Za-z0-9])[A-Za-z]:[\\/]+(Bosun|DevMode)(?![\w-]|\.\w)', '(?<![\w.-])/mnt/[a-z]/(Bosun|DevMode)(?![\w-]|\.\w)',
  '(?<![A-Za-z0-9])[A-Za-z]:[\\/]+scratch(?![\w-]|\.\w)', '(?<![\w.-])/mnt/[a-z]/scratch(?![\w-]|\.\w)'
)
# A setting that is not there (a contributor's checkout) leaves the name unchecked; a config that cannot be read is an
# incomplete check, never a quiet one.
try { $profileName = "$((Get-BosunOverlayValue -Key profile_name -Root $Repo).Value)".Trim() }
catch {
  # The reader names the file by its full path, which shows where the checkout is: it is named by its place in the
  # checkout instead.
  $why = "$($_.Exception.Message)".Replace([IO.Path]::GetFullPath((Join-Path $Repo '.private/config.toml')), '.private/config.toml')
  "machine-path-check: incomplete: the overlay's profile_name could not be read ($why)"; exit 2
}
if ($profileName) { $name = [regex]::Escape($profileName); $forms += "\b$name\b", "/home/$name\b" }
$pattern = [regex]::new('(?i)' + ($forms -join '|'))

# Git's own bytes, as git would publish them; standard input, when given, is written while the output is read.
function Invoke-GitBytes([string[]]$arguments, [byte[]]$feed = $null) {
  $start = [Diagnostics.ProcessStartInfo]::new('git')
  foreach ($a in @('-C', $Repo) + $arguments) { $start.ArgumentList.Add($a) }
  $start.RedirectStandardInput = $null -ne $feed; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false
  $process = [Diagnostics.Process]::Start($start)
  $err = $process.StandardError.ReadToEndAsync()
  # The output is read on .NET's own task while the input is written here, so neither pipe can fill and stall git.
  $buffer = [IO.MemoryStream]::new(); $reading = $process.StandardOutput.BaseStream.CopyToAsync($buffer)
  if ($null -ne $feed) { $process.StandardInput.BaseStream.Write($feed, 0, $feed.Length); $process.StandardInput.Close() }
  $reading.Wait(); $process.WaitForExit(); $null = $err.Result
  [pscustomobject]@{ Code = $process.ExitCode; Bytes = $buffer.ToArray() }
}
# The files to read: every blob of -Commit, or each file the commits in -CommitsFile add or change (against their
# first parent, a root commit against nothing), each blob once.
function Read-Records([byte[]]$bytes) {
  foreach ($record in ([Text.Encoding]::UTF8.GetString($bytes) -split "`0")) { if ($record) { $record } }
}
$seen = [Collections.Generic.HashSet[string]]::new()
$entries = [Collections.Generic.List[object]]::new()
function Add-Entry([string]$path, [string]$sha, [string]$type) {
  if ($type -notin 'blob', 'object' -or $sha -match '^0+$') { return }  # a submodule's commit, or a file the commit removes
  if ($path -like 'docs/reviews/*' -or $path -like 'docs/phase-runs/*') { return }
  if ($seen.Add("$sha $path")) { $entries.Add([pscustomobject]@{ Path = $path; Sha = $sha }) }
}
if ($CommitsFile) {
  $commits = @(try { [IO.File]::ReadAllLines($CommitsFile) | ForEach-Object { $_.Trim() } | Where-Object { $_ } } catch { "machine-path-check: incomplete: the list of commits could not be read"; exit 2 })
  $label = "the $($commits.Count) commit(s) pushed"
  foreach ($c in $commits) {
    # The object's own text is published too: a commit's message, an annotated tag's annotation.
    $kind = [Text.Encoding]::ASCII.GetString((Invoke-GitBytes @('cat-file', '-t', $c)).Bytes).Trim()
    if ($kind -eq 'tag') { Add-Entry "the annotation of tag $($c.Substring(0, [Math]::Min(12, $c.Length)))" $c 'object'; continue }
    if ($kind -ne 'commit') { "machine-path-check: incomplete: $c is not a commit or tag"; exit 2 }
    Add-Entry "the message of commit $($c.Substring(0, [Math]::Min(12, $c.Length)))" $c 'object'
    $diff = Invoke-GitBytes @('diff-tree', '-r', '-z', '--no-commit-id', '--root', '-m', '--first-parent', '--diff-filter=AMRCT', $c)
    if ($diff.Code) { "machine-path-check: incomplete: git could not read the changes of $c"; exit 2 }
    $records = @(Read-Records $diff.Bytes)
    for ($i = 0; $i -lt $records.Count; $i++) {
      if (-not $records[$i].StartsWith(':')) { continue }
      $fields = $records[$i].Substring(1) -split ' '
      $step = if ($fields[4] -match '^[RC]') { 2 } else { 1 }
      $path = $records[$i + $step]; $i += $step
      Add-Entry $path $fields[3] $(if ($fields[1] -eq '160000') { 'commit' } else { 'blob' })
    }
  }
} else {
  $label = $Commit
  $tree = Invoke-GitBytes @('ls-tree', '-r', '-z', '--full-tree', $Commit)
  if ($tree.Code) { "machine-path-check: incomplete: git could not read the files of $Commit"; exit 2 }
  foreach ($record in (Read-Records $tree.Bytes)) {
    $meta, $path = $record -split "`t", 2
    $mode, $type, $sha = $meta -split ' '
    Add-Entry $path $sha $type
  }
}
$found = 0
if ($entries.Count) {
  $batch = Invoke-GitBytes @('cat-file', '--batch') ([Text.Encoding]::ASCII.GetBytes((($entries | ForEach-Object { $_.Sha }) -join "`n") + "`n"))
  if ($batch.Code) { "machine-path-check: incomplete: git could not read the blobs of $label"; exit 2 }
  $bytes = $batch.Bytes; $at = 0
  foreach ($entry in $entries) {
    $shown = if ($pattern.IsMatch($entry.Path)) { 'a path' } else { $entry.Path }
    # Each object comes back as "<sha> blob <size>\n<content>\n".
    $eol = [Array]::IndexOf($bytes, [byte]10, $at)
    $header = if ($eol -ge 0) { [Text.Encoding]::ASCII.GetString($bytes, $at, $eol - $at) -split ' ' } else { @() }
    if ($header.Count -lt 3 -or $header[1] -notin 'blob', 'commit', 'tag') { "machine-path-check: incomplete: $shown could not be read"; exit 2 }
    $size = [int]$header[2]
    $text = [Text.Encoding]::UTF8.GetString($bytes, $eol + 1, $size).Replace([string][char]0, '')
    $at = $eol + 1 + $size + 1
    $lines = $text -split "`r?`n"
    $todo = $entry.Path -like 'todo/*' -and $entry.Path -like '*.md'
    $hits = [Collections.Generic.List[int]]::new()
    $section = [Collections.Generic.List[int]]::new(); $stamped = $false
    for ($n = 0; $n -le $lines.Count; $n++) {
      $end = $n -eq $lines.Count
      if ($todo -and ($end -or $lines[$n] -match '^## ')) { if (-not $stamped) { $hits.AddRange($section) }; $section.Clear(); $stamped = $false }
      if ($end) { break }
      if ($todo -and $lines[$n] -match '^> \*\*Verified:\*\*') { $stamped = $true }
      if ($pattern.IsMatch($lines[$n])) { if ($todo) { $section.Add($n + 1) } else { $hits.Add($n + 1) } }
    }
    $named = $pattern.IsMatch($entry.Path)
    if ($hits.Count -or $named) {
      $found++
      $where = @(); if ($named) { $where += 'its name' }; if ($hits.Count) { $where += "line $($hits -join ', ')" }
      "machine-path-check: ${shown}: $($where -join '; ')"
    }
  }
}
$note = if ($profileName) { '' } else { '; no profile_name is set up (BOSUN_PROFILE_NAME or the private overlay), so the owner''s profile name was not checked' }
if ($found) { "machine-path-check: $found file(s) of $label hold the owner's machine paths$note"; exit 1 }
"machine-path-check: clean ($($entries.Count) files of $label, history excluded$note)"
exit 0
