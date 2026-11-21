# Tests scripts/machine-path-check.ps1 (D00 T05 §8) over scratch repositories with synthetic owner paths and a
# synthetic profile name: each form in a live file is found (exit 1), stamped TODO text, reviews, phase runs and
# generic sample paths are not (exit 0), and an unreadable file or no profile name is an incomplete check (exit 2).
# The synthetic name never reaches the check's output.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$check = Join-Path $repoRoot 'scripts\machine-path-check.ps1'
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-machine-path-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'USERNAME', 'USER'
$saved = @{}; foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..2]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
git config --global user.name 'Path Test'; git config --global user.email 'path-test@example.invalid'
$pwshExe = (Get-Process -Id $PID).Path
$profileName = 'zqprofile'
# Every owner-path form is built at run time, so this file holds none of them itself (the check reads it too).
$gh = 'Git' + 'Hub'; $sc = 'scr' + 'atch'; $bo = 'Bos' + 'un'; $dm = 'Dev' + 'Mode'
$n = 0
function New-Repo([hashtable]$files) {
  $script:n++
  $repo = Join-Path $work "repo-$($script:n)"
  git init -q $repo
  foreach ($path in $files.Keys) {
    $full = Join-Path $repo $path
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    [IO.File]::WriteAllText($full, $files[$path])
  }
  git -C $repo add -A; git -C $repo commit -q -m files
  $repo
}
function Invoke-Check([string]$repo, [string]$user = $profileName) {
  $env:USERNAME = $user; Remove-Item -LiteralPath Env:USER -ErrorAction Ignore
  try { $said = (& $pwshExe -NoProfile -File $check -Repo $repo 2>&1) -join "`n"; $code = $LASTEXITCODE } finally { $env:USERNAME = $saved['USERNAME'] }
  [pscustomobject]@{ Code = $code; Said = $said }
}

try {
  # Every form, alone in a live file, is found, and the output never shows the synthetic name.
  $forms = [ordered]@{
    'a Windows checkout folder' = "see D:\$gh\tool\readme"
    'a forward-slash checkout folder' = "see D:/$gh/tool"
    'a WSL checkout folder' = "cd /mnt/d/$gh/tool"
    'an encoded file URI' = "file:///d%3A/$gh/tool"
    'a checkout under a drive root' = "in D:\$bo now"
    'a WSL checkout under a drive root' = "in /mnt/c/$dm now"
    'a scratch drive' = "logs in R:\$sc\run"
    'the profile name' = "C:\Users\$profileName\AppData"
    'the WSL home' = "/home/$profileName/src"
  }
  foreach ($form in $forms.Keys) {
    $r = Invoke-Check (New-Repo @{ 'notes.txt' = "first line`n$($forms[$form])`n" })
    Check "$form in a live file is found" ($r.Code -eq 1 -and $r.Said -match 'notes\.txt: line 2' -and -not $r.Said.Contains($profileName)) "($($r.Code): $($r.Said))"
  }
  # A file whose own path holds the name is shown as "a path".
  $r = Invoke-Check (New-Repo @{ "$profileName-notes.txt" = 'clean' + "`nD:\$gh\x`n" })
  Check 'a file named with the profile name is shown as a path' ($r.Code -eq 1 -and $r.Said -match 'a path: line 2' -and -not $r.Said.Contains($profileName)) "($($r.Said))"

  # A partly stamped TODO: the open section's match is found, the stamped one's is not.
  $todo = "# TODO`n`n## 1. Done one`n`nIt lived in D:\$gh\old.`n`n> **Verified:** 2026-01-01 | §1 | abc`n`n## 2. Open one`n`nStill in D:\$gh\new.`n"
  $r = Invoke-Check (New-Repo @{ 'todo/00-x/TODO-01-x.md' = $todo })
  Check 'an open section of a partly stamped TODO is found, its stamped section is not' ($r.Code -eq 1 -and $r.Said -match '(?m)TODO-01-x\.md: line 11$') "($($r.Said))"

  # Only history, stamped text and generic samples: clean.
  $clean = @{
    'todo/00-x/TODO-01-x.md' = "# TODO`n`n## 1. Done`n`nD:\$gh\old and C:\Users\$profileName.`n`n> **Verified:** 2026-01-01 | §1 | abc`n"
    'docs/reviews/00-x/D00-T01-s1.md' = "quoted D:\$gh\x"
    'docs/phase-runs/2026-01-01-phase-0.md' = "ran in R:\$sc"
    'tests/sample.cs' = 'var p = @"C:\Windows\System32"; var q = "C:\work\app"; var r = "/mnt/c/src/app";'
  }
  $r = Invoke-Check (New-Repo $clean)
  Check 'stamped text, reviews, phase runs and generic sample paths are clean' ($r.Code -eq 0 -and $r.Said -match 'clean') "($($r.Code): $($r.Said))"

  # An incomplete check is never clean.
  $held = New-Repo @{ 'held.txt' = 'clean' }
  $lock = [IO.File]::Open((Join-Path $held 'held.txt'), 'Open', 'Read', 'None')
  try { $r = Invoke-Check $held } finally { $lock.Dispose() }
  Check 'a file that cannot be read makes the check incomplete' ($r.Code -eq 2 -and $r.Said -match 'incomplete') "($($r.Code): $($r.Said))"
  $r = Invoke-Check (New-Repo @{ 'a.txt' = 'clean' }) ''
  Check 'no profile name to check makes it incomplete' ($r.Code -eq 2 -and $r.Said -match 'no USERNAME or USER') "($($r.Code): $($r.Said))"
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
