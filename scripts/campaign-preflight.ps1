# One command that proves the unattended campaign can run before anyone walks away (D00 T03 §9 and §10). Every check
# prints ok or FAIL, and each FAIL names the command that fixes it. Exits 1 when any check fails, 2 on a bad argument.
#
#   pwsh scripts/campaign-preflight.ps1                  every check
#   pwsh scripts/campaign-preflight.ps1 -Only gh,ci      a subset, by name (see $Checks below); it says it is one
#
# Options for the owner: -Repo (this checkout), -ProbeSeconds (120, at least 1, each external call's limit),
# -MinFreeGB (50, at least 1, on each of -FreeDrives, by default the drive this checkout is on), -MinLoginDays (30, at least 1, left on Claude's
# sign-in or token), -ReviewerModel (gpt-6-astra) and -Distro (Ubuntu). The other parameters point the checks at test
# doubles (paths, registry keys, a task store) and are for tests/preflight_test.ps1.
#
# Tools are found on the PATH a run's shortcut launches with (the machine and user PATH in the registry), not this
# shell's, which may predate an installer's change. Each probe runs under -ProbeSeconds and is stopped with its whole
# process tree when it overruns.
param(
  [string[]]$Only = @(),
  [string]$Repo = (Split-Path $PSScriptRoot),
  [string]$SearchPath = '',
  [int]$ProbeSeconds = 120,
  [string]$ReviewerModel = 'gpt-6-astra',
  [string]$Distro = 'Ubuntu',
  [double]$MinFreeGB = 50,
  [int]$MinLoginDays = 30,
  [string]$UserEnvKey = 'HKCU:\Environment',
  [string]$TokenRecord = (Join-Path $env:LOCALAPPDATA 'Bosun\claude-token.json'),
  [string[]]$FreeDrives = @(),
  [string]$HoldRecord = (Join-Path $env:LOCALAPPDATA 'Bosun\campaign-hold.json'),
  [string]$PowerCfg = (Join-Path $env:SystemRoot 'System32\powercfg.exe'),
  [string]$LockKey = 'HKCU:\Control Panel\Desktop',
  [string]$PolicyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU',
  [string]$BinDir = (Join-Path $env:ProgramData 'Bosun\bin'),
  [string]$DotnetDir = '',
  [string]$TaskStore = '',
  [string]$TaskPrefix = 'Bosun',
  [string]$InactivityKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System',
  [string]$Acceptance = '',
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')
# A threshold below its floor would let a check pass what it must refuse, so it is a bad argument, not a setting.
foreach ($bad in @(@('ProbeSeconds', $ProbeSeconds, 1), @('MinFreeGB', $MinFreeGB, 1), @('MinLoginDays', $MinLoginDays, 1)) | Where-Object { $_[1] -lt $_[2] }) { "-$($bad[0]) must be at least $($bad[2]), not $($bad[1])"; exit 2 }
if (-not $SearchPath) { $SearchPath = Get-RegistryPath }
if (-not $Acceptance) { $Acceptance = Join-Path $Repo 'docs\acceptance\preflight.md' }
# The campaign writes where the checkout is, so that drive is the one that must not fill: never a drive named in code.
if (-not $FreeDrives) { $FreeDrives = @([IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $Repo).Path)) }
$failed = 0
# Written to the host, not returned: a check that fails inside a helper whose result is assigned (as Probe's is) would
# otherwise hand its own report back as that result.
function Pass([string]$what) { Write-Host "ok   $what" }
function Fail([string]$what, [string]$fix) {
  Write-Host "FAIL $what"
  Write-Host "     fix: $fix"
  $script:failed++
}
function Tail([string]$text) { (($text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 2) -join ' / ').Trim() }

# A tool on a run's PATH, run under the probe limit. $null (after failing the check) when it isn't there.
function Probe([string]$tool, [string[]]$arguments, [string]$fix, [string]$dir = '', [hashtable]$environment = @{}) {
  $exe = Find-Tool $tool $SearchPath
  if (-not $exe) { Fail "$tool is not on the PATH a run launches with" $fix; return $null }
  $r = Invoke-Probe $exe $arguments $ProbeSeconds $SearchPath $dir $environment
  if ($r.TimedOut) { Fail "$tool $($arguments -join ' ') gave no answer within $ProbeSeconds seconds and was stopped" $fix; return $null }
  $r
}

# --- Tools -------------------------------------------------------------------------------------------------------

# The CMake Visual Studio ships, and how to get it onto PATH, or why it can't be found.
function Find-VsCMake {
  if (-not (Test-Path $VsWhere)) {
    return @{ Fix = "vswhere isn't at $VsWhere, so Visual Studio can't be found: install Visual Studio 2022 or later with the 'C++ CMake tools for Windows' component" }
  }
  # Every installed instance, newest first: the newest may lack CMake while an older one has it.
  $all = @(& $VsWhere -products '*' -sort -property installationPath 2>$null | Where-Object { $_ })
  if (-not $all) {
    return @{ Fix = "vswhere found no Visual Studio: install it with the 'C++ CMake tools for Windows' component" }
  }
  foreach ($vs in $all) {
    $folder = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
    if (Test-Path (Join-Path $folder 'cmake.exe')) {
      return @{ Folder = $folder; Fix = "add $folder to your user PATH with ``pwsh scripts/add-cmake-to-path.ps1 -Apply``, then open a new shell" }
    }
  }
  @{ Fix = "no Visual Studio here has CMake ($($all -join ', ')): add the 'C++ CMake tools for Windows' component in the Visual Studio Installer" }
}

function Test-CMake {
  $minimum = [version]'3.25'  # cmake_minimum_required in CMakeLists.txt
  $vs = Find-VsCMake
  # The cmake a run would use: the first on its PATH, as the build and every section's checkpoint use it.
  $found = Find-Tool 'cmake' $SearchPath
  if (-not $found) {
    Fail 'cmake is not on PATH' $vs.Fix
    return
  }
  $r = Invoke-Probe $found @('--version') $ProbeSeconds $SearchPath
  $text = ($r.Out -split "`n" | Select-Object -First 1) -as [string]
  if ($r.TimedOut -or $r.Code -ne 0 -or $text -notmatch 'cmake version (\d+\.\d+(?:\.\d+)?)') {
    Fail "cmake at $found doesn't run (exit $($r.Code), said `"$text`")" "remove or repair it, or put a working one first on PATH: $($vs.Fix)"
    return
  }
  $version = [version]$Matches[1]
  if ($version -lt $minimum) {
    Fail "cmake at $found is $version, older than the $minimum CMakeLists.txt needs" "put a newer one first on PATH: $($vs.Fix)"
    return
  }
  Pass "cmake $version at $found"
}

function Test-DenyList {
  # The campaign's deny list must be able to run here: under bypass mode nothing else stops what it refuses.
  $python = Find-Tool 'python' $SearchPath
  if (-not $python) { $python = Find-Tool 'python3' $SearchPath }
  if (-not $python) {
    Fail 'no python on PATH, so the deny list hook (scripts/tool-guard.py) can''t run' 'install Python 3 and put it on PATH'
    return
  }
  $r = Invoke-Probe $python @((Join-Path $PSScriptRoot 'tool-guard.py'), '--self-test') $ProbeSeconds $SearchPath
  if ($r.TimedOut -or $r.Code -ne 0) {
    Fail "the deny list's self-test failed: $(($r.Out -split "`n" | Where-Object { $_ -match 'FAIL' }) -join '; ')" 'run `python scripts/tool-guard.py --self-test` and fix what it names'
    return
  }
  Pass "deny list self-test ($(@($r.Out -split "`n").Count - 1) cases)"
}

function Test-Bosun {
  $fix = 'install it with `pwsh scripts/install.ps1`, which puts %LOCALAPPDATA%\Bosun\bin on the user PATH'
  $r = Probe 'bosun' @('status') $fix
  if (-not $r) { return }
  if ($r.Code -ne 0) { Fail "bosun status failed (exit $($r.Code)): $(Tail $r.Out)" $fix; return }
  Pass "bosun ($(Tail $r.Out))"
}

function Test-Dotnet {
  $fix = 'install the .NET 11 SDK: `winget install Microsoft.DotNet.SDK.Preview` (admin setup''s task replaces it with the final when it ships)'
  $r = Probe 'dotnet' @('--list-sdks') $fix
  if (-not $r) { return }
  $sdks = @($r.Out -split "`n" | Where-Object { $_ -match '^\s*11\.0\.1\d\d(-\S+)?\s+\[' } | ForEach-Object { ($_ -split '\s+')[0] })
  if ($r.Code -ne 0 -or -not $sdks) {
    $have = @($r.Out -split "`n" | Where-Object { $_ -match '^\s*\d' } | ForEach-Object { ($_ -split '\s+')[0] })
    Fail "no .NET 11 SDK (11.0.1xx) in dotnet --list-sdks (has: $(if ($have) { $have -join ', ' } else { 'none' }))" $fix
    return
  }
  $final = Test-FinalSdk $r.Out
  Pass ".NET 11 SDK $($sdks -join ', ')$(if (-not $final) { ' (a release candidate; the final installs when it ships)' })"
}

function Test-Gh {
  $r = Probe 'gh' @('auth', 'status') '`gh auth login`, then run the preflight again'
  if (-not $r) { return }
  if ($r.Code -ne 0) { Fail "gh is not signed in (gh auth status: $(Tail $r.Out))" '`gh auth login`, then run the preflight again'; return }
  Pass 'gh signed in'
}

# Claude as a run's session signs in: the user's CLAUDE_CODE_OAUTH_TOKEN from the registry, as a window opened from
# the shortcut gets it, and none of the variables that would bill an account. The token is passed, never printed.
function Test-Claude {
  $login = 'run `claude`, then /login with the subscription account, and run the preflight again'
  $long = 'in your own terminal run `claude setup-token` (a long-lived token for unattended jobs), then `pwsh scripts/store-claude-token.ps1` to keep it as your user variable CLAUDE_CODE_OAUTH_TOKEN, and run the preflight again'
  $token = Get-RegValue $UserEnvKey 'CLAUDE_CODE_OAUTH_TOKEN'
  $environment = @{ CLAUDE_CODE_OAUTH_TOKEN = $token }  # $null removes one this shell has but a new window wouldn't
  foreach ($k in 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY') { $environment[$k] = $null }
  $quiet = [IO.Path]::GetTempPath()  # outside any repo, so no project's hooks run
  $r = Probe 'claude' @('auth', 'status', '--json') $login $quiet $environment
  if (-not $r) { return }
  $status = $null
  if ($r.Out -match '(?s)\{.*\}') { try { $status = $Matches[0] | ConvertFrom-Json } catch { $status = $null } }
  if (-not $status -or -not $status.loggedIn) { Fail "claude is not signed in ($(Tail $r.Out))" $login; return }
  if ($status.apiProvider -ne 'firstParty') { Fail "claude is set to use $($status.apiProvider), which bills a cloud account" 'unset CLAUDE_CODE_USE_* and any provider in its settings, then run the preflight again'; return }
  switch ($status.authMethod) {
    'oauth_token' {
      # A setup token lasts a year from the day it was made, which nothing reports, so the store script records the day.
      $issued = $null
      try { $issued = [datetime]::ParseExact([string](Get-Content -LiteralPath $TokenRecord -Raw | ConvertFrom-Json).issued, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } catch { $issued = $null }
      if (-not $issued) { Fail "the stored token's issue day isn't recorded at $TokenRecord, so its year can't be checked" '`pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <the day you ran claude setup-token>`, then run the preflight again'; return }
      $ends = $issued.AddYears(1)
      $days = [math]::Floor(($ends - [datetime]::UtcNow.Date).TotalDays)
      if ($days -lt $MinLoginDays) { Fail "the stored token, made on $($issued.ToString('yyyy-MM-dd')), ends on $($ends.ToString('yyyy-MM-dd')), in $days days, sooner than the $MinLoginDays a campaign may need" $long; return }
      $how = "a long-lived token (claude setup-token), which ends on $($ends.ToString('yyyy-MM-dd'))"
    }
    'claude.ai' {
      $ends = $status.loginExpiresAt
      if ($ends -and $ends -isnot [datetime]) { try { $ends = [datetime]::Parse([string]$ends, [Globalization.CultureInfo]::InvariantCulture) } catch { $ends = $null } }
      if (-not $ends) { Fail "claude auth status doesn't say when the sign-in ends, so the campaign may lose it" $long; return }
      $days = [math]::Floor(($ends.ToUniversalTime() - [datetime]::UtcNow).TotalDays)
      if ($days -lt $MinLoginDays) { Fail "the Claude sign-in ends on $($ends.ToUniversalTime().ToString('yyyy-MM-dd')), in $days days, sooner than the $MinLoginDays a campaign may need" $long; return }
      $how = "the subscription sign-in, which ends on $($ends.ToUniversalTime().ToString('yyyy-MM-dd'))"
    }
    default { Fail "claude would sign in with $($status.authMethod), which bills an API account" "remove the API key or apiKeyHelper, then $login"; return }
  }
  # `auth status` reads a token as signed in without trying it, so only a real reply proves it works.
  $r = Probe 'claude' @('-p', 'Reply with the single word READY and nothing else.', '--model', 'haiku') $login $quiet $environment
  if (-not $r) { return }
  if ($r.Code -ne 0 -or $r.Out -notmatch 'READY') { Fail "claude signed in with $how but didn't reply READY (exit $($r.Code)): $(Tail $r.Out)" $(if ($status.authMethod -eq 'oauth_token') { "the token may be wrong or revoked: $long" } else { $login }); return }
  Pass "claude signed in with $how, and replied"
}

function Test-Codex {
  $fix = '`codex login`, then run the preflight again'
  $r = Probe 'codex' @('login', 'status') $fix
  if (-not $r) { return }
  if ($r.Code -ne 0 -or $r.Out -notmatch 'Logged in') { Fail "codex is not signed in ($(Tail $r.Out))" $fix; return }
  # A real reply from the pinned reviewer, through the same read-only exec the review dispatcher uses.
  $r = Probe 'codex' @('exec', '-m', $ReviewerModel, '-c', 'model_reasoning_effort=low', '--sandbox', 'read-only', '--ephemeral', '--skip-git-repo-check', 'Reply with the single word READY and nothing else.') "check ``codex exec -m $ReviewerModel`` answers in a shell: sign in again with ``codex login`` or wait out a usage limit"
  if (-not $r) { return }
  if ($r.Code -ne 0 -or $r.Out -notmatch 'READY') { Fail "$ReviewerModel didn't reply READY (exit $($r.Code)): $(Tail $r.Out)" "check ``codex exec -m $ReviewerModel`` answers in a shell: sign in again with ``codex login`` or wait out a usage limit"; return }
  Pass "codex signed in, and $ReviewerModel replied"
}

function Test-Wsl {
  $utf8 = @{ WSL_UTF8 = '1' }
  $r = Probe 'wsl' @('-d', $Distro, '-e', 'true') "``wsl --install -d $Distro``" '' $utf8
  if (-not $r) { return }
  if ($r.Code -ne 0) { Fail "WSL $Distro doesn't start (exit $($r.Code)): $(Tail $r.Out)" "``wsl --install -d $Distro``, or ``wsl --shutdown`` and try again"; return }
  $full = (Resolve-Path $Repo).Path
  $mnt = '/mnt/' + $full.Substring(0, 1).ToLower() + ($full.Substring(2) -replace '\\', '/')
  $r = Probe 'wsl' @('-d', $Distro, '-e', 'test', '-d', "$mnt/.git") '`wsl --shutdown`, then try again' '' $utf8
  if (-not $r) { return }
  if ($r.Code -ne 0) { Fail "$Distro can't see this repo at $mnt" "``wsl --shutdown`` then try again; if it persists, check /etc/wsl.conf in $Distro doesn't disable automount"; return }
  Pass "WSL $Distro sees $mnt"
}

# --- The repository ----------------------------------------------------------------------------------------------

function Git([string[]]$arguments) { Probe 'git' (@('-C', $Repo) + $arguments) 'install Git for Windows and put it on PATH' }

function Test-Tree {
  $r = Git @('status', '--porcelain')
  if (-not $r) { return }
  if ($r.Code -ne 0) { Fail "git status failed in ${Repo}: $(Tail $r.Out)" "check $Repo is a git checkout"; return }
  if ($r.Out) { Fail "the tree at $Repo has uncommitted changes ($(@($r.Out -split "`n").Count) paths)" 'commit or stash them (`git status` lists them), then run the preflight again'; return }
  Pass 'clean tree'
}

function Test-Pushed {
  $head = Git @('rev-parse', 'HEAD')
  if (-not $head) { return }
  $remote = Git @('ls-remote', 'origin', 'refs/heads/main')
  if (-not $remote) { return }
  if ($remote.Code -ne 0 -or $remote.Out -notmatch '^([0-9a-f]{40})\s') {
    Fail "the remote's main can't be read now (git ls-remote origin: $(Tail $remote.Out))" '`gh auth status`, or check `git remote -v` points at the repository, then run the preflight again'
    return
  }
  $theirs = $Matches[1]
  $ours = $head.Out.Trim()
  if ($theirs -eq $ours) { Pass "HEAD $($ours.Substring(0, 12)) is main on the remote"; return }
  $ancestor = Git @('merge-base', '--is-ancestor', $theirs, $ours)
  if ($ancestor -and $ancestor.Code -eq 0) {
    Fail "HEAD $($ours.Substring(0, 12)) has commits the remote's main ($($theirs.Substring(0, 12))) doesn't" '`git push origin HEAD:main`, then run the preflight again'
  } else {
    Fail "the remote's main moved on to $($theirs.Substring(0, 12)), which this clone doesn't hold" '`git pull --ff-only`, then run the preflight again'
  }
}

# Whether main on the remote refuses a force push and its own deletion, for everyone, admins included: the deny list
# and the dispatch hook catch a session's own spellings of those, but only the remote's protection makes them
# impossible (D00 T03 §17). Asked of GitHub through gh, for the repository and host this checkout's origin names,
# spelled out in the request: gh would otherwise take GH_REPO or GH_HOST from the environment, and could read the
# protection of another repository and pass.
function Test-Protection {
  $origin = Git @('remote', 'get-url', 'origin')
  if (-not $origin) { return }
  $url = $origin.Out.Trim()
  if ($origin.Code -ne 0 -or $url -notmatch '^(?:https://(?:[^@/]+@)?|ssh://git@|git@)([^/:]+)[/:]([^/]+)/([^/]+?)(?:\.git)?/?$') {
    Fail "this checkout's origin ($url) is not a GitHub repository, so main's protection can't be checked" 'point origin at the GitHub repository (`git remote set-url origin <url>`), then run the preflight again'
    return
  }
  $hostName, $owner, $name = $Matches[1], $Matches[2], $Matches[3]
  $endpoint = "repos/$owner/$name/branches/main/protection"
  $undo = "to put main back unprotected: gh api --hostname $hostName --method DELETE $endpoint"
  $fix = "turn it on: gh api --hostname $hostName --method PUT $endpoint with enforce_admins true and allow_force_pushes and allow_deletions false (D00 T03 §17), then run the preflight again"
  $r = Probe 'gh' @('api', '--hostname', $hostName, $endpoint) $fix $Repo @{ GH_REPO = $null; GH_HOST = $null; GH_ENTERPRISE_TOKEN = $null }
  if (-not $r) { return }
  if ($r.Code -ne 0) {
    if ($r.Out -match 'Branch not protected') { Fail "main on the remote is not protected: a force push or a deletion of main would be accepted" $fix }
    else { Fail "main's protection can't be read (gh api: $(Tail $r.Out))" '`gh auth status`, and check that this account may read the repository''s settings, then run the preflight again' }
    return
  }
  try { $got = $r.Out | ConvertFrom-Json -AsHashtable -ErrorAction Stop } catch { $got = $null }
  if ($got -isnot [Collections.IDictionary]) { Fail "main's protection came back as something other than GitHub's JSON: $(Tail $r.Out)" '`gh api repos/{owner}/{repo}/branches/main/protection` by hand, then run the preflight again'; return }
  # Each is an object with an `enabled` boolean; anything else (missing, null, a string) is not a yes.
  function Get-Enabled([string]$name) {
    $value = $got[$name]
    if ($value -is [Collections.IDictionary] -and $value['enabled'] -is [bool]) { return $value['enabled'] }
    return $null
  }
  $admins, $force, $delete = (Get-Enabled 'enforce_admins'), (Get-Enabled 'allow_force_pushes'), (Get-Enabled 'allow_deletions')
  $wrong = @()
  if ($null -eq $admins -or $null -eq $force -or $null -eq $delete) {
    $absent = @(@{ enforce_admins = $admins; allow_force_pushes = $force; allow_deletions = $delete }.GetEnumerator() | Where-Object { $null -eq $_.Value } | ForEach-Object Key | Sort-Object)
    Fail "main's protection does not say $($absent -join ', ') (GitHub's answer lacks them, or not as true or false)" $fix
    return
  }
  if (-not $admins) { $wrong += 'admins are not held to it (enforce_admins is off), so the account the run pushes as could still force-push' }
  if ($force) { $wrong += 'force pushes are allowed' }
  if ($delete) { $wrong += 'deleting main is allowed' }
  if ($wrong) { Fail "main's protection is partial: $($wrong -join '; ')" $fix; return }
  Pass "main of $owner/$name is protected: force pushes and deletion refused, admins included ($undo)"
}

function Test-Ci {
  $r = Probe 'python' @((Join-Path $Repo 'scripts\ci_status.py'), '--sha', 'HEAD', '--use-proven') 'install Python 3 and put it on PATH' $Repo
  if (-not $r) { return }
  $why = Tail $r.Out
  switch ($r.Code) {
    0 { Pass "CI green on HEAD ($why)" }
    1 { Fail "CI is red on HEAD: $why" 'fix the failing run (`python scripts/ci_status.py --sha HEAD` names it), push, then run the preflight again' }
    3 { Fail "CI can't be read: $why" '`gh auth status`, then run the preflight again' }
    4 { Fail "CI is still running on HEAD: $why" 'wait for it (`gh run watch`), then run the preflight again' }
    5 {
      $start = @($r.Out -split "`n" | Where-Object { $_ -match '^to get a real run: ' } | ForEach-Object { ($_ -replace '^to get a real run: ', '').Trim() })
      # ci_status.py offers the command only while the remote branch is at HEAD, and says so when it is not.
      $how = if ($start) { "start a run: $($start -join '; ')" }
      elseif ($r.Out -match 'has moved on to') { 'git pull --ff-only' }
      elseif ($r.Out -match 'no run was started:') { '`gh auth status`' }
      else { 'push HEAD so CI runs on it' }
      Fail "CI has no run for HEAD: $why" "$how, then run the preflight again"
    }
    default { Fail "ci_status.py failed (exit $($r.Code)): $why" 'run `python scripts/ci_status.py --sha HEAD --use-proven` and fix what it says' }
  }
}

function Test-Guard {
  # The guard holds a run only through its hooks, in the session's own window: nothing is scheduled, so what can be
  # wrong is a state it can't read or a hook that failed open.
  $fix = 'run `python scripts/run-guard.py status` and fix what it says'
  $r = Probe 'python' @((Join-Path $Repo 'scripts\run-guard.py'), 'status', '--json') 'install Python 3 and put it on PATH' $Repo
  if (-not $r) { return }
  $status = $null
  if ($r.Code -eq 0 -and $r.Out -match '(?s)\{.*\}') { try { $status = $Matches[0] | ConvertFrom-Json } catch { $status = $null } }
  if (-not $status) { Fail "the run guard gave no status (exit $($r.Code)): $(Tail $r.Out)" $fix; return }
  if ($status.state -eq 'unreadable') { Fail "the run guard can't read its state: $($status.state_error)" $fix; return }
  $failures = @($status.hook_failures)
  if ($failures) { Fail "a run guard hook failed open, so it would not hold a run: $($failures[-1].error)" $fix; return }
  Pass "run guard healthy (run-guard.py status: $($status.state))"
}

# --- The machine -------------------------------------------------------------------------------------------------

function Test-Space {
  foreach ($d in $FreeDrives) {
    try { $free = (New-Object IO.DriveInfo $d).AvailableFreeSpace / 1GB }
    catch { Fail "drive $d can't be read ($($_.Exception.Message))" "check $d is attached, or name the drives to check with -FreeDrives"; continue }
    if ($free -lt $MinFreeGB) {
      Fail ("drive $d has {0:N0} GB free, under the {1:N0} GB the campaign needs" -f $free, $MinFreeGB) "free space on $d"
    } else {
      Pass ("drive $d has {0:N0} GB free" -f $free)
    }
  }
}

function Test-Hold {
  $fix = '`pwsh scripts/campaign-hold.ps1 -Apply`'
  try { $saved = Read-Json $HoldRecord } catch { Fail $_.Exception.Message $fix; return }
  if (-not $saved) { Fail "the hold isn't applied (no record at $HoldRecord)" $fix; return }
  # The values Windows obeys are the active plan's, so a plan switched to after the hold isn't held at all.
  try { $active = Get-ActiveScheme $PowerCfg } catch { Fail "the active power plan can't be read: $($_.Exception.Message)" $fix; return }
  if ($active -ne $saved.Scheme) { Fail "the active power plan is $active, not the held $($saved.Scheme), so its sleep and display timeouts apply" "``powercfg /setactive $($saved.Scheme)``, then run the preflight again"; return }
  try { $now = Get-HoldState $PowerCfg $LockKey $saved.Scheme } catch { Fail "the held values can't be read: $($_.Exception.Message)" $fix; return }
  $drift = @()
  foreach ($p in $now.Power) { foreach ($line in 'AC', 'DC') { if ($p.$line -ne 0) { $drift += "$($p.Setting) $line is $($p.$line)" } } }
  if ($now.Lock -ne '0') { $drift += "ScreenSaverIsSecure is $($now.Lock)" }
  if ($drift) { Fail "the hold drifted: $($drift -join '; ')" $fix; return }
  Pass 'hold applied: no sleep, hibernate or display timeout, no screen saver lock'
}

function Test-Setup {
  $fix = "in an elevated shell: ``pwsh $(Join-Path $PSScriptRoot 'campaign-admin-setup.ps1') -Apply``"
  $dir = $DotnetDir
  if (-not $dir) { $dir = Resolve-RunDotnet $SearchPath }
  if (-not $dir) { Fail 'no dotnet.exe on a run''s PATH, so the SDK task has no folder' 'install the .NET SDK, then run admin setup'; return }
  $wrong = @()
  try { $now = Get-UpdateState $PolicyKey } catch { Fail "the update policy can't be read: $($_.Exception.Message)" $fix; return }
  foreach ($name in $UpdateValues.Keys) {
    if ($now[$name].Value -ne $UpdateValues[$name]) { $wrong += "$name is $(if ($now[$name].Present) { $now[$name].Value } else { 'absent' }), not $($UpdateValues[$name])" }
  }
  $defs = Get-CampaignTaskDefs $BinDir $dir $TaskPrefix
  foreach ($d in $defs) {
    try { $live = Get-CampaignTask $d.Name $TaskStore } catch { $wrong += "task '$($d.Name)' can't be read: $($_.Exception.Message)"; continue }
    if (-not $live -and $d.Name -eq $defs[0].Name) {
      # The SDK task removes itself once the final SDK is in its folder; gone with no final SDK is a fault.
      $dotnet = Find-Tool 'dotnet' $dir
      $list = if ($dotnet) { (Invoke-Probe $dotnet @('--list-sdks') $ProbeSeconds $SearchPath).Out } else { '' }
      if (Test-FinalSdk $list) { continue }
      $wrong += "task '$($d.Name)' not registered, and $dir has no final 11.0.1xx SDK that would explain it"
      continue
    }
    foreach ($x in (Compare-CampaignTask $d $live)) { $wrong += "task '$($d.Name)' $x" }
  }
  if ($wrong) { Fail "admin setup isn't in place: $($wrong -join '; ')" $fix; return }
  Pass "admin setup in place: update restarts held, task '$($defs[0].Name)' as defined or finished"
}

function Test-Lock {
  try { $secs = Get-RegValue $InactivityKey 'InactivityTimeoutSecs' } catch { Fail "the inactivity policy can't be read: $($_.Exception.Message)" 'run the preflight in a normal user shell'; return }
  if ($secs -and [int]$secs -gt 0) {
    Fail "a machine policy locks this PC after $secs seconds idle (InactivityTimeoutSecs)" "ask whoever manages this PC (Intune) to lift it, or set $InactivityKey InactivityTimeoutSecs to 0 in an elevated shell"
    return
  }
  Pass 'no machine inactivity lock'
}

function Test-Owner {
  $fix = "the owner confirms it, and it is recorded in $Acceptance"
  if (-not (Test-Path -LiteralPath $Acceptance)) { Fail "no owner record at $Acceptance (extra usage off, reboot check)" $fix; return }
  $text = [IO.File]::ReadAllText($Acceptance)
  $missing = @()
  if ($text -notmatch '(?m)^- Extra usage: off\b[^\r\n]*\b\d{4}-\d{2}-\d{2}\b') { $missing += 'extra usage off, with a date, in the Claude account''s settings' }
  if ($text -notmatch '(?m)^- Reboot check: passed\b[^\r\n]*\b\d{4}-\d{2}-\d{2}\b') { $missing += 'the reboot check passed, with a date' }
  if ($missing) { Fail "the owner's record doesn't show $($missing -join ', or ')" $fix; return }
  Pass 'owner record: extra usage off, reboot check passed'
}

$Checks = [ordered]@{
  bosun = { Test-Bosun }; cmake = { Test-CMake }; denylist = { Test-DenyList }; dotnet = { Test-Dotnet }; gh = { Test-Gh }
  claude = { Test-Claude }; codex = { Test-Codex }; wsl = { Test-Wsl }; tree = { Test-Tree }; pushed = { Test-Pushed }; protection = { Test-Protection }; ci = { Test-Ci }; guard = { Test-Guard }
  space = { Test-Space }; hold = { Test-Hold }; setup = { Test-Setup }; lock = { Test-Lock }; owner = { Test-Owner }
}
$names = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$unknown = @($names | Where-Object { -not $Checks.Contains($_) })
if ($unknown) { "unknown check(s): $($unknown -join ', '); the checks are $($Checks.Keys -join ', ')"; exit 2 }
if (-not $names) { $names = @($Checks.Keys) }
foreach ($n in $names) { & $Checks[$n] }
if ($failed) {
  "$failed check(s) failed"
  exit 1
}
# A subset passing proves only those checks, so it says so rather than clearing the campaign.
if ($Only) { "checks passed: $($names -join ', ') (a subset; run without -Only for the full preflight)"; exit 0 }
'preflight passed'
