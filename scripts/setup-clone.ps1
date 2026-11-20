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
foreach ($name in 'pre-commit', 'post-merge') {
  if (-not (Test-Path -LiteralPath (Join-Path $Repo "$hooks/$name") -PathType Leaf) -or -not "$(git -C $Repo ls-files -- "$hooks/$name" 2>$null)") {
    Stop-Setup "$hooks/$name is missing from $Repo or not tracked by git; restore it (git checkout -- $hooks/$name), then run this again"
  }
}
# Git on Linux and WSL ignores a hook that is not executable, and what a clone there gets is the mode git recorded.
foreach ($entry in @(git -C $Repo ls-files -s -- $hooks 2>$null)) {
  $mode, $null, $null, $path = "$entry" -split '\s+', 4
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
function Get-OverlayOrigin([string]$folder) {
  $value = git -C $folder config --get remote.origin.url 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
# Whether a clone's origin is the source asked for: the GitHub URL of owner/name in HTTPS or SSH form, the same
# resolved local path, or the same URL.
function Test-OverlayOrigin([string]$origin, [hashtable]$source) {
  if (-not $origin) { return $false }
  switch ($source.Kind) {
    'github' {
      $name = [regex]::Escape($source.Value)
      return $origin -match "^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)$name(\.git)?/?$"
    }
    'path' {
      if (-not [IO.Path]::IsPathRooted($origin)) { return $false }
      # Windows folders compare without case, as Windows opens them; elsewhere a path's case is part of its name.
      $compare = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
      return [string]::Equals([IO.Path]::GetFullPath($origin).TrimEnd('\', '/'), [IO.Path]::GetFullPath($source.Value).TrimEnd('\', '/'), $compare)
    }
    default { return $origin.TrimEnd('/') -ceq $source.Value.TrimEnd('/') }
  }
}
# The source forms setup accepts. Anything else is refused without repeating the value, which may hold a credential.
function Get-OverlaySource([string]$value) {
  if ($value -match '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$') { return @{ Kind = 'github'; Value = $value } }
  if ($value -match '^https://') {
    $uri = $null
    # No user information, query or fragment: each can carry a token, which git would keep in .private/.git/config.
    if ([Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment -and $value -notmatch '[\s?#@]') { return @{ Kind = 'url'; Value = $value } }
    return $null
  }
  # An SSH user name is allowed, a password (user:secret@) is not.
  if ($value -match '^ssh://([A-Za-z0-9._-]+@)?[A-Za-z0-9.-]+(:\d+)?/[^\s?#]+$' -or $value -match '^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s?#:@][^\s?#@]*$') { return @{ Kind = 'url'; Value = $value } }
  if (-not $value.StartsWith('-') -and [IO.Path]::IsPathRooted($value) -and (Test-Path -LiteralPath $value -PathType Container)) {
    return @{ Kind = 'path'; Value = (Resolve-Path -LiteralPath $value).ProviderPath }
  }
  $null
}

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
# A link (a junction or symbolic link) leads out of the checkout, to a folder another checkout may own: never followed.
if ($item -and $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { Skip-Overlay '.private is a link to another folder, not the overlay; it was left as it is' }
elseif (Test-Path -LiteralPath $private -PathType Leaf) { Skip-Overlay '.private is a file, not the overlay; it was left as it is' }
elseif (Test-Path -LiteralPath $private -PathType Container) {
  if (-not (Test-Path -LiteralPath (Join-Path $private '.git'))) { Skip-Overlay '.private is a folder that is not a git checkout; it was left as it is' }
  elseif ("$(git -C $private config --local --get bosun.overlay 2>$null)" -eq 'true') { 'overlay: already present in .private' }
  elseif (Test-OverlayOrigin (Get-OverlayOrigin $private) $source) {
    git -C $private config --local bosun.overlay true 2>&1 | Out-Null
    if ("$(git -C $private config --local --get bosun.overlay 2>$null)" -ne 'true') { Stop-Setup '.private is the overlay, but it could not be marked as one (git config --local bosun.overlay true)' }
    'overlay: .private is the overlay; marked it as one, so every pull keeps it current'
  } else { Skip-Overlay '.private is a checkout of another repository; it was left as it is' }
} else {
  $ready = $true
  if ($source.Kind -eq 'github') {
    if (-not (Get-Command gh -CommandType Application -ErrorAction Ignore)) { Skip-Overlay 'gh is not installed, so the private overlay cannot be looked for'; $ready = $false }
    else {
      gh auth status *> $null
      if ($LASTEXITCODE) { Skip-Overlay 'gh is not signed in, so the private overlay cannot be looked for'; $ready = $false }
      else {
        gh repo view $source.Value --json name *> $null
        if ($LASTEXITCODE) { Skip-Overlay "this account cannot see $($source.Value)"; $ready = $false }
      }
    }
  }
  if ($ready) {
    # Cloned beside the checkout's own git data first, and moved to .private only once it is checked and marked, so
    # a failed or interrupted clone is never taken for the overlay and a retry starts clean.
    $gitDir = "$(git -C $Repo rev-parse --absolute-git-dir 2>$null)"
    if ($LASTEXITCODE -or -not $gitDir) { Stop-Setup 'the checkout''s git folder could not be found for the overlay clone' }
    $partial = Join-Path $gitDir ('bosun-overlay-clone-' + [guid]::NewGuid().ToString('N'))
    try {
      "overlay: cloning $(if ($source.Kind -eq 'github') { $source.Value } else { 'the source in BOSUN_OVERLAY_REPO' }) into .private"
      $said = if ($source.Kind -eq 'github') { gh repo clone $source.Value $partial -- --quiet 2>&1 } else { git clone --quiet -- $source.Value $partial 2>&1 }
      if ($LASTEXITCODE -or -not (Test-Path -LiteralPath (Join-Path $partial '.git'))) {
        # A clone's own message can name a URL with a credential in it, so it is shown only for owner/name sources.
        $why = if ($source.Kind -eq 'github') { " ($((@($said) | Select-Object -Last 1 | Out-String).Trim()))" } else { '' }
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
    if (-not (Test-OverlayOrigin (Get-OverlayOrigin $private) $source)) { Stop-Setup '.private does not read back as the overlay after the clone' }
    'overlay: cloned into .private and marked; every pull of this checkout that merges now pulls it too'
    "overlay: to remove it, check 'git -C .private status' and 'git -C .private log --branches --not --remotes' for edits and commits not pushed yet, then delete .private; the checkout works without it, and running this script again brings it back"
  }
}

exit 0
