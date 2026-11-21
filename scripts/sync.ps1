# Shows and syncs this checkout and its private overlay in one command (D00 T05 §3).
#
#   pwsh scripts/sync.ps1 -Check   one line per repository, changes nothing; exit 0 when both are level and clean
#   pwsh scripts/sync.ps1          the same, then fast-forwards what is behind and pushes what is ahead, all or nothing
#
# The checkout is the one holding this script, and the overlay its .private folder, wherever the command is run from.
# Each line reads `public: <branch>, <state>` or `overlay: <branch>, <state>`. A plain run changes nothing at all
# when either repository is in a state it does not act on (diverged, uncommitted changes, a detached HEAD, no
# upstream or a deleted one, an unreachable remote, a .private that is not the overlay, an overlay whose remote
# points somewhere else, or a git command that failed): it says why and exits 1. Otherwise it works on the overlay
# first, then on this checkout: a fast-forward that never replaces an ignored file, a push of the current branch to
# its own upstream branch only, and an empty overlay's first commit. It never commits, stashes or stages. Every run
# ends with both lines read again. Exit 0 when both end level and clean (no overlay counts as clean), 1 otherwise.
[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')

if (-not (Get-Command git -CommandType Application -ErrorAction Ignore)) { 'sync: git is not on PATH: install Git for Windows, then run this again'; exit 1 }

# Git takes these from the environment over -C, so a caller's GIT_DIR would point every command below elsewhere. They
# are cleared for this run and put back when it ends, since a script run with & shares its caller's environment.
$gitVariables = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX'
$callerGit = @{}
foreach ($name in $gitVariables) {
  $callerGit[$name] = [Environment]::GetEnvironmentVariable($name)
  Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore
}
try {
$publicRoot = Split-Path $PSScriptRoot
$overlayRoot = Join-Path $publicRoot '.private'

function Invoke-Git([string]$repo, [string[]]$arguments) {
  $out = & git -C $repo @arguments 2>$null
  [pscustomobject]@{ Code = $LASTEXITCODE; Out = @($out | ForEach-Object { "$_" }) }
}

# One repository's state: the line to print, and what a plain run would do (merge, push, first) or why it won't act.
function Get-RepoState([string]$label, [string]$repo, [switch]$Overlay) {
  $state = [ordered]@{ Label = $label; Repo = $repo; Branch = $null; Text = $null; Ok = $false; Action = 'none'; Remote = $null; Upstream = $null; Default = $null; Commit = $null; Target = $null; RemoteTip = $null; PushUrl = $null; FetchUrls = $null; PushUrls = $null }
  function Done([string]$text, [bool]$ok = $false, [string]$action = 'none') { $state.Text = $text; $state.Ok = $ok; $state.Action = $action; [pscustomobject]$state }

  if ($Overlay) {
    if (-not (Get-Item -LiteralPath $repo -Force -ErrorAction Ignore)) { return Done 'not present' $true }
    $fault = Get-OverlayCloneFault $repo
    if ($fault) { return Done "not the overlay ($fault)" }
    if ((Invoke-Git $repo @('config', '--local', '--get', 'bosun.overlay')).Out -join '' -ne 'true') { return Done 'not the overlay (not marked; pwsh scripts/setup-clone.ps1 marks a checkout of it)' }
  }
  $branch = Invoke-Git $repo @('symbolic-ref', '--quiet', 'HEAD')
  if ($branch.Code) { return Done 'detached HEAD' }
  # The full name, with refs/heads/ taken off: --short abbreviates to heads/main when a tag is also called main.
  $state.Branch = ($branch.Out -join '') -replace '^refs/heads/', ''
  $born = (Invoke-Git $repo @('rev-parse', '--verify', '--quiet', 'HEAD')).Code -eq 0
  $remote = (Invoke-Git $repo @('config', '--local', '--get', "branch.$($state.Branch).remote")).Out -join ''
  $merge = (Invoke-Git $repo @('config', '--local', '--get', "branch.$($state.Branch).merge")).Out -join ''
  if (-not $remote -and $Overlay -and -not $born) { $remote = 'origin' }
  if (-not $remote -or $remote -eq '.') { return Done 'no upstream branch' }
  $state.Remote = $remote

  if ($Overlay) {
    # The mark says which checkout this is, not where a push sends its commits: every fetch and push address of the
    # remote must be the overlay source itself.
    $looked = Get-BosunOverlayValue -Key overlay-repo -Root $publicRoot -VariableOnly
    $source = Get-OverlaySource $(if ($looked.Source -eq 'setup-required') { 'rizonetech/Bosun-private' } else { $looked.Value })
    if (-not $source) { return Done 'not the overlay (BOSUN_OVERLAY_REPO is not a supported overlay source)' }
    $state.FetchUrls = @((Invoke-Git $repo @('remote', 'get-url', '--all', $remote)).Out | Where-Object { $_ })
    $state.PushUrls = @((Invoke-Git $repo @('remote', 'get-url', '--push', '--all', $remote)).Out | Where-Object { $_ })
    $urls = @($state.FetchUrls) + @($state.PushUrls)
    if (-not $state.FetchUrls -or -not $state.PushUrls -or @($urls | Where-Object { -not (Test-OverlayOrigin $_ $source) }).Count) { return Done 'push goes elsewhere (its remote is not the overlay source)' }
  } else {
    $state.PushUrls = @((Invoke-Git $repo @('remote', 'get-url', '--push', '--all', $remote)).Out | Where-Object { $_ })
  }

  # Read locally before the fetch: a damaged index fails the fetch too, and must be named as what it is.
  # --no-optional-locks: status may otherwise refresh and rewrite the index, and -Check changes nothing.
  $status = Invoke-Git $repo @('--no-optional-locks', 'status', '--porcelain', '--untracked-files=normal')
  if ($status.Code) { return Done 'could not be read (status)' }
  $dirty = [bool]($status.Out -join '')
  # The one branch needed is asked of the remote, then fetched by an explicit refspec into its tracking ref: no
  # configured refspec or mapping (--refmap= empties the mappings git would otherwise add for the same branch, such as
  # one into a tag), prune or pruneTags setting applies, no tag is fetched and FETCH_HEAD is left alone.
  function Get-Branch([string]$name) {
    $remoteRefs = Invoke-Git $repo @('ls-remote', '--exit-code', '--heads', $remote, "refs/heads/$name")
    if ($remoteRefs.Code -eq 2) { return 'gone' }
    if ($remoteRefs.Code) { return 'unreachable' }
    $tip = (($remoteRefs.Out | Select-Object -First 1) -split '\s+')[0]
    $fetched = Invoke-Git $repo @('fetch', '--quiet', '--no-prune', '--no-tags', '--no-write-fetch-head', '--refmap=', $remote, "+refs/heads/${name}:refs/remotes/$remote/$name")
    # The remote answered just above, so a fetch that fails now could not store what it got (a lock, permissions, disk).
    if ($fetched.Code) { return 'unstored' }
    if ((Invoke-Git $repo @('rev-parse', '--verify', '--quiet', "refs/remotes/$remote/$name")).Out -join '' -ne $tip) { return 'moved' }
    $tip
  }

  if (-not $born) {
    # An overlay cloned while empty: its first commit lands on the remote's default branch, whatever its name, as the
    # remote reports it (a fetch refspec naming another branch cannot hide it).
    $lookup = Invoke-Git $repo @('ls-remote', '--symref', $remote, 'HEAD')
    if ($lookup.Code) { return Done 'remote unreachable' }
    $head = $lookup.Out | Where-Object { $_ -match '^ref: refs/heads/(\S+)\s+HEAD$' } | Select-Object -First 1
    if ($head -and $head -match '^ref: refs/heads/(\S+)') {
      $state.Default = $Matches[1]
      $tip = Get-Branch $state.Default
      if ($tip -eq 'unreachable') { return Done 'remote unreachable' }
      if ($tip -eq 'moved') { return Done 'could not be read (its remote moved while it was read)' }
      if ($tip -eq 'unstored') { return Done 'could not be read (what it fetched could not be stored: a lock, permissions or a full disk)' }
      if ($tip -ne 'gone') {
        $state.Target = $tip
        if ($dirty) { return Done "empty, first commit waiting on $($state.Default), uncommitted changes" }
        return Done "empty, first commit waiting on $($state.Default)" $false 'first'
      }
    }
    # No default branch reported: the overlay is empty only if the remote has no branch at all.
    $any = Invoke-Git $repo @('ls-remote', '--heads', $remote)
    if ($any.Code) { return Done 'remote unreachable' }
    if ($any.Out -join '') { return Done 'empty, its remote has branches but no default branch to take (set the remote''s HEAD)' }
    if ($dirty) { return Done 'empty, no commit yet, uncommitted changes' }
    return Done 'empty, no commit yet' $true
  }
  if (-not $merge) { return Done 'no upstream branch' }
  $state.Upstream = $merge -replace '^refs/heads/', ''
  $tip = Get-Branch $state.Upstream
  if ($tip -eq 'gone') { return Done 'upstream branch gone (its remote branch was deleted)' }
  if ($tip -eq 'unreachable') { return Done 'remote unreachable' }
  if ($tip -eq 'moved') { return Done 'could not be read (its remote moved while it was read)' }
  if ($tip -eq 'unstored') { return Done 'could not be read (what it fetched could not be stored: a lock, permissions or a full disk)' }
  # The tip the remote has now, which a push is conditioned on below.
  $state.RemoteTip = $tip
  # Every input a plain run acts on is fixed here, once, and the counts are of these exact commits: the local commit,
  # the remote tip, and the one address a push would go to. Nothing later reads HEAD, the remote or its URL again.
  $state.Commit = (Invoke-Git $repo @('rev-parse', '--verify', '--quiet', 'HEAD')).Out -join ''
  $state.Target = $state.RemoteTip
  # A push goes through the remote's name, so git sends it to the very address `remote get-url --push` reported (with
  # any insteadOf or pushInsteadOf rewrite applied, and for the overlay validated above), and no URL, with or without a
  # credential in it, reaches a command line. A URL given straight to git push would be rewritten again.
  $state.PushUrl = if (@($state.PushUrls).Count -eq 1) { $state.PushUrls[0] } else { $null }
  $counts = Invoke-Git $repo @('rev-list', '--left-right', '--count', "$($state.Commit)...$($state.Target)")
  if (-not $state.Commit -or $counts.Code -or ($counts.Out -join '') -notmatch '^(\d+)\s+(\d+)$') { return Done 'could not be read (compare)' }
  $ahead, $behind = [int]$Matches[1], [int]$Matches[2]
  $parts = @()
  if ($ahead -and $behind) { $parts += "diverged (ahead $ahead, behind $behind)" }
  elseif ($ahead) { $parts += "ahead $ahead" }
  elseif ($behind) { $parts += "behind $behind" }
  if ($dirty) { $parts += 'uncommitted changes' }
  if (-not $parts) { return Done 'level and clean' $true }
  $action = if ($dirty -or ($ahead -and $behind)) { 'none' } elseif ($ahead) { 'push' } else { 'merge' }
  if ($action -eq 'push' -and -not $state.PushUrl) { return Done "$($parts -join ', '), pushes to more than one address" }
  Done ($parts -join ', ') $false $action
}

function Get-States { @((Get-RepoState 'overlay' $overlayRoot -Overlay), (Get-RepoState 'public' $publicRoot)) }
function Write-States($states) {
  foreach ($s in $states[1], $states[0]) {
    if ($s.Branch) { "$($s.Label): $($s.Branch), $($s.Text)" } else { "$($s.Label): $($s.Text)" }
  }
}

$states = Get-States
if ($Check) {
  Write-States $states
  exit $(if (@($states | Where-Object { -not $_.Ok }).Count) { 1 } else { 0 })
}

# All or nothing: one repository in a state this run does not act on means neither is changed.
$blocked = @($states | Where-Object { -not $_.Ok -and $_.Action -eq 'none' })
if ($blocked) {
  # Nothing is synced, but the agent rules still follow .private as it is: an overlay unmarked or removed loses
  # its import here too (D00 T05 §5).
  $rules = Update-BosunAgentRules $publicRoot
  if ($rules -eq 'removed') { 'sync: agent rules: the import of .private/AGENTS.md was taken out of CLAUDE.local.md' }
  Write-States $states
  foreach ($s in $blocked) { "sync: $(if ($rules -eq 'removed') { 'nothing was synced' } else { 'nothing was changed' }), because $($s.Label) is $($s.Text)" }
  exit 1
}

$done = @()
$failed = $null
$undo = $null
# The public fast-forward runs scripts/hooks/post-merge, which finds the overlay already dealt with: this run never
# fast-forwards the public checkout while it refuses the overlay. Its output, like git's, is not shown.
foreach ($s in $states | Where-Object { $_.Action -ne 'none' }) {
  $step = switch ($s.Action) {
    'merge' { @{ Did = "fast-forwarded $($s.Label)"; Args = @('-c', "branch.$($s.Branch).mergeOptions=", '-c', 'merge.autoStash=false', 'merge', '--ff-only', '--no-overwrite-ignore', '--no-squash', '--quiet', $s.Target); Why = 'the fast-forward was refused (an incoming file would replace an ignored or local one, or the branch moved meanwhile)' } }
    'push' { @{ Did = "pushed $($s.Label) to its upstream branch"; Args = @('-c', 'push.followTags=false', 'push', '--quiet', '--no-follow-tags', "--force-with-lease=refs/heads/$($s.Upstream):$($s.RemoteTip)", $s.Remote, "$($s.Commit):refs/heads/$($s.Upstream)"); Why = 'the push was refused (its upstream moved on or was deleted meanwhile, or it does not accept this push)' } }
    'first' { @{ Did = "gave $($s.Label) its first commit"; Args = @('checkout', '--quiet', '--no-overwrite-ignore', '-b', $s.Default, $s.Target); Why = 'its first commit could not be checked out (an incoming file would replace an ignored one)' } }
  }
  # The checkout must still be where it was looked at: a branch switched or a commit made meanwhile stops the run.
  if ($s.Action -ne 'first' -and ((Invoke-Git $s.Repo @('rev-parse', 'HEAD')).Out -join '' -ne $s.Commit -or (((Invoke-Git $s.Repo @('symbolic-ref', '--quiet', 'HEAD')).Out -join '') -replace '^refs/heads/', '') -ne $s.Branch)) {
    $failed = "sync: $($s.Label): it changed while sync ran (another branch or commit); nothing more was done"; break
  }
  # Still clean, as it was when looked at: an edit saved meanwhile, or a status that cannot be read now (a locked
  # index), stops the run before the branch moves.
  $again = Invoke-Git $s.Repo @('--no-optional-locks', 'status', '--porcelain', '--untracked-files=normal')
  if ($again.Code) { $failed = "sync: $($s.Label): its status could not be read again before acting; nothing more was done"; break }
  if ($again.Out -join '') {
    $failed = "sync: $($s.Label): it has uncommitted changes made while sync ran; nothing more was done"; break
  }
  # The address a push goes to is still the one read when it was looked at.
  if ($s.Action -eq 'push' -and "$((Invoke-Git $s.Repo @('remote', 'get-url', '--push', '--all', $s.Remote)).Out -join "`n")" -ne $s.PushUrl) {
    $failed = "sync: $($s.Label): its push address changed while sync ran; nothing more was done"; break
  }
  # A push only ever moves its upstream forward: the tip it was leased on must be in the commit it sends.
  if ($s.Action -eq 'push' -and (Invoke-Git $s.Repo @('merge-base', '--is-ancestor', $s.RemoteTip, $s.Commit)).Code) {
    $failed = "sync: $($s.Label): its commit no longer contains its upstream; nothing more was done"; break
  }
  # No git or remote text is shown: a remote's message can carry a URL with a credential in it.
  # The first commit's branch records its upstream before the checkout, set directly (--track would need a fetch
  # refspec matching it): a run stopped between the two can always be run again, and an unborn overlay retries cleanly.
  if ($s.Action -eq 'first' -and ((Invoke-Git $s.Repo @('config', '--local', "branch.$($s.Default).remote", $s.Remote)).Code -or (Invoke-Git $s.Repo @('config', '--local', "branch.$($s.Default).merge", "refs/heads/$($s.Default)")).Code)) {
    $failed = "sync: $($s.Label): the upstream of its first commit's branch could not be recorded (its git config is locked or read-only)"; break
  }  if ($s.Action -eq 'push') {
    # scripts/hooks/pre-push refuses a push holding a secret or a private string; its own lines (leak-scan:, holding no
    # remote text) are passed on so the finding or the missing-tool remedy is seen. Nothing else git says is shown.
    $said = & git -C $s.Repo @($step.Args) 2>&1
    $pushed = -not $LASTEXITCODE
    @($said | ForEach-Object { "$_" } | Where-Object { $_ -match '^leak-scan: ' })
    if (-not $pushed) { $failed = "sync: $($s.Label): $($step.Why)"; break }
  } elseif ((Invoke-Git $s.Repo $step.Args).Code) { $failed = "sync: $($s.Label): $($step.Why)"; break }

  # The step is judged by what it left, not by git's exit code alone: a setting that changes what git does (a squash
  # in branch.<name>.mergeOptions, a rewrite of the push address) cannot pass for success.
  $result = if ($s.Action -eq 'push') {
    $now = Invoke-Git $s.Repo @('ls-remote', '--heads', $s.Remote, "refs/heads/$($s.Upstream)")
    ((($now.Out | Select-Object -First 1) -split '\s+')[0]) -eq $s.Commit
  } else {
    ((Invoke-Git $s.Repo @('rev-parse', 'HEAD')).Out -join '') -eq $s.Target -and
      -not ((Invoke-Git $s.Repo @('--no-optional-locks', 'status', '--porcelain', '--untracked-files=normal')).Out -join '')
  }
  if (-not $result) { $failed = "sync: $($s.Label): the $($s.Action) did not leave what it should (a git setting changed what it did); see: git -C $(if ($s.Label -eq 'overlay') { '.private' } else { '.' }) status"; break }
  $done += $step.Did
  if ($s.Action -eq 'first') { $undo = "to undo its upstream setting: git -C .private config --unset branch.$($s.Default).remote; git -C .private config --unset branch.$($s.Default).merge" }
}

if ($failed) {
  $failed
  "sync: done before it: $(if ($done) { $done -join '; ' } else { 'nothing' })"
}
elseif ($done) { "sync: $($done -join '; ')" }
if ($undo) { "sync: $undo" }
# The overlay's agent rules follow .private as this run left it (D00 T05 §5).
$rules = Update-BosunAgentRules $publicRoot
if ($rules -eq 'added') { 'sync: agent rules: CLAUDE.local.md now imports .private/AGENTS.md' }
elseif ($rules -eq 'removed') { 'sync: agent rules: the import of .private/AGENTS.md was taken out of CLAUDE.local.md' }
$states = Get-States
Write-States $states
exit $(if ($failed -or @($states | Where-Object { -not $_.Ok }).Count) { 1 } else { 0 })
} finally {
  foreach ($name in $gitVariables) { if ($null -ne $callerGit[$name]) { Set-Item -LiteralPath "Env:$name" -Value $callerGit[$name] } }
}
