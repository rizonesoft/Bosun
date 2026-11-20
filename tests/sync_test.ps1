# Tests scripts/sync.ps1 (D00 T05 §3) against temporary repositories: a public bare remote and an overlay bare remote,
# an owner checkout of each with distinct, known commits, and the real scripts/hooks/post-merge installed. The
# operator's gh, tokens, overlay setting, home and git config never reach a case.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-sync-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# --- isolation ---
$saved = @{}
$gitVariables = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX'
foreach ($name in @('PATH', 'HOME', 'USERPROFILE', 'BOSUN_OVERLAY_REPO', 'GH_TOKEN', 'GITHUB_TOKEN', 'GH_CONFIG_DIR', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM') + $gitVariables) {
  $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwshExe = (Get-Process -Id $PID).Path
$env:PATH = (@((Split-Path $gitExe), (Split-Path $pwshExe), (Join-Path $env:SystemRoot 'System32')) | Select-Object -Unique) -join ';'
$env:HOME = Join-Path $work 'home'; $env:USERPROFILE = $env:HOME
New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
Set-Content -LiteralPath $env:GIT_CONFIG_GLOBAL -Value "[user]`n`tname = Sync Test`n`temail = sync-test@example.invalid`n[init]`n`tdefaultBranch = main`n[pull]`n`trebase = false"
# Cleared through Env:, which removes a variable; [Environment]::SetEnvironmentVariable with $null from PowerShell sets it
# to an empty string, and git takes an empty GIT_DIR as a path.
# The caller's repository variables go too, before the first fixture command, or a git add could write into the
# caller's own index.
foreach ($name in @('BOSUN_OVERLAY_REPO', 'GH_TOKEN', 'GITHUB_TOKEN') + $gitVariables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:GH_CONFIG_DIR = Join-Path $work 'gh-config'

$count = 0
function Get-Head([string]$folder, [string]$ref = 'HEAD') { "$(git -C $folder rev-parse $ref 2>$null)" }
function New-Bare([string]$name, [scriptblock]$Fill) {
  $seed = Join-Path $work "seed-$name"
  git init -q $seed
  if ($Fill) { & $Fill $seed; git -C $seed add -A; git -C $seed commit -q -m "seed $name" }
  $bare = Join-Path $work "$name.git"
  if ($Fill) { git clone -q --bare $seed $bare 2>$null } else { git init -q --bare $bare }
  $bare
}
# A commit added to a remote from a checkout of its own; returns the commit.
function Add-Remote([string]$bare, [string]$file, [string]$branch = 'main') {
  $script:count++
  $pusher = Join-Path $work "pusher-$script:count"
  git clone -q --branch $branch $bare $pusher 2>$null
  Set-Content -LiteralPath (Join-Path $pusher $file) -Value "remote change $script:count"
  git -C $pusher add -A; git -C $pusher commit -q -m "remote $script:count"
  git -C $pusher push -q origin HEAD 2>$null
  Get-Head $pusher
}
# A commit made in a checkout, not pushed; returns the commit.
function Add-Local([string]$checkout, [string]$file) {
  $script:count++
  Set-Content -LiteralPath (Join-Path $checkout $file) -Value "local change $script:count"
  git -C $checkout add -A; git -C $checkout commit -q -m "local $script:count"
  Get-Head $checkout
}
$publicFill = {
  param($seed)
  New-Item -ItemType Directory -Path (Join-Path $seed 'scripts\hooks'), (Join-Path $seed 'scripts\lib') | Out-Null
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\sync.ps1') -Destination (Join-Path $seed 'scripts\sync.ps1')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\lib\overlay.ps1') -Destination (Join-Path $seed 'scripts\lib\overlay.ps1')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\hooks\post-merge') -Destination (Join-Path $seed 'scripts\hooks\post-merge')
  Set-Content -LiteralPath (Join-Path $seed '.gitignore') -Value ".private/`n.private-clone-*/"
  Set-Content -LiteralPath (Join-Path $seed 'README.md') -Value 'public'
  git -C $seed add -A
  git -C $seed update-index --add --chmod=+x scripts/hooks/post-merge
}
$public = New-Bare 'public' $publicFill
$overlay = New-Bare 'overlay' { param($seed) Set-Content -LiteralPath (Join-Path $seed 'config.toml') -Value 'vault_item = "x"' }
$env:BOSUN_OVERLAY_REPO = $overlay

# An owner checkout: the public clone with its hooks on, and the overlay cloned into .private and marked.
function New-Owner([string]$name, [string]$overlaySource = $overlay, [switch]$NoOverlay) {
  $checkout = Join-Path $work $name
  git clone -q $public $checkout 2>$null
  git -C $checkout config core.hooksPath scripts/hooks
  if (-not $NoOverlay) {
    git clone -q $overlaySource (Join-Path $checkout '.private') 2>$null
    git -C (Join-Path $checkout '.private') config --local bosun.overlay true
  }
  $checkout
}
function Invoke-Sync([string]$checkout, [switch]$Check, [string]$from = $work) {
  Push-Location -LiteralPath $from
  try {
    $arguments = @('-NoProfile', '-File', (Join-Path $checkout 'scripts\sync.ps1'))
    if ($Check) { $arguments += '-Check' }
    $text = & $pwshExe @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
  } finally { Pop-Location }
}
# Everything a run could change, in one string: both checkouts' HEADs, branches, index and files, and both remotes.
function Get-Snapshot([string]$checkout) {
  $private = Join-Path $checkout '.private'
  $parts = foreach ($repo in $checkout, $private) {
    if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
      git -C $repo for-each-ref --format='%(refname) %(objectname)' refs/heads
      Get-Head $repo
      git -C $repo status --porcelain --untracked-files=all --ignored
      git -C $repo ls-files -s
    }
  }
  $parts += git --git-dir=$public for-each-ref --format='%(refname) %(objectname)'
  $parts += git --git-dir=$overlay for-each-ref --format='%(refname) %(objectname)'
  $parts -join "`n"
}

try {
  # --- level ---
  $owner = New-Owner 'owner'
  $private = Join-Path $owner '.private'
  $r = Invoke-Sync $owner -Check
  Check 'both level and clean: -Check exits 0 with one line each' ($r.Exit -eq 0 -and $r.Text -match 'public: main, level and clean' -and $r.Text -match 'overlay: main, level and clean') "(exit $($r.Exit): $($r.Text))"

  # --- -Check reports and changes nothing ---
  $incoming = Add-Remote $public 'incoming.txt'
  $outgoing = Add-Local $private 'outgoing.txt'
  $before = Get-Snapshot $owner
  $r = Invoke-Sync $owner -Check
  Check '-Check reports the fetched counts' ($r.Exit -eq 1 -and $r.Text -match 'public: main, behind 1' -and $r.Text -match 'overlay: main, ahead 1') "(exit $($r.Exit): $($r.Text))"
  Check '-Check changes no branch, file, index or remote ref' ((Get-Snapshot $owner) -eq $before)

  # --- a plain run: behind public, ahead overlay ---
  $r = Invoke-Sync $owner
  Check 'a plain run fast-forwards public to the incoming commit' ((Get-Head $owner) -eq $incoming) "(exit $($r.Exit): $($r.Text))"
  Check 'and pushes the exact overlay commit to its upstream' ((Get-Head $overlay 'refs/heads/main') -eq $outgoing -and (Get-Head $private) -eq $outgoing)
  Check 'and ends with both level, exit 0' ($r.Exit -eq 0 -and $r.Text -match 'public: main, level and clean' -and $r.Text -match 'overlay: main, level and clean') "($($r.Text))"
  Check 'with one line per repository, and no line from the hook' (([regex]::Matches($r.Text, '(?m)^overlay:')).Count -eq 1 -and ([regex]::Matches($r.Text, '(?m)^public:')).Count -eq 1) "($($r.Text))"

  # --- the other direction: ahead public, behind overlay ---
  $outgoingPublic = Add-Local $owner 'public-outgoing.txt'
  $incomingOverlay = Add-Remote $overlay 'overlay-incoming.txt'
  $r = Invoke-Sync $owner
  Check 'a plain run pushes public and fast-forwards the overlay' ($r.Exit -eq 0 -and (Get-Head $public 'refs/heads/main') -eq $outgoingPublic -and (Get-Head $private) -eq $incomingOverlay) "(exit $($r.Exit): $($r.Text))"

  # --- the real hook, with public behind: sync deals with the overlay, the hook stays out ---
  $incoming = Add-Remote $public 'hooked.txt'
  $incomingOverlay = Add-Remote $overlay 'hooked-overlay.txt'
  $r = Invoke-Sync $owner
  Check 'with the hook installed, both end at their incoming commits and one line each' ($r.Exit -eq 0 -and (Get-Head $owner) -eq $incoming -and (Get-Head $private) -eq $incomingOverlay -and ([regex]::Matches($r.Text, '(?m)^overlay:')).Count -eq 1 -and $r.Text -notmatch 'overlay: updated') "($($r.Text))"

  # --- an upstream branch with another name ---
  git -C $private checkout -q -b work --track origin/main 2>$null
  $outgoing = Add-Local $private 'renamed-upstream.txt'
  $r = Invoke-Sync $owner
  Check 'a branch tracking an upstream of another name is pushed there' ($r.Exit -eq 0 -and (Get-Head $overlay 'refs/heads/main') -eq $outgoing -and -not (git --git-dir=$overlay rev-parse --verify --quiet refs/heads/work)) "(exit $($r.Exit): $($r.Text))"
  git -C $private checkout -q main 2>$null; git -C $private merge -q --ff-only work 2>$null; git -C $private branch -q -D work

  # --- all or nothing: a diverged public beside an ahead overlay ---
  $null = Add-Remote $public 'diverge-remote.txt'
  $null = Add-Local $owner 'diverge-local.txt'
  $null = Add-Local $private 'waits.txt'
  $before = Get-Snapshot $owner
  $r = Invoke-Sync $owner
  Check 'a diverged public stops the run before anything changes' ($r.Exit -eq 1 -and $r.Text -match 'nothing was changed, because public is diverged' -and (Get-Snapshot $owner) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard '@{u}'
  $r = Invoke-Sync $owner
  Check 'once public is level, the waiting overlay commit goes' ($r.Exit -eq 0 -and (Get-Head $overlay 'refs/heads/main') -eq (Get-Head $private)) "(exit $($r.Exit): $($r.Text))"

  # --- a dirty, ahead overlay is not touched ---
  $null = Add-Local $private 'dirty-ahead.txt'
  Set-Content -LiteralPath (Join-Path $private 'config.toml') -Value 'vault_item = "being edited"'
  $before = Get-Snapshot $owner
  $r = Invoke-Sync $owner -Check
  Check 'a dirty overlay: -Check exits 1 and says so' ($r.Exit -eq 1 -and $r.Text -match 'overlay: main, ahead 1, uncommitted changes') "(exit $($r.Exit): $($r.Text))"
  $r = Invoke-Sync $owner
  Check 'a dirty, ahead overlay keeps its files, index, HEAD and remote ref' ($r.Exit -eq 1 -and $r.Text -match 'nothing was changed, because overlay is ahead 1, uncommitted changes' -and (Get-Snapshot $owner) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $private checkout -q -- config.toml
  $null = Invoke-Sync $owner

  # --- an ignored file an incoming change would replace is kept ---
  Add-Content -LiteralPath (Join-Path $private '.git\info\exclude') -Value 'owner.local'
  Set-Content -LiteralPath (Join-Path $private 'owner.local') -Value 'my local value'
  $overlayHead = Get-Head $private
  $null = Add-Remote $overlay 'owner.local'
  $r = Invoke-Sync $owner
  Check 'a fast-forward that would replace an ignored file is refused, and the file kept' ($r.Exit -eq 1 -and $r.Text -match 'overlay: the fast-forward was refused' -and (Get-Content -LiteralPath (Join-Path $private 'owner.local') -Raw).Trim() -eq 'my local value' -and (Get-Head $private) -eq $overlayHead) "(exit $($r.Exit): $($r.Text))"
  Remove-Item -LiteralPath (Join-Path $private 'owner.local')
  $null = Invoke-Sync $owner

  # --- a second push refused after the first succeeded, then a retry ---
  $outgoing = Add-Local $private 'first-push.txt'
  $outgoingPublic = Add-Local $owner 'second-push.txt'
  $hook = Join-Path $public 'hooks\pre-receive'
  Set-Content -LiteralPath $hook -Value "#!/bin/sh`necho 'remote: secret-internal-detail' >&2`nexit 1"
  $r = Invoke-Sync $owner
  Check 'a refused second push says what was done before it' ($r.Exit -eq 1 -and $r.Text -match 'public: the push was refused' -and $r.Text -match 'done before it: pushed overlay' -and (Get-Head $overlay 'refs/heads/main') -eq $outgoing) "(exit $($r.Exit): $($r.Text))"
  Check 'and shows none of the remote''s own text' ($r.Text -notmatch 'secret-internal-detail')
  Remove-Item -LiteralPath $hook
  $r = Invoke-Sync $owner
  Check 'a retry once the cause is gone finishes' ($r.Exit -eq 0 -and (Get-Head $public 'refs/heads/main') -eq $outgoingPublic) "(exit $($r.Exit): $($r.Text))"

  # --- push goes elsewhere: the remote's push address is not the overlay source ---
  git -C $private remote set-url --push origin $public
  $null = Add-Local $private 'would-leak.txt'
  $publicRefs = git --git-dir=$public for-each-ref --format='%(refname) %(objectname)'
  $r = Invoke-Sync $owner -Check
  Check 'an overlay whose push address points elsewhere is named' ($r.Exit -eq 1 -and $r.Text -match 'overlay: main, push goes elsewhere') "(exit $($r.Exit): $($r.Text))"
  $r = Invoke-Sync $owner
  Check 'and nothing is pushed anywhere' ($r.Exit -eq 1 -and "$(git --git-dir=$public for-each-ref --format='%(refname) %(objectname)')" -eq "$publicRefs" -and $r.Text -match 'nothing was changed') "(exit $($r.Exit): $($r.Text))"
  git -C $private config --unset remote.origin.pushurl
  $null = Invoke-Sync $owner

  # --- a remote with two push addresses: sync pushes to one validated address or not at all ---
  $second = Join-Path $work 'second-push.git'
  git clone -q --bare $public $second 2>$null
  git -C $owner remote set-url --push origin $public
  git -C $owner remote set-url --add --push origin $second
  $null = Add-Local $owner 'two-addresses.txt'
  $publicMain = Get-Head $public 'refs/heads/main'; $secondMain = Get-Head $second 'refs/heads/main'
  $r = Invoke-Sync $owner
  Check 'a remote that pushes to two addresses is named and nothing is pushed' ($r.Exit -eq 1 -and $r.Text -match 'public: main, ahead 1, pushes to more than one address' -and (Get-Head $public 'refs/heads/main') -eq $publicMain -and (Get-Head $second 'refs/heads/main') -eq $secondMain) "(exit $($r.Exit): $($r.Text))"
  git -C $owner config --unset-all remote.origin.pushurl
  $null = Invoke-Sync $owner
  # --- pushInsteadOf: the commit goes where get-url --push says, and that address is the one checked ---
  $decoy = Join-Path $work 'decoy-overlay.git'
  git clone -q --bare $overlay $decoy 2>$null
  git -C $private config "url.$decoy.pushInsteadOf" $overlay
  $decoyMain = Get-Head $decoy 'refs/heads/main'
  $null = Add-Local $private 'rewritten.txt'
  $r = Invoke-Sync $owner
  Check 'a pushInsteadOf that rewrites the overlay address is named and nothing leaves' ($r.Exit -eq 1 -and $r.Text -match 'overlay: main, push goes elsewhere' -and (Get-Head $decoy 'refs/heads/main') -eq $decoyMain) "(exit $($r.Exit): $($r.Text))"
  git -C $private remote set-url --push origin $overlay
  $r = Invoke-Sync $owner
  Check 'with an explicit push address, which pushInsteadOf leaves alone, the commit goes to the overlay itself' ($r.Exit -eq 0 -and (Get-Head $overlay 'refs/heads/main') -eq (Get-Head $private) -and (Get-Head $decoy 'refs/heads/main') -eq $decoyMain) "(exit $($r.Exit): $($r.Text))"
  git -C $private config --unset "url.$decoy.pushInsteadOf"; git -C $private config --unset remote.origin.pushurl

  # --- -Check leaves FETCH_HEAD alone ---
  git -C $owner fetch -q origin main 2>$null
  $fetchHead = Get-Content -LiteralPath (Join-Path $owner '.git\FETCH_HEAD') -Raw
  $null = Add-Remote $public 'fetch-head.txt'
  $r = Invoke-Sync $owner -Check
  Check '-Check leaves FETCH_HEAD as the operator''s own fetch left it' ((Get-Content -LiteralPath (Join-Path $owner '.git\FETCH_HEAD') -Raw) -eq $fetchHead -and $r.Text -match 'public: main, behind 1') "($($r.Text))"
  $null = Invoke-Sync $owner
  # --- an upstream branch deleted on its remote ---
  git -C $private checkout -q -b topic 2>$null
  git -C $private push -q -u origin topic 2>$null
  git --git-dir=$overlay update-ref -d refs/heads/topic
  $null = Add-Local $private 'topic.txt'
  $r = Invoke-Sync $owner
  Check 'an upstream branch deleted on its remote is named and not pushed back' ($r.Exit -eq 1 -and $r.Text -match 'overlay: topic, upstream branch gone' -and -not (git --git-dir=$overlay rev-parse --verify --quiet refs/heads/topic)) "(exit $($r.Exit): $($r.Text))"
  git -C $private checkout -q main 2>$null; git -C $private branch -q -D topic

  # --- states that are not acted on ---
  git -C $private checkout -q --detach 2>$null
  $r = Invoke-Sync $owner -Check
  Check 'a detached HEAD is named' ($r.Exit -eq 1 -and $r.Text -match 'overlay: detached HEAD') "($($r.Text))"
  git -C $private checkout -q main 2>$null
  git -C $private config --local --unset bosun.overlay
  $r = Invoke-Sync $owner -Check
  Check 'an unmarked .private is not the overlay' ($r.Exit -eq 1 -and $r.Text -match 'overlay: not the overlay \(not marked') "($($r.Text))"
  git -C $private config --local bosun.overlay true
  git -C $owner checkout -q -b local-only 2>$null
  $r = Invoke-Sync $owner -Check
  Check 'a branch with no upstream is named' ($r.Exit -eq 1 -and $r.Text -match 'public: local-only, no upstream branch') "($($r.Text))"
  git -C $owner checkout -q main 2>$null
  $index = Join-Path $private '.git\index'
  $savedIndex = [IO.File]::ReadAllBytes($index)
  [IO.File]::WriteAllBytes($index, [byte[]](1..64))
  $r = Invoke-Sync $owner
  [IO.File]::WriteAllBytes($index, $savedIndex)
  Check 'an unreadable index is never taken for clean' ($r.Exit -eq 1 -and $r.Text -match 'overlay: main, could not be read \(status\)') "($($r.Text))"
  git -C $owner remote set-url origin (Join-Path $work 'no-such-remote.git')
  $r = Invoke-Sync $owner -Check
  Check 'an unreachable remote is named' ($r.Exit -eq 1 -and $r.Text -match 'public: main, remote unreachable') "($($r.Text))"
  git -C $owner remote set-url origin $public

  # --- tags: never pruned, never pushed along ---
  git -C $owner config fetch.pruneTags true
  git -C $owner tag local-recovery
  git -C $owner config push.followTags true
  $tagged = Add-Local $owner 'tagged.txt'
  git -C $owner tag -a release-notes -m 'private tag message'
  $r = Invoke-Sync $owner -Check
  Check '-Check keeps a local-only tag even with fetch.pruneTags' ("$(git -C $owner tag --list local-recovery)" -eq 'local-recovery') "($($r.Text))"
  git -C $owner config remote.origin.prune true
  git -C $owner config remote.origin.pruneTags true
  $r = Invoke-Sync $owner -Check
  Check 'and with remote.origin.prune and remote.origin.pruneTags' ("$(git -C $owner tag --list local-recovery)" -eq 'local-recovery') "($($r.Text))"
  git -C $owner config --unset remote.origin.prune; git -C $owner config --unset remote.origin.pruneTags
  $r = Invoke-Sync $owner
  Check 'a push sends the branch commit and no tag, even with push.followTags' ($r.Exit -eq 0 -and (Get-Head $public 'refs/heads/main') -eq $tagged -and -not (git --git-dir=$public tag --list)) "(exit $($r.Exit): $($r.Text) tags: $(git --git-dir=$public tag --list))"
  git -C $owner config --unset fetch.pruneTags; git -C $owner config --unset push.followTags

  # --- the hook's own fetch, run by sync's public fast-forward, prunes nothing either ---
  git -C $private tag overlay-only
  git -C $private config fetch.prune true; git -C $private config fetch.pruneTags true
  $incoming = Add-Remote $public 'hook-prune.txt'
  $r = Invoke-Sync $owner
  Check 'a public fast-forward keeps a local-only overlay tag through the hook' ($r.Exit -eq 0 -and (Get-Head $owner) -eq $incoming -and "$(git -C $private tag --list overlay-only)" -eq 'overlay-only') "(exit $($r.Exit): $($r.Text))"
  git -C $private config --unset fetch.prune; git -C $private config --unset fetch.pruneTags
  # --- a configured tag refspec does not move a local tag ---
  git -C $owner tag release
  $tagPusher = Join-Path $work 'tag-pusher'
  git clone -q $public $tagPusher 2>$null
  Set-Content -LiteralPath (Join-Path $tagPusher 'tagged-remote.txt') -Value 'remote tag'
  git -C $tagPusher add -A; git -C $tagPusher commit -q -m 'remote tag'
  git -C $tagPusher tag release; git -C $tagPusher push -q origin HEAD refs/tags/release 2>$null
  git -C $owner config --add remote.origin.fetch '+refs/tags/*:refs/tags/*'
  $localTag = Get-Head $owner 'refs/tags/release'
  $r = Invoke-Sync $owner -Check
  Check 'a configured tag refspec does not move a local tag in -Check' ((Get-Head $owner 'refs/tags/release') -eq $localTag) "($($r.Text))"
  git -C $owner config --unset remote.origin.fetch '\+refs/tags/\*:refs/tags/\*'
  git -C $owner tag -d release | Out-Null

  # --- a squash in mergeOptions cannot pass for a fast-forward ---
  git -C $owner config branch.main.mergeOptions --squash
  $r = Invoke-Sync $owner
  Check 'with mergeOptions --squash the fast-forward still moves the branch, index clean' ($r.Exit -eq 0 -and (Get-Head $owner) -eq (Get-Head $public 'refs/heads/main') -and -not (git -C $owner diff --cached --name-only)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner config --unset branch.main.mergeOptions
  # --- a tag with the branch's own name does not hide its upstream ---
  git -C $owner tag main
  $r = Invoke-Sync $owner -Check
  Check 'a tag named like the branch leaves its upstream found' ($r.Text -match 'public: main, level and clean') "($($r.Text))"
  git -C $owner tag -d main | Out-Null
  # --- -Check never rewrites the index ---
  $touched = Join-Path $private 'config.toml'
  (Get-Item -LiteralPath $touched).LastWriteTime = (Get-Date).AddMinutes(-5)
  $indexBefore = (Get-FileHash -LiteralPath (Join-Path $private '.git\index')).Hash
  $r = Invoke-Sync $owner -Check
  Check '-Check leaves the index file byte for byte' ((Get-FileHash -LiteralPath (Join-Path $private '.git\index')).Hash -eq $indexBefore) "($($r.Text))"
  # --- run with & in the caller's own session: its git variables are put back ---
  $probe = Join-Path $work 'caller-probe.ps1'
  Set-Content -LiteralPath $probe -Value "`$env:GIT_INDEX_FILE = 'caller-index'; & '$(Join-Path $owner 'scripts\sync.ps1')' -Check | Out-Null; 'after=' + `$env:GIT_INDEX_FILE"
  $said = & $pwshExe -NoProfile -File $probe 2>&1 | Out-String
  Check 'a caller''s GIT_INDEX_FILE is the same after sync ran in its session' ($said -match 'after=caller-index') "($said)"

  # --- no git on PATH ---
  $savedPath = $env:PATH
  $env:PATH = (@((Split-Path $pwshExe), (Join-Path $env:SystemRoot 'System32')) | Select-Object -Unique) -join ';'
  try { $r = Invoke-Sync $owner -Check } finally { $env:PATH = $savedPath }
  Check 'with no git on PATH it says so and exits 1' ($r.Exit -eq 1 -and $r.Text -match 'git is not on PATH') "(exit $($r.Exit): $($r.Text))"
  # --- run from another checkout, with GIT_DIR pointing at it ---
  $elsewhere = Join-Path $work 'elsewhere'
  git init -q $elsewhere
  Set-Content -LiteralPath (Join-Path $elsewhere 'mine.txt') -Value 'dirty work'
  git -C $elsewhere add mine.txt
  $elseBefore = "$(git -C $elsewhere status --porcelain) $(git -C $elsewhere ls-files -s)"
  $incoming = Add-Remote $public 'from-elsewhere.txt'
  $env:GIT_DIR = Join-Path $elsewhere '.git'
  try { $r = Invoke-Sync $owner -from $elsewhere } finally { $env:GIT_DIR = $null }
  Check 'run from another checkout with GIT_DIR set, it syncs its own and leaves that one alone' ($r.Exit -eq 0 -and (Get-Head $owner) -eq $incoming -and "$(git -C $elsewhere status --porcelain) $(git -C $elsewhere ls-files -s)" -eq $elseBefore -and -not (git -C $elsewhere rev-parse --verify --quiet HEAD)) "(exit $($r.Exit): $($r.Text))"

  # --- an empty overlay whose first commit arrives while public is current ---
  $empty = New-Bare 'empty-overlay' $null
  $env:BOSUN_OVERLAY_REPO = $empty
  $fresh = New-Owner 'fresh' $empty
  $freshPrivate = Join-Path $fresh '.private'
  Add-Content -LiteralPath (Join-Path $freshPrivate '.git\info\exclude') -Value 'keep.local'
  Set-Content -LiteralPath (Join-Path $freshPrivate 'keep.local') -Value 'kept'
  $r = Invoke-Sync $fresh -Check
  Check 'an empty overlay with nothing pushed is level' ($r.Exit -eq 0 -and $r.Text -match 'overlay: main, empty, no commit yet') "(exit $($r.Exit): $($r.Text))"
  $firstPusher = Join-Path $work 'first-pusher'
  git init -q -b trunk $firstPusher
  Set-Content -LiteralPath (Join-Path $firstPusher 'first.txt') -Value 'first'
  git -C $firstPusher add -A; git -C $firstPusher commit -q -m first
  git -C $firstPusher push -q $empty trunk 2>$null
  git --git-dir=$empty symbolic-ref HEAD refs/heads/trunk
  # A fetch refspec naming only main cannot hide a first commit on trunk.
  git -C $freshPrivate config remote.origin.fetch '+refs/heads/main:refs/remotes/origin/main'
  $r = Invoke-Sync $fresh -Check
  Check '-Check names the waiting first commit and does not take it' ($r.Exit -eq 1 -and $r.Text -match 'overlay: main, empty, first commit waiting on trunk' -and -not (git -C $freshPrivate rev-parse --verify --quiet HEAD)) "(exit $($r.Exit): $($r.Text))"
  $r = Invoke-Sync $fresh
  Check 'a plain run takes it, tracks it, and keeps an ignored local file' ($r.Exit -eq 0 -and (Get-Head $freshPrivate) -eq (Get-Head $firstPusher) -and "$(git -C $freshPrivate config --local --get branch.trunk.remote)" -eq 'origin' -and "$(git -C $freshPrivate config --local --get branch.trunk.merge)" -eq 'refs/heads/trunk' -and (Get-Content -LiteralPath (Join-Path $freshPrivate 'keep.local') -Raw).Trim() -eq 'kept') "(exit $($r.Exit): $($r.Text))"
  $env:BOSUN_OVERLAY_REPO = $overlay

  # --- an empty overlay whose remote has a branch but no HEAD naming it ---
  $headless = Join-Path $work 'headless.git'
  git init -q --bare $headless
  git --git-dir=$headless symbolic-ref HEAD refs/heads/master
  $env:BOSUN_OVERLAY_REPO = $headless
  $lost = New-Owner 'headless-owner' $headless
  git -C $firstPusher push -q $headless trunk 2>$null
  $r = Invoke-Sync $lost -Check
  Check 'an empty overlay whose remote has branches but no default is not reported clean' ($r.Exit -eq 1 -and $r.Text -match 'branches but no default branch') "(exit $($r.Exit): $($r.Text))"
  $env:BOSUN_OVERLAY_REPO = $overlay
  # --- a stranger's clone: no overlay at all ---
  $stranger = New-Owner 'stranger' -NoOverlay
  $r = Invoke-Sync $stranger -Check
  Check 'a clone without an overlay is level when public is' ($r.Exit -eq 0 -and $r.Text -match 'overlay: not present') "(exit $($r.Exit): $($r.Text))"
  $incoming = Add-Remote $public 'for-stranger.txt'
  $r = Invoke-Sync $stranger
  Check 'and a plain run updates public and makes no overlay' ($r.Exit -eq 0 -and (Get-Head $stranger) -eq $incoming -and -not (Test-Path (Join-Path $stranger '.private'))) "(exit $($r.Exit): $($r.Text))"
} finally {
  foreach ($name in $saved.Keys) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
