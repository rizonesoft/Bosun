# Tests scripts/leak-scan.ps1 and scripts/hooks/pre-push (D00 T05 §4) through real pushes to local bare remotes: a
# secret (a token built at run time, so no token-shaped text is ever committed here) or a deny-list string anywhere
# in what a push publishes refuses it, the remote's refs read back unchanged, and nothing matched is ever printed.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
# The prerequisite first, so a machine without gitleaks leaves no scratch folder behind.
if (-not (Get-Command gitleaks -CommandType Application -ErrorAction Ignore)) { 'FAIL gitleaks is not installed; this test needs it (winget install --id Gitleaks.Gitleaks -e --version 8.30.1 --scope user)'; exit 1 }
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-leak-scan-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# --- isolation ---
$gitVariables = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX'
$saved = @{}
foreach ($name in @('PATH', 'HOME', 'USERPROFILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'BOSUN_OVERLAY_REPO', 'BOSUN_SCRATCH') + $gitVariables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in @('BOSUN_OVERLAY_REPO') + $gitVariables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwshExe = (Get-Process -Id $PID).Path
$gitleaks = Get-Command gitleaks -CommandType Application -ErrorAction Ignore | Select-Object -First 1
if (-not $gitleaks) { 'FAIL gitleaks is not installed; this test needs it (winget install --id Gitleaks.Gitleaks -e --version 8.30.1 --scope user)'; exit 1 }
$withTools = (@((Split-Path $gitExe), (Split-Path $pwshExe), (Split-Path $gitleaks.Source), (Join-Path $env:SystemRoot 'System32')) | Select-Object -Unique) -join ';'
$withoutGitleaks = (@((Split-Path $gitExe), (Split-Path $pwshExe), (Join-Path $env:SystemRoot 'System32')) | Select-Object -Unique) -join ';'
$env:PATH = $withTools
$env:HOME = Join-Path $work 'home'; $env:USERPROFILE = $env:HOME
New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
Set-Content -LiteralPath $env:GIT_CONFIG_GLOBAL -Value "[user]`n`tname = Leak Test`n`temail = leak-test@example.invalid`n[init]`n`tdefaultBranch = main"
# The scan writes its report under BOSUN_SCRATCH: set it to a folder of the test's own, and look there.
$reportDir = Join-Path $work 'reports'
New-Item -ItemType Directory -Path $reportDir | Out-Null
$env:BOSUN_SCRATCH = $reportDir
$tempBefore = @(Get-ChildItem -LiteralPath $reportDir -Filter 'bosun-leak-scan-*.json' -ErrorAction Ignore).Name

# A GitHub token shape, made here so the repository never holds one.
$letters = [char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
function New-Token { 'gh' + 'p_' + (-join (1..36 | ForEach-Object { $letters | Get-Random })) }

# --- fixtures ---
$count = 0
function Get-Head([string]$repo, [string]$ref = 'HEAD') { "$(git -C $repo rev-parse --verify --quiet $ref 2>$null)" }
function Get-Refs([string]$bare) { (git --git-dir=$bare for-each-ref --format='%(refname) %(objectname)') -join "`n" }
$seed = Join-Path $work 'seed'
git init -q $seed
New-Item -ItemType Directory -Path (Join-Path $seed 'scripts\hooks'), (Join-Path $seed 'scripts\lib') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\leak-scan.ps1') -Destination (Join-Path $seed 'scripts\leak-scan.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\sync.ps1') -Destination (Join-Path $seed 'scripts\sync.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\lib\overlay.ps1') -Destination (Join-Path $seed 'scripts\lib\overlay.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\hooks\pre-push') -Destination (Join-Path $seed 'scripts\hooks\pre-push')
Set-Content -LiteralPath (Join-Path $seed '.gitignore') -Value ".private/"
Set-Content -LiteralPath (Join-Path $seed 'notes.txt') -Value "line one`nline two`nline three"
git -C $seed add -A; git -C $seed update-index --chmod=+x scripts/hooks/pre-push; git -C $seed commit -q -m seed
function New-Remote([string]$name) { $bare = Join-Path $work "$name.git"; git clone -q --bare $seed $bare 2>$null; $bare }
function New-Checkout([string]$name, [string]$bare, [string[]]$Deny) {
  $checkout = Join-Path $work $name
  git clone -q $bare $checkout 2>$null
  git -C $checkout config core.hooksPath scripts/hooks
  if ($Deny) {
    New-Item -ItemType Directory -Path (Join-Path $checkout '.private') | Out-Null
    [IO.File]::WriteAllText((Join-Path $checkout '.private\deny-list.txt'), ($Deny -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
  }
  $checkout
}
function Add-Commit([string]$checkout, [string]$file, [string]$content, [string]$message = 'change') {
  $script:count++
  $path = Join-Path $checkout $file
  New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
  [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
  git -C $checkout add -A; git -C $checkout commit -q -m $message
  Get-Head $checkout
}
function Invoke-Push([string]$checkout, [string[]]$arguments) {
  $text = (git -C $checkout push origin @arguments 2>&1 | Out-String)
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
}
$entry = 'Acme Corp (EU)'

try {
  # --- clean ---
  $remote = New-Remote 'remote'
  $owner = New-Checkout 'owner' $remote -Deny @('# private strings', '', $entry)
  $clean = Add-Commit $owner 'clean.txt' 'nothing private here'
  $r = Invoke-Push $owner @('main')
  Check 'a clean push goes through' ($r.Exit -eq 0 -and (Get-Head $remote 'refs/heads/main') -eq $clean) "(exit $($r.Exit): $($r.Text))"

  # --- a secret at the tip ---
  $token = New-Token
  $before = Get-Refs $remote
  $null = Add-Commit $owner 'config\settings.txt' "user = me`ntoken = $token`n"
  $r = Invoke-Push $owner @('main')
  Check 'a secret is refused, the remote unchanged' ($r.Exit -ne 0 -and $r.Text -match 'leak-scan: .*config/settings.txt:2: secret \(github-pat\)' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  Check 'and nothing of the token is printed' (-not $r.Text.Contains($token) -and -not $r.Text.Contains($token.Substring(4, 12)))
  git -C $owner reset -q --hard origin/main

  # --- a secret in an intermediate commit, removed at the tip ---
  $null = Add-Commit $owner 'temp.txt' "token = $(New-Token)"
  $null = Add-Commit $owner 'temp.txt' 'removed'
  $r = Invoke-Push $owner @('main')
  Check 'a secret in a commit before the tip is refused' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\)' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main

  # --- a multi-ref push whose second ref holds the hit ---
  $null = Add-Commit $owner 'fine.txt' 'fine'
  git -C $owner branch -q second
  git -C $owner checkout -q second
  $null = Add-Commit $owner 'second.txt' "token = $(New-Token)"
  git -C $owner checkout -q main
  $r = Invoke-Push $owner @('main', 'second')
  Check 'a multi-ref push with the hit on its second ref is refused, every ref unchanged' ($r.Exit -ne 0 -and $r.Text -match 'second.txt:1: secret \(github-pat\)' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner branch -q -D second; git -C $owner reset -q --hard origin/main

  # --- a first push to an empty remote, the hit in the root commit ---
  $empty = Join-Path $work 'empty.git'
  git init -q --bare $empty
  $rooted = Join-Path $work 'rooted'
  git init -q $rooted
  Copy-Item -LiteralPath (Join-Path $seed 'scripts') -Destination (Join-Path $rooted 'scripts') -Recurse
  [IO.File]::WriteAllText((Join-Path $rooted 'root.txt'), "token = $(New-Token)", [Text.UTF8Encoding]::new($false))
  git -C $rooted add -A; git -C $rooted update-index --chmod=+x scripts/hooks/pre-push; git -C $rooted commit -q -m root
  git -C $rooted remote add origin $empty; git -C $rooted config core.hooksPath scripts/hooks
  $r = Invoke-Push $rooted @('main')
  Check 'a first push to an empty remote with the hit in the root commit is refused' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\)' -and -not (Get-Refs $empty)) "(exit $($r.Exit): $($r.Text))"

  # --- a stale tracking ref is not trusted ---
  $hit = Add-Commit $owner 'stale.txt' "token = $(New-Token)"
  # Fixture only, in a throwaway remote: the state the guard prevents (a hit already on the server) is built past it.
  git -C $owner push -q --no-verify origin "HEAD:refs/heads/parked" 2>$null
  git --git-dir=$remote update-ref -d refs/heads/parked
  Check 'the tracking ref still names the deleted branch' ((Get-Head $owner 'refs/remotes/origin/parked') -eq $hit)
  git -C $owner reset -q --hard origin/main
  $r = Invoke-Push $owner @("${hit}:refs/heads/republished")
  Check 'a branch republishing commits the remote lost is scanned, despite the tracking ref' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\)' -and -not (Get-Head $remote 'refs/heads/republished')) "(exit $($r.Exit): $($r.Text))"
  git -C $owner update-ref -d refs/remotes/origin/parked

  # --- deny list: content at a known line, literal and case-insensitive ---
  $before = Get-Refs $remote
  $denied = Add-Commit $owner 'docs\client.md' "# Notes`n`nWorking for ACME CORP (eu) this week.`n"
  $r = Invoke-Push $owner @('main')
  Check 'a deny-list string in added content is refused with its commit, file and line' ($r.Exit -ne 0 -and $r.Text -match "leak-scan: $($denied.Substring(0, 12)): docs/client.md:3: deny-list entry" -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  Check 'and neither the entry nor the line is printed' ($r.Text -notmatch '(?i)acme' -and $r.Text -notmatch 'this week')
  git -C $owner reset -q --hard origin/main
  $near = Add-Commit $owner 'near.txt' 'acme corp eu, without the brackets'
  $r = Invoke-Push $owner @('main')
  Check 'a near-match passes (literal, not a pattern)' ($r.Exit -eq 0 -and (Get-Head $remote 'refs/heads/main') -eq $near) "(exit $($r.Exit): $($r.Text))"

  # --- deny list: a renamed path, a message, a merge resolution, a tag annotation ---
  $before = Get-Refs $remote
  git -C $owner mv notes.txt "acme corp (eu) notes.txt"
  git -C $owner commit -q -m 'rename'
  $r = Invoke-Push $owner @('main')
  Check 'a path renamed to a deny-list string is refused and the path not shown' ($r.Exit -ne 0 -and $r.Text -match 'a path' -and $r.Text -notmatch '(?i)acme' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  $null = Add-Commit $owner 'plain.txt' 'plain' 'Weekly work for acme corp (EU)'
  $r = Invoke-Push $owner @('main')
  Check 'a deny-list string in a commit message is refused' ($r.Exit -ne 0 -and $r.Text -match 'commit message or author: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  git -C $owner checkout -q -b side
  $null = Add-Commit $owner 'side.txt' 'side'
  git -C $owner checkout -q main
  $null = Add-Commit $owner 'main.txt' 'main'
  git -C $owner merge -q --no-commit --no-ff side 2>$null
  [IO.File]::WriteAllText((Join-Path $owner 'merge.txt'), "resolved for Acme Corp (EU)`ntoken = $(New-Token)`n", [Text.UTF8Encoding]::new($false))
  git -C $owner add -A; git -C $owner commit -q -m 'merge side'
  $r = Invoke-Push $owner @('main')
  Check 'a deny-list string only in a merge resolution is refused' ($r.Exit -ne 0 -and $r.Text -match 'merge.txt:1: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  Check 'and a secret only in a merge resolution is refused' ($r.Text -match 'merge.txt:2: secret \(github-pat\)') "($($r.Text))"
  git -C $owner reset -q --hard origin/main; git -C $owner branch -q -D side
  git -C $owner tag -a v-private -m 'Release for Acme Corp (EU)'
  $r = Invoke-Push $owner @('refs/tags/v-private')
  Check 'a tag annotation holding a deny-list string is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list v-private)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d v-private | Out-Null

  # --- the deny pass reads what is published, not git's display of it ---
  $before = Get-Refs $remote
  $null = Add-Commit $owner 'docs\patch-notes.md' "Example:`n`n--- a/old.md`n+++ b/Acme Corp (EU).md`n@@ -1 +1 @@`n"
  $r = Invoke-Push $owner @('main')
  Check 'an entry inside a pasted diff header in a file is still found' ($r.Exit -ne 0 -and $r.Text -match 'docs/patch-notes.md:4: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  $cafe = New-Checkout 'cafe' $remote -Deny @('Café')
  $null = Add-Commit $cafe 'docs\Café.md' 'clean content'
  $r = Invoke-Push $cafe @('main')
  Check 'a non-ASCII path holding an entry is found (git would quote it in its display)' ($r.Exit -ne 0 -and $r.Text -match 'a path: deny-list entry' -and $r.Text -notmatch '(?i)caf') "(exit $($r.Exit): $($r.Text))"
  $utf16 = Join-Path $owner 'report.txt'
  [IO.File]::WriteAllText($utf16, "Status report`r`nClient: Acme Corp (EU)`r`n", [Text.Encoding]::Unicode)
  git -C $owner add -A; git -C $owner commit -q -m 'report'
  $r = Invoke-Push $owner @('main')
  Check 'an entry in a UTF-16 file git calls binary is found, at its line' ($r.Exit -ne 0 -and $r.Text -match 'report.txt:2: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  $plain16 = Join-Path $owner 'plain16.txt'
  [IO.File]::WriteAllBytes($plain16, [Text.Encoding]::Unicode.GetBytes("Status
Client: Acme Corp (EU)"))
  git -C $owner add -A; git -C $owner commit -q -m 'plain16'
  $r16 = Invoke-Push $owner @('main')
  Check 'an entry in UTF-16 without a byte-order mark is found at its own line' ($r16.Exit -ne 0 -and $r16.Text -match 'plain16.txt:2: deny-list entry') "(exit $($r16.Exit): $($r16.Text))"
  git -C $owner reset -q --hard origin/main
  $null = Add-Commit $owner 'Acme Corp (EU) keys.txt' "token = $(New-Token)"
  $r = Invoke-Push $owner @('main')
  Check 'a secret in a file named with an entry is reported with the path hidden' ($r.Exit -ne 0 -and $r.Text -match 'a path:1: secret' -and $r.Text -notmatch '(?i)acme') "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  # --- a tag inside a tag carries its own annotation ---
  git -C $owner tag -a rc-inner -m 'Release for Acme Corp (EU)'
  git -C $owner tag -a v-outer -m 'clean release note' rc-inner
  $r = Invoke-Push $owner @('refs/tags/v-outer')
  Check 'a clean tag pointing at a tag whose annotation holds an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list v-outer)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d v-outer rc-inner | Out-Null

  # --- a zero-byte file and an empty commit are clean ---
  $null = Add-Commit $owner '.gitkeep' ''
  git -C $owner commit -q --allow-empty -m 'empty'
  $r = Invoke-Push $owner @('main')
  Check 'a zero-byte file and an empty commit push cleanly with a deny list' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"

  # --- only what a commit adds: an entry already published is not refused again ---
  $published = Add-Commit $owner 'docs\history.md' "Project log`nClient: Acme Corp (EU)`nmore`nClient: Acme Corp (EU)`n"
  git -C $owner push -q --no-verify origin main 2>$null   # fixture only: the entry already on the remote, past the guard
  $null = Add-Commit $owner 'docs\history.md' "Project log, edited`nClient: Acme Corp (EU)`nmore`nClient: Acme Corp (EU)`n"
  $r = Invoke-Push $owner @('main')
  Check 'an unrelated edit beside an entry already published is not refused' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"
  $null = Add-Commit $owner 'docs\history.md' "Project log, edited`nClient: Acme Corp (EU)`nmore`n"
  $r = Invoke-Push $owner @('main')
  Check 'a cleanup removing one of two entries is not refused' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"
  $before = Get-Refs $remote
  $null = Add-Commit $owner 'docs\history.md' "Project log, edited`nClient: Acme Corp (EU)`nmore`nNew line for acme corp (eu)`n"
  $r = Invoke-Push $owner @('main')
  Check 'a new line with an entry in that file is refused at its line' ($r.Exit -ne 0 -and $r.Text -match 'docs/history.md:4: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  # --- an embedded checkout's path is published too ---
  $before = Get-Refs $remote
  $embedded = Join-Path $owner 'acme corp (eu) checkout'
  git init -q $embedded
  Set-Content -LiteralPath (Join-Path $embedded 'x.txt') -Value 'x'
  git -C $embedded add -A; git -C $embedded commit -q -m x
  git -C $owner add -A 2>$null; git -C $owner commit -q -m 'neutral'
  $r = Invoke-Push $owner @('main')
  Check 'an embedded checkout named with an entry is refused, the name not shown' ($r.Exit -ne 0 -and $r.Text -match 'a path: deny-list entry' -and $r.Text -notmatch '(?i)acme' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  Remove-Item -LiteralPath $embedded -Recurse -Force

  # --- a secret in a file's own name is never printed ---
  $nameToken = New-Token
  $null = Add-Commit $owner "$nameToken.txt" "token = $nameToken"
  $r = Invoke-Push $owner @('main')
  Check 'a secret in a file name is refused and its path hidden' ($r.Exit -ne 0 -and $r.Text -match 'a path:1: secret' -and -not $r.Text.Contains($nameToken) -and -not $r.Text.Contains($nameToken.Substring(4, 12))) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main

  # --- every secret found is kept from every path shown ---
  $firstToken = New-Token; $secondToken = New-Token
  $null = Add-Commit $owner "notes-$firstToken.txt" "a = $firstToken`nb = $secondToken`n"
  $r = Invoke-Push $owner @('main')
  Check 'with two secrets in one file, neither is shown through its path' ($r.Exit -ne 0 -and -not $r.Text.Contains($firstToken) -and -not $r.Text.Contains($secondToken) -and -not $r.Text.Contains($firstToken.Substring(4, 12))) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main

  # --- an embedded checkout replaced by a clean file of the same name ---
  $linkDir = Join-Path $owner 'vendor-tool'
  git init -q $linkDir
  Set-Content -LiteralPath (Join-Path $linkDir 'x.txt') -Value 'x'
  git -C $linkDir add -A; git -C $linkDir commit -q -m x
  git -C $owner add -A 2>$null; git -C $owner commit -q -m 'add a gitlink'
  $null = Invoke-Push $owner @('main')
  git -C $owner rm -q --cached vendor-tool
  Remove-Item -LiteralPath $linkDir -Recurse -Force
  $null = Add-Commit $owner 'vendor-tool' 'now a plain file'
  $r = Invoke-Push $owner @('main')
  Check 'a submodule entry replaced by a clean file pushes' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"

  # --- UTF-32 text, and tags on files ---
  $before = Get-Refs $remote
  [IO.File]::WriteAllText((Join-Path $owner 'report32.txt'), "Status`nClient: Acme Corp (EU)`n", [Text.Encoding]::UTF32)
  git -C $owner add -A; git -C $owner commit -q -m 'report32'
  $r = Invoke-Push $owner @('main')
  Check 'an entry in a UTF-32 file is found at its line' ($r.Exit -ne 0 -and $r.Text -match 'report32.txt:2: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  git -C $owner tag -a file-v1 -m 'release' 'HEAD:clean.txt'
  $r = Invoke-Push $owner @('refs/tags/file-v1')
  Check 'a clean tag on a file pushes' ($r.Exit -eq 0 -and (git --git-dir=$remote tag --list file-v1)) "(exit $($r.Exit): $($r.Text))"
  $privateBlob = "Client: Acme Corp (EU)" | git -C $owner hash-object -w --stdin
  git -C $owner tag -a file-v2 -m 'release' $privateBlob
  $r = Invoke-Push $owner @('refs/tags/file-v2')
  Check 'a tag on a file holding an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list file-v2)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d file-v2 | Out-Null

  # --- a tag on a tree, and a long chain of tags ---
  $null = Add-Commit $owner 'pack\inner\client.txt' 'Client: Acme Corp (EU)'
  git -C $owner tag -a tree-v1 -m 'release' 'HEAD:pack'
  git -C $owner reset -q --hard origin/main
  $r = Invoke-Push $owner @('refs/tags/tree-v1')
  Check 'a tag on a tree publishing a file with an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list tree-v1)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d tree-v1 | Out-Null
  git -C $owner tag -a chain-0 -m 'Release for Acme Corp (EU)'
  foreach ($n in 1..55) { git -C $owner tag -a "chain-$n" -m 'clean' "chain-$($n - 1)" }
  $r = Invoke-Push $owner @('refs/tags/chain-55')
  Check 'an entry at the bottom of a 56-tag chain is found' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list chain-55)) "(exit $($r.Exit): $($r.Text))"
  foreach ($n in 0..55) { git -C $owner tag -d "chain-$n" | Out-Null }

  # --- a tagged tree is checked by whole paths ---
  $pathy = New-Checkout 'pathy' $remote -Deny @('clients/demo-private')
  $null = Add-Commit $pathy 'export\clients\demo-private\report.txt' ''
  git -C $pathy tag -a export-v1 -m 'release' 'HEAD:export'
  git -C $pathy reset -q --hard origin/main
  $r = Invoke-Push $pathy @('refs/tags/export-v1')
  Check 'a deny entry holding a separator is found in a tagged tree''s full paths' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list export-v1)) "(exit $($r.Exit): $($r.Text))"

  # --- a secret a tag publishes without any commit ---
  $blobToken = New-Token
  $secretBlob = "token = $blobToken" | git -C $owner hash-object -w --stdin
  git -C $owner tag -a blob-secret -m 'release' $secretBlob
  $r = Invoke-Push $owner @('refs/tags/blob-secret')
  Check 'a tag on a file holding a secret is refused by gitleaks' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\) in a file it publishes' -and -not $r.Text.Contains($blobToken) -and -not (git --git-dir=$remote tag --list blob-secret)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d blob-secret | Out-Null

  # --- a push to a bare address works the same as one to a named remote ---
  $null = Add-Commit $owner 'by-address.txt' "token = $(New-Token)"
  $r = [pscustomobject]@{ Text = (git -C $owner push $remote main 2>&1 | Out-String); Exit = $LASTEXITCODE }
  Check 'a push to a bare address is scanned and refused' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\)') "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main

  # --- a second UTF-16 line with an entry, and an author holding one ---
  $before = Get-Refs $remote
  [IO.File]::WriteAllBytes((Join-Path $owner 'list16.txt'), [Text.Encoding]::Unicode.GetBytes("Client: Acme Corp (EU)`n"))
  git -C $owner add -A; git -C $owner commit -q -m 'list16'
  git -C $owner push -q --no-verify origin main 2>$null   # fixture only: the first occurrence already published
  $before = Get-Refs $remote
  [IO.File]::WriteAllBytes((Join-Path $owner 'list16.txt'), [Text.Encoding]::Unicode.GetBytes("Client: Acme Corp (EU)`nClient: Acme Corp (EU)`n"))
  git -C $owner add -A; git -C $owner commit -q -m 'list16 again'
  $r = Invoke-Push $owner @('main')
  Check 'a second occurrence added to a UTF-16 file is refused' ($r.Exit -ne 0 -and $r.Text -match 'list16.txt:2: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main
  Set-Content -LiteralPath (Join-Path $owner 'authored.txt') -Value 'clean'
  git -C $owner add -A
  git -C $owner -c user.name='Someone' -c user.email='someone@acme corp (eu).example' commit -q -m 'clean change'
  $r = Invoke-Push $owner @('main')
  Check 'an author address holding an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'commit message or author: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  git -C $owner reset -q --hard origin/main

  # --- UTF-16 with a byte-order mark and no NUL byte (CJK text) ---
  $cjk = New-Checkout 'cjk' $remote -Deny @("$([char]0x6771)$([char]0x4EAC)")
  [IO.File]::WriteAllBytes((Join-Path $cjk 'label.txt'), [byte[]](@(0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes("$([char]0x6771)$([char]0x4EAC)")))
  git -C $cjk add -A; git -C $cjk commit -q -m 'label'
  $r = Invoke-Push $cjk @('main')
  Check 'a UTF-16 file with a mark and no NUL byte is read as UTF-16' ($r.Exit -ne 0 -and $r.Text -match 'label.txt:1: deny-list entry') "(exit $($r.Exit): $($r.Text))"

  # --- a push address that differs from the fetch address: the push destination is what is asked ---
  $fetchFrom = New-Remote 'fetch-from'
  $pushTo = Join-Path $work 'push-to.git'
  git init -q --bare $pushTo
  $split = New-Checkout 'split' $fetchFrom -Deny @($entry)
  $null = Add-Commit $split 'already.txt' 'Client: Acme Corp (EU)'
  git -C $split push -q --no-verify origin main 2>$null   # fixture only: the string already on the fetch remote
  git -C $split remote set-url --push origin $pushTo
  $r = Invoke-Push $split @('main')
  Check 'with a separate push address, commits that address lacks are scanned' ($r.Exit -ne 0 -and $r.Text -match 'already.txt:1: deny-list entry' -and -not (git --git-dir=$pushTo for-each-ref)) "(exit $($r.Exit): $($r.Text))"

  # --- UTF-32 text with a character above U+FFFF ---
  $astral = "Orchid$([char]::ConvertFromUtf32(0x1F512))"
  $orchid = New-Checkout 'orchid' $remote -Deny @($astral)
  [IO.File]::WriteAllText((Join-Path $orchid 'report.txt'), "$astral`n", [Text.Encoding]::UTF32)
  git -C $orchid add -A; git -C $orchid commit -q -m 'report'
  $r = Invoke-Push $orchid @('main')
  Check 'an entry with a supplementary character in a UTF-32 file is found' ($r.Exit -ne 0 -and $r.Text -match 'report.txt:1: deny-list entry') "(exit $($r.Exit): $($r.Text))"

  # --- a Windows-1252 ("ANSI") file, no NUL and no mark ---
  $ansi = New-Checkout 'ansi' $remote -Deny @("Ren$([char]0xE9)e")
  [IO.File]::WriteAllBytes((Join-Path $ansi 'people.csv'), [Text.Encoding]::GetEncoding(1252).GetBytes("name`nRen$([char]0xE9)e Martin`n"))
  git -C $ansi add -A; git -C $ansi commit -q -m 'people'
  $r = Invoke-Push $ansi @('main')
  Check 'an entry in a Windows-1252 file is found at its line' ($r.Exit -ne 0 -and $r.Text -match 'people.csv:2: deny-list entry') "(exit $($r.Exit): $($r.Text))"

  # --- a branch name holding an entry, with clean commits ---
  $refOwner = New-Checkout 'ref-owner' $remote -Deny @('fixture-client')
  $r2 = Invoke-Push $refOwner @('HEAD:refs/heads/work-for-fixture-client')
  Check 'a branch named with an entry is refused though its commits are clean' ($r2.Exit -ne 0 -and $r2.Text -match 'a ref being pushed: deny-list entry' -and $r2.Text -notmatch 'fixture-client' -and -not (git --git-dir=$remote for-each-ref 'refs/heads/work-for-*')) "(exit $($r2.Exit): $($r2.Text))"

  # --- a newline in a file name cannot start a line of its own ---
  $blob = 'Client: Acme Corp (EU)' | git -C $owner hash-object -w --stdin
  git -C $owner update-index --add --cacheinfo "100644,$blob,odd`nFAKE STATUS.txt" 2>$null
  if ($LASTEXITCODE -eq 0) {
    git -C $owner commit -q -m 'odd name'
    $r = Invoke-Push $owner @('main')
    Check 'a newline in a reported path is shown escaped, not as a line' ($r.Exit -ne 0 -and $r.Text -match 'odd\\x0aFAKE STATUS.txt:1: deny-list entry' -and $r.Text -notmatch '(?m)^FAKE STATUS') "(exit $($r.Exit): $($r.Text))"
    git -C $owner reset -q --hard origin/main
  } else { 'note a newline in a file name: git here refuses such a path in the index, so that case runs only where git accepts one' }

  # --- a legacy-encoded commit message, and a file pushed to a custom ref ---
  $cafe2 = New-Checkout 'cafe2' $remote -Deny @("Caf$([char]0xE9)")
  $messageFile = Join-Path $work 'cp1252-message.txt'
  [IO.File]::WriteAllBytes($messageFile, [Text.Encoding]::GetEncoding(1252).GetBytes("Visit to Caf$([char]0xE9) Central"))
  git -C $cafe2 -c i18n.commitEncoding=windows-1252 commit -q --allow-empty -F $messageFile
  $r = Invoke-Push $cafe2 @('main')
  Check 'an entry in a Windows-1252 commit message is found' ($r.Exit -ne 0 -and $r.Text -match 'commit message or author: deny-list entry') "(exit $($r.Exit): $($r.Text))"
  $archived = 'Client: Acme Corp (EU)' | git -C $owner hash-object -w --stdin
  $r = Invoke-Push $owner @("${archived}:refs/archive/fixture")
  Check 'a file pushed to a custom ref is checked' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote for-each-ref refs/archive)) "(exit $($r.Exit): $($r.Text))"

  # --- a commit message in any encoding git knows (Windows-1250 here) ---
  $lodz = "$([char]0x141)$([char]0xF3)d$([char]0x17A)"
  $polish = New-Checkout 'polish' $remote -Deny @($lodz)
  $message1250 = Join-Path $work 'cp1250-message.txt'
  [IO.File]::WriteAllBytes($message1250, [Text.Encoding]::GetEncoding(1250).GetBytes("Trip to $lodz"))
  git -C $polish -c i18n.commitEncoding=windows-1250 commit -q --allow-empty -F $message1250
  $r = Invoke-Push $polish @('main')
  Check 'an entry in a Windows-1250 commit message is found through git''s re-encoding' ($r.Exit -ne 0 -and $r.Text -match 'commit message or author: deny-list entry') "(exit $($r.Exit): $($r.Text))"

  # --- secrets the patch scan cannot see: a binary file, a tag annotation ---
  $plainClone = New-Checkout 'plain-clone' $remote
  $binToken = New-Token
  [IO.File]::WriteAllBytes((Join-Path $plainClone 'blob.bin'), [byte[]](@(0, 1, 2, 0) + [Text.Encoding]::ASCII.GetBytes("`ntoken = $binToken`n")))
  git -C $plainClone add -A; git -C $plainClone commit -q -m 'binary'
  $r = Invoke-Push $plainClone @('main')
  Check 'a secret in a file git calls binary is refused, without a deny list' ($r.Exit -ne 0 -and $r.Text -match 'blob.bin:\d+: secret \(github-pat\) in a file git does not diff' -and -not $r.Text.Contains($binToken)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone reset -q --hard origin/main
  $tagToken = New-Token
  git -C $plainClone tag -a release-notes-2 -m "release token = $tagToken"
  $r = Invoke-Push $plainClone @('refs/tags/release-notes-2')
  Check 'a secret in a tag annotation on a published commit is refused, without a deny list' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\) in its annotation' -and -not $r.Text.Contains($tagToken) -and -not (git --git-dir=$remote tag --list release-notes-2)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone tag -d release-notes-2 | Out-Null

  # --- a text file git does not diff because of an attribute (*.png binary) ---
  Set-Content -LiteralPath (Join-Path $plainClone '.gitattributes') -Value '*.png binary'
  git -C $plainClone add -A; git -C $plainClone commit -q -m 'attributes'
  $null = Invoke-Push $plainClone @('main')
  $pngToken = New-Token
  Set-Content -LiteralPath (Join-Path $plainClone 'preview.png') -Value "HTTP/1.1 403 Forbidden`ntoken = $pngToken"
  git -C $plainClone add -A; git -C $plainClone commit -q -m 'preview'
  $r = Invoke-Push $plainClone @('main')
  Check 'a text file marked binary by an attribute is still scanned for secrets' ($r.Exit -ne 0 -and $r.Text -match 'preview.png:\d+: secret \(github-pat\) in a file git does not diff' -and -not $r.Text.Contains($pngToken)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone reset -q --hard origin/main

  # --- a secret pasted into a commit message ---
  $msgToken = New-Token
  git -C $plainClone commit -q --allow-empty -m "token = $msgToken"
  $r = Invoke-Push $plainClone @('main')
  Check 'a secret in a commit message is refused, without a deny list' ($r.Exit -ne 0 -and $r.Text -match 'commit message or author: secret \(github-pat\) in the commit itself' -and -not $r.Text.Contains($msgToken)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone reset -q --hard origin/main

  # --- a secret in a lightweight tag's name ---
  $refToken = New-Token
  git -C $plainClone tag "release-$refToken" HEAD
  $r = Invoke-Push $plainClone @("refs/tags/release-$refToken")
  Check 'a secret in a ref name is refused, without a deny list' ($r.Exit -ne 0 -and $r.Text -match 'a ref being pushed: secret \(github-pat\) in its name' -and -not $r.Text.Contains($refToken) -and -not (git --git-dir=$remote tag --list "release-*")) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone tag -d "release-$refToken" | Out-Null

  # --- a clean tag whose name holds a no-break space ---
  $nbspTag = "release$([char]0xA0)candidate"
  git -C $plainClone tag $nbspTag HEAD
  $r = Invoke-Push $plainClone @("refs/tags/$nbspTag")
  Check 'a clean tag named with a no-break space pushes' ($r.Exit -eq 0 -and (git --git-dir=$remote tag --list "release*candidate")) "(exit $($r.Exit): $($r.Text))"

  # --- a non-ASCII branch name holding an entry, read as git writes it ---
  $cafeRef = New-Checkout 'cafe-ref' $remote -Deny @("caf$([char]0xE9)")
  $r = Invoke-Push $cafeRef @("HEAD:refs/heads/caf$([char]0xE9)-notes")
  Check 'a non-ASCII branch name holding an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a ref being pushed: deny-list entry' -and -not (git --git-dir=$remote for-each-ref 'refs/heads/caf*')) "(exit $($r.Exit): $($r.Text))"

  # --- a caller's own GIT_CONFIG_COUNT entries survive the scan ---
  $env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = 'core.bosunTestMarker'; $env:GIT_CONFIG_VALUE_0 = 'kept'
  try {
    $null = Add-Commit $plainClone 'config-count.txt' 'clean'
    $r = [pscustomobject]@{ Text = (git -C $plainClone push $remote main 2>&1 | Out-String); Exit = $LASTEXITCODE }
    Check 'a push by address with inherited GIT_CONFIG_COUNT entries goes through' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"
  } finally { foreach ($n in 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0') { Remove-Item -LiteralPath "Env:$n" -ErrorAction Ignore } }

  # --- a secret only in a folder name (an embedded checkout's), clean content and messages ---
  $dirToken = New-Token
  $tokenDir = Join-Path $plainClone "vendor-$dirToken"
  git init -q $tokenDir
  Set-Content -LiteralPath (Join-Path $tokenDir 'x.txt') -Value 'x'
  git -C $tokenDir add -A; git -C $tokenDir commit -q -m 'neutral'
  git -C $plainClone add -A 2>$null; git -C $plainClone commit -q -m 'neutral'
  $r = Invoke-Push $plainClone @('main')
  Check 'a secret only in a folder name is refused, the name hidden' ($r.Exit -ne 0 -and $r.Text -match 'a file or folder name: secret \(github-pat\) in a path it adds' -and -not $r.Text.Contains($dirToken)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone reset -q --hard origin/main
  Remove-Item -LiteralPath $tokenDir -Recurse -Force

  # --- a secret only in a file name inside a tagged tree ---
  $treeToken = New-Token
  $emptyBlob = '' | git -C $plainClone hash-object -w --stdin
  git -C $plainClone update-index --add --cacheinfo "100644,$emptyBlob,keys-$treeToken.txt"
  $treeId = git -C $plainClone write-tree
  git -C $plainClone tag snapshot $treeId
  git -C $plainClone reset -q --hard HEAD
  $r = Invoke-Push $plainClone @('refs/tags/snapshot')
  Check 'a secret only in a file name inside a tagged tree is refused' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\) in a path it publishes' -and -not $r.Text.Contains($treeToken) -and -not (git --git-dir=$remote tag --list snapshot)) "(exit $($r.Exit): $($r.Text))"
  git -C $plainClone tag -d snapshot | Out-Null

  # --- a Windows-1252 tag annotation ---
  $notes = Join-Path $work 'cp1252-notes.txt'
  [IO.File]::WriteAllBytes($notes, [Text.Encoding]::GetEncoding(1252).GetBytes("Release for Caf$([char]0xE9) Central"))
  git -C $cafe2 tag -a release-1252 -F $notes HEAD
  $r = Invoke-Push $cafe2 @('refs/tags/release-1252')
  Check 'an entry in a Windows-1252 tag annotation is found' ($r.Exit -ne 0 -and $r.Text -match 'a tag or other object being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list release-1252)) "(exit $($r.Exit): $($r.Text))"

  # --- a destination with 900 tags on distinct commits: one clean push still goes through ---
  $tagged = New-Checkout 'many-tags' (New-Remote 'many-tags-remote')
  $stream = [Text.StringBuilder]::new()
  for ($n = 1; $n -le 900; $n++) {
    [void]$stream.Append("commit refs/tags/bulk-$n`ncommitter T <t@example.invalid> $(1700000000 + $n) +0000`ndata 4`nb$('{0:d3}' -f ($n % 1000))`n$(if ($n -eq 1) { "from refs/heads/main`n" } else { "from refs/tags/bulk-$($n - 1)`n" })`n")
  }
  # Written with LF line ends and fed as a file: PowerShell's pipe would end each line with CRLF, which fast-import refuses.
  $streamFile = Join-Path $work 'bulk-tags.fi'
  [IO.File]::WriteAllText($streamFile, $stream.ToString(), [Text.UTF8Encoding]::new($false))
  Start-Process -FilePath $gitExe -ArgumentList '-C', "`"$tagged`"", 'fast-import', '--quiet' -RedirectStandardInput $streamFile -NoNewWindow -Wait
  git -C $tagged push -q --no-verify origin 'refs/tags/*:refs/tags/*' 2>$null   # fixture only: the destination's own tags
  $bulk = @(git --git-dir="$(Join-Path $work 'many-tags-remote.git')" tag --list 'bulk-*')
  Check 'the destination holds the 900 tags the case needs' ($bulk.Count -eq 900) "($($bulk.Count) tags)"
  $null = Add-Commit $tagged 'one-more.txt' 'clean'
  $r = Invoke-Push $tagged @('main')
  Check 'a clean push to a destination with 900 tags goes through' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"
  Check 'and the scan leaves no temporary refs behind' (-not (git -C $tagged for-each-ref 'refs/bosun-leak-scan/'))

  # --- blank and # lines ignored ---
  $commented = New-Checkout 'commented' $remote -Deny @('', '# Acme', '   ')
  $fine = Add-Commit $commented 'acme.txt' '# Acme is only a comment in that list'
  $r = Invoke-Push $commented @('main')
  Check 'blank and # lines in the deny list match nothing' ($r.Exit -eq 0 -and (Get-Head $remote 'refs/heads/main') -eq $fine) "(exit $($r.Exit): $($r.Text))"
  git -C $owner pull -q --ff-only 2>$null

  # --- refusals on every failure ---
  $before = Get-Refs $remote
  $null = Add-Commit $owner 'harmless.txt' 'harmless'
  # Hiding gitleaks needs it in a folder of its own: a package-manager shim folder holding git or pwsh too cannot be
  # left off PATH without them (the hook needs a real pwsh executable, which a shim script cannot stand in for).
  if ((Split-Path $gitleaks.Source) -in (Split-Path $gitExe), (Split-Path $pwshExe)) {
    'note without gitleaks: gitleaks shares a folder with git or pwsh here, so it cannot be hidden from the hook; that case runs where it has a folder of its own'
  } else {
    $env:PATH = $withoutGitleaks
    try { $r = Invoke-Push $owner @('main') } finally { $env:PATH = $withTools }
    Check 'without gitleaks the push is refused with the install command' ($r.Exit -ne 0 -and $r.Text -match 'gitleaks is not installed' -and $r.Text -match 'winget install --id Gitleaks.Gitleaks' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  }
  $lock = [IO.File]::Open((Join-Path $owner '.private\deny-list.txt'), 'Open', 'ReadWrite', 'None')
  try { $r = Invoke-Push $owner @('main') } finally { $lock.Dispose() }
  Check 'a deny list that cannot be read refuses the push' ($r.Exit -ne 0 -and $r.Text -match 'deny list .* could not be read' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
  Push-Location -LiteralPath $owner
  try { $said = & $pwshExe -NoProfile -File (Join-Path $owner 'scripts\leak-scan.ps1') -Remote (Join-Path $work 'no-such-remote.git') -Updates "refs/heads/main $(Get-Head $owner) refs/heads/main $('0' * 40)" 2>&1 | Out-String; $code = $LASTEXITCODE } finally { Pop-Location }
  Check 'a remote that cannot be listed refuses' ($code -eq 1 -and $said -match 'remote could not be listed') "(exit ${code}: $said)"
  $r = Invoke-Push $owner @('main')
  Check 'and once the cause is gone the push goes through' ($r.Exit -eq 0) "(exit $($r.Exit): $($r.Text))"

  # --- no deny list: gitleaks still runs ---
  $bare = New-Checkout 'no-deny' $remote
  $null = Add-Commit $bare 'secret.txt' "token = $(New-Token)"
  $r = Invoke-Push $bare @('main')
  Check 'without a deny list a secret is still refused' ($r.Exit -ne 0 -and $r.Text -match 'secret \(github-pat\)') "(exit $($r.Exit): $($r.Text))"

  # --- a deletion publishes nothing ---
  git -C $owner push -q origin "HEAD:refs/heads/to-delete" 2>$null
  $r = Invoke-Push $owner @(':refs/heads/to-delete')
  Check 'a deletion push goes through' ($r.Exit -eq 0 -and -not (Get-Head $remote 'refs/heads/to-delete')) "(exit $($r.Exit): $($r.Text))"

  # --- a guarded sync shows the guard's reason ---
  $syncer = New-Checkout 'syncer' $remote
  $null = Add-Commit $syncer 'sync-secret.txt' "token = $(New-Token)"
  $publicMain = Get-Head $remote 'refs/heads/main'
  $said = & $pwshExe -NoProfile -File (Join-Path $syncer 'scripts\sync.ps1') 2>&1 | Out-String
  Check 'sync passes on the guard''s reason and the remote is unchanged' ($said -match 'leak-scan: .*secret \(github-pat\)' -and $said -match 'the push was refused' -and (Get-Head $remote 'refs/heads/main') -eq $publicMain) "($said)"

  $tempAfter = @(Get-ChildItem -LiteralPath $reportDir -Filter 'bosun-leak-scan-*.json' -ErrorAction Ignore).Name | Where-Object { $_ -notin $tempBefore }
  Check 'no gitleaks report is left behind in the scratch folder' (-not $tempAfter -and (Get-ChildItem -LiteralPath $reportDir -Force | Measure-Object).Count -eq 0) "($($tempAfter -join ', '))"
} finally {
  foreach ($name in $saved.Keys) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
