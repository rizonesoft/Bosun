# The preflight's checks against fake tools, disposable repos, test registry keys and a JSON task store, and the PATH
# script against a test variable. Nothing here touches the real user PATH, policy, power scheme or tasks.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$failed = 0
function Check($ok, $what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-preflight-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$system = Join-Path $env:SystemRoot 'System32'

function Fake([string]$name, [string]$body) {
  $dir = Join-Path $work $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content (Join-Path $dir 'cmake.cmd') "@echo off`r`n$body" -Encoding ascii
  $dir
}
# The preflight with $path as the watchdog's PATH. Without -Only, the CMake and deny list checks these cases cover.
function Preflight([string]$path, [string[]]$extra = @()) {
  if ($extra -notcontains '-Only') { $extra += @('-Only', 'cmake,denylist') }
  $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-preflight.ps1') -SearchPath $path @extra 2>&1
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}
# A fake tool $name.cmd in its own folder, returning that folder.
function Tool([string]$folder, [string]$name, [string]$body) {
  $dir = Join-Path $work $folder
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content (Join-Path $dir "$name.cmd") "@echo off`r`n$body" -Encoding ascii
  $dir
}

try {
  $good = Fake 'good' 'echo cmake version 3.31.6'
  $old = Fake 'old' 'echo cmake version 3.20.0'
  $broken = Fake 'broken' "echo something went wrong`r`nexit /b 1"
  $vsNone = Join-Path $work 'vs-empty'
  New-Item -ItemType Directory -Force $vsNone | Out-Null
  $vswhereNoCMake = Join-Path $work 'vswhere-nocmake.cmd'
  Set-Content $vswhereNoCMake "@echo $vsNone" -Encoding ascii

  # The deny list's self-test needs Python, so a PATH that should pass carries its folder.
  $pythonDir = Split-Path (Get-Command python -CommandType Application | Select-Object -First 1).Source
  $r = Preflight "$good;$pythonDir;$system"
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   cmake 3\.31\.6' -and $r.Text -match 'ok   deny list self-test') 'a CMake of 3.25 or later and a working deny list pass'
  $r = Preflight "$good;$system"
  Check ($r.Code -eq 1 -and $r.Text -match "no python on PATH, so the deny list hook") 'no Python fails, since the deny list could not run'
  $r = Preflight $system
  Check ($r.Code -eq 1 -and $r.Text -match 'cmake is not on PATH' -and $r.Text -match 'fix: ') 'no CMake fails with a fix'
  $r = Preflight $system @('-VsWhere', (Join-Path $work 'no-vswhere.exe'))
  Check ($r.Code -eq 1 -and $r.Text -match "vswhere isn't at") 'a missing vswhere is named'
  $r = Preflight $system @('-VsWhere', $vswhereNoCMake)
  Check ($r.Code -eq 1 -and $r.Text -match 'no Visual Studio here has CMake') 'Visual Studio without CMake is named'
  $r = Preflight "$old;$good;$system"
  Check ($r.Code -eq 1 -and $r.Text -match 'older than the 3\.25') 'an older CMake first on PATH fails'
  $r = Preflight "$broken;$good;$system"
  Check ($r.Code -eq 1 -and $r.Text -match "doesn't run") 'a broken CMake first on PATH fails'
  # Two installations, the newer without CMake: the older one's folder is the fix.
  $vsOld = Join-Path $work 'vs-older'
  $vsOldCMake = Join-Path $vsOld 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
  New-Item -ItemType Directory -Force $vsOldCMake | Out-Null
  Set-Content (Join-Path $vsOldCMake 'cmake.exe') ''
  $vswhereTwo = Join-Path $work 'vswhere-two.cmd'
  Set-Content $vswhereTwo "@echo off`r`necho $vsNone`r`necho $vsOld" -Encoding ascii
  $r = Preflight $system @('-VsWhere', $vswhereTwo)
  Check ($r.Code -eq 1 -and $r.Text.Contains($vsOldCMake)) 'with a newer install lacking CMake, the older one that has it is named'
  $vs = & (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -property installationPath 2>$null
  if ($vs) {
    $r = Preflight $system
    Check ($r.Text -match 'add-cmake-to-path\.ps1 -Apply') 'with Visual Studio CMake installed, the fix is the PATH script'
  }

  # --- The tools, each failing with its fix and passing when good ---
  # A subset that passes says it is one, and a threshold below its floor is a bad argument.
  $d = Tool 'bosun-subset' 'bosun' 'echo available: 100 of 200 MB'
  $r = Preflight "$d;$system" @('-Only', 'bosun')
  Check ($r.Code -eq 0 -and $r.Text -match 'checks passed: bosun \(a subset; run without -Only' -and $r.Text -notmatch 'preflight passed') 'a passing subset is labelled a subset, never "preflight passed"'
  foreach ($bad in @(@('-ProbeSeconds', '0'), @('-MinFreeGB', '-1'), @('-MinLoginDays', '-30'))) {
    $r = Preflight $system (@('-Only', 'bosun') + $bad)
    Check ($r.Code -eq 2 -and $r.Text -match "$($bad[0]) must be at least 1") "$($bad[0]) $($bad[1]) is refused with exit 2"
  }
  $r = Preflight $system @('-Only', 'nope')
  Check ($r.Code -eq 2 -and $r.Text -match 'the checks are bosun, cmake') 'an unknown check name exits 2 and lists the checks'
  $r = Preflight $system @('-Only', 'bosun')
  Check ($r.Code -eq 1 -and $r.Text -match 'bosun is not on the PATH the watchdog launches with' -and $r.Text -match 'fix: install it with `pwsh scripts/install.ps1`') 'no bosun fails with its fix'
  $d = Tool 'bosun-bad' 'bosun' "echo cannot reach the logger`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'bosun')
  Check ($r.Code -eq 1 -and $r.Text -match 'bosun status failed \(exit 1\)') 'a failing bosun status fails'
  $d = Tool 'bosun-good' 'bosun' 'echo available: 100 of 200 MB'
  $r = Preflight "$d;$system" @('-Only', 'bosun')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   bosun') 'a working bosun passes'

  $cases = @(
    @{ Name = 'no SDKs'; Out = 'rem'; Ok = $false },
    @{ Name = 'only older SDKs'; Out = 'echo 10.0.403 [C:\x\sdk]'; Ok = $false },
    @{ Name = 'the release candidate'; Out = "echo 10.0.403 [C:\x\sdk]`r`necho 11.0.100-rc.2.26504.105 [C:\x\sdk]"; Ok = $true },
    @{ Name = 'a final SDK'; Out = 'echo 11.0.100 [C:\x\sdk]'; Ok = $true }
  )
  foreach ($c in $cases) {
    $d = Tool "dotnet-$($cases.IndexOf($c))" 'dotnet' $c.Out
    $r = Preflight "$d;$system" @('-Only', 'dotnet')
    if ($c.Ok) { Check ($r.Code -eq 0 -and $r.Text -match 'ok   \.NET 11 SDK') "dotnet with $($c.Name) passes" }
    else { Check ($r.Code -eq 1 -and $r.Text -match 'no \.NET 11 SDK' -and $r.Text -match 'winget install Microsoft\.DotNet\.SDK') "dotnet with $($c.Name) fails with its fix" }
  }

  $d = Tool 'gh-out' 'gh' "echo You are not logged into any GitHub hosts. To log in, run: gh auth login`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'gh')
  Check ($r.Code -eq 1 -and $r.Text -match 'gh is not signed in' -and $r.Text -match 'fix: `gh auth login`') 'a signed-out gh makes the preflight exit 1 with its fix'
  $d = Tool 'gh-in' 'gh' 'echo Logged in to github.com account someone'
  $r = Preflight "$d;$system" @('-Only', 'gh')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   gh signed in') 'a signed-in gh passes'

  $d = Tool 'codex-out' 'codex' "echo Not logged in`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'codex')
  Check ($r.Code -eq 1 -and $r.Text -match 'codex is not signed in' -and $r.Text -match 'fix: `codex login`') 'a signed-out codex fails with its fix'
  $d = Tool 'codex-mute' 'codex' "if `"%1`"==`"login`" (echo Logged in using ChatGPT& exit /b 0)`r`necho error: model not available`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'codex')
  Check ($r.Code -eq 1 -and $r.Text -match "gpt-6-astra didn't reply READY" -and $r.Text -match 'codex exec -m gpt-6-astra') 'a reviewer that does not reply fails with its fix'
  $d = Tool 'codex-good' 'codex' "if `"%1`"==`"login`" (echo Logged in using ChatGPT& exit /b 0)`r`necho READY"
  $r = Preflight "$d;$system" @('-Only', 'codex')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   codex signed in, and gpt-6-astra replied') 'a signed-in codex whose reviewer replies passes'

  # Claude, as the watchdog's sessions would sign in. The fake reports a token sign-in only when the token reaches it,
  # and an API key sign-in only when one does.
  $claudeKey = "HKCU:\Software\BosunTest\preflight-env-$PID"
  New-Item -Path $claudeKey -Force | Out-Null
  $d = Tool 'claude-fake' 'claude' (@(
      'if not "%1"=="auth" goto reply',
      'if defined ANTHROPIC_API_KEY (echo {"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty"}& exit /b 0)',
      'if defined CLAUDE_CODE_OAUTH_TOKEN (echo {"loggedIn":true,"authMethod":"oauth_token","apiProvider":"firstParty"}& exit /b 0)',
      'echo %FAKE_CLAUDE_STATUS%',
      'echo Warning: a line on stderr 1>&2',
      'exit /b 0',
      ':reply',
      'if "%CLAUDE_CODE_OAUTH_TOKEN%"=="bad" (echo Invalid API key& exit /b 1)',
      'if defined FAKE_CLAUDE_MUTE (echo API Error& exit /b 1)',
      'echo READY') -join "`r`n")
  $tokenRecord = Join-Path $work 'claude-token.json'
  $claude = @('-Only', 'claude', '-UserEnvKey', $claudeKey, '-TokenRecord', $tokenRecord)
  function Status([int]$days) { '{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","loginExpiresAt":"' + [datetime]::UtcNow.AddDays($days).ToString('o') + '"}' }
  $env:FAKE_CLAUDE_STATUS = '{"loggedIn":false}'
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match 'claude is not signed in' -and $r.Text -match 'fix: run `claude`, then /login') 'a signed-out claude fails with its fix'
  $env:FAKE_CLAUDE_STATUS = Status 10
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match 'the Claude sign-in ends on \d{4}-\d{2}-\d{2}, in (9|10) days, sooner than the 30' -and $r.Text -match 'claude setup-token' -and $r.Text -match 'store-claude-token\.ps1') 'a sign-in that ends within 30 days fails, naming setup-token'
  $env:FAKE_CLAUDE_STATUS = '{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty"}'
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match "doesn't say when the sign-in ends") 'a sign-in with no end date fails'
  $env:FAKE_CLAUDE_STATUS = Status 60
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   claude signed in with the subscription sign-in, which ends on') 'a sign-in with 60 days left that replies passes'
  $env:ANTHROPIC_API_KEY = 'a-secret-value'
  $r = Preflight "$d;$system" $claude
  Remove-Item Env:ANTHROPIC_API_KEY
  Check ($r.Code -eq 0 -and $r.Text -notmatch 'a-secret-value') 'an API key in this shell is stripped, as the guard strips it'
  $env:FAKE_CLAUDE_STATUS = '{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"bedrock","loginExpiresAt":"2099-01-01T00:00:00Z"}'
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match 'set to use bedrock') 'a cloud provider fails'
  $env:FAKE_CLAUDE_STATUS = Status 60
  $env:FAKE_CLAUDE_MUTE = '1'
  $r = Preflight "$d;$system" $claude
  Remove-Item Env:FAKE_CLAUDE_MUTE
  Check ($r.Code -eq 1 -and $r.Text -match "didn't reply READY") 'a claude that does not reply fails'
  # The token from the user's registry reaches the probe; a bad one fails on the real reply, never printed.
  $env:FAKE_CLAUDE_STATUS = Status 3
  'tok-ok-123' | & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord | Out-Null
  Check ((Get-Item $claudeKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-ok-123') 'store-claude-token.ps1 keeps a piped token in the given key'
  Check ((Get-Content $tokenRecord -Raw) -notmatch 'tok-ok-123' -and (Get-Content $tokenRecord -Raw | ConvertFrom-Json).issued -eq [datetime]::UtcNow.ToString('yyyy-MM-dd')) 'and records the day it was made, never the token'
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   claude signed in with a long-lived token \(claude setup-token\), which ends on' -and $r.Text -notmatch 'tok-ok-123') 'a stored token passes even with the claude.ai sign-in nearly over, and is never printed'
  # A setup token lasts a year from the day it was made: one with under 30 days left fails, and so does one with no day.
  $old = [datetime]::UtcNow.AddDays(-350).ToString('yyyy-MM-dd')
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -RecordOnly -IssuedOn $old | Out-Null
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match "the stored token, made on $old, ends on" -and $r.Text -match 'claude setup-token') 'a token with under 30 days of its year left fails, naming setup-token'
  Remove-Item $tokenRecord
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match "issue day isn't recorded" -and $r.Text -match '-RecordOnly -IssuedOn') 'a token with no recorded day fails, naming how to record it'
  $future = [datetime]::UtcNow.AddDays(2).ToString('yyyy-MM-dd')
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -RecordOnly -IssuedOn $future | Out-Null
  Check ($LASTEXITCODE -eq 2 -and -not (Test-Path $tokenRecord)) 'a day after today is refused and records nothing'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -RecordOnly -IssuedOn ([datetime]::UtcNow.ToString('yyyy-MM-dd')) | Out-Null
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 0) '-RecordOnly records the day for a token already stored'
  # Removal reports a part it couldn't do: the token goes, and a locked issue record is named, not claimed removed.
  $held = [IO.File]::Open($tokenRecord, 'Open', 'Read', 'None')
  try { $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -Remove; $code = $LASTEXITCODE } finally { $held.Dispose() }
  Check ($code -eq 1 -and ($out -join ' ') -match 'issue day is still at' -and $null -eq (Get-Item $claudeKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN', $null)) 'a removal that leaves the issue record says so'
  'tok-ok-123' | & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord | Out-Null
  $env:CLAUDE_CODE_OAUTH_TOKEN = 'from-this-shell'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -Remove | Out-Null
  $r = Preflight "$d;$system" $claude
  Remove-Item Env:CLAUDE_CODE_OAUTH_TOKEN
  Check ($r.Code -eq 1 -and $r.Text -match 'in [23] days') 'a token only this shell has is not what the watchdog sees, so it does not count'
  'bad' | & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord | Out-Null
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match "didn't reply READY" -and $r.Text -match 'the token may be wrong or revoked') 'a token that does not work fails on the real reply'
  $out = 'one two' | & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord
  Check ($LASTEXITCODE -eq 2 -and (Get-Item $claudeKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'bad') 'a value with a space inside is refused and changes nothing'
  Remove-Item Env:FAKE_CLAUDE_STATUS

  $d = Tool 'wsl-none' 'wsl' "echo There is no distribution with the supplied name.`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  Check ($r.Code -eq 1 -and $r.Text -match "WSL Ubuntu doesn't start" -and $r.Text -match 'wsl --install -d Ubuntu') 'a missing WSL distro fails with its fix'
  $d = Tool 'wsl-nomount' 'wsl' "if `"%4`"==`"true`" exit /b 0`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  # The mount is the checkout's own drive, whichever that is: this test once named /mnt/d and failed on any other.
  $mnt = '/mnt/' + (Resolve-Path $repo).Path.Substring(0, 1).ToLower() + '/'
  Check ($r.Code -eq 1 -and $r.Text -match "can't see this repo at $mnt" -and $r.Text -match 'wsl --shutdown') "a WSL that cannot see $mnt fails with its fix"
  $d = Tool 'wsl-good' 'wsl' 'exit /b 0'
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  Check ($r.Code -eq 0 -and $r.Text -match "ok   WSL Ubuntu sees $mnt") 'a WSL that sees the repo passes'

  # Branch protection on main, read from GitHub's own JSON. Each answer is a stand-in gh that prints that answer.
  # The stand-in also records what it was asked, and the GH_REPO and GH_HOST it saw, so the request is checked too.
  # The check reads the checkout's origin with git, so git's own folder rides with each stand-in on the short PATH.
  $gitsHere = @(Get-Command git -CommandType Application -All | ForEach-Object Source)
  $gitHere = Split-Path (@($gitsHere | Where-Object { (Split-Path (Split-Path $_) -Leaf) -eq 'cmd' }) + $gitsHere | Select-Object -First 1)
  function Protection([string]$name, [string]$answer, [int]$code = 0) {
    $file = Join-Path $work "$name.json"
    [IO.File]::WriteAllText($file, $answer)
    $body = "echo %*^|GH_REPO=%GH_REPO%^|GH_HOST=%GH_HOST%> `"$work\$name.asked`"`r`ntype `"$file`"`r`nexit /b $code"
    $dir = Tool $name 'gh' $body
    "$dir;$gitHere"
  }
  function Shape([object]$admins = $true, [object]$force = $false, [object]$delete = $false) {
    $body = [ordered]@{ url = 'https://api.github.com/repos/o/r/branches/main/protection' }
    if ($null -ne $admins) { $body.enforce_admins = @{ url = 'x'; enabled = $admins } }
    if ($null -ne $force) { $body.allow_force_pushes = @{ enabled = $force } }
    if ($null -ne $delete) { $body.allow_deletions = @{ enabled = $delete } }
    $body | ConvertTo-Json -Depth 4 -Compress
  }
  $r = Preflight "$(Protection 'gh-protected' (Shape));$system" @('-Only', 'protection')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   main of [\w.-]+/[\w.-]+ is protected: force pushes and deletion refused, admins included' -and $r.Text -match 'method DELETE repos/') 'full protection passes, naming the repository and the undo'
  foreach ($case in @(
      @{ Name = 'gh-force'; Answer = (Shape $true $true $false); Says = 'force pushes are allowed' },
      @{ Name = 'gh-delete'; Answer = (Shape $true $false $true); Says = 'deleting main is allowed' },
      @{ Name = 'gh-admins'; Answer = (Shape $false $false $false); Says = 'admins are not held to it' },
      @{ Name = 'gh-no-admins'; Answer = (Shape $null $false $false); Says = 'does not say enforce_admins' },
      @{ Name = 'gh-string'; Answer = '{"enforce_admins":{"enabled":"true"},"allow_force_pushes":{"enabled":false},"allow_deletions":{"enabled":false}}'; Says = 'does not say enforce_admins' },
      @{ Name = 'gh-flat'; Answer = '{"enforce_admins":true,"allow_force_pushes":false,"allow_deletions":false}'; Says = 'does not say allow_deletions, allow_force_pushes, enforce_admins' },
      @{ Name = 'gh-garbage'; Answer = 'not json at all'; Says = 'something other than GitHub''s JSON' },
      @{ Name = 'gh-off'; Answer = "{`"message`":`"Branch not protected`",`"status`":`"404`"}`r`ngh: Branch not protected (HTTP 404)"; Code = 1; Says = 'main on the remote is not protected' },
      @{ Name = 'gh-denied'; Answer = 'gh: Not Found (HTTP 404)'; Code = 1; Says = 'protection can''t be read' })) {
    $code = if ($case.Code) { $case.Code } else { 0 }
    $r = Preflight "$(Protection $case.Name $case.Answer $code);$system" @('-Only', 'protection')
    Check ($r.Code -eq 1 -and $r.Text -match [regex]::Escape($case.Says) -and $r.Text -match 'fix: ') "protection that reads $($case.Name -replace '^gh-', '') fails with its fix"
  }
  # The undo puts main back unprotected, and the check then fails: proved on a stand-in, never on the live repository.
  $undone = Protection 'gh-undone' '{"message":"Branch not protected","status":"404"}' 1
  $r = Preflight "$undone;$system" @('-Only', 'protection')
  Check ($r.Code -eq 1 -and $r.Text -match 'not protected' -and $r.Text -match 'gh api --hostname [\w.-]+ --method PUT repos/') 'after the undo the check fails and names how to turn it back on'
  # The repository read is the one this checkout's origin names, on its host, whatever GH_REPO and GH_HOST say.
  $origin = (git -C $repo remote get-url origin).Trim()
  if ($origin -match '([^/:]+)[/:]([^/]+)/([^/]+?)(?:\.git)?/?$') { $wantHost, $wantPath = $Matches[1], "repos/$($Matches[2])/$($Matches[3])/branches/main/protection" }
  $env:GH_REPO = 'someone-else/protected-repo'
  $env:GH_HOST = 'elsewhere.example.com'
  try {
    $d = Protection 'gh-bound' (Shape)
    $r = Preflight "$d;$system" @('-Only', 'protection')
  } finally { Remove-Item Env:GH_REPO, Env:GH_HOST -ErrorAction Ignore }
  $asked = (Get-Content -LiteralPath (Join-Path $work 'gh-bound.asked') -Raw).Trim()
  Check ($r.Code -eq 0 -and $asked -match ('^api --hostname ' + [regex]::Escape($wantHost) + ' ' + [regex]::Escape($wantPath) + '\s*\|') -and $asked -match '\|GH_REPO=\|' -and $asked -match '\|GH_HOST=$') "the protection read names this checkout's origin and host, and GH_REPO and GH_HOST do not reach gh ($asked)"
  $local = Join-Path $work 'local-origin'
  git init -q -b main $local
  git -C $local remote add origin (Join-Path $work 'somewhere.git')
  $r = Preflight "$(Protection 'gh-local' (Shape));$system" @('-Only', 'protection', '-Repo', $local)
  Check ($r.Code -eq 1 -and $r.Text -match 'is not a GitHub repository' -and -not (Test-Path -LiteralPath (Join-Path $work 'gh-local.asked'))) 'a checkout whose origin is not on GitHub fails with its fix and asks gh nothing'

  $marker = "-n 41 -w 1000 127.0.0.$((Get-Random -Maximum 200) + 2)"
  $d = Tool 'gh-protection-hang' 'gh' "`"%SystemRoot%\System32\PING.EXE`" $marker >nul"
  $r = Preflight "$d;$gitHere;$system" @('-Only', 'protection', '-ProbeSeconds', '2')
  Start-Sleep -Milliseconds 500
  $left = @(Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" })
  $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore }
  Check ($r.Code -eq 1 -and $r.Text -match 'gave no answer within 2 seconds and was stopped' -and $left.Count -eq 0) 'a protection read that hangs is stopped at its limit, with its whole tree'
  $listed = & $pwsh -NoProfile -File (Join-Path $repo 'scripts/campaign-preflight.ps1') -Only nope 2>&1 | Out-String
  Check ($listed -match 'the checks are [^\r\n]*\bprotection\b') 'protection is one of the default checks a full preflight runs'

  # A probe that hangs is stopped at its limit, with its whole tree.
  $marker = "-n 41 -w 1000 127.0.0.$((Get-Random -Maximum 200) + 2)"
  $d = Tool 'gh-hang' 'gh' "`"%SystemRoot%\System32\PING.EXE`" $marker >nul"
  $r = Preflight "$d;$system" @('-Only', 'gh', '-ProbeSeconds', '2')
  Start-Sleep -Milliseconds 500
  $left = @(Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" })
  $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore }
  Check ($r.Code -eq 1 -and $r.Text -match 'gave no answer within 2 seconds and was stopped' -and $r.Text -match 'fix: `gh auth login`') 'a hung probe fails at its limit with its fix'
  Check ($left.Count -eq 0) 'and leaves no process of its tree behind'

  # --- The repository ---
  # Git for Windows' own launcher (Git\cmd\git.exe) when it is on PATH: a shell started from Git Bash puts the inner
  # ucrt64\bin\git.exe first, and that one can't reach a remote from a PATH that holds nothing else of Git's.
  $gits = @(Get-Command git -CommandType Application -All | ForEach-Object Source)
  $gitDir = Split-Path (@($gits | Where-Object { (Split-Path (Split-Path $_) -Leaf) -eq 'cmd' }) + $gits | Select-Object -First 1)
  $tools = "$gitDir;$pythonDir;$system"
  $origin = Join-Path $work 'origin.git'
  $clone = Join-Path $work 'clone'
  $other = Join-Path $work 'other'
  git init -q --bare -b main $origin
  git clone -q $origin $clone 2>&1 | Out-Null
  New-Item -ItemType Directory -Force (Join-Path $clone 'scripts') | Out-Null
  # Stand-ins for the repo's own CI and guard scripts, answering with the exit code the test sets.
  Set-Content (Join-Path $clone 'scripts\ci_status.py') "import os, sys`ncode = int(os.environ.get('BOSUN_TEST_CI', '0'))`nprint('build.yml: faked')`nnote = os.environ.get('BOSUN_TEST_CI_NOTE', 'to get a real run: gh workflow run build.yml --ref main')`nif code == 5: print(note)`nsys.exit(code)" -Encoding utf8
  Set-Content (Join-Path $clone 'scripts\run-guard.py') "import os, sys`nprint('task: faked')`nsys.exit(int(os.environ.get('BOSUN_TEST_GUARD', '0')))" -Encoding utf8
  git -C $clone add -A; git -C $clone -c user.name=t -c user.email=t@t commit -q -m seed; git -C $clone push -q origin main 2>&1 | Out-Null
  $repoArgs = @('-Only', 'tree,pushed,ci,guard', '-Repo', $clone)
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   clean tree' -and $r.Text -match 'is main on the remote' -and $r.Text -match 'ok   CI green' -and $r.Text -match 'ok   watchdog healthy') 'a clean, pushed repo with green CI and a healthy guard passes'
  Set-Content (Join-Path $clone 'dirty.txt') 'x'
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 1 -and $r.Text -match 'has uncommitted changes' -and $r.Text -match 'fix: commit or stash') 'a dirty tree fails with its fix'
  git -C $clone add -A; git -C $clone -c user.name=t -c user.email=t@t commit -q -m local
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 1 -and $r.Text -match "has commits the remote's main" -and $r.Text -match 'git push origin HEAD:main') 'an unpushed commit fails with its fix'
  git -C $clone push -q origin main 2>&1 | Out-Null
  git clone -q $origin $other 2>&1 | Out-Null
  Set-Content (Join-Path $other 'more.txt') 'y'
  git -C $other add -A; git -C $other -c user.name=t -c user.email=t@t commit -q -m more; git -C $other push -q origin main 2>&1 | Out-Null
  $tracking = git -C $clone rev-parse origin/main
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 1 -and $tracking -eq (git -C $clone rev-parse HEAD) -and $r.Text -match "the remote's main moved on" -and $r.Text -match 'git pull --ff-only') 'a remote that moved on fails though the tracking ref still matches HEAD'
  git -C $clone pull -q --ff-only 2>&1 | Out-Null
  git -C $clone remote set-url origin (Join-Path $work 'no-such-remote.git')
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 1 -and $r.Text -match "the remote's main can't be read now") 'an unreachable remote fails with its fix'
  git -C $clone remote set-url origin $origin
  foreach ($c in @(@{ Code = '1'; Says = 'CI is red on HEAD' }, @{ Code = '3'; Says = "CI can't be read" }, @{ Code = '4'; Says = 'CI is still running' }, @{ Code = '5'; Says = 'CI has no run for HEAD' })) {
    $env:BOSUN_TEST_CI = $c.Code
    $r = Preflight $tools @('-Only', 'ci', '-Repo', $clone)
    Check ($r.Code -eq 1 -and $r.Text -match $c.Says -and $r.Text -match 'fix: ') "CI exit $($c.Code) fails with its fix"
  }
  Check ($r.Text -match 'fix: start a run: gh workflow run build\.yml --ref main, then' -and $r.Text -notmatch 'push HEAD') 'a pushed commit with no run is told to start one, not to push'
  # With no command to offer, the fix follows what ci_status.py said: the branch moved on, or its head can't be read.
  $env:BOSUN_TEST_CI_NOTE = 'main has moved on to 0123456789012345678901234567890123456789: gate that commit (--sha 0123456789012345678901234567890123456789)'
  $r = Preflight $tools @('-Only', 'ci', '-Repo', $clone)
  Check ($r.Code -eq 1 -and $r.Text -match 'fix: git pull --ff-only' -and $r.Text -notmatch 'push HEAD' -and $r.Text -notmatch 'gh workflow run') 'a branch that moved on is told to pull, not to push or start a run'
  $env:BOSUN_TEST_CI_NOTE = "no run was started: the remote head of main couldn't be read, so no run was started: gh api exited 1: HTTP 502"
  $r = Preflight $tools @('-Only', 'ci', '-Repo', $clone)
  Check ($r.Code -eq 1 -and $r.Text -match 'fix: `gh auth status`' -and $r.Text -notmatch 'push HEAD') 'an unreadable remote head is told to check gh, not to push'
  Remove-Item Env:BOSUN_TEST_CI, Env:BOSUN_TEST_CI_NOTE
  $env:BOSUN_TEST_GUARD = '1'
  $r = Preflight $tools @('-Only', 'guard', '-Repo', $clone)
  Remove-Item Env:BOSUN_TEST_GUARD
  Check ($r.Code -eq 1 -and $r.Text -match "the watchdog isn't healthy" -and $r.Text -match 'run-guard.py status --verify') 'an unhealthy guard fails with its fix'

  # --- The machine ---
  $drive = [IO.Path]::GetPathRoot($work)
  $r = Preflight $system @('-Only', 'space', '-FreeDrives', $drive, '-MinFreeGB', '1000000000')
  Check ($r.Code -eq 1 -and $r.Text -match 'GB free, under the' -and $r.Text -match 'fix: free space on') 'a free space shortfall fails with its fix'
  $r = Preflight $system @('-Only', 'space', '-FreeDrives', $drive, '-MinFreeGB', '1')
  Check ($r.Code -eq 0) 'enough free space passes'
  # No drive is named in code: with none given, the one checked is the checkout's own. The repo once moved off a
  # drive the check still named, and the preflight failed on a drive nothing used.
  $r = Preflight $system @('-Only', 'space', '-Repo', $clone, '-MinFreeGB', '1')
  $own = [regex]::Escape([IO.Path]::GetPathRoot($clone))
  Check ($r.Code -eq 0 -and @($r.Text -split "`n" | Where-Object { $_ -match '^(ok|FAIL)\s+drive ' }).Count -eq 1 -and $r.Text -match "ok   drive $own has") 'with no drive named, only the checkout''s own drive is checked'

  $powerState = Join-Path $work 'power.json'
  Set-Content $powerState '{"scheme": "381b4222-f694-41f0-9685-ff5bb260df2e", "values": {"SUB_SLEEP/STANDBYIDLE": {"AC": 1800, "DC": 600}, "SUB_SLEEP/HIBERNATEIDLE": {"AC": 0, "DC": 0}, "SUB_VIDEO/VIDEOIDLE": {"AC": 7200, "DC": 180}}}'
  $powercfg = Join-Path $work 'powercfg.cmd'
  Set-Content $powercfg "@`"$((Get-Command python -CommandType Application | Select-Object -First 1).Source)`" `"$(Join-Path $PSScriptRoot 'fakes\powercfg.py')`" %*" -Encoding ascii
  $lockKey = "HKCU:\Software\BosunTest\preflight-$PID\Desktop"
  New-Item -Path $lockKey -Force | Out-Null
  Set-ItemProperty -LiteralPath $lockKey -Name ScreenSaverIsSecure -Value '1'
  $holdRecord = Join-Path $work 'campaign-hold.json'
  $holdArgs = @('-Only', 'hold', '-HoldRecord', $holdRecord, '-PowerCfg', $powercfg, '-LockKey', $lockKey)
  $env:BOSUN_FAKE_POWERCFG = $powerState
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 1 -and $r.Text -match "the hold isn't applied" -and $r.Text -match 'campaign-hold.ps1 -Apply') 'no hold fails with its fix'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-hold.ps1') -Apply -Record $holdRecord -PowerCfg $powercfg -LockKey $lockKey | Out-Null
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   hold applied') 'an applied hold passes'
  & $powercfg /setacvalueindex 381b4222-f694-41f0-9685-ff5bb260df2e SUB_VIDEO VIDEOIDLE 600
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 1 -and $r.Text -match 'the hold drifted: VIDEOIDLE AC is 600') 'a held value that drifted fails with its fix'
  & $powercfg /setacvalueindex 381b4222-f694-41f0-9685-ff5bb260df2e SUB_VIDEO VIDEOIDLE 0
  $fake = Get-Content $powerState -Raw | ConvertFrom-Json
  $fake | Add-Member -NotePropertyName active -NotePropertyValue '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' -Force
  $fake | ConvertTo-Json -Depth 5 | Set-Content $powerState -Encoding utf8
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 1 -and $r.Text -match 'the active power plan is 8c5e7fda' -and $r.Text -match 'powercfg /setactive 381b4222') 'another power plan made active after the hold fails, naming the held one'
  Remove-Item Env:BOSUN_FAKE_POWERCFG

  $policy = "HKCU:\Software\BosunTest\preflight-$PID\AU"
  $store = Join-Path $work 'tasks.json'
  $bin = Join-Path $work 'bin'
  $dotnetDir = Join-Path $work 'dotnet-home'
  New-Item -ItemType Directory -Force $dotnetDir | Out-Null
  $sdkList = Join-Path $dotnetDir 'sdks.txt'
  Set-Content $sdkList '11.0.100-rc.2.26504.105 [x]'
  Set-Content (Join-Path $dotnetDir 'dotnet.cmd') "@type `"$sdkList`"" -Encoding ascii
  $place = @('-PolicyKey', $policy, '-TaskStore', $store, '-BinDir', $bin, '-DotnetDir', $dotnetDir)
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match "admin setup isn't in place" -and $r.Text -match 'NoAutoRebootWithLoggedOnUsers is absent' -and $r.Text -match 'campaign-admin-setup.ps1 -Apply') 'no admin setup fails with its fix'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Apply -Record (Join-Path $work 'admin.json') @place | Out-Null
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   admin setup in place') 'admin setup in place passes'
  $good = [IO.File]::ReadAllText($store)
  $drifts = @(
    @{ Name = 'disabled'; Field = 'Enabled'; Value = $false; Says = 'disabled' },
    @{ Name = 'a changed action'; Field = 'Arguments'; Value = '-File C:\elsewhere.ps1'; Says = 'arguments are' },
    @{ Name = 'another principal'; Field = 'User'; Value = 'SomeoneElse'; Says = 'runs as SomeoneElse' },
    @{ Name = 'a changed trigger'; Field = 'Triggers'; Value = @('Daily'); Says = 'triggers are Daily' }
  )
  foreach ($x in $drifts) {
    $all = $good | ConvertFrom-Json
    $all.'Bosun dotnet final'.($x.Field) = $x.Value
    Set-Content $store ($all | ConvertTo-Json -Depth 5)
    $r = Preflight $system (@('-Only', 'setup') + $place)
    Check ($r.Code -eq 1 -and $r.Text -match "task 'Bosun dotnet final' $([regex]::Escape($x.Says))") "a task with $($x.Name) fails"
  }
  $all = $good | ConvertFrom-Json
  $all.PSObject.Properties.Remove('Bosun dotnet final')
  Set-Content $store ($all | ConvertTo-Json -Depth 5)
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match "task 'Bosun dotnet final' not registered, and .* has no final 11\.0\.1xx SDK") 'an absent SDK task with no final SDK fails'
  Add-Content $sdkList '11.0.100 [x]'
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 0) 'an absent SDK task with the final SDK in its folder passes'

  $inactivity = "HKCU:\Software\BosunTest\preflight-$PID\System"
  New-Item -Path $inactivity -Force | Out-Null
  $r = Preflight $system @('-Only', 'lock', '-InactivityKey', $inactivity)
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   no machine inactivity lock') 'no inactivity policy passes'
  New-ItemProperty -LiteralPath $inactivity -Name InactivityTimeoutSecs -Value 900 -PropertyType DWord | Out-Null
  $r = Preflight $system @('-Only', 'lock', '-InactivityKey', $inactivity)
  Check ($r.Code -eq 1 -and $r.Text -match 'locks this PC after 900 seconds idle' -and $r.Text -match 'fix: ') 'a forced inactivity lock fails with its fix'

  # --- The owner's record ---
  $acceptance = Join-Path $work 'preflight.md'
  $ownerArgs = @('-Only', 'owner', '-Acceptance', $acceptance)
  $r = Preflight $system $ownerArgs
  Check ($r.Code -eq 1 -and $r.Text -match 'no owner record at') 'no owner record fails'
  foreach ($c in @(
      @{ Name = 'extra usage on'; Text = "- Extra usage: on, said by the owner on 2026-11-08.`n- Reboot check: passed on 2026-11-08." },
      @{ Name = 'extra usage unconfirmed'; Text = "- Extra usage: unconfirmed.`n- Reboot check: passed on 2026-11-08." },
      @{ Name = 'no reboot entry'; Text = '- Extra usage: off, confirmed by the owner on 2026-11-08.' })) {
    Set-Content $acceptance $c.Text
    $r = Preflight $system $ownerArgs
    Check ($r.Code -eq 1 -and $r.Text -match "the owner's record doesn't show" -and $r.Text -match 'the owner confirms it') "an owner record with $($c.Name) fails, naming what the owner confirms"
  }
  Set-Content $acceptance "- Extra usage: off, confirmed by the owner on 2026-11-08.`n- Reboot check: passed on 2026-11-08: D: came back."
  $r = Preflight $system $ownerArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   owner record') 'a complete owner record passes'

  # The PATH script, on a variable of its own.
  $variable = "BOSUN_PREFLIGHT_TEST_$PID"
  $record = Join-Path $work 'path-changes.json'
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
  $key.SetValue($variable, 'C:\keep\a;%USERPROFILE%\keep b', [Microsoft.Win32.RegistryValueKind]::ExpandString)
  $read = { $key.GetValue($variable, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
  $script = Join-Path $repo 'scripts\add-cmake-to-path.ps1'
  $cmakeHome = Join-Path $work 'vs-cmake'
  New-Item -ItemType Directory -Force $cmakeHome | Out-Null
  Set-Content (Join-Path $cmakeHome 'cmake.exe') '' # a stand-in: the script only checks that it is there
  $good = $cmakeHome
  $scriptArgs = @('-Folder', $good, '-Variable', $variable, '-Record', $record)
  $preview = & $pwsh -NoProfile -File $script @scriptArgs
  Check ($preview -match 'would add' -and (& $read) -eq 'C:\keep\a;%USERPROFILE%\keep b') 'the preview changes nothing'
  & $pwsh -NoProfile -File $script -Apply @scriptArgs | Out-Null
  Check ((& $read) -eq "C:\keep\a;%USERPROFILE%\keep b;$good") 'apply adds the folder last and keeps every entry as written'
  $again = & $pwsh -NoProfile -File $script -Apply @scriptArgs
  Check ($again -match 'already' -and ((& $read) -split ';' | Where-Object { $_ -eq $good }).Count -eq 1) 'a repeat adds nothing'
  Check ((Get-Content $record -Raw | ConvertFrom-Json).$variable -contains $good) 'what it added is recorded'
  & $pwsh -NoProfile -File $script -Undo @scriptArgs | Out-Null
  # A relative folder is saved as its full path, and one with ';' is refused before anything changes.
  $before = & $read
  Push-Location $work
  try { & $pwsh -NoProfile -File $script -Apply -Folder 'vs-cmake' -Variable $variable -Record $record | Out-Null }
  finally { Pop-Location }
  Check ((& $read) -eq "$before;$cmakeHome") 'a relative folder is added as its full path'
  & $pwsh -NoProfile -File $script -Undo @scriptArgs | Out-Null
  Check ((& $read) -eq $before) 'and undo removes it'
  $semi = Join-Path $work 'a;b'
  New-Item -ItemType Directory -Force $semi | Out-Null
  Set-Content (Join-Path $semi 'cmake.exe') ''
  & $pwsh -NoProfile -File $script -Apply -Folder $semi -Variable $variable -Record $record 2>&1 | Out-Null
  Check ($LASTEXITCODE -ne 0 -and (& $read) -eq $before) "a folder with ';' is refused and PATH is unchanged"
  # A record that can't be saved stops the change before it happens.
  $blocker = Join-Path $work 'not-a-folder'
  Set-Content $blocker 'a file where the record folder would go'
  $before = & $read
  & $pwsh -NoProfile -File $script -Apply -Folder $good -Variable $variable -Record (Join-Path $blocker 'path-changes.json') 2>&1 | Out-Null
  Check ($LASTEXITCODE -ne 0 -and (& $read) -eq $before) 'an unsaveable record stops the change before PATH moves'
  & $pwsh -NoProfile -File $script -Apply @scriptArgs | Out-Null
  & $pwsh -NoProfile -File $script -Undo @scriptArgs | Out-Null
  Check ((& $read) -eq 'C:\keep\a;%USERPROFILE%\keep b') 'undo removes only its own entry'
  Check (-not ((Get-Content $record -Raw | ConvertFrom-Json).PSObject.Properties.Name -contains $variable)) 'undo clears the record'
}
finally {
  if ($key -and $variable) { $key.DeleteValue($variable, $false) }
  Remove-Item Env:BOSUN_FAKE_POWERCFG, Env:BOSUN_TEST_CI, Env:BOSUN_TEST_GUARD -ErrorAction Ignore
  Remove-Item -LiteralPath "HKCU:\Software\BosunTest\preflight-$PID" -Recurse -ErrorAction Ignore
  Remove-Item -LiteralPath "HKCU:\Software\BosunTest\preflight-env-$PID" -Recurse -ErrorAction Ignore
  Remove-Item Env:FAKE_CLAUDE_STATUS, Env:FAKE_CLAUDE_MUTE, Env:CLAUDE_CODE_OAUTH_TOKEN, Env:ANTHROPIC_API_KEY -ErrorAction Ignore
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
