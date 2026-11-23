# Sets up a clone of this repository, once. Safe to run again: it changes only what is not already set.
#
#   pwsh scripts/setup-clone.ps1                        turn on the repository's git hooks in this clone
#   pwsh scripts/setup-clone.ps1 -Repo <folder>         target another clone (default: the one this script is in)
#   pwsh scripts/setup-clone.ps1 -Undo                  restore any earlier core.hooksPath (or unset it) and clear bosun.previousHooksPath
#   pwsh scripts/setup-clone.ps1 -OverlayUpdates off|on pause or resume automatic .private pulls in post-merge (keeps bosun.overlay, branches, stashes and ignored files)
#   pwsh scripts/setup-clone.ps1 -CheckPrePush          read-only check that pre-push, leak-scan.ps1, pwsh and gitleaks are all ready
#
# Hooks: sets core.hooksPath to scripts/hooks in the clone's own config (--local), saving any earlier value in
# bosun.previousHooksPath before replacing it so every run (already on included) prints the exact undo command.
#
# Overlay: clones the private overlay (BOSUN_OVERLAY_REPO, else rizonetech/Bosun-private) into the ignored .private
# folder when gh can see it, checks its origin, and marks it (bosun.overlay in its config) so scripts/hooks/post-merge
# pulls it after every pull that merges unless paused with -OverlayUpdates off (bosun.overlayAutoUpdate = false).
# Without gh, sign-in or access it says so in one line and goes on. An existing .private is never changed, except that
# a checkout of the overlay itself is marked.
#   Undo: delete .private, once 'git -C .private status' and unpushed commits are seen to (the script prints this)
#
# Nothing is scheduled: the watchdog task that resumed an unattended run was retired on 2026-11-12, with the
# -Watchdog option that installed it. A clone that still has one removes it with `python scripts/run-guard.py uninstall`.
#
# Every change is said before it is made and read back after. Exit 0 when the clone is set up (or checked), 1 with the
# cause when something could not be done; nothing is reported as done that was not read back.
[CmdletBinding()]  # so an option this script doesn't have (the retired -Watchdog) is refused, never silently ignored
param(
  [string]$Repo = (Split-Path $PSScriptRoot),
  [switch]$Undo,
  [string]$OverlayUpdates,
  [switch]$CheckPrePush
)
$ErrorActionPreference = 'Stop'
$hooks = 'scripts/hooks'

function Stop-Setup([string]$why) { "setup-clone: $why"; exit 1 }

$modeCount = ([int][bool]$Undo) + ([int](-not [string]::IsNullOrEmpty($OverlayUpdates))) + ([int][bool]$CheckPrePush)
if ($modeCount -gt 1) { Stop-Setup 'use at most one of -Undo, -OverlayUpdates, or -CheckPrePush' }
if (-not [string]::IsNullOrEmpty($OverlayUpdates) -and $OverlayUpdates -notin @('off', 'on')) {
  Stop-Setup "-OverlayUpdates must be 'off' or 'on', not '$OverlayUpdates'"
}

if (-not (Get-Command git -CommandType Application -ErrorAction Ignore)) { Stop-Setup 'git is not on PATH: install Git for Windows, then run this again' }
if (-not (Test-Path -LiteralPath $Repo -PathType Container)) { Stop-Setup "$Repo is not a folder" }
$Repo = (Resolve-Path -LiteralPath $Repo).ProviderPath
$top = git -C $Repo rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or -not $top -or [IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ne $Repo.TrimEnd('\', '/')) {
  Stop-Setup "$Repo is not the top folder of a git checkout"
}

function Get-HooksPath {
  $value = git -C $Repo config --local --get core.hooksPath 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
function Get-EffectiveHooksPath {
  $value = git -C $Repo config --get core.hooksPath 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
function Get-PreviousHooksPath {
  $value = git -C $Repo config --local --get bosun.previousHooksPath 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
function Format-HooksUndo([string]$prev) {
  $gitUndo = if ([string]::IsNullOrEmpty($prev)) { '--unset core.hooksPath' } else { "core.hooksPath `"$prev`"" }
  "hooks: to put it back: pwsh scripts/setup-clone.ps1 -Repo `"$Repo`" -Undo (or: git -C `"$Repo`" config --local $gitUndo)"
}

# --- -CheckPrePush (read-only diagnostic; makes zero changes to the checkout or .private) ---
if ($CheckPrePush) {
  $effective = Get-EffectiveHooksPath
  if ($effective -ne $hooks) {
    Stop-Setup "pre-push check failed: effective core.hooksPath is $(if ($null -eq $effective) { 'not set' } else { "'$effective'" }), expected '$hooks'; run 'pwsh scripts/setup-clone.ps1 -Repo `"$Repo`"' to enable hooks"
  }
  $prePushPath = Join-Path $Repo "$hooks/pre-push"
  if (-not (Test-Path -LiteralPath $prePushPath -PathType Leaf) -or -not "$(git -C $Repo ls-files -- "$hooks/pre-push" 2>$null)") {
    Stop-Setup "pre-push check failed: $hooks/pre-push is missing from $Repo or not tracked by git; restore it (git checkout -- $hooks/pre-push)"
  }
  $entry = "$(git -C $Repo ls-files -s -- "$hooks/pre-push" 2>$null)"
  $mode = ($entry -split '\s+', 2)[0]
  if ($mode -ne '100755') {
    Stop-Setup "pre-push check failed: $hooks/pre-push is not recorded in git as an executable (mode '$mode'); run 'git update-index --chmod=+x $hooks/pre-push' and commit"
  }
  if (-not $IsWindows -and -not ((Get-Item -LiteralPath $prePushPath).UnixFileMode -band [IO.UnixFileMode]::UserExecute)) {
    Stop-Setup "pre-push check failed: $hooks/pre-push is not executable in this working copy; run 'chmod +x $hooks/pre-push'"
  }
  if (-not (Test-Path -LiteralPath (Join-Path $Repo 'scripts/leak-scan.ps1') -PathType Leaf)) {
    Stop-Setup "pre-push check failed: scripts/leak-scan.ps1 is missing from $Repo; restore it (git checkout -- scripts/leak-scan.ps1)"
  }
  if (-not (Get-Command pwsh -CommandType Application -ErrorAction Ignore)) {
    Stop-Setup "pre-push check failed: pwsh is not on PATH, so $hooks/pre-push cannot run scripts/leak-scan.ps1; add pwsh (PowerShell 7) to PATH"
  }
  $gitleaksInstall = if ($IsWindows) {
    "install gitleaks 8.30.1 (winget install --id Gitleaks.Gitleaks, or gitleaks_8.30.1_windows_x64.zip)"
  } else {
    "install gitleaks 8.30.1 from gitleaks_8.30.1_linux_x64.tar.gz and verify against gitleaks_8.30.1_checksums.txt"
  }
  $gitleaks = Get-Command gitleaks -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if (-not $gitleaks) {
    Stop-Setup "pre-push check failed: gitleaks is not installed or not on PATH; $gitleaksInstall"
  }
  try {
    $glOut = & $gitleaks.Source version 2>&1 | Out-String
    $glCode = $LASTEXITCODE
  } catch {
    Stop-Setup "pre-push check failed: gitleaks could not start ($($_.Exception.Message)); $gitleaksInstall"
  }
  if ($glCode -ne 0) {
    $detail = $glOut.Trim()
    Stop-Setup "pre-push check failed: gitleaks exited $glCode$(if ($detail) { " ($detail)" }); $gitleaksInstall"
  }
  $ver = ($glOut -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
  $verText = if ($ver) { $ver.Trim() } else { 'present' }
  "pre-push: ok (hook $hooks/pre-push active, scripts/leak-scan.ps1 present, pwsh on PATH, and gitleaks $verText available)"
  exit 0
}

# --- -Undo (restores bosun.previousHooksPath or unsets core.hooksPath without touching .private) ---
if ($Undo) {
  $current = Get-HooksPath
  $prev = Get-PreviousHooksPath
  if ($null -ne $prev) {
    "hooks: restoring core.hooksPath to '$prev' in $Repo"
    $said = git -C $Repo config --local core.hooksPath $prev 2>&1
    if ($LASTEXITCODE) { Stop-Setup "could not restore core.hooksPath to '$prev' ($(($said | Out-String).Trim()))" }
    if ((Get-HooksPath) -ne $prev) { Stop-Setup "core.hooksPath was written as '$prev' but does not read back as '$prev'; bosun.previousHooksPath was kept" }
    $said = git -C $Repo config --local --unset bosun.previousHooksPath 2>&1
    if ($LASTEXITCODE) { Stop-Setup "restored core.hooksPath to '$prev', but could not unset bosun.previousHooksPath ($(($said | Out-String).Trim()))" }
    if ($null -ne (Get-PreviousHooksPath)) { Stop-Setup "restored core.hooksPath to '$prev', but bosun.previousHooksPath still reads back" }
    "hooks: restored core.hooksPath to '$prev' and removed bosun.previousHooksPath"
    exit 0
  }
  if ($null -eq $current) {
    'hooks: already unset (core.hooksPath is not set)'
    exit 0
  }
  if ($current -ne $hooks) {
    Stop-Setup "refusing to undo: core.hooksPath is '$current' (not '$hooks') and no bosun.previousHooksPath is stored"
  }
  $said = git -C $Repo config --local --unset core.hooksPath 2>&1
  if ($LASTEXITCODE) { Stop-Setup "could not unset core.hooksPath ($(($said | Out-String).Trim()))" }
  if ($null -ne (Get-HooksPath)) { Stop-Setup 'core.hooksPath was unset but still reads back' }
  "hooks: unset core.hooksPath (was '$hooks')"
  exit 0
}

# --- -OverlayUpdates off|on (pauses or resumes automatic post-merge pulls in .private without removing bosun.overlay) ---
if ($OverlayUpdates) {
  . (Join-Path $PSScriptRoot 'lib/overlay.ps1')
  $private = Join-Path $Repo '.private'
  if (-not (Test-Path -LiteralPath $private -PathType Container)) {
    Stop-Setup "overlay updates cannot be changed: .private does not exist in $Repo"
  }
  $fault = Get-OverlayCloneFault $private
  if ($fault) { Stop-Setup "overlay updates cannot be changed: $fault" }
  try { $lookup = Get-BosunOverlayValue -Key overlay-repo -Root $Repo -VariableOnly }
  catch { Stop-Setup "the overlay source could not be read: $($_.Exception.Message)" }
  $raw = if ($lookup.Source -eq 'setup-required') { 'rizonetech/Bosun-private' } else { $lookup.Value }
  $source = Get-OverlaySource $raw
  if (-not $source) { Stop-Setup 'BOSUN_OVERLAY_REPO is not a supported overlay source' }
  if ("$(git -C $private config --local --get bosun.overlay 2>$null)" -ne 'true') {
    Stop-Setup '.private is not marked as the overlay (bosun.overlay is not true); run setup-clone.ps1 first'
  }
  if (-not (Test-OverlayOrigin (Get-OverlayOrigin $private) $source)) {
    Stop-Setup '.private is a checkout of another repository; refusing to change its overlay update setting'
  }
  $want = if ($OverlayUpdates -eq 'off') { 'false' } else { 'true' }
  $said = git -C $private config --local bosun.overlayAutoUpdate $want 2>&1
  if ($LASTEXITCODE) { Stop-Setup "could not set bosun.overlayAutoUpdate to $want ($(($said | Out-String).Trim()))" }
  if ("$(git -C $private config --local --get bosun.overlayAutoUpdate 2>$null)" -ne $want) {
    Stop-Setup "bosun.overlayAutoUpdate was written as $want but does not read back as $want"
  }
  if ($OverlayUpdates -eq 'off') {
    "overlay: automatic updates turned off (bosun.overlayAutoUpdate is false; bosun.overlay, local branches, stashes and ignored files were left untouched). To turn them back on: pwsh scripts/setup-clone.ps1 -Repo `"$Repo`" -OverlayUpdates on"
  } else {
    "overlay: automatic updates turned on (bosun.overlayAutoUpdate is true; post-merge will fast-forward .private). To turn them off: pwsh scripts/setup-clone.ps1 -Repo `"$Repo`" -OverlayUpdates off"
  }
  exit 0
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
$before = Get-HooksPath
if ($before -eq $hooks) {
  "hooks: already on (core.hooksPath is $hooks)"
  Format-HooksUndo (Get-PreviousHooksPath)
} else {
  "hooks: core.hooksPath is $(if ($null -eq $before) { 'not set' } else { "'$before'" }) in $Repo; setting it to $hooks"
  if ($null -ne $before) {
    # Save and read back the earlier value BEFORE replacing core.hooksPath so a failed write never loses it.
    $said = git -C $Repo config --local bosun.previousHooksPath $before 2>&1
    if ($LASTEXITCODE) { Stop-Setup "could not set core.hooksPath: failed to save bosun.previousHooksPath ($(($said | Out-String).Trim())); it is still '$before'" }
    if ((Get-PreviousHooksPath) -ne $before) { Stop-Setup "could not set core.hooksPath: bosun.previousHooksPath was written as '$before' but does not read back; it is still '$before'" }
  } elseif ($null -ne (Get-PreviousHooksPath)) {
    # Clear any stale bosun.previousHooksPath on a fresh clone whose core.hooksPath is unset.
    $said = git -C $Repo config --local --unset bosun.previousHooksPath 2>&1
    if ($LASTEXITCODE) { Stop-Setup "could not clear stale bosun.previousHooksPath ($(($said | Out-String).Trim()))" }
    if ($null -ne (Get-PreviousHooksPath)) { Stop-Setup 'stale bosun.previousHooksPath was unset but still reads back' }
  }
  $said = git -C $Repo config --local core.hooksPath $hooks 2>&1
  if ($LASTEXITCODE) { Stop-Setup "could not set core.hooksPath ($(($said | Out-String).Trim())); it is still $(if ($null -eq $before) { 'not set' } else { "'$before'" })" }
  if ((Get-HooksPath) -ne $hooks) { Stop-Setup "core.hooksPath was written but does not read back as $hooks" }
  'hooks: on.'
  Format-HooksUndo $before
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
    "overlay: to undo the mark only (the checkout stays as it is): git -C `"$private`" config --local --unset bosun.overlay"
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

exit 0
