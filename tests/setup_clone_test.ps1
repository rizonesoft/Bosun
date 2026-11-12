# Tests scripts/setup-clone.ps1 against temporary clones: the hooks setting is written to the clone named, read
# back, left alone when it is already right, and never reported when it could not be written; and -Watchdog installs
# only over a task the guard says is missing. The guard in each clone is a stand-in that answers from a file, so no
# scheduled task is touched.
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot '..\scripts\setup-clone.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("bosun-setup-clone-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# The stand-in guard: `status` answers with the task state in guard-state.txt (or not in JSON, or not at all), and
# `install` does what guard-install.txt says. Every call is recorded.
$guard = @'
import json, os, sys
here = os.path.dirname(os.path.abspath(__file__))
def read(name, default):
    try:
        return open(os.path.join(here, name), encoding="utf-8").read().strip()
    except FileNotFoundError:
        return default
with open(os.path.join(here, "guard-calls.txt"), "a", encoding="utf-8") as calls:
    calls.write(" ".join(sys.argv[1:]) + "\n")
state = read("guard-state.txt", "missing")
if sys.argv[1] == "status":
    if state == "fails":
        sys.exit(2)
    if state == "garbage":
        print("active, probably")
        sys.exit(0)
    if state == "no-task":
        print(json.dumps({"verify": {"healthy": False, "watchdog": "Fixture run watchdog", "problems": []}}))
        sys.exit(1)
    problems = [] if state == "ok" else [f"the task Fixture run watchdog is {state}"]
    print(json.dumps({"state": "inactive", "verify": {"healthy": state == "ok", "watchdog": "Fixture run watchdog", "task": state, "problems": problems}}))
    sys.exit(0 if state == "ok" else 1)
if sys.argv[1] == "install":
    how = read("guard-install.txt", "ok")
    if how == "fail":
        print("ERROR: Access is denied.")
        sys.exit(1)
    if how == "ok":
        with open(os.path.join(here, "guard-state.txt"), "w", encoding="utf-8") as out:
            out.write("ok")
    sys.exit(0)
'@
function New-Clone([string]$name, [switch]$NoHook, [switch]$NotExecutable) {
  $clone = Join-Path $work $name
  git init -q -b main $clone
  New-Item -ItemType Directory -Force (Join-Path $clone 'scripts\hooks') | Out-Null
  if (-not $NoHook) {
    Set-Content -LiteralPath (Join-Path $clone 'scripts/hooks/pre-commit') -Value "#!/bin/sh`nexit 0"
    git -C $clone add scripts/hooks/pre-commit 2>$null
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
function Set-Guard([string]$clone, [string]$state, [string]$install = 'ok') {
  Set-Content -LiteralPath (Join-Path $clone 'scripts\guard-state.txt') -Value $state
  Set-Content -LiteralPath (Join-Path $clone 'scripts\guard-install.txt') -Value $install
  Remove-Item -LiteralPath (Join-Path $clone 'scripts\guard-calls.txt') -ErrorAction Ignore
}
function Get-Calls([string]$clone) { @(Get-Content -LiteralPath (Join-Path $clone 'scripts\guard-calls.txt') -ErrorAction Ignore) }

try {
  # --- hooks ---
  $clone = New-Clone 'fresh'
  $r = Invoke-Setup @('-Repo', $clone)
  Check 'a fresh clone gets core.hooksPath, read back' ($r.Exit -eq 0 -and (Get-Setting $clone) -eq 'scripts/hooks' -and $r.Text -match 'is not set' -and $r.Text -match 'hooks: on') "(exit $($r.Exit): $($r.Text))"
  Check 'it prints the command that puts the setting back' ($r.Text -match 'config --local --unset core\.hooksPath')
  Check 'without -Watchdog the guard is never run' (-not (Get-Calls $clone))

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

  # --- watchdog ---
  $w = New-Clone 'watchdog'
  Set-Guard $w 'ok'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
  Check 'a watchdog already installed for the clone is left alone' ($r.Exit -eq 0 -and $r.Text -match 'already installed for this checkout' -and -not (Get-Calls $w | Where-Object { $_ -like 'install*' })) "(exit $($r.Exit): $($r.Text))"

  Set-Guard $w 'missing'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
  $calls = Get-Calls $w
  Check 'a missing watchdog is announced, installed once and read back' ($r.Exit -eq 0 -and @($calls | Where-Object { $_ -eq 'install' }).Count -eq 1 -and $calls[-1] -eq 'status --verify --json' -and
    $r.Text -match "installing the scheduled task 'Fixture run watchdog'" -and $r.Text -match 'installed and read back' -and $r.Text -match 'run-guard\.py uninstall') "(exit $($r.Exit), calls: $($calls -join ' | '): $($r.Text))"
  Check 'the preview names the clone and what the task runs' ($r.Text -match [regex]::Escape($w) -and $r.Text -match 'run-guard\.py watch')

  foreach ($case in @(
      @{ State = 'mismatched'; Says = 'rename this folder' },
      @{ State = 'unreadable'; Says = 'Task Scheduler' },
      @{ State = 'garbage'; Says = 'did not answer in JSON' },
      @{ State = 'fails'; Says = 'printed nothing' },
      @{ State = 'no-task'; Says = 'did not say what state the task is in' })) {
    Set-Guard $w $case.State
    $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
    Check "a watchdog that reads '$($case.State)' exits 1, says why and installs nothing" ($r.Exit -eq 1 -and $r.Text -match $case.Says -and
      -not (Get-Calls $w | Where-Object { $_ -like 'install*' }) -and $r.Text -notmatch 'installed and read back') "(exit $($r.Exit): $($r.Text))"
  }

  Set-Guard $w 'missing' 'fail'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
  Check 'an install that fails exits 1 with what it said' ($r.Exit -eq 1 -and $r.Text -match 'could not be installed' -and $r.Text -match 'Access is denied') "(exit $($r.Exit): $($r.Text))"
  Set-Guard $w 'missing' 'noop'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog')
  Check 'an install that exits 0 and registers nothing is not reported as installed' ($r.Exit -eq 1 -and $r.Text -match "the task reads 'missing'" -and $r.Text -notmatch 'installed and read back') "(exit $($r.Exit): $($r.Text))"

  $gitDir = Split-Path (Get-Command git -CommandType Application | Select-Object -First 1).Source
  Set-Guard $w 'missing'
  $r = Invoke-Setup @('-Repo', $w, '-Watchdog') -path "$gitDir;$pwshDir;$system"
  Check 'with no python on PATH nothing is looked at or installed' ($r.Exit -eq 1 -and $r.Text -match 'python is not on PATH' -and -not (Get-Calls $w)) "(exit $($r.Exit): $($r.Text))"
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore }
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
