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
  $state = [ordered]@{ Label = $label; Repo = $repo; Branch = $null; Text = $null; Ok = $false; Action = 'none'; Remote = $null; Upstream = $null; Default = $null; Commit = $null; Target = $null }
  function Done([string]$text, [bool]$ok = $false, [string]$action = 'none') { $state.Text = $text; $state.Ok = $ok; $state.Action = $action; [pscustomobject]$state }

  if ($Overlay) {
    if (-not (Get-Item -LiteralPath $repo -Force -ErrorAction Ignore)) { return Done 'not present' $true }
    $fault = Get-OverlayCloneFault $repo
    if ($fault) { return Done "not the overlay ($fault)" }
    if ((Invoke-Git $repo @('config', '--local', '--get', 'bosun.overlay')).Out -join '' -ne 'true') { return Done 'not the overlay (not marked; pwsh scripts/setup-clone.ps1 marks a checkout of it)' }
  }
  $branch = Invoke-Git $repo @('symbolic-ref', '--quiet', '--short', 'HEAD')
  if ($branch.Code) { return Done 'detached HEAD' }
  $state.Branch = $branch.Out -join ''
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
    $urls = @((Invoke-Git $repo @('remote', 'get-url', '--all', $remote)).Out) + @((Invoke-Git $repo @('remote', 'get-url', '--push', '--all', $remote)).Out)
    if (-not $urls -or @($urls | Where-Object { -not (Test-OverlayOrigin $_ $source) }).Count) { return Done 'push goes elsewhere (its remote is not the overlay source)' }
  }

  # Read locally before the fetch: a damaged index fails the fetch too, and must be named as what it is.
  # --no-optional-locks: status may otherwise refresh and rewrite the index, and -Check changes nothing.
  $status = Invoke-Git $repo @('--no-optional-locks', 'status', '--porcelain', '--untracked-files=normal')
  if ($status.Code) { return Done 'could not be read (status)' }
  $dirty = [bool]($status.Out -join '')
  # Branches are pruned so a deleted upstream shows as gone; tags never are (a fetch.pruneTags setting would delete a
  # local-only tag), and none are fetched.
  if ((Invoke-Git $repo @('-c', 'fetch.pruneTags=false', 'fetch', '--quiet', '--prune', '--no-tags', $remote)).Code) { return Done 'remote unreachable' }

  if (-not $born) {
    # An overlay cloned while empty: its first commit lands on the remote's default branch, whatever its name.
    $lookup = Invoke-Git $repo @('ls-remote', '--symref', $remote, 'HEAD')
    if ($lookup.Code) { return Done 'remote unreachable' }
    $head = $lookup.Out | Where-Object { $_ -match '^ref: refs/heads/(\S+)\s+HEAD$' } | Select-Object -First 1
    if ($head -and $head -match '^ref: refs/heads/(\S+)') {
      $state.Default = $Matches[1]
      if ((Invoke-Git $repo @('rev-parse', '--verify', '--quiet', "refs/remotes/$remote/$($state.Default)")).Code -eq 0) {
        if ($dirty) { return Done "empty, first commit waiting on $($state.Default), uncommitted changes" }
        return Done "empty, first commit waiting on $($state.Default)" $false 'first'
      }
    }
    return Done 'empty, no commit yet' (-not $dirty)
  }
  if (-not $merge) { return Done 'no upstream branch' }
  $state.Upstream = $merge -replace '^refs/heads/', ''
  if ((Invoke-Git $repo @('rev-parse', '--verify', '--quiet', "refs/remotes/$remote/$($state.Upstream)")).Code) { return Done 'upstream branch gone (its remote branch was deleted)' }
  $counts = Invoke-Git $repo @('rev-list', '--left-right', '--count', "HEAD...refs/remotes/$remote/$($state.Upstream)")
  if ($counts.Code -or ($counts.Out -join '') -notmatch '^(\d+)\s+(\d+)$') { return Done 'could not be read (compare)' }
  $ahead, $behind = [int]$Matches[1], [int]$Matches[2]
  # The exact commits looked at: a plain run acts on these, never on whatever HEAD or the upstream is by then.
  $state.Commit = (Invoke-Git $repo @('rev-parse', 'HEAD')).Out -join ''
  $state.Target = (Invoke-Git $repo @('rev-parse', "refs/remotes/$remote/$($state.Upstream)")).Out -join ''
  $parts = @()
  if ($ahead -and $behind) { $parts += "diverged (ahead $ahead, behind $behind)" }
  elseif ($ahead) { $parts += "ahead $ahead" }
  elseif ($behind) { $parts += "behind $behind" }
  if ($dirty) { $parts += 'uncommitted changes' }
  if (-not $parts) { return Done 'level and clean' $true }
  $action = if ($dirty -or ($ahead -and $behind)) { 'none' } elseif ($ahead) { 'push' } else { 'merge' }
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
  Write-States $states
  foreach ($s in $blocked) { "sync: nothing was changed, because $($s.Label) is $($s.Text)" }
  exit 1
}

$done = @()
$failed = $null
# The public fast-forward runs scripts/hooks/post-merge, which finds the overlay already dealt with: this run never
# fast-forwards the public checkout while it refuses the overlay. Its output, like git's, is not shown.
foreach ($s in $states | Where-Object { $_.Action -ne 'none' }) {
  $step = switch ($s.Action) {
    'merge' { @{ Did = "fast-forwarded $($s.Label)"; Args = @('merge', '--ff-only', '--no-overwrite-ignore', '--quiet', $s.Target); Why = 'the fast-forward was refused (an incoming file would replace an ignored or local one, or the branch moved meanwhile)' } }
    'push' { @{ Did = "pushed $($s.Label) to its upstream branch"; Args = @('-c', 'push.followTags=false', 'push', '--quiet', '--no-follow-tags', $s.Remote, "$($s.Commit):refs/heads/$($s.Upstream)"); Why = 'the push was refused (its upstream moved on meanwhile, or it does not accept this push)' } }
    'first' { @{ Did = "gave $($s.Label) its first commit"; Args = @('checkout', '--quiet', '--no-overwrite-ignore', '--track', "$($s.Remote)/$($s.Default)"); Why = 'its first commit could not be checked out (an incoming file would replace an ignored one)' } }
  }
  # The checkout must still be where it was looked at: a branch switched or a commit made meanwhile stops the run.
  if ($s.Action -ne 'first' -and ((Invoke-Git $s.Repo @('rev-parse', 'HEAD')).Out -join '' -ne $s.Commit -or (Invoke-Git $s.Repo @('symbolic-ref', '--quiet', '--short', 'HEAD')).Out -join '' -ne $s.Branch)) {
    $failed = "sync: $($s.Label): it changed while sync ran (another branch or commit); nothing more was done"; break
  }
  # No git or remote text is shown: a remote's message can carry a URL with a credential in it.
  if ((Invoke-Git $s.Repo $step.Args).Code) { $failed = "sync: $($s.Label): $($step.Why)"; break }
  $done += $step.Did
}

if ($failed) {
  $failed
  "sync: done before it: $(if ($done) { $done -join '; ' } else { 'nothing' })"
}
elseif ($done) { "sync: $($done -join '; ')" }
$states = Get-States
Write-States $states
exit $(if ($failed -or @($states | Where-Object { -not $_.Ok }).Count) { 1 } else { 0 })
} finally {
  foreach ($name in $gitVariables) { if ($null -ne $callerGit[$name]) { Set-Item -LiteralPath "Env:$name" -Value $callerGit[$name] } }
}
