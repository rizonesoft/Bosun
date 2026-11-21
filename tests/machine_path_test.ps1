# Tests scripts/machine-path-check.ps1 (D00 T05 §8) over scratch repositories with synthetic owner paths and a
# synthetic profile name: each form in a committed live file is found (exit 1), whatever the working tree holds, a
# symbolic link's target included; stamped TODO text, reviews, phase runs, sites and generic sample paths are not
# (exit 0); a contributor's own login is never taken for the owner's name; and a commit that cannot be read is an
# incomplete check (exit 2). The synthetic name never reaches the check's output.
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
$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'BOSUN_PROFILE_NAME', 'USERNAME', 'USER'
$saved = @{}; foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..2]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
git config --global user.name 'Path Test'; git config --global user.email 'path-test@example.invalid'; git config --global core.symlinks true
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
function Invoke-Check([string]$repo, [string]$profile = $profileName, [string[]]$more = @()) {
  if ($profile) { $env:BOSUN_PROFILE_NAME = $profile } else { Remove-Item -LiteralPath Env:BOSUN_PROFILE_NAME -ErrorAction Ignore }
  try { $said = (& $pwshExe -NoProfile -File $check -Repo $repo @more 2>&1) -join "`n"; $code = $LASTEXITCODE }
  finally { Remove-Item -LiteralPath Env:BOSUN_PROFILE_NAME -ErrorAction Ignore }
  [pscustomobject]@{ Code = $code; Said = $said }
}

try {
  # Every form, alone in a committed live file, is found, and the output never shows the synthetic name.
  $forms = [ordered]@{
    'a Windows checkout folder' = "see D:\$gh\tool\readme"
    'the checkout folder alone' = "cd D:\$gh"
    'a forward-slash checkout folder' = "see D:/$gh/tool"
    'a WSL checkout folder' = "cd /mnt/d/$gh/tool"
    'the WSL checkout folder alone' = "cd /mnt/d/$gh"
    'an encoded file URI' = "file:///d%3A/$gh/tool"
    'a checkout under a drive root' = "in D:\$bo now"
    'a WSL checkout under a drive root' = "in /mnt/c/$dm now"
    'a scratch drive' = "logs in R:\$sc\run"
    'the profile name' = "C:\Users\$profileName\AppData"
    'the WSL home' = "/home/$profileName/src"
  }
  foreach ($form in $forms.Keys) {
    $r = Invoke-Check (New-Repo @{ 'notes.txt' = "first line`n$($forms[$form])`n" })
    Check "$form in a committed file is found" ($r.Code -eq 1 -and $r.Said -match 'notes\.txt: line 2' -and -not $r.Said.Contains($profileName)) "($($r.Code): $($r.Said))"
  }
  # A file whose own path holds a match is found by its name and shown as "a path".
  $r = Invoke-Check (New-Repo @{ "$profileName-notes.txt" = 'clean' })
  Check 'a file named with the profile name is found by its name, shown as a path' ($r.Code -eq 1 -and $r.Said -match 'a path: its name' -and -not $r.Said.Contains($profileName)) "($($r.Said))"

  # What is published is checked: a path fixed only in the working tree is still found in the commit.
  $fixedLocally = New-Repo @{ 'notes.txt' = "see D:\$gh\tool`n" }
  [IO.File]::WriteAllText((Join-Path $fixedLocally 'notes.txt'), "see C:\src\tool`n")
  $r = Invoke-Check $fixedLocally
  Check 'a path fixed only in the working tree is still found in the commit' ($r.Code -eq 1 -and $r.Said -match 'notes\.txt: line 1') "($($r.Said))"
  git -C $fixedLocally commit -q -am fix
  $r = Invoke-Check $fixedLocally
  Check 'and once the fix is committed it is clean' ($r.Code -eq 0) "($($r.Said))"
  # A symbolic link is published as its target text, even when the target does not exist.
  $linked = Join-Path $work 'linked'
  git init -q $linked
  $made = $true
  try { New-Item -ItemType SymbolicLink -Path (Join-Path $linked 'link.txt') -Target "D:\$gh\nowhere\file.txt" -Force -ErrorAction Stop | Out-Null } catch { $made = $false }
  if ($made) {
    git -C $linked add -A; git -C $linked commit -q -m link
    $r = Invoke-Check $linked
    Check 'a symbolic link whose target holds a checkout path is found' ($r.Code -eq 1 -and $r.Said -match 'link\.txt: line 1') "($($r.Said))"
  } else { Check 'a symbolic link can be made for the link case' $false '(symbolic links need Developer Mode or an elevated shell)' }

  # A partly stamped TODO: the open section's match is found, the stamped one's is not.
  $todo = "# TODO`n`n## 1. Done one`n`nIt lived in D:\$gh\old.`n`n> **Verified:** 2026-01-01 | §1 | abc`n`n## 2. Open one`n`nStill in D:\$gh\new.`n"
  $r = Invoke-Check (New-Repo @{ 'todo/00-x/TODO-01-x.md' = $todo })
  Check 'an open section of a partly stamped TODO is found, its stamped section is not' ($r.Code -eq 1 -and $r.Said -match '(?m)TODO-01-x\.md: line 11$') "($($r.Said))"

  # Only history, stamped text, sites and generic samples: clean.
  $clean = @{
    'todo/00-x/TODO-01-x.md' = "# TODO`n`n## 1. Done`n`nD:\$gh\old and C:\Users\$profileName.`n`n> **Verified:** 2026-01-01 | §1 | abc`n"
    'docs/reviews/00-x/D00-T01-s1.md' = "quoted D:\$gh\x"
    'docs/phase-runs/2026-01-01-phase-0.md' = "ran in R:\$sc"
    'tests/sample.cs' = 'var p = @"C:\Windows\System32"; var q = "C:\work\app"; var r = "/mnt/c/src/app";'
    'README.md' = "See https://$($gh.ToLower()).com/org/repo and https://$($gh.ToLower()).com/org/$($bo.ToLower())."
  }
  $r = Invoke-Check (New-Repo $clean)
  Check 'stamped text, reviews, phase runs, sites and generic sample paths are clean' ($r.Code -eq 0 -and $r.Said -match 'clean') "($($r.Code): $($r.Said))"

  # A contributor's own login is never taken for the owner's name: with no profile_name set up, only the path forms
  # are checked, and the output says so.
  $env:USERNAME = 'dev'; $env:USER = 'dev'
  $r = Invoke-Check (New-Repo @{ 'notes.txt' = "a dev note, run by dev`n" }) ''
  Check 'with no profile_name set up a contributor''s login is not checked, and that is said' ($r.Code -eq 0 -and $r.Said -match 'no profile_name is set up') "($($r.Code): $($r.Said))"
  $r = Invoke-Check (New-Repo @{ 'notes.txt' = "cd D:\$gh`n" }) ''
  Check 'and the path forms are still checked' ($r.Code -eq 1) "($($r.Said))"

  # A push's commits: a path added in one and removed in the next is still found when both are listed.
  $pushed = New-Repo @{ 'base.txt' = 'clean' }
  [IO.File]::WriteAllText((Join-Path $pushed 'note.txt'), "built in D:\$gh\tool`n"); git -C $pushed add -A; git -C $pushed commit -q -m add
  $added = "$(git -C $pushed rev-parse HEAD)"
  git -C $pushed rm -q note.txt; git -C $pushed commit -q -m remove
  $removed = "$(git -C $pushed rev-parse HEAD)"
  $list = Join-Path $work 'pushed.txt'
  [IO.File]::WriteAllLines($list, [string[]]@($removed, $added))
  $r = Invoke-Check $pushed $profileName @('-CommitsFile', $list)
  Check 'a path added in one pushed commit and removed in the next is found' ($r.Code -eq 1 -and $r.Said -match 'note\.txt: line 1') "($($r.Code): $($r.Said))"
  [IO.File]::WriteAllLines($list, [string[]]@($removed))
  $r = Invoke-Check $pushed $profileName @('-CommitsFile', $list)
  Check 'and the removing commit alone adds nothing to find' ($r.Code -eq 0) "($($r.Code): $($r.Said))"

  # An overlay config that cannot be read is an incomplete check; a config without the key keeps contributor behaviour.
  $configured = New-Repo @{ 'a.txt' = 'clean' }
  New-Item -ItemType Directory -Path (Join-Path $configured '.private') | Out-Null
  [IO.File]::WriteAllText((Join-Path $configured '.private\config.toml'), "profile_name = `"one`"`nprofile_name = `"two`"`n")
  $r = Invoke-Check $configured ''
  Check 'an overlay config that cannot be read makes the check incomplete, naming it by its place in the checkout' ($r.Code -eq 2 -and $r.Said -match 'profile_name could not be read' -and $r.Said -match '\.private/config\.toml' -and -not $r.Said.Contains($work)) "($($r.Code): $($r.Said))"
  [IO.File]::WriteAllText((Join-Path $configured '.private\config.toml'), "vault_item = `"op://x/y/z`"`n")
  $r = Invoke-Check $configured ''
  Check 'and one without the key checks the path forms and says the name was not' ($r.Code -eq 0 -and $r.Said -match 'no profile_name is set up') "($($r.Code): $($r.Said))"

  # A commit that cannot be read is an incomplete check, never a clean one.
  $r = Invoke-Check (New-Repo @{ 'a.txt' = 'clean' }) $profileName @('-Commit', 'no-such-commit')
  Check 'a commit that cannot be read makes the check incomplete' ($r.Code -eq 2 -and $r.Said -match 'incomplete') "($($r.Code): $($r.Said))"
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
