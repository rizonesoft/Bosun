# Tests scripts/setup-clone.ps1 against temporary clones: the hooks setting is written to the clone named, read
# back, left alone when it is already right, and never reported when it could not be written; and nothing is
# scheduled or run for a clone. The guard in each clone is a stand-in that records every call made to it.
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot '..\scripts\setup-clone.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("bosun-setup-clone-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
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
    git -C $clone add scripts/hooks/pre-commit scripts/hooks/post-merge 2>$null
    git -C $clone update-index --chmod=+x scripts/hooks/post-merge
    if (-not $NotExecutable) { git -C $clone update-index --chmod=+x scripts/hooks/pre-commit }
  }
  Set-Content -LiteralPath (Join-Path $clone 'scripts\run-guard.py') -Value $guard -Encoding utf8
  $clone
}
function Get-Setting([string]$clone) { $v = git -C $clone config --local --get core.hooksPath 2>$null; if ($LASTEXITCODE) { $null } else { "$v" } }
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
  Check 'an earlier value is replaced, named, and given back as the undo' ($r.Exit -eq 0 -and (Get-Setting $husky) -eq 'scripts/hooks' -and $r.Text -match "is '\.husky'" -and $r.Text -match 'config --local core\.hooksPath "\.husky"') "(exit $($r.Exit): $($r.Text))"

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
} finally {
  foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
