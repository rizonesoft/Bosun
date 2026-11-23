# Tests scripts/setup-clone.ps1 against temporary clones: the hooks setting is written to the clone named, read
# back, left alone when it is already right, and never reported when it could not be written; and nothing is
# scheduled or run for a clone. The guard in each clone is a stand-in that records every call made to it.
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot '..\scripts\setup-clone.ps1'
. (Join-Path $PSScriptRoot '..\scripts\lib\scratch.ps1')
$work = New-BosunScratchFolder -Name 'setup-clone-test'
$failed = 0
# The setup script also clones the private overlay when gh can see it (D00 T05 §2, tested in
# tests/overlay_clone_test.ps1). Here no gh is on PATH (and any that were would find an empty config and no token),
# and no overlay source is set: these clones never reach the operator's account or the real overlay.
$savedEnv = @{}
foreach ($name in 'GH_CONFIG_DIR', 'GH_TOKEN', 'GITHUB_TOKEN', 'BOSUN_OVERLAY_REPO', 'PATH', 'HOME', 'USERPROFILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM') { $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:GH_CONFIG_DIR = Join-Path $work 'gh-config'
# Its own home, git config and a PATH of git, pwsh and Windows only, so no real gh and no user setting reaches a case.
$env:HOME = Join-Path $work 'home'
$env:USERPROFILE = $env:HOME
New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'
$env:GIT_CONFIG_NOSYSTEM = '1'
Set-Content -LiteralPath $env:GIT_CONFIG_GLOBAL -Value "[user]`n`tname = Setup Test`n`temail = setup-test@example.invalid`n[init]`n`tdefaultBranch = main"
$env:PATH = (@((Split-Path (Get-Command git -CommandType Application | Select-Object -First 1).Source), $PSHOME, (Join-Path $env:SystemRoot 'System32')) | Select-Object -Unique) -join ';'
foreach ($name in 'GH_TOKEN', 'GITHUB_TOKEN', 'BOSUN_OVERLAY_REPO') { [Environment]::SetEnvironmentVariable($name, $null) }

# The caller's git repository variables are cleared before the first fixture command, so no git call here reads or
# writes the caller's repository or index.
foreach ($name in 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX') { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# The stand-in guard records every call made to it: setting up a clone must make none.
$guard = @'
import os, sys
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "guard-calls.txt"), "a", encoding="utf-8") as calls:
    calls.write(" ".join(sys.argv[1:]) + "\n")
'@
function New-Clone([string]$name, [switch]$NoHook, [switch]$NotExecutable) {
  $clone = Join-Path $work $name
  git init -q -b main $clone
  New-Item -ItemType Directory -Force (Join-Path $clone 'scripts\hooks') | Out-Null
  if (-not $NoHook) {
    Set-Content -LiteralPath (Join-Path $clone 'scripts/hooks/pre-commit') -Value "#!/bin/sh`nexit 0"
    Set-Content -LiteralPath (Join-Path $clone 'scripts/hooks/post-merge') -Value "#!/bin/sh`nexit 0"
    Set-Content -LiteralPath (Join-Path $clone 'scripts/hooks/pre-push') -Value "#!/bin/sh`nexit 0"
    git -C $clone add scripts/hooks/pre-commit scripts/hooks/post-merge scripts/hooks/pre-push 2>$null
    git -C $clone update-index --chmod=+x scripts/hooks/post-merge scripts/hooks/pre-push
    if (-not $NotExecutable) { git -C $clone update-index --chmod=+x scripts/hooks/pre-commit }
  }
  Set-Content -LiteralPath (Join-Path $clone 'scripts\run-guard.py') -Value $guard -Encoding utf8
  $clone
}
function Get-Setting([string]$clone, [string]$key = 'core.hooksPath') { $v = git -C $clone config --local --get $key 2>$null; if ($LASTEXITCODE) { $null } else { "$v" } }
function Invoke-Setup([string[]]$arguments, [string]$from = $work, [string]$path = $env:PATH) {
  $saved = $env:PATH
  Push-Location -LiteralPath $from
  try {
    $env:PATH = $path
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $text = & $pwsh -NoProfile -File $setup @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
  } finally { $env:PATH = $saved; Pop-Location }
}
function Get-Calls([string]$clone) { @(Get-Content -LiteralPath (Join-Path $clone 'scripts\guard-calls.txt') -ErrorAction Ignore) }

try {
  # --- hooks ---
  $clone = New-Clone 'fresh'
  $r = Invoke-Setup @('-Repo', $clone)
  Check 'a fresh clone gets core.hooksPath, read back' ($r.Exit -eq 0 -and (Get-Setting $clone) -eq 'scripts/hooks' -and $r.Text -match 'is not set' -and $r.Text -match 'hooks: on') "(exit $($r.Exit): $($r.Text))"
  Check 'it prints the command that puts the setting back' ($r.Text -match 'config --local --unset core\.hooksPath')
  Check 'setting up a clone never runs its run guard' (-not (Get-Calls $clone))
  Check 'with no gh on PATH the overlay is skipped in one line, and none is made' ($r.Text -match 'overlay: skipped, gh is not installed' -and -not (Test-Path -LiteralPath (Join-Path $clone '.private'))) "($($r.Text))"

  $config = Get-Content -LiteralPath (Join-Path $clone '.git\config') -Raw
  $r = Invoke-Setup @('-Repo', $clone)
  Check 'a second run changes nothing and says so' ($r.Exit -eq 0 -and $r.Text -match 'hooks: already on' -and (Get-Content -LiteralPath (Join-Path $clone '.git\config') -Raw) -ceq $config) "(exit $($r.Exit): $($r.Text))"

  $spaced = New-Clone 'a clone with spaces'
  $other = New-Clone 'other'
  $r = Invoke-Setup @('-Repo', $spaced) -from $other
  Check 'run from another clone, -Repo sets up the one it names, spaces and all' ($r.Exit -eq 0 -and (Get-Setting $spaced) -eq 'scripts/hooks') "(exit $($r.Exit): $($r.Text))"
  Check 'and the clone it was run from is left untouched' ($null -eq (Get-Setting $other))

  $husky = New-Clone 'husky'
  git -C $husky config --local core.hooksPath '.husky'
  $r = Invoke-Setup @('-Repo', $husky)
  Check 'an earlier value is replaced, named, and given back as the undo' ($r.Exit -eq 0 -and (Get-Setting $husky) -eq 'scripts/hooks' -and $r.Text -match "is '\.husky'" -and $r.Text -match "config --local core\.hooksPath '\.husky'") "(exit $($r.Exit): $($r.Text))"

  # --- refusals ---
  $plain = Join-Path $work 'plain'
  New-Item -ItemType Directory -Path $plain | Out-Null
  $r = Invoke-Setup @('-Repo', $plain)
  Check 'a folder that is not a checkout is refused' ($r.Exit -eq 1 -and $r.Text -match 'not the top folder of a git checkout') "(exit $($r.Exit): $($r.Text))"
  $r = Invoke-Setup @('-Repo', (Join-Path $clone 'scripts'))
  Check 'a folder inside a checkout is refused, not set up as one' ($r.Exit -eq 1 -and $r.Text -match 'not the top folder of a git checkout') "(exit $($r.Exit): $($r.Text))"
  $r = Invoke-Setup @('-Repo', (Join-Path $work 'no-such-folder'))
  Check 'a folder that does not exist is refused' ($r.Exit -eq 1 -and $r.Text -match 'is not a folder')
  $bare = New-Clone 'no-hook' -NoHook
  $r = Invoke-Setup @('-Repo', $bare)
  Check 'a checkout with no hook file is refused and not changed' ($r.Exit -eq 1 -and $r.Text -match 'no scripts/hooks/pre-commit' -and $null -eq (Get-Setting $bare)) "(exit $($r.Exit): $($r.Text))"

  $plainMode = New-Clone 'not-executable' -NotExecutable
  $r = Invoke-Setup @('-Repo', $plainMode)
  Check 'a hook not recorded as executable is refused, with the command that fixes it' ($r.Exit -eq 1 -and $r.Text -match 'update-index --chmod=\+x scripts/hooks/pre-commit' -and $null -eq (Get-Setting $plainMode)) "(exit $($r.Exit): $($r.Text))"

  $locked = New-Clone 'locked'
  git -C $locked config --local core.hooksPath 'old/hooks'
  Set-Content -LiteralPath (Join-Path $locked '.git\config.lock') -Value 'another git is writing the config'
  $r = Invoke-Setup @('-Repo', $locked)
  Remove-Item -LiteralPath (Join-Path $locked '.git\config.lock')
  Check 'a config that cannot be written exits 1 with the cause' ($r.Exit -eq 1 -and $r.Text -match 'could not set core\.hooksPath' -and $r.Text -notmatch 'hooks: on') "(exit $($r.Exit): $($r.Text))"
  Check 'and the earlier value is still there' ((Get-Setting $locked) -eq 'old/hooks' -and $r.Text -match "still 'old/hooks'")

  # A write that reports success and changes nothing (a stand-in git first on PATH drops it): the setting does not
  # read back, so it is not reported as on.
  $realGit = (Get-Command git -CommandType Application | Select-Object -First 1).Source
  $fakeBin = Join-Path $work 'fake-git'
  New-Item -ItemType Directory -Path $fakeBin | Out-Null
  Set-Content -LiteralPath (Join-Path $fakeBin 'fake-git.ps1') -Value @"
`$write = (`$args -contains 'config') -and (`$args -contains '--local') -and (`$args -notcontains '--get') -and (`$args[-2] -eq 'core.hooksPath')
if (`$write) { exit 0 }
& '$realGit' @args
exit `$LASTEXITCODE
"@
  Set-Content -LiteralPath (Join-Path $fakeBin 'git.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-git.ps1`" %*"
  $dropped = New-Clone 'write-dropped'
  $r = Invoke-Setup @('-Repo', $dropped) -path "$fakeBin;$env:PATH"
  Check 'a write that does not read back exits 1 and is not reported as on' ($r.Exit -eq 1 -and $r.Text -match 'does not read back' -and $r.Text -notmatch 'hooks: on' -and
    $null -eq (Get-Setting $dropped)) "(exit $($r.Exit): $($r.Text))"

  $pwshDir = $PSHOME
  $system = Join-Path $env:SystemRoot 'System32'
  $r = Invoke-Setup @('-Repo', $clone) -path "$pwshDir;$system"
  Check 'with no git on PATH it says so' ($r.Exit -eq 1 -and $r.Text -match 'git is not on PATH') "(exit $($r.Exit): $($r.Text))"

  # --- nothing is scheduled ---
  $w = New-Clone 'no-watchdog'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
  Check 'the retired -Watchdog option is refused, and nothing is set up or run for it' ($r.Exit -ne 0 -and $r.Text -match 'Watchdog' -and $null -eq (Get-Setting $w) -and -not (Get-Calls $w)) "(exit $($r.Exit): $($r.Text))"

  # --- D00 T07 §6: persistent bosun.previousHooksPath, second-run undo, -Undo, stale-key cleanup, dropped-write retry ---
  $rHusky2 = Invoke-Setup @('-Repo', $husky)
  Check 'a second run on a clone with an earlier hooksPath still prints the undo command' ($rHusky2.Exit -eq 0 -and
    (Get-Setting $husky 'bosun.previousHooksPath') -eq '.husky' -and
    $rHusky2.Text -match 'hooks: already on' -and $rHusky2.Text -match '-Undo' -and
    $rHusky2.Text -match "config --local core\.hooksPath '\.husky'") "(exit $($rHusky2.Exit): $($rHusky2.Text))"
  $rHuskyUndo = Invoke-Setup @('-Repo', $husky, '-Undo')
  Check '-Undo restores the earlier hooksPath and removes bosun.previousHooksPath' ($rHuskyUndo.Exit -eq 0 -and
    (Get-Setting $husky) -eq '.husky' -and $null -eq (Get-Setting $husky 'bosun.previousHooksPath')) "(exit $($rHuskyUndo.Exit): $($rHuskyUndo.Text))"
  $rHuskyUndo2 = Invoke-Setup @('-Repo', $husky, '-Undo')
  Check '-Undo refuses an unrelated core.hooksPath when no bosun.previousHooksPath is stored' ($rHuskyUndo2.Exit -eq 1 -and
    (Get-Setting $husky) -eq '.husky' -and $rHuskyUndo2.Text -match 'refusing to undo') "(exit $($rHuskyUndo2.Exit): $($rHuskyUndo2.Text))"

  # Fresh clone with stale bosun.previousHooksPath seeded while core.hooksPath is unset, run twice via -Repo from $other,
  # then execute the ACTUAL emitted undo command from $other and read both keys back absent (Completion Gap 4).
  $staleFresh = New-Clone 'stale-fresh'
  git -C $staleFresh config --local bosun.previousHooksPath 'stale/hooks'
  $rStale1 = Invoke-Setup @('-Repo', $staleFresh) -from $other
  $rStale2 = Invoke-Setup @('-Repo', $staleFresh) -from $other
  $undoMatch = [regex]::Match($rStale2.Text, "hooks: to put it back: (pwsh '[^']+' -Repo '[^']+' -Undo)")
  $emittedUndo = if ($undoMatch.Success) { $undoMatch.Groups[1].Value } else { '' }
  $emittedExit = -1
  if ($emittedUndo) {
    Push-Location -LiteralPath $other
    try {
      $null = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command $emittedUndo 2>&1 | Out-String
      $emittedExit = $LASTEXITCODE
    } finally { Pop-Location }
  }
  Check 'a fresh clone clears stale bosun.previousHooksPath, prints -Repo undo on second run, and -Undo clears both keys' ($rStale1.Exit -eq 0 -and
    $rStale2.Exit -eq 0 -and $undoMatch.Success -and $emittedExit -eq 0 -and
    $null -eq (Get-Setting $staleFresh) -and $null -eq (Get-Setting $staleFresh 'bosun.previousHooksPath') -and
    $null -eq (Get-Setting $other)) "(r1=$($rStale1.Exit), r2=$($rStale2.Exit), emitted='$emittedUndo', exit=$emittedExit, cur='$(Get-Setting $staleFresh)', prev='$(Get-Setting $staleFresh 'bosun.previousHooksPath')')"

  # Completion Gap 1: -Undo -Repo against a foreign repository (even one using core.hooksPath=scripts/hooks) refuses and leaves its config byte-identical.
  $foreign = Join-Path $work 'foreign-repo'
  git init -q -b main $foreign
  New-Item -ItemType Directory -Force (Join-Path $foreign 'scripts/hooks') | Out-Null
  Set-Content -LiteralPath (Join-Path $foreign 'scripts/hooks/pre-commit') -Value "#!/bin/sh`nexit 0"
  git -C $foreign config --local core.hooksPath 'scripts/hooks'
  $foreignCfgBefore = Get-Content -LiteralPath (Join-Path $foreign '.git\config') -Raw
  $rForeignUndo = Invoke-Setup @('-Repo', $foreign, '-Undo')
  Check '-Undo refuses a foreign repository using scripts/hooks and leaves its config byte-identical' ($rForeignUndo.Exit -eq 1 -and
    $rForeignUndo.Text -match 'not a checkout of this repository' -and
    (Get-Content -LiteralPath (Join-Path $foreign '.git\config') -Raw) -ceq $foreignCfgBefore) "(exit=$($rForeignUndo.Exit): $($rForeignUndo.Text))"

  # Completion Gap 2: setup refuses to report success when a higher-scope override beats local core.hooksPath.
  $overrideClone = New-Clone 'override-scope'
  $savedGlobalCfg = $env:GIT_CONFIG_GLOBAL
  $overrideGlobal = Join-Path $work 'gitconfig-override'
  Set-Content -LiteralPath $overrideGlobal -Value "[user]`n`tname = T`n`temail = t@example.invalid`n[core]`n`thooksPath = global/hooks`n"
  # Local beats global, whereas GIT_CONFIG_PARAMETERS (command scope) beats local!
  $savedGitParams = $env:GIT_CONFIG_PARAMETERS
  try {
    $env:GIT_CONFIG_PARAMETERS = "'core.hooksPath=command/hooks'"
    $rOverride = Invoke-Setup @('-Repo', $overrideClone)
    Check 'setup refuses to report hooks active when a higher-scope core.hooksPath override wins' ($rOverride.Exit -eq 1 -and
      $rOverride.Text -match 'overridden by command' -and $rOverride.Text -match 'command/hooks') "(exit=$($rOverride.Exit): $($rOverride.Text))"
  } finally {
    if ($null -eq $savedGitParams) { Remove-Item Env:GIT_CONFIG_PARAMETERS -ErrorAction Ignore } else { $env:GIT_CONFIG_PARAMETERS = $savedGitParams }
    $env:GIT_CONFIG_GLOBAL = $savedGlobalCfg
  }

  # Completion Gap 3: a git config read error (exit 128) on bosun.previousHooksPath stops -Undo before touching core.hooksPath.
  $errBin = Join-Path $work 'fake-git-readerr'
  New-Item -ItemType Directory -Path $errBin | Out-Null
  Set-Content -LiteralPath (Join-Path $errBin 'fake-git.ps1') -Value @"
if ((`$args -contains 'config') -and (`$args -contains 'bosun.previousHooksPath') -and (`$args -contains '--get')) {
  [Console]::Error.WriteLine('fatal: bad config line 1')
  exit 128
}
& '$realGit' @args
exit `$LASTEXITCODE
"@
  Set-Content -LiteralPath (Join-Path $errBin 'git.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-git.ps1`" %*"
  $readErrClone = New-Clone 'read-err'
  git -C $readErrClone config --local core.hooksPath 'scripts/hooks'
  $rReadErr = Invoke-Setup @('-Repo', $readErrClone, '-Undo') -path "$errBin;$env:PATH"
  Check '-Undo stops before unsetting core.hooksPath when reading bosun.previousHooksPath fails with exit 128' ($rReadErr.Exit -eq 1 -and
    $rReadErr.Text -match 'could not read local git config' -and (Get-Setting $readErrClone) -eq 'scripts/hooks') "(exit=$($rReadErr.Exit), cur='$(Get-Setting $readErrClone)': $($rReadErr.Text))"

  # R1 F2 & F4: -Undo refuses to overwrite a hook path installed after setup, and paths with $ are single-quoted literally.
  $postChange = New-Clone 'clone-$team'
  git -C $postChange config --local core.hooksPath 'team-hooks'
  $rPc1 = Invoke-Setup @('-Repo', $postChange)
  git -C $postChange config --local core.hooksPath 'new-team-hooks'
  $rPcUndo = Invoke-Setup @('-Repo', $postChange, '-Undo')
  Check '-Undo refuses to overwrite a hooksPath installed after setup and quotes $ paths literally' ($rPc1.Exit -eq 0 -and
    $rPc1.Text -match ([regex]::Escape("-Repo '$postChange' -Undo")) -and $rPcUndo.Exit -eq 1 -and
    (Get-Setting $postChange) -eq 'new-team-hooks' -and (Get-Setting $postChange 'bosun.previousHooksPath') -eq 'team-hooks') "(r1=$($rPc1.Exit), undo=$($rPcUndo.Exit), cur='$(Get-Setting $postChange)', prev='$(Get-Setting $postChange 'bosun.previousHooksPath')')"

  # Dropped-write ordering: failing to save bosun.previousHooksPath leaves core.hooksPath untouched;
  # failing to restore core.hooksPath on -Undo keeps bosun.previousHooksPath so retry succeeds.
  $fakeBin2 = Join-Path $work 'fake-git-order'
  New-Item -ItemType Directory -Path $fakeBin2 | Out-Null
  Set-Content -LiteralPath (Join-Path $fakeBin2 'fake-git.ps1') -Value @"
`$dropKey = `$env:BOSUN_TEST_DROP_KEY
`$isWrite = (`$args -contains 'config') -and (`$args -contains '--local') -and (`$args -notcontains '--get') -and (`$args -contains `$dropKey)
if (`$dropKey -and `$isWrite) { exit 0 }
& '$realGit' @args
exit `$LASTEXITCODE
"@
  Set-Content -LiteralPath (Join-Path $fakeBin2 'git.cmd') -Value "@`"$(Join-Path $PSHOME 'pwsh.exe')`" -NoProfile -File `"%~dp0fake-git.ps1`" %*"
  $orderClone = New-Clone 'write-order'
  git -C $orderClone config --local core.hooksPath '.custom'
  try {
    $env:BOSUN_TEST_DROP_KEY = 'bosun.previousHooksPath'
    $rDropPrev = Invoke-Setup @('-Repo', $orderClone) -path "$fakeBin2;$env:PATH"
    Check 'a dropped bosun.previousHooksPath write stops before replacing core.hooksPath' ($rDropPrev.Exit -eq 1 -and
      (Get-Setting $orderClone) -eq '.custom' -and $null -eq (Get-Setting $orderClone 'bosun.previousHooksPath')) "(exit $($rDropPrev.Exit), cur='$(Get-Setting $orderClone)')"

    Remove-Item Env:BOSUN_TEST_DROP_KEY -ErrorAction Ignore
    $rOkOrder = Invoke-Setup @('-Repo', $orderClone)
    $env:BOSUN_TEST_DROP_KEY = 'core.hooksPath'
    $rDropRestore = Invoke-Setup @('-Repo', $orderClone, '-Undo') -path "$fakeBin2;$env:PATH"
    Remove-Item Env:BOSUN_TEST_DROP_KEY -ErrorAction Ignore
    $rRetryUndo = Invoke-Setup @('-Repo', $orderClone, '-Undo')
    Check 'a dropped core.hooksPath restore during -Undo keeps bosun.previousHooksPath so a retry restores the original path' ($rOkOrder.Exit -eq 0 -and
      $rDropRestore.Exit -eq 1 -and $rRetryUndo.Exit -eq 0 -and
      (Get-Setting $orderClone) -eq '.custom' -and $null -eq (Get-Setting $orderClone 'bosun.previousHooksPath')) "(drop=$($rDropRestore.Exit), retry=$($rRetryUndo.Exit), cur='$(Get-Setting $orderClone)')"
  } finally { Remove-Item Env:BOSUN_TEST_DROP_KEY -ErrorAction Ignore }

  # Conflicting modes, invalid -OverlayUpdates, and explicitly empty -OverlayUpdates '' refuse before any mutation (R1 F3)
  $conflictClone = New-Clone 'conflict'
  $rConf1 = Invoke-Setup @('-Repo', $conflictClone, '-Undo', '-CheckPrePush')
  $rConf2 = Invoke-Setup @('-Repo', $conflictClone, '-OverlayUpdates', 'maybe')
  $rConf3 = Invoke-Setup @('-Repo', $conflictClone, '-OverlayUpdates', '')
  Check 'conflicting mode switches and invalid or empty -OverlayUpdates refuse before mutating the clone' ($rConf1.Exit -eq 1 -and
    $rConf2.Exit -eq 1 -and $rConf3.Exit -eq 1 -and $null -eq (Get-Setting $conflictClone) -and $rConf1.Text -match 'at most one' -and
    $rConf2.Text -match "must be 'off' or 'on'" -and $rConf3.Text -match "must be 'off' or 'on'") "(c1=$($rConf1.Exit), c2=$($rConf2.Exit), c3=$($rConf3.Exit))"

  # --- D00 T07 §6: -OverlayUpdates off|on preserves bosun.overlay, branches, stashes, ignored files, and controls post-merge ---
  $ovHost = New-Clone 'ov-host'
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\hooks\post-merge') -Destination (Join-Path $ovHost 'scripts/hooks/post-merge') -Force
  git -C $ovHost add scripts/hooks/post-merge; git -C $ovHost update-index --chmod=+x scripts/hooks/post-merge
  $rNoOv = Invoke-Setup @('-Repo', $ovHost, '-OverlayUpdates', 'off')
  Check '-OverlayUpdates refuses when .private is absent' ($rNoOv.Exit -eq 1 -and $rNoOv.Text -match '\.private does not exist') "(exit $($rNoOv.Exit): $($rNoOv.Text))"

  # Completion Gap 6 (part 1): -OverlayUpdates refuses an existing unmarked .private and leaves its config byte-identical.
  $unmarkedHost = New-Clone 'ov-unmarked'
  $unmarkedPriv = Join-Path $unmarkedHost '.private'
  git init -q -b main $unmarkedPriv
  $unmarkedCfgBefore = Get-Content -LiteralPath (Join-Path $unmarkedPriv '.git\config') -Raw
  $rUnmarked = Invoke-Setup @('-Repo', $unmarkedHost, '-OverlayUpdates', 'off')
  Check '-OverlayUpdates refuses an existing unmarked .private without changing its config' ($rUnmarked.Exit -eq 1 -and
    $rUnmarked.Text -match 'not marked as the overlay' -and
    (Get-Content -LiteralPath (Join-Path $unmarkedPriv '.git\config') -Raw) -ceq $unmarkedCfgBefore) "(exit $($rUnmarked.Exit): $($rUnmarked.Text))"

  $ovSeed = Join-Path $work 'ov-seed'
  git init -q -b main $ovSeed
  Set-Content -LiteralPath (Join-Path $ovSeed 'config.toml') -Value "key = 'v1'`n"
  Set-Content -LiteralPath (Join-Path $ovSeed '.gitignore') -Value "*.ignored`n"
  git -C $ovSeed add -A; git -C $ovSeed commit -q -m 'v1'
  $ovRemote = Join-Path $work 'ov-remote.git'
  git clone -q --bare $ovSeed $ovRemote 2>$null
  $ovPrivate = Join-Path $ovHost '.private'
  git clone -q $ovRemote $ovPrivate 2>$null
  $env:BOSUN_OVERLAY_REPO = $ovRemote
  try {
    $rOvSetup = Invoke-Setup @('-Repo', $ovHost)
    # Create local branch, stash, and ignored owner file inside .private
    git -C $ovPrivate branch owner-branch
    Set-Content -LiteralPath (Join-Path $ovPrivate 'config.toml') -Value "key = 'stashed'`n"
    git -C $ovPrivate stash push -q -m 'owner-stash'
    Set-Content -LiteralPath (Join-Path $ovPrivate 'owner.ignored') -Value 'keep-me'
    # Advance upstream remote by 1 commit
    $ovPush = Join-Path $work 'ov-push'
    git clone -q $ovRemote $ovPush 2>$null
    Set-Content -LiteralPath (Join-Path $ovPush 'config.toml') -Value "key = 'v2'`n"
    git -C $ovPush add -A; git -C $ovPush commit -q -m 'v2'; git -C $ovPush push -q origin main 2>$null
    $headBefore = (git -C $ovPrivate rev-parse HEAD).Trim()
    $headRemote = (git -C $ovPush rev-parse HEAD).Trim()

    $rOvOff = Invoke-Setup @('-Repo', $ovHost, '-OverlayUpdates', 'off')
    # Ordinary setup run must NOT silently re-enable automatic overlay updates
    $rOvSetupAgain = Invoke-Setup @('-Repo', $ovHost)
    $shExe = Join-Path (Split-Path (Split-Path $realGit)) 'bin\sh.exe'
    if (-not (Test-Path -LiteralPath $shExe)) { $shExe = (Get-Command sh -CommandType Application | Select-Object -First 1).Source }
    Push-Location -LiteralPath $ovHost
    try { $pmOffOut = & $shExe (Join-Path $ovHost 'scripts/hooks/post-merge') 2>&1 | Out-String } finally { Pop-Location }
    $headAfterOff = (git -C $ovPrivate rev-parse HEAD).Trim()
    # Completion Gap 6 (part 2): post-merge also honors Git boolean spellings such as 'off'.
    git -C $ovPrivate config --local bosun.overlayAutoUpdate 'off'
    Push-Location -LiteralPath $ovHost
    try { $pmBoolOut = & $shExe (Join-Path $ovHost 'scripts/hooks/post-merge') 2>&1 | Out-String } finally { Pop-Location }
    $headAfterBool = (git -C $ovPrivate rev-parse HEAD).Trim()
    git -C $ovPrivate config --local bosun.overlayAutoUpdate 'false'
    $autoAfterOff = (git -C $ovPrivate config --local --get bosun.overlayAutoUpdate 2>$null).Trim()
    $markAfterOff = (git -C $ovPrivate config --local --get bosun.overlay 2>$null).Trim()

    $rOvOn = Invoke-Setup @('-Repo', $ovHost, '-OverlayUpdates', 'on')
    Push-Location -LiteralPath $ovHost
    try { $pmOnOut = & $shExe (Join-Path $ovHost 'scripts/hooks/post-merge') 2>&1 | Out-String } finally { Pop-Location }
    $headAfterOn = (git -C $ovPrivate rev-parse HEAD).Trim()
    $markAfterOn = (git -C $ovPrivate config --local --get bosun.overlay 2>$null).Trim()
    $hasBranch = [bool]((git -C $ovPrivate branch --list owner-branch) -match 'owner-branch')
    $hasStash = [bool]((git -C $ovPrivate stash list) -match 'owner-stash')
    $hasIgnored = (Get-Content -LiteralPath (Join-Path $ovPrivate 'owner.ignored') -Raw).Trim() -eq 'keep-me'

    Check '-OverlayUpdates off survives setup, pauses post-merge, on resumes it, and bosun.overlay + branch + stash + ignored survive' ($rOvSetup.Exit -eq 0 -and
      $rOvOff.Exit -eq 0 -and $rOvSetupAgain.Exit -eq 0 -and $autoAfterOff -eq 'false' -and
      $headAfterOff -eq $headBefore -and $pmOffOut -match 'automatic overlay updates are off' -and
      $headAfterBool -eq $headBefore -and $pmBoolOut -match 'automatic overlay updates are off' -and
      $rOvOn.Exit -eq 0 -and $headAfterOn -eq $headRemote -and $pmOnOut -match 'updated to' -and
      $markAfterOff -eq 'true' -and $markAfterOn -eq 'true' -and $hasBranch -and $hasStash -and $hasIgnored) "(off=$($rOvOff.Exit), auto='$autoAfterOff', hOff=$($headAfterOff -eq $headBefore), hBool=$($headAfterBool -eq $headBefore), hOn=$($headAfterOn -eq $headRemote), mark=$markAfterOff/$markAfterOn, branch=$hasBranch, stash=$hasStash, ignored=$hasIgnored)"
  } finally { [Environment]::SetEnvironmentVariable('BOSUN_OVERLAY_REPO', $null) }

  # --- D00 T07 §6: -CheckPrePush (read-only; exercises every prerequisite refusal and one pass) ---
  $ppClone = New-Clone 'prepush'
  Set-Content -LiteralPath (Join-Path $ppClone 'scripts/leak-scan.ps1') -Value '# stub'
  $glBin = Join-Path $work 'fake-gitleaks'
  New-Item -ItemType Directory -Path $glBin | Out-Null
  Set-Content -LiteralPath (Join-Path $glBin 'gitleaks.cmd') -Value "@echo 8.30.1`r`n@exit /b 0"
  $glBadBin = Join-Path $work 'fake-gitleaks-bad'
  New-Item -ItemType Directory -Path $glBadBin | Out-Null
  Set-Content -LiteralPath (Join-Path $glBadBin 'gitleaks.cmd') -Value "@echo broken >&2`r`n@exit /b 2"

  # 1) core.hooksPath unset -> refuses and leaves config and .private untouched
  $rPpUnset = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  Check '-CheckPrePush refuses unset core.hooksPath without mutating the clone' ($rPpUnset.Exit -eq 1 -and
    $rPpUnset.Text -match 'core\.hooksPath' -and $null -eq (Get-Setting $ppClone) -and
    -not (Test-Path -LiteralPath (Join-Path $ppClone '.private'))) "(exit $($rPpUnset.Exit): $($rPpUnset.Text))"

  # 2) wrong effective core.hooksPath (including wrong case 'Scripts/Hooks', R1 F1) -> refuses
  git -C $ppClone config --local core.hooksPath '.other'
  $rPpWrong = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  git -C $ppClone config --local core.hooksPath 'Scripts/Hooks'
  $rPpCase = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  Check '-CheckPrePush refuses a wrong effective core.hooksPath' ($rPpWrong.Exit -eq 1 -and $rPpWrong.Text -match '\.other' -and
    $rPpCase.Exit -eq 1 -and $rPpCase.Text -match 'Scripts/Hooks') "(wrong=$($rPpWrong.Exit), case=$($rPpCase.Exit): $($rPpCase.Text))"

  # Enable hooks properly
  git -C $ppClone config --local core.hooksPath 'scripts/hooks'
  $cfgBeforeCheck = Get-Content -LiteralPath (Join-Path $ppClone '.git\config') -Raw

  # 3) gitleaks missing -> refuses naming gitleaks
  $rPpNoGl = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush')
  Check '-CheckPrePush refuses when gitleaks is missing, naming it' ($rPpNoGl.Exit -eq 1 -and $rPpNoGl.Text -match 'gitleaks is not installed') "(exit $($rPpNoGl.Exit): $($rPpNoGl.Text))"

  # 4) gitleaks exits non-zero -> refuses naming gitleaks
  $rPpBadGl = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBadBin;$env:PATH"
  Check '-CheckPrePush refuses when gitleaks exits non-zero' ($rPpBadGl.Exit -eq 1 -and $rPpBadGl.Text -match 'gitleaks exited 2') "(exit $($rPpBadGl.Exit): $($rPpBadGl.Text))"

  # 5) pwsh missing from PATH (set inside child so pwsh.exe startup cannot re-inject $PSHOME) -> refuses naming pwsh
  $gitOnlyPath = (Split-Path $realGit) + ';' + (Join-Path $env:SystemRoot 'System32') + ';' + $glBin
  $noPwshOut = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command "`$env:PATH = '$gitOnlyPath'; & '$setup' -Repo '$ppClone' -CheckPrePush" 2>&1 | Out-String
  $noPwshExit = $LASTEXITCODE
  Check '-CheckPrePush refuses when pwsh is not on PATH' ($noPwshExit -eq 1 -and $noPwshOut -match 'pwsh is not on PATH') "(exit ${noPwshExit}: $noPwshOut)"

  # 6) scripts/leak-scan.ps1 missing -> refuses naming leak-scan.ps1
  Remove-Item -LiteralPath (Join-Path $ppClone 'scripts/leak-scan.ps1') -Force
  $rPpNoScan = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  Set-Content -LiteralPath (Join-Path $ppClone 'scripts/leak-scan.ps1') -Value '# stub'
  Check '-CheckPrePush refuses when scripts/leak-scan.ps1 is missing' ($rPpNoScan.Exit -eq 1 -and $rPpNoScan.Text -match 'leak-scan\.ps1 is missing') "(exit $($rPpNoScan.Exit): $($rPpNoScan.Text))"

  # 7) pre-push not executable in git index -> refuses
  git -C $ppClone update-index --chmod=-x scripts/hooks/pre-push
  $rPpMode = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  git -C $ppClone update-index --chmod=+x scripts/hooks/pre-push
  Check '-CheckPrePush refuses when pre-push is not recorded executable in git' ($rPpMode.Exit -eq 1 -and $rPpMode.Text -match 'update-index --chmod=\+x') "(exit $($rPpMode.Exit): $($rPpMode.Text))"

  # 8) pre-push untracked (present on disk, removed from index) AND missing from disk -> both refuse (Completion Gap 8)
  git -C $ppClone rm -q --cached scripts/hooks/pre-push
  $rPpUntracked = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  Remove-Item -LiteralPath (Join-Path $ppClone 'scripts/hooks/pre-push') -Force
  $rPpMissing = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  Set-Content -LiteralPath (Join-Path $ppClone 'scripts/hooks/pre-push') -Value "#!/bin/sh`nexit 0"
  git -C $ppClone add scripts/hooks/pre-push; git -C $ppClone update-index --chmod=+x scripts/hooks/pre-push
  Check '-CheckPrePush refuses when pre-push is missing or untracked' ($rPpUntracked.Exit -eq 1 -and
    $rPpUntracked.Text -match 'not tracked by git' -and $rPpMissing.Exit -eq 1 -and $rPpMissing.Text -match 'pre-push is missing') "(untracked=$($rPpUntracked.Exit), missing=$($rPpMissing.Exit))"

  # 9) All present -> single pass, exit 0, zero mutations (config, index hash, and .private unchanged)
  $idxHashBefore = (Get-FileHash -LiteralPath (Join-Path $ppClone '.git\index') -Algorithm SHA256).Hash
  $rPpOk = Invoke-Setup @('-Repo', $ppClone, '-CheckPrePush') -path "$glBin;$env:PATH"
  $idxHashAfter = (Get-FileHash -LiteralPath (Join-Path $ppClone '.git\index') -Algorithm SHA256).Hash
  Check '-CheckPrePush passes with one line when hook and gitleaks are ready, leaving clone untouched' ($rPpOk.Exit -eq 0 -and
    $rPpOk.Text -match 'pre-push: ok' -and $rPpOk.Text -match '8\.30\.1' -and
    (Get-Content -LiteralPath (Join-Path $ppClone '.git\config') -Raw) -ceq $cfgBeforeCheck -and
    $idxHashAfter -eq $idxHashBefore -and
    -not (Test-Path -LiteralPath (Join-Path $ppClone '.private'))) "(exit $($rPpOk.Exit): $($rPpOk.Text))"
} finally {
  foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
