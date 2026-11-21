# Sets up a clone of this repository, once. Safe to run again: it changes only what is not already set.
#
#   pwsh scripts/setup-clone.ps1                 turn on the repository's git hooks in this clone
#   pwsh scripts/setup-clone.ps1 -Repo <folder>  set up another clone (default: the one this script is in)
#
# Hooks: sets core.hooksPath to scripts/hooks in the clone's own config (--local), so scripts/hooks/pre-commit
# refuses a commit that would leave the generated plan stale or the TODO graph invalid.
#   Undo: git config --local --unset core.hooksPath   (the script prints the exact command, with any earlier value)
#
# Overlay: clones the private overlay (BOSUN_OVERLAY_REPO, else rizonetech/Bosun-private) into the ignored .private
# folder when gh can see it, checks its origin, and marks it (bosun.overlay in its config) so scripts/hooks/post-merge
# pulls it after every pull that merges. Without gh, sign-in or access it says so in one line and goes on. An existing
# .private is never changed, except that a checkout of the overlay itself is marked.
#   Undo: delete .private, once 'git -C .private status' and unpushed commits are seen to (the script prints this)
#
# Nothing is scheduled: the watchdog task that resumed an unattended run was retired on 2026-11-12, with the
# -Watchdog option that installed it. A clone that still has one removes it with `python scripts/run-guard.py uninstall`.
#
# Every change is said before it is made and read back after. Exit 0 when the clone is set up, 1 with the cause
# when something could not be done; nothing is reported as done that was not read back.
[CmdletBinding()]  # so an option this script doesn't have (the retired -Watchdog) is refused, never silently ignored
param(
  [string]$Repo = (Split-Path $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$hooks = 'scripts/hooks'

function Stop-Setup([string]$why) { "setup-clone: $why"; exit 1 }

if (-not (Get-Command git -CommandType Application -ErrorAction Ignore)) { Stop-Setup 'git is not on PATH: install Git for Windows, then run this again' }
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { Stop-Setup "$Repo is not a folder" }
$Repo = (Resolve-Path -LiteralPath $Repo).ProviderPath
$top = git -C $Repo rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or -not $top -or [IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ne $Repo.TrimEnd('\', '/')) {
  Stop-Setup "$Repo is not the top folder of a git checkout"
}
if (-not (Test-Path -LiteralPath (Join-Path $Repo "$hooks/pre-commit") -PathType Leaf)) {
  Stop-Setup "$Repo has no $hooks/pre-commit, so it is not a checkout of this repository"
}
# The hooks this checkout relies on: each must be there and tracked, or setup would report what no hook does.
foreach ($name in 'pre-commit', 'post-merge', 'pre-push') {
  if (-not (Test-Path -LiteralPath (Join-Path $Repo "$hooks/$name") -PathType Leaf) -or -not "$(git -C $Repo ls-files -- "$hooks/$name" 2>$null)") {
    Stop-Setup "$hooks/$name is missing from $Repo or not tracked by git; restore it (git checkout -- $hooks/$name), then run this again"
  }
}
# Git on Linux and WSL ignores a hook that is not executable, and what a clone there gets is the mode git recorded.
foreach ($entry in @(git -C $Repo ls-files -s -- $hooks 2>$null)) {
  $mode, $null, $null, $path = "$entry" -split '\s+', 4
  # Off Windows the file on disk must be executable too: git runs a hook by its file mode, not by what the index says.
  $onDisk = Join-Path $Repo $path
  if ($mode -eq '100755' -and -not $IsWindows -and (Test-Path -LiteralPath $onDisk) -and -not ((Get-Item -LiteralPath $onDisk).UnixFileMode -band [IO.UnixFileMode]::UserExecute)) {
    Stop-Setup "$path is not executable in this working copy, so git would skip it: run 'chmod +x $path', then run this again"
  }
  if ($mode -ne '100755') {
    Stop-Setup "$path is not recorded in git as an executable, so a clone on Linux or WSL would skip it: run 'git update-index --chmod=+x $path' and commit, then run this again"
  }
}

# --- hooks ---
function Get-HooksPath {
  $value = git -C $Repo config --local --get core.hooksPath 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
$before = Get-HooksPath
if ($before -eq $hooks) { "hooks: already on (core.hooksPath is $hooks)" }
else {
  "hooks: core.hooksPath is $(if ($null -eq $before) { 'not set' } else { "'$before'" }) in $Repo; setting it to $hooks"
  $said = git -C $Repo config --local core.hooksPath $hooks 2>&1
  if ($LASTEXITCODE) { Stop-Setup "could not set core.hooksPath ($(($said | Out-String).Trim())); it is still $(if ($null -eq $before) { 'not set' } else { "'$before'" })" }
  if ((Get-HooksPath) -ne $hooks) { Stop-Setup "core.hooksPath was written but does not read back as $hooks" }
  $undo = if ($null -eq $before) { '--unset core.hooksPath' } else { "core.hooksPath `"$before`"" }
  "hooks: on. To put it back: git -C `"$Repo`" config --local $undo"
}

# --- overlay ---
# The private overlay (rizonetech/Bosun-private, D00 T05 §2) is cloned into the ignored .private folder when the
# account can see it. A stranger's clone skips it with one line and is set up all the same.
. (Join-Path $PSScriptRoot 'lib/overlay.ps1')
function Skip-Overlay([string]$why) { "overlay: skipped, $why" }

$private = Join-Path $Repo '.private'
# From the variable alone: .private/config.toml lives in the folder being checked, so it must not name what it is checked against.
try { $lookup = Get-BosunOverlayValue -Key overlay-repo -Root $Repo -VariableOnly }
catch { Stop-Setup "the overlay source could not be read: $($_.Exception.Message)" }
$raw = if ($lookup.Source -eq 'setup-required') { 'rizonetech/Bosun-private' } else { $lookup.Value }
$source = Get-OverlaySource $raw
if (-not $source) {
  Stop-Setup 'BOSUN_OVERLAY_REPO is not a supported overlay source: use owner/name, an absolute path to a repository, an https:// URL without user information, an ssh:// URL or git@host:path'
}

$item = Get-Item -LiteralPath $private -Force -ErrorAction Ignore
if ($item) {
  $fault = Get-OverlayCloneFault $private
  if ($fault) { Skip-Overlay "$fault; it was left as it is" }
  elseif ("$(git -C $private config --local --get bosun.overlay 2>$null)" -eq 'true') { 'overlay: already present in .private' }
  elseif (Test-OverlayOrigin (Get-OverlayOrigin $private) $source) {
    git -C $private config --local bosun.overlay true 2>&1 | Out-Null
    if ("$(git -C $private config --local --get bosun.overlay 2>$null)" -ne 'true') { Stop-Setup '.private is the overlay, but it could not be marked as one (git config --local bosun.overlay true)' }
    'overlay: .private is the overlay; marked it as one, so every pull of this checkout that merges keeps it current'
    'overlay: to undo the mark only (the checkout stays as it is): git -C .private config --local --unset bosun.overlay; pwsh scripts/update-agent-rules.ps1 (which takes the overlay''s rules out of CLAUDE.local.md)'
  } else { Skip-Overlay '.private is a checkout of another repository; it was left as it is' }
} else {
  $ready = $true
  if ($source.Kind -eq 'github') {
    if (-not (Get-Command gh -CommandType Application -ErrorAction Ignore)) { Skip-Overlay 'gh is not installed, so the private overlay cannot be looked for'; $ready = $false }
    else {
      # The probe is the repository itself; sign-in on github.com is asked only to say why it failed, so an expired
      # login on another host never hides an overlay this account can see.
      gh repo view $source.Value --json name *> $null
      if ($LASTEXITCODE) {
        gh auth status --hostname github.com *> $null
        if ($LASTEXITCODE) { Skip-Overlay 'gh is not signed in to github.com, so the private overlay cannot be looked for' }
        else { Skip-Overlay "this account cannot see $($source.Value)" }
        $ready = $false
      }
    }
  }
  if ($ready) {
    # Cloned beside the checkout's own git data first, and moved to .private only once it is checked and marked, so
    # a failed or interrupted clone is never taken for the overlay and a retry starts clean.
    # Staged in the checkout itself (ignored, as .private-clone-*), so the move to .private is a rename on one volume
    # whatever folder holds the checkout's git data.
    foreach ($left in @(Get-ChildItem -LiteralPath $Repo -Directory -Force -Filter '.private-clone-*' -ErrorAction Ignore)) {
      "overlay: $($left.Name) is left from a clone that was cut off; delete it when no setup is running"
    }
    $partial = Join-Path $Repo ('.private-clone-' + [guid]::NewGuid().ToString('N'))
    try {
      "overlay: cloning $(if ($source.Kind -eq 'github') { $source.Value } else { 'the source in BOSUN_OVERLAY_REPO' }) into .private"
      $said = if ($source.Kind -eq 'github') { gh repo clone $source.Value $partial -- --quiet 2>&1 } else { git clone --quiet -- $source.Value $partial 2>&1 }
      if ($LASTEXITCODE -or -not (Test-Path -LiteralPath (Join-Path $partial '.git') -PathType Container)) {
        # The clone's own last line says why (a full disk, a refused permission, an unreachable remote), with every URL
        # and anything shaped like user@host taken out first: a transport message can carry a credential.
        $last = "$((@($said) | Where-Object { "$_".Trim() } | Select-Object -Last 1 | Out-String).Trim())"
        $last = $last -replace '[A-Za-z][A-Za-z0-9+.-]*://\S+', '<url>' -replace '\S+@\S+', '<address>'
        $why = if ($last) { " ($last)" } else { '' }
        Stop-Setup "the overlay could not be cloned$why; nothing was left in .private, so running this again starts clean"
      }
      if (-not (Test-OverlayOrigin (Get-OverlayOrigin $partial) $source)) { Stop-Setup 'the overlay clone''s origin is not the source asked for; it was removed' }
      git -C $partial config --local bosun.overlay true 2>&1 | Out-Null
      if ("$(git -C $partial config --local --get bosun.overlay 2>$null)" -ne 'true') { Stop-Setup 'the overlay clone could not be marked as the overlay; it was removed' }
      # Directory.Move refuses a destination that exists, so a clone finished meanwhile by another run is never nested.
      try { [IO.Directory]::Move($partial, $private) }
      catch { Stop-Setup "the overlay could not be moved into .private ($($_.Exception.Message)); it was removed" }
    } finally {
      if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
    }
    $fault = Get-OverlayCloneFault $private
    if ($fault -or -not (Test-OverlayOrigin (Get-OverlayOrigin $private) $source)) { Stop-Setup '.private does not read back as the overlay after the clone' }
    'overlay: cloned into .private and marked; every pull of this checkout that merges now pulls it too'
    "overlay: to remove it, first look for work only it holds: 'git -C .private status --ignored' (edits, new and ignored files), 'git -C .private stash list' (stashes) and 'git -C .private log --branches --not --remotes' (commits not pushed); keep what you need, then delete .private. The checkout works without it, and running this script again brings it back"
  }
}

# The overlay's agent rules follow .private as it now is (D00 T05 §5).
$rules = Update-BosunAgentRules $Repo
if ($rules -eq 'added') { 'overlay: agent rules: CLAUDE.local.md now imports .private/AGENTS.md' }
elseif ($rules -eq 'removed') { 'overlay: agent rules: the import of .private/AGENTS.md was taken out of CLAUDE.local.md' }

exit 0
