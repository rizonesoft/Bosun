# Finds the owner's machine paths in live public files (D00 T05 §8): paths that show the owner's own machine, not the
# generic sample paths the app's tests need. Checked:
#   - the folder the owner's checkouts live in, as a Windows path, a WSL /mnt/<drive>/ path or a percent-encoded
#     file: URI (<drive>:\GitHub\, /mnt/<drive>/GitHub/, <drive>%3A/GitHub);
#   - a checkout kept directly under a drive root (<drive>:\Bosun, <drive>:\DevMode, and their WSL spellings);
#   - the scratch drives' folder (<drive>:\scratch, /mnt/<drive>/scratch);
#   - the owner's Windows user-profile name and WSL home, read at run time from USERNAME (USER on Linux) and never written into a
#     public file.
# Every tracked file is read except docs/reviews/ and docs/phase-runs/ (history), and a stamped section of a todo/
# file (one carrying a Verified: line) is skipped whole: it is history too. Each finding prints the file and line
# numbers, never the matched text, and a path that itself holds the profile name is shown as "a path".
#
#   pwsh scripts/machine-path-check.ps1 [-Repo <checkout>]
#
# Exit 0 when clean, 1 with findings, 2 when the check is incomplete (no profile name to check, or a file it cannot
# read): an incomplete check is never reported clean.
[CmdletBinding()]
param([string]$Repo = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
# Windows names the profile in USERNAME, Linux and WSL in USER.
$profileName = "$(if ($env:USERNAME) { $env:USERNAME } else { $env:USER })".Trim()
if (-not $profileName) { 'machine-path-check: incomplete: no USERNAME or USER to check the files against'; exit 2 }
$name = [regex]::Escape($profileName)
$forms = @(
  '[A-Za-z]:[\\/]+GitHub[\\/]', '/mnt/[a-z]/GitHub/', '[A-Za-z]%3[Aa][\\/]+GitHub',
  '[A-Za-z]:[\\/]+(Bosun|DevMode)\b', '/mnt/[a-z]/(Bosun|DevMode)\b',
  '[A-Za-z]:[\\/]+scratch\b', '/mnt/[a-z]/scratch\b',
  $name, "/home/$name\b"
)
$pattern = [regex]::new('(?i)' + ($forms -join '|'))
$files = @(& git -C $Repo ls-files -z 2>$null | ForEach-Object { $_ -split "`0" } | Where-Object { $_ })
if ($LASTEXITCODE) { 'machine-path-check: incomplete: git could not list the tracked files'; exit 2 }
$found = 0; $unread = 0
foreach ($file in $files) {
  if ($file -like 'docs/reviews/*' -or $file -like 'docs/phase-runs/*') { continue }
  $full = Join-Path $Repo $file
  if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }  # tracked but deleted in the working tree
  try { $lines = [IO.File]::ReadAllLines($full) } catch { "machine-path-check: incomplete: $(if ($pattern.IsMatch($file)) { 'a path' } else { $file }) could not be read"; $unread++; continue }
  $stampedTodo = $file -like 'todo/*' -and $file -like '*.md'
  $hits = [Collections.Generic.List[int]]::new()
  $section = [Collections.Generic.List[int]]::new(); $stamped = $false
  $flush = { if (-not $stamped) { foreach ($n in $section) { $hits.Add($n) } }; $section.Clear(); $script:stamped = $false }
  for ($n = 0; $n -lt $lines.Count; $n++) {
    if ($stampedTodo -and $lines[$n] -match '^## ') { & $flush; $stamped = $false }
    if ($stampedTodo -and $lines[$n] -match '^> \*\*Verified:\*\*') { $stamped = $true }
    if ($pattern.IsMatch($lines[$n])) { if ($stampedTodo) { $section.Add($n + 1) } else { $hits.Add($n + 1) } }
  }
  if ($stampedTodo) { & $flush }
  if ($hits.Count) {
    $found++
    "machine-path-check: $(if ($pattern.IsMatch($file)) { 'a path' } else { $file }): line $($hits -join ', ')"
  }
}
if ($unread) { "machine-path-check: incomplete: $unread file(s) could not be read"; exit 2 }
if ($found) { "machine-path-check: $found file(s) hold the owner's machine paths"; exit 1 }
"machine-path-check: clean ($($files.Count) tracked files, history excluded)"
exit 0
