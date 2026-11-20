# Tests the overlay half of scripts/setup-clone.ps1 and scripts/hooks/post-merge (D00 T05 §2) against temporary
# repositories: local bare remotes stand in for the public repository and the overlay, and a stand-in gh stands in
# for GitHub. The operator's own gh, tokens, overlay setting and git config never reach a case.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$setup = Join-Path $repoRoot 'scripts\setup-clone.ps1'
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-overlay-clone-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# --- isolation: nothing of the operator's reaches a case ---
$saved = @{}
foreach ($name in 'PATH', 'BOSUN_OVERLAY_REPO', 'GH_TOKEN', 'GITHUB_TOKEN', 'GH_CONFIG_DIR', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'FAKE_GH_MODE', 'FAKE_GH_REMOTE', 'FAKE_GH_LOG') {
  $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwshExe = (Get-Process -Id $PID).Path
$system = Join-Path $env:SystemRoot 'System32'
# A PATH with git, pwsh and Windows only: no real gh can be found on it.
$bare = (@((Split-Path $gitExe), (Split-Path $pwshExe), $system) | Select-Object -Unique) -join ';'
$fakeBin = Join-Path $work 'fake-gh'
New-Item -ItemType Directory -Path $fakeBin | Out-Null
Set-Content -LiteralPath (Join-Path $fakeBin 'fake-gh.ps1') -Encoding utf8 -Value @'
Add-Content -LiteralPath $env:FAKE_GH_LOG -Value ($args -join ' ')
$mode = $env:FAKE_GH_MODE
if ($args[0] -eq 'auth') { exit ([int]($mode -eq 'signed-out')) }
if ($args[0] -eq 'repo' -and $args[1] -eq 'view') { exit ([int]($mode -eq 'refused')) }
if ($args[0] -eq 'repo' -and $args[1] -eq 'clone') {
  if ($mode -eq 'clone-fails') { 'fatal: the transfer was cut off'; exit 1 }
  # Another run finishing first: .private appears in the checkout while this clone is still being made.
  if ($mode -eq 'race') { $won = Join-Path (Split-Path (Split-Path $args[3])) '.private'; New-Item -ItemType Directory -Path $won | Out-Null; Set-Content -LiteralPath (Join-Path $won 'first.txt') -Value 'the first run' }
  git clone --quiet -- $env:FAKE_GH_REMOTE $args[3] 2>&1 | Out-Null
  if ($LASTEXITCODE) { exit 1 }
  git -C $args[3] remote set-url origin "https://github.com/$($args[2]).git"
  exit 0
}
exit 2
'@
Set-Content -LiteralPath (Join-Path $fakeBin 'gh.cmd') -Value "@`"$pwshExe`" -NoProfile -File `"%~dp0fake-gh.ps1`" %*"
$env:PATH = $bare
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'
$env:GIT_CONFIG_NOSYSTEM = '1'
Set-Content -LiteralPath $env:GIT_CONFIG_GLOBAL -Value "[user]`n`tname = Overlay Test`n`temail = overlay-test@example.invalid`n[init]`n`tdefaultBranch = main`n[pull]`n`trebase = false"
foreach ($name in 'BOSUN_OVERLAY_REPO', 'GH_TOKEN', 'GITHUB_TOKEN') { [Environment]::SetEnvironmentVariable($name, $null) }
$env:GH_CONFIG_DIR = Join-Path $work 'gh-config'
$env:FAKE_GH_LOG = Join-Path $work 'gh-calls.txt'

function Invoke-Setup([string]$repo, [string]$from = $work, [switch]$WithGh) {
  Push-Location -LiteralPath $from
  try {
    $env:PATH = if ($WithGh) { "$fakeBin;$bare" } else { $bare }
    $text = & $pwshExe -NoProfile -File $setup -Repo $repo 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
  } finally { $env:PATH = $bare; Pop-Location }
}
function Get-Head([string]$folder) { "$(git -C $folder rev-parse HEAD 2>$null)" }
function Get-Partials([string]$clone) { @(Get-ChildItem -LiteralPath (Join-Path $clone '.git') -Filter 'bosun-overlay-clone-*' -Force) }
$seedCount = 0
# A bare remote holding one commit, made from a seed checkout.
function New-Remote([string]$name, [scriptblock]$Fill) {
  $seed = Join-Path $work "seed-$name"
  git init -q $seed
  & $Fill $seed
  git -C $seed add -A
  git -C $seed commit -q -m "seed $name"
  $remote = Join-Path $work "$name.git"
  git clone -q --bare $seed $remote 2>$null
  $remote
}
# Adds a commit to a remote through a checkout of its own, and returns the new commit.
function Add-Commit([string]$remote, [string]$file) {
  $script:seedCount++
  $pusher = Join-Path $work "pusher-$script:seedCount"
  git clone -q $remote $pusher 2>$null
  Set-Content -LiteralPath (Join-Path $pusher $file) -Value "change $script:seedCount"
  git -C $pusher add -A
  git -C $pusher commit -q -m "change $script:seedCount"
  git -C $pusher push -q origin HEAD 2>$null
  Get-Head $pusher
}
# The public repository: the real hooks, and the setup script's own checks satisfied.
$hookFill = {
  param($seed)
  New-Item -ItemType Directory -Path (Join-Path $seed 'scripts\hooks') | Out-Null
  Set-Content -LiteralPath (Join-Path $seed 'scripts\hooks\pre-commit') -Value "#!/bin/sh`nexit 0"
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\hooks\post-merge') -Destination (Join-Path $seed 'scripts\hooks\post-merge')
  Set-Content -LiteralPath (Join-Path $seed '.gitignore') -Value '.private/'
  Set-Content -LiteralPath (Join-Path $seed 'README.md') -Value 'public'
  git -C $seed add -A
  git -C $seed update-index --add --chmod=+x scripts/hooks/pre-commit scripts/hooks/post-merge
}
$public = New-Remote 'public' $hookFill
$overlayRemote = New-Remote 'overlay' { param($seed) Set-Content -LiteralPath (Join-Path $seed 'config.toml') -Value 'vault_item = "x"' }
$emptyRemote = Join-Path $work 'empty.git'
git init -q --bare $emptyRemote
function New-Clone([string]$name) {
  $clone = Join-Path $work $name
  git clone -q $public $clone 2>$null
  $clone
}

try {
  # --- setup: the clone ---
  $owner = New-Clone 'owner'
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $r = Invoke-Setup $owner
  $private = Join-Path $owner '.private'
  Check 'a local path source is cloned with no gh at all' ($r.Exit -eq 0 -and (Test-Path (Join-Path $private '.git')) -and $r.Text -match 'cloned into \.private') "(exit $($r.Exit): $($r.Text))"
  Check 'its origin reads back as the source' ([IO.Path]::GetFullPath("$(git -C $private config --get remote.origin.url)") -eq [IO.Path]::GetFullPath($overlayRemote))
  Check 'it is marked as the overlay' ("$(git -C $private config --get bosun.overlay)" -eq 'true')
  Check 'no partial clone is left in the git folder' ((Get-Partials $owner).Count -eq 0)
  Check 'the undo is printed' ($r.Text -match 'delete \.private' -and $r.Text -match 'log --branches --not --remotes')
  Check 'the public checkout does not see the overlay' (-not (git -C $owner status --porcelain))
  $r = Invoke-Setup $owner
  Check 'a second run leaves the overlay as it is and says so' ($r.Exit -eq 0 -and $r.Text -match 'already present') "(exit $($r.Exit): $($r.Text))"

  $env:BOSUN_OVERLAY_REPO = $null
  Remove-Item -LiteralPath $env:FAKE_GH_LOG -ErrorAction Ignore
  $default = New-Clone 'default-source'
  $env:FAKE_GH_MODE = 'ok'; $env:FAKE_GH_REMOTE = $overlayRemote
  $r = Invoke-Setup $default -WithGh
  $calls = @(Get-Content -LiteralPath $env:FAKE_GH_LOG -ErrorAction Ignore)
  Check 'the default source is looked for and cloned through gh' ($r.Exit -eq 0 -and $calls -contains 'repo view rizonetech/Bosun-private --json name' -and ($calls | Where-Object { $_ -like 'repo clone rizonetech/Bosun-private *' })) "(exit $($r.Exit): $($r.Text) calls: $($calls -join ' | '))"
  Check 'and its GitHub origin reads back' ("$(git -C (Join-Path $default '.private') config --get remote.origin.url)" -eq 'https://github.com/rizonetech/Bosun-private.git')

  foreach ($case in @(
      @{ Name = 'gh missing'; Gh = $false; Mode = 'ok'; Says = 'gh is not installed' },
      @{ Name = 'gh signed out'; Gh = $true; Mode = 'signed-out'; Says = 'not signed in' },
      @{ Name = 'access refused'; Gh = $true; Mode = 'refused'; Says = 'cannot see rizonetech/Bosun-private' })) {
    $clone = New-Clone ('skip-' + ($case.Name -replace ' ', '-'))
    $env:FAKE_GH_MODE = $case.Mode
    $r = if ($case.Gh) { Invoke-Setup $clone -WithGh } else { Invoke-Setup $clone }
    Check "$($case.Name): one line, exit 0, no overlay" ($r.Exit -eq 0 -and $r.Text -match "overlay: skipped, .*$($case.Says)" -and -not (Test-Path (Join-Path $clone '.private')) -and "$(git -C $clone config --get core.hooksPath)" -eq 'scripts/hooks') "(exit $($r.Exit): $($r.Text))"
  }

  $retry = New-Clone 'clone-fails'
  $env:FAKE_GH_MODE = 'clone-fails'
  $r = Invoke-Setup $retry -WithGh
  Check 'a probe that passes and a clone that fails exits 1 with the reason' ($r.Exit -eq 1 -and $r.Text -match 'could not be cloned' -and $r.Text -match 'transfer was cut off') "(exit $($r.Exit): $($r.Text))"
  Check 'and leaves no .private and no partial clone' (-not (Test-Path (Join-Path $retry '.private')) -and (Get-Partials $retry).Count -eq 0)
  $env:FAKE_GH_MODE = 'ok'
  $r = Invoke-Setup $retry -WithGh
  Check 'a retry then clones it' ($r.Exit -eq 0 -and "$(git -C (Join-Path $retry '.private') config --get bosun.overlay)" -eq 'true') "(exit $($r.Exit): $($r.Text))"

  $wrong = New-Clone 'wrong-origin'
  $env:FAKE_GH_MODE = 'ok'; $env:FAKE_GH_REMOTE = $overlayRemote
  # A stand-in that clones fine but leaves the origin pointing somewhere else: the clone is not taken for the overlay.
  $realFake = Get-Content -LiteralPath (Join-Path $fakeBin 'fake-gh.ps1') -Raw
  Set-Content -LiteralPath (Join-Path $fakeBin 'fake-gh.ps1') -Value ($realFake -replace 'https://github.com/\$\(\$args\[2\]\)\.git', 'https://github.com/someone/else.git')
  $r = Invoke-Setup $wrong -WithGh
  Set-Content -LiteralPath (Join-Path $fakeBin 'fake-gh.ps1') -Value $realFake
  Check 'a clone whose origin is not the source is refused and removed' ($r.Exit -eq 1 -and $r.Text -match 'origin is not the source' -and -not (Test-Path (Join-Path $wrong '.private')) -and (Get-Partials $wrong).Count -eq 0) "(exit $($r.Exit): $($r.Text))"

  # --- setup: an existing .private is never changed ---
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $other = Join-Path $work 'other.git'
  git clone -q --bare $public $other 2>$null
  $existing = [ordered]@{
    'another repository''s checkout' = { param($p) git clone -q $other $p 2>$null }
    'a plain folder'                  = { param($p) New-Item -ItemType Directory -Path $p | Out-Null; Set-Content -LiteralPath (Join-Path $p 'notes.txt') -Value 'mine' }
    'a file'                          = { param($p) Set-Content -LiteralPath $p -Value 'a file named .private' }
  }
  foreach ($case in $existing.GetEnumerator()) {
    $clone = New-Clone ('existing-' + ($case.Key -replace '\W', ''))
    $p = Join-Path $clone '.private'
    & $case.Value $p
    $before = if (Test-Path $p -PathType Container) { (Get-ChildItem -LiteralPath $p -Force -Recurse | ForEach-Object FullName) -join '|' } else { Get-Content -LiteralPath $p -Raw }
    $config = if (Test-Path (Join-Path $p '.git\config')) { Get-Content -LiteralPath (Join-Path $p '.git\config') -Raw } else { '' }
    $r = Invoke-Setup $clone
    $after = if (Test-Path $p -PathType Container) { (Get-ChildItem -LiteralPath $p -Force -Recurse | ForEach-Object FullName) -join '|' } else { Get-Content -LiteralPath $p -Raw }
    $configAfter = if (Test-Path (Join-Path $p '.git\config')) { Get-Content -LiteralPath (Join-Path $p '.git\config') -Raw } else { '' }
    Check "$($case.Key) in .private is left exactly as it was" ($r.Exit -eq 0 -and $r.Text -match 'overlay: skipped, \.private is' -and $before -eq $after -and $config -eq $configAfter) "(exit $($r.Exit): $($r.Text))"
  }
  $matching = New-Clone 'existing-matching'
  git clone -q $overlayRemote (Join-Path $matching '.private') 2>$null
  $r = Invoke-Setup $matching
  Check 'an unmarked checkout of the overlay itself is marked, and said' ($r.Exit -eq 0 -and $r.Text -match 'marked it as one' -and "$(git -C (Join-Path $matching '.private') config --get bosun.overlay)" -eq 'true') "(exit $($r.Exit): $($r.Text))"

  # --- setup: .private never vouches for itself ---
  $env:BOSUN_OVERLAY_REPO = $null
  $self = New-Clone 'self-vouching'
  $selfPrivate = Join-Path $self '.private'
  git clone -q $other $selfPrivate 2>$null
  Set-Content -LiteralPath (Join-Path $selfPrivate 'config.toml') -Value "overlay-repo = '$other'"
  $r = Invoke-Setup $self
  Check 'a checkout whose own config.toml names its own origin is not marked' ($r.Exit -eq 0 -and $r.Text -match 'checkout of another repository' -and -not "$(git -C $selfPrivate config --get bosun.overlay)") "(exit $($r.Exit): $($r.Text))"

  # --- setup: a link is never followed ---
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $linked = New-Clone 'linked'
  New-Item -ItemType Junction -Path (Join-Path $linked '.private') -Target (Join-Path $matching '.private') | Out-Null
  git -C (Join-Path $matching '.private') config --unset bosun.overlay
  $config = Get-Content -LiteralPath (Join-Path $matching '.private\.git\config') -Raw
  $r = Invoke-Setup $linked
  Check 'a .private that is a junction is left alone, and its target not marked' ($r.Exit -eq 0 -and $r.Text -match 'is a link' -and (Get-Content -LiteralPath (Join-Path $matching '.private\.git\config') -Raw) -eq $config) "(exit $($r.Exit): $($r.Text))"
  git -C (Join-Path $matching '.private') config bosun.overlay true

  # --- setup: a run that finishes first is never nested in ---
  $raced = New-Clone 'raced'
  $env:BOSUN_OVERLAY_REPO = $null
  $env:FAKE_GH_MODE = 'race'; $env:FAKE_GH_REMOTE = $overlayRemote
  $r = Invoke-Setup $raced -WithGh
  Check 'a .private made by another run meanwhile is not moved into' ($r.Exit -eq 1 -and $r.Text -match 'could not be moved into \.private' -and (@(Get-ChildItem -LiteralPath (Join-Path $raced '.private') -Force).Name -join ',') -eq 'first.txt' -and (Get-Partials $raced).Count -eq 0) "(exit $($r.Exit): $($r.Text))"
  $env:FAKE_GH_MODE = 'ok'

  # --- setup: sources ---
  foreach ($bad in 'https://someone:s3cretTOKEN@example.com/overlay.git', 'https://example.com/overlay.git?access_token=s3cretTOKEN', 'https://example.com/overlay.git#s3cretTOKEN', 'ssh://git:s3cretTOKEN@example.com/overlay.git', 'git:s3cretTOKEN@example.com:overlay.git', '-uupload-pack=evil', 'not a source') {
    $clone = New-Clone ('bad-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    $env:BOSUN_OVERLAY_REPO = $bad
    $r = Invoke-Setup $clone
    Check "an unsupported source is refused without repeating it ($($bad.Substring(0, 12)))" ($r.Exit -eq 1 -and $r.Text -match 'not a supported overlay source' -and $r.Text -notmatch 's3cretTOKEN' -and $r.Text -notmatch 'upload-pack' -and -not (Test-Path (Join-Path $clone '.private'))) "(exit $($r.Exit): $($r.Text))"
  }

  # --- setup: -Repo names the checkout, wherever it is run from ---
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $spaced = New-Clone 'a checkout with spaces'
  $runFrom = New-Clone 'run-from-here'
  $r = Invoke-Setup $spaced -from $runFrom
  Check '-Repo from another checkout gives only the one it names an overlay' ($r.Exit -eq 0 -and (Test-Path (Join-Path $spaced '.private\.git')) -and -not (Test-Path (Join-Path $runFrom '.private'))) "(exit $($r.Exit): $($r.Text))"

  # --- setup: every hook must be recorded as executable ---
  $plain = New-Clone 'post-merge-not-executable'
  git -C $plain update-index --chmod=-x scripts/hooks/post-merge
  $r = Invoke-Setup $plain
  Check 'a post-merge not recorded as executable is refused with the fix' ($r.Exit -eq 1 -and $r.Text -match 'scripts/hooks/post-merge is not recorded in git as an executable' -and $r.Text -match 'update-index --chmod=\+x scripts/hooks/post-merge') "(exit $($r.Exit): $($r.Text))"

  # --- the post-merge hook, through a real pull ---
  function Invoke-Pull([string]$clone) { (git -C $clone pull --no-rebase 2>&1 | Out-String) }
  $incoming = Add-Commit $overlayRemote 'from-elsewhere.txt'
  $publicHead = Add-Commit $public 'public-1.txt'
  $out = Invoke-Pull $owner
  Check 'a pull fast-forwards a behind, clean overlay' ((Get-Head $private) -eq $incoming -and $out -match 'overlay: updated to') "($out)"
  Check 'and the public checkout is at the incoming commit' ((Get-Head $owner) -eq $publicHead)

  $publicHead = Add-Commit $public 'public-2.txt'
  $out = Invoke-Pull $owner
  Check 'a pull with nothing new for the overlay says it is current' ($out -match 'overlay: already current' -and (Get-Head $owner) -eq $publicHead) "($out)"

  $overlayHead = Get-Head $private
  $null = Add-Commit $overlayRemote 'dirty-incoming.txt'
  Set-Content -LiteralPath (Join-Path $private 'local-edit.txt') -Value 'not committed'
  Set-Content -LiteralPath (Join-Path $private 'config.toml') -Value 'vault_item = "edited"'
  $index = "$(git -C $private diff --cached --stat)"
  $publicHead = Add-Commit $public 'public-3.txt'
  $out = Invoke-Pull $owner
  Check 'a dirty overlay is not pulled, and says why' ((Get-Head $private) -eq $overlayHead -and $out -match 'uncommitted changes') "($out)"
  Check 'its files and index are as they were' ((Get-Content -LiteralPath (Join-Path $private 'local-edit.txt') -Raw).Trim() -eq 'not committed' -and (Get-Content -LiteralPath (Join-Path $private 'config.toml') -Raw).Trim() -eq 'vault_item = "edited"' -and "$(git -C $private diff --cached --stat)" -eq $index)
  Check 'and the public pull still succeeded' ((Get-Head $owner) -eq $publicHead)
  git -C $private checkout -q -- config.toml
  Remove-Item -LiteralPath (Join-Path $private 'local-edit.txt')

  git -C $private config --unset bosun.overlay
  $overlayHead = Get-Head $private
  $publicHead = Add-Commit $public 'public-4.txt'
  $out = Invoke-Pull $owner
  Check 'an unmarked checkout in .private is not pulled' ((Get-Head $private) -eq $overlayHead -and $out -match 'not marked as the overlay') "($out)"
  git -C $private config bosun.overlay true

  git -C $private pull -q --ff-only 2>$null
  Set-Content -LiteralPath (Join-Path $private 'local.txt') -Value 'local'
  git -C $private add -A; git -C $private commit -q -m 'local'
  $local = Get-Head $private
  $null = Add-Commit $overlayRemote 'diverging.txt'
  $publicHead = Add-Commit $public 'public-5.txt'
  $out = Invoke-Pull $owner
  Check 'a diverged overlay is left as it was, and says so' ((Get-Head $private) -eq $local -and $out -match 'could not be fast-forwarded' -and (Get-Head $owner) -eq $publicHead) "($out)"

  $empty = New-Clone 'empty-overlay'
  $env:BOSUN_OVERLAY_REPO = $emptyRemote
  $r = Invoke-Setup $empty
  $null = Add-Commit $public 'public-6.txt'
  $out = Invoke-Pull $empty
  Check 'an overlay with no upstream branch yet says so' ($r.Exit -eq 0 -and $out -match 'no upstream branch yet') "(setup $($r.Exit): $($r.Text) pull: $out)"
  $first = Add-Commit $emptyRemote 'first.txt'
  $null = Add-Commit $public 'public-6b.txt'
  $out = Invoke-Pull $empty
  Check 'once the empty overlay gets its first commit, a pull takes it' ((Get-Head (Join-Path $empty '.private')) -eq $first -and $out -match 'its first commit') "($out)"
  $second = Add-Commit $emptyRemote 'second.txt'
  $null = Add-Commit $public 'public-6c.txt'
  $out = Invoke-Pull $empty
  Check 'and tracks it from then on' ((Get-Head (Join-Path $empty '.private')) -eq $second -and $out -match 'overlay: updated to') "($out)"

  # A setting that hides untracked files does not hide them from the clean check.
  $hidden = Join-Path $empty '.private'
  git -C $hidden config status.showUntrackedFiles no
  Set-Content -LiteralPath (Join-Path $hidden 'owner-notes.txt') -Value 'untracked'
  $before = Get-Head $hidden
  $null = Add-Commit $emptyRemote 'third.txt'
  $null = Add-Commit $public 'public-6d.txt'
  $out = Invoke-Pull $empty
  Check 'an untracked file stops the pull even with status.showUntrackedFiles=no' ((Get-Head $hidden) -eq $before -and $out -match 'uncommitted changes') "($out)"

  # --- the hook run with the public repository's GIT_DIR, as git may run it ---
  $direct = New-Clone 'direct'
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $null = Invoke-Setup $direct
  $directPrivate = Join-Path $direct '.private'
  git -C $directPrivate reset -q --hard HEAD~1
  $wanted = "$(git -C $directPrivate rev-parse '@{u}')"
  $publicBefore = Get-Head $direct
  Push-Location -LiteralPath $direct
  try {
    $env:GIT_DIR = Join-Path $direct '.git'
    $out = & (Join-Path (Split-Path (Split-Path $gitExe)) 'bin\sh.exe') scripts/hooks/post-merge 2>&1 | Out-String
  } finally { Remove-Item Env:GIT_DIR; Pop-Location }
  Check 'with GIT_DIR set to the public repository, the hook pulls the overlay' ((Get-Head $directPrivate) -eq $wanted -and $out -match 'overlay: updated') "($out)"
  Check 'and leaves the public repository where it was' ((Get-Head $direct) -eq $publicBefore)

  # --- the hook never follows a junction to another checkout's overlay ---
  $hopper = New-Clone 'hook-junction'
  $targetPrivate = Join-Path $direct '.private'
  git -C $hopper config core.hooksPath scripts/hooks
  New-Item -ItemType Junction -Path (Join-Path $hopper '.private') -Target $targetPrivate | Out-Null
  git -C $targetPrivate reset -q --hard HEAD~1
  $targetHead = Get-Head $targetPrivate
  $null = Add-Commit $public 'public-junction.txt'
  $out = Invoke-Pull $hopper
  Check 'a .private junction is not pulled through' ((Get-Head $targetPrivate) -eq $targetHead -and $out -match 'is a link') "($out)"
  (Get-Item -LiteralPath (Join-Path $hopper '.private')).Delete()

  # --- undo ---
  Remove-Item -LiteralPath $private -Recurse -Force
  $publicHead = Add-Commit $public 'public-7.txt'
  $out = Invoke-Pull $owner
  Check 'with .private deleted a pull still works and makes no overlay' ((Get-Head $owner) -eq $publicHead -and -not (Test-Path $private) -and $out -notmatch 'overlay:') "(head $(Get-Head $owner) want $publicHead, private $(Test-Path $private): $out)"
  $env:BOSUN_OVERLAY_REPO = $overlayRemote
  $r = Invoke-Setup $owner
  Check 'running setup again brings it back' ($r.Exit -eq 0 -and "$(git -C $private config --get bosun.overlay)" -eq 'true') "(exit $($r.Exit): $($r.Text))"
} finally {
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
