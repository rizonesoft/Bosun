# Tests scripts/leak-scan.ps1 and scripts/hooks/pre-push (D00 T05 §4) through real pushes to local bare remotes: a
# secret (a token built at run time, so no token-shaped text is ever committed here) or a deny-list string anywhere
# in what a push publishes refuses it, the remote's refs read back unchanged, and nothing matched is ever printed.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
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
  Check 'a deny-list string in a commit message is refused' ($r.Exit -ne 0 -and $r.Text -match 'commit message: deny-list entry' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
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
  Check 'a tag annotation holding a deny-list string is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list v-private)) "(exit $($r.Exit): $($r.Text))"
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
  Check 'a clean tag pointing at a tag whose annotation holds an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list v-outer)) "(exit $($r.Exit): $($r.Text))"
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
  Check 'a tag on a file holding an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list file-v2)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d file-v2 | Out-Null

  # --- a tag on a tree, and a long chain of tags ---
  $null = Add-Commit $owner 'pack\inner\client.txt' 'Client: Acme Corp (EU)'
  git -C $owner tag -a tree-v1 -m 'release' 'HEAD:pack'
  git -C $owner reset -q --hard origin/main
  $r = Invoke-Push $owner @('refs/tags/tree-v1')
  Check 'a tag on a tree publishing a file with an entry is refused' ($r.Exit -ne 0 -and $r.Text -match 'a tag being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list tree-v1)) "(exit $($r.Exit): $($r.Text))"
  git -C $owner tag -d tree-v1 | Out-Null
  git -C $owner tag -a chain-0 -m 'Release for Acme Corp (EU)'
  foreach ($n in 1..55) { git -C $owner tag -a "chain-$n" -m 'clean' "chain-$($n - 1)" }
  $r = Invoke-Push $owner @('refs/tags/chain-55')
  Check 'an entry at the bottom of a 56-tag chain is found' ($r.Exit -ne 0 -and $r.Text -match 'a tag being pushed: deny-list entry' -and -not (git --git-dir=$remote tag --list chain-55)) "(exit $($r.Exit): $($r.Text))"
  foreach ($n in 0..55) { git -C $owner tag -d "chain-$n" | Out-Null }

  # --- blank and # lines ignored ---
  $commented = New-Checkout 'commented' $remote -Deny @('', '# Acme', '   ')
  $fine = Add-Commit $commented 'acme.txt' '# Acme is only a comment in that list'
  $r = Invoke-Push $commented @('main')
  Check 'blank and # lines in the deny list match nothing' ($r.Exit -eq 0 -and (Get-Head $remote 'refs/heads/main') -eq $fine) "(exit $($r.Exit): $($r.Text))"
  git -C $owner pull -q --ff-only 2>$null

  # --- refusals on every failure ---
  $before = Get-Refs $remote
  $null = Add-Commit $owner 'harmless.txt' 'harmless'
  $env:PATH = $withoutGitleaks
  try { $r = Invoke-Push $owner @('main') } finally { $env:PATH = $withTools }
  Check 'without gitleaks the push is refused with the install command' ($r.Exit -ne 0 -and $r.Text -match 'gitleaks is not installed' -and $r.Text -match 'winget install --id Gitleaks.Gitleaks' -and (Get-Refs $remote) -eq $before) "(exit $($r.Exit): $($r.Text))"
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
