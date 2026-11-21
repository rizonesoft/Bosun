# Tests how agents get the overlay's owner-only rules (D00 T05 §5): the public rule files carry the pointer and no
# private reference or machine path, and Update-BosunAgentRules (scripts/lib/overlay.ps1) keeps the ignored
# CLAUDE.local.md importing .private/AGENTS.md exactly while .private is the marked overlay holding it, through its own
# calls and through setup, the post-merge hook and sync, in temporary checkouts.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-agent-rules-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# --- the tracked rule files ---
$agents = [IO.File]::ReadAllText((Join-Path $repoRoot 'AGENTS.md'))
$claude = [IO.File]::ReadAllText((Join-Path $repoRoot 'CLAUDE.md'))
Check 'AGENTS.md points every agent at .private/AGENTS.md, behind the overlay mark on a real folder' ($agents -match '\.private/AGENTS\.md' -and $agents -match 'bosun\.overlay' -and $agents -match 'not a link or junction' -and $agents -match 'linked worktree')
foreach ($file in 'GEMINI.md', 'CODEX.md') {
  $text = [IO.File]::ReadAllText((Join-Path $repoRoot $file))
  Check "$file defers to AGENTS.md and leaves the pointer to it" ($text -match '\(\./AGENTS\.md\)|`AGENTS\.md`|\bAGENTS\.md\b' -and $text -notmatch '\.private/AGENTS\.md')
}
# A machine path: a drive-rooted folder (C:\Users\..., D:/...), or a home folder on Linux or under WSL's /mnt.
$machinePath = '(?i)\b[A-Z]:[\\/][^\s`)]|/home/[a-z_][a-z0-9_-]*/|/mnt/[a-z]/users/'
foreach ($pair in @(@('AGENTS.md', $agents), @('CLAUDE.md', $claude))) {
  Check "$($pair[0]) holds no op:// reference" ($pair[1] -notmatch 'op://')
  $hit = [regex]::Match($pair[1], $machinePath)
  Check "$($pair[0]) holds no machine path" (-not $hit.Success) "(at: $($hit.Value))"
}

# --- isolation for the checkouts below ---
$saved = @{}
$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX', 'HOME', 'USERPROFILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'BOSUN_OVERLAY_REPO'
foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..6]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:HOME = Join-Path $work 'home'; $env:USERPROFILE = $env:HOME; New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
git config --global user.name 'Rules Test'; git config --global user.email 'rules-test@example.invalid'; git config --global init.defaultBranch main
$pwshExe = (Get-Process -Id $PID).Path
$sh = Join-Path (Split-Path (Split-Path (Get-Command git -CommandType Application | Select-Object -First 1).Source)) 'bin\sh.exe'

. (Join-Path $repoRoot 'scripts\lib\overlay.ps1')
$block = "<!-- bosun-overlay-rules: written by scripts/setup-clone.ps1; do not edit between these lines -->`n@.private/AGENTS.md`n<!-- /bosun-overlay-rules -->`n"
function Read-Local([string]$checkout) { $f = Join-Path $checkout 'CLAUDE.local.md'; if (Test-Path -LiteralPath $f) { [IO.File]::ReadAllText($f) } else { $null } }
function New-Overlay([string]$checkout, [switch]$Unmarked, [switch]$NoRules) {
  $private = Join-Path $checkout '.private'
  git init -q $private
  if (-not $NoRules) { [IO.File]::WriteAllText((Join-Path $private 'AGENTS.md'), "# Owner rules`n") }
  if (-not $Unmarked) { git -C $private config --local bosun.overlay true }
}

try {
  # --- the helper itself ---
  $one = Join-Path $work 'one'
  git init -q $one
  New-Overlay $one
  $r = Update-BosunAgentRules $one
  Check 'a marked overlay holding AGENTS.md gets the import' ($r -eq 'added' -and (Read-Local $one) -ceq $block) "($r)"
  $r = Update-BosunAgentRules $one
  Check 'a second run changes nothing' ($r -eq 'unchanged' -and (Read-Local $one) -ceq $block) "($r)"
  [IO.File]::WriteAllText((Join-Path $one 'CLAUDE.local.md'), "my own line`n$block" + "my later line`n")
  Remove-Item -LiteralPath (Join-Path $one '.private\AGENTS.md')
  $r = Update-BosunAgentRules $one
  Check 'without AGENTS.md the import goes, the owner''s own lines kept as written' ($r -eq 'removed' -and (Read-Local $one) -ceq "my own line`nmy later line`n") "(${r}: $(Read-Local $one))"
  [IO.File]::WriteAllText((Join-Path $one '.private\AGENTS.md'), "# Owner rules`n")
  $r = Update-BosunAgentRules $one
  Check 'the import comes back ahead of the owner''s lines' ($r -eq 'added' -and (Read-Local $one) -ceq "${block}my own line`nmy later line`n") "($r)"
  git -C (Join-Path $one '.private') config --local --unset bosun.overlay
  $r = Update-BosunAgentRules $one
  Check 'an unmarked .private loses the import' ($r -eq 'removed' -and (Read-Local $one) -ceq "my own line`nmy later line`n") "($r)"

  # A block whose closing line was lost: the owner's lines after it survive every later run.
  $broken = Join-Path $work 'broken'
  git init -q $broken
  New-Overlay $broken
  $startLine = $block.Split("`n")[0]
  [IO.File]::WriteAllText((Join-Path $broken 'CLAUDE.local.md'), "$startLine`n@.private/AGENTS.md`nmy instruction`n")
  $null = Update-BosunAgentRules $broken; $null = Update-BosunAgentRules $broken
  Check 'a block missing its closing line never takes the owner''s lines with it' ((Read-Local $broken).Contains("my instruction`n") -and (Read-Local $broken).StartsWith($block)) "($(Read-Local $broken))"
  git -C (Join-Path $broken '.private') config --local --unset bosun.overlay
  $null = Update-BosunAgentRules $broken
  Check 'and once trust is withdrawn no import line is left, the owner''s line kept' ((Read-Local $broken) -ceq "my instruction`n") "($(Read-Local $broken))"
  # A rename that fails (the file held open without delete sharing) leaves no temporary copy to be committed.
  $held = Join-Path $work 'held'
  git init -q $held
  New-Overlay $held
  [IO.File]::WriteAllText((Join-Path $held 'CLAUDE.local.md'), "my line`n")
  $lock = [IO.File]::Open((Join-Path $held 'CLAUDE.local.md'), 'Open', 'Read', 'Read')
  try { try { $null = Update-BosunAgentRules $held; $threw = $false } catch { $threw = $true } } finally { $lock.Dispose() }
  Check 'a failed rename refuses and leaves no temporary file' ($threw -and -not @(Get-ChildItem -LiteralPath $held -Filter 'CLAUDE.local.md.tmp*' -Force).Count -and (Read-Local $held) -ceq "my line`n")

  # Byte for byte: a last line with no line break, through an import added and taken out again.
  $exact = Join-Path $work 'exact'
  git init -q $exact
  New-Overlay $exact
  [IO.File]::WriteAllText((Join-Path $exact 'CLAUDE.local.md'), 'one instruction, no final line break')
  $before = [IO.File]::ReadAllBytes((Join-Path $exact 'CLAUDE.local.md'))
  $null = Update-BosunAgentRules $exact
  git -C (Join-Path $exact '.private') config --local --unset bosun.overlay
  $null = Update-BosunAgentRules $exact
  Check 'an import added and taken out leaves the owner''s file byte for byte as it was' ([Linq.Enumerable]::SequenceEqual([byte[]]$before, [IO.File]::ReadAllBytes((Join-Path $exact 'CLAUDE.local.md')))) "($(Read-Local $exact))"

  $two = Join-Path $work 'two'
  git init -q $two
  New-Item -ItemType Directory -Path (Join-Path $two '.private') | Out-Null
  [IO.File]::WriteAllText((Join-Path $two '.private\AGENTS.md'), "# Not the overlay`n")
  $r = Update-BosunAgentRules $two
  Check 'a plain folder at .private with AGENTS.md is never imported' ($r -eq 'unchanged' -and $null -eq (Read-Local $two)) "($r)"
  $three = Join-Path $work 'three'
  git init -q $three
  New-Overlay $three
  $null = Update-BosunAgentRules $three
  Remove-Item -LiteralPath (Join-Path $three '.private') -Recurse -Force
  $r = Update-BosunAgentRules $three
  Check 'with .private gone, a CLAUDE.local.md holding only the import is removed' ($r -eq 'removed' -and -not (Test-Path -LiteralPath (Join-Path $three 'CLAUDE.local.md'))) "($r)"

  # --- through setup, the post-merge hook and sync, in a checkout with its own copy of the scripts ---
  $remote = Join-Path $work 'public.git'
  git init -q --bare $remote
  $seed = Join-Path $work 'seed'
  git clone -q $remote $seed 2>$null
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts') -Destination (Join-Path $seed 'scripts') -Recurse
  Set-Content -LiteralPath (Join-Path $seed '.gitignore') -Value ".private/`nCLAUDE.local.md"
  git -C $seed add -A
  foreach ($hook in 'pre-commit', 'post-merge', 'pre-push') { git -C $seed update-index --chmod=+x "scripts/hooks/$hook" }
  git -C $seed commit -q -m seed; git -C $seed push -q origin main 2>$null
  $checkout = Join-Path $work 'checkout'
  git clone -q $remote $checkout 2>$null
  New-Overlay $checkout
  $env:BOSUN_OVERLAY_REPO = 'rizonetech/Bosun-private'   # a valid source; .private already exists, so nothing is fetched
  $said = & $pwshExe -NoProfile -File (Join-Path $checkout 'scripts\setup-clone.ps1') -Repo $checkout 2>&1 | Out-String
  Check 'setup writes the import for a marked overlay holding AGENTS.md' ((Read-Local $checkout) -ceq $block -and $said -match 'CLAUDE.local.md now imports') "($said)"

  Remove-Item -LiteralPath (Join-Path $checkout '.private\AGENTS.md')
  Push-Location -LiteralPath $checkout
  try { $said = & $sh scripts/hooks/post-merge 2>&1 | Out-String } finally { Pop-Location }
  Check 'the post-merge hook takes the import out once the overlay has no AGENTS.md' ($null -eq (Read-Local $checkout) -and $said -match 'taken out of CLAUDE.local.md') "($said)"
  [IO.File]::WriteAllText((Join-Path $checkout '.private\AGENTS.md'), "# Owner rules`n")
  Push-Location -LiteralPath $checkout
  try { $said = & $sh scripts/hooks/post-merge 2>&1 | Out-String } finally { Pop-Location }
  Check 'and puts it back when the overlay gains AGENTS.md' ((Read-Local $checkout) -ceq $block) "($said)"
  # Setup's printed undo of the mark, run as printed, leaves no import behind before any sync or pull.
  $undo = ([regex]::Match([IO.File]::ReadAllText((Join-Path $repoRoot 'scripts\setup-clone.ps1')), 'to undo the mark only \(the checkout stays as it is\): ([^''(]+)')).Groups[1].Value.Trim()
  Check 'setup''s printed undo of the mark also refreshes the agent rules' ($undo -match 'update-agent-rules\.ps1') "($undo)"
  Push-Location -LiteralPath $checkout
  try { foreach ($step in $undo -split ';\s*') { Invoke-Expression $step | Out-Null } } finally { Pop-Location }
  Check 'and run as printed, it leaves no import behind' ($null -eq (Read-Local $checkout)) "($(Read-Local $checkout))"
  git -C (Join-Path $checkout '.private') config --local bosun.overlay true
  $null = Update-BosunAgentRules $checkout
  # The hook cannot replace a CLAUDE.local.md held open without delete sharing: it says so, and still exits 0.
  Remove-Item -LiteralPath (Join-Path $checkout '.private\AGENTS.md')
  $lock = [IO.File]::Open((Join-Path $checkout 'CLAUDE.local.md'), 'Open', 'Read', 'Read')
  Push-Location -LiteralPath $checkout
  try { $said = & $sh scripts/hooks/post-merge 2>&1 | Out-String; $hookExit = $LASTEXITCODE } finally { Pop-Location; $lock.Dispose() }
  Check 'a rules update the hook cannot write is reported, the hook still exiting 0' ($hookExit -eq 0 -and $said -match 'CLAUDE.local.md could not be updated' -and (Read-Local $checkout) -ceq $block) "(exit ${hookExit}: $said)"
  [IO.File]::WriteAllText((Join-Path $checkout '.private\AGENTS.md'), "# Owner rules`n")

  # Setup's own undo, then sync: nothing is synced (the overlay is unmarked), but the import still goes.
  git -C (Join-Path $checkout '.private') config --local --unset bosun.overlay
  $said = & $pwshExe -NoProfile -File (Join-Path $checkout 'scripts\sync.ps1') 2>&1 | Out-String
  Check 'sync takes the import out once the overlay is unmarked, though it syncs nothing' ($null -eq (Read-Local $checkout) -and $said -match 'taken out of CLAUDE.local.md' -and $said -match 'nothing was synced' -and $said -notmatch 'nothing was changed') "($said)"
  git -C (Join-Path $checkout '.private') config --local bosun.overlay true
  $null = Update-BosunAgentRules $checkout
  Remove-Item -LiteralPath (Join-Path $checkout '.private') -Recurse -Force
  $said = & $pwshExe -NoProfile -File (Join-Path $checkout 'scripts\sync.ps1') 2>&1 | Out-String
  Check 'sync takes the import out once .private is gone' ($null -eq (Read-Local $checkout) -and $said -match 'taken out of CLAUDE.local.md') "($said)"
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
