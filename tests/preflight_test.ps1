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
# The preflight with $path as a run's PATH. Without -Only, the CMake and deny list checks these cases cover.
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
  # A cmake that answers and exits at once while a child it started keeps its output open: the probe still ends at
  # -ProbeSeconds, and the child with it (the completion review of D00 T03 §18).
  $python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
  $holdPy = Join-Path $work 'hold-output.py'
  Set-Content $holdPy "import os, sys, time`nopen(sys.argv[1], 'w').write(str(os.getpid()))`ntime.sleep(30)" -Encoding ascii
  $heldPid = Join-Path $work 'held-output.pid'
  $leaves = Tool 'leaves-child' 'cmake' "start /b `"`" `"$python`" `"$holdPy`" `"$heldPid`"`r`necho cmake version 3.31.6"
  $clock = [Diagnostics.Stopwatch]::StartNew()
  $r = Preflight "$leaves;$system" @('-Only', 'cmake', '-ProbeSeconds', '3')
  $took = $clock.Elapsed.TotalSeconds
  $held = if (Test-Path $heldPid) { [int](Get-Content $heldPid) } else { 0 }
  Check ($took -lt 20 -and $held -and -not (Get-Process -Id $held -ErrorAction Ignore)) ("a probe whose child holds its output open ends at its limit, and the child with it ({0:N1} s, pid {1})" -f $took, $held)
  if ($held) { Stop-Process -Id $held -Force -ErrorAction Ignore }

  # A vswhere that hangs, with a child of its own: stopped at -ProbeSeconds, and with the preflight when it is killed.
  $python = (Get-Command python -CommandType Application | Select-Object -First 1).Source
  $hangPy = Join-Path $work 'hang.py'
  Set-Content $hangPy "import os, sys, time`nopen(sys.argv[1], 'w').write(str(os.getpid()))`ntime.sleep(600)" -Encoding ascii
  $hangPid = Join-Path $work 'vswhere.pid'
  $vswhereHang = Join-Path $work 'vswhere-hang.cmd'
  Set-Content $vswhereHang "@`"$python`" `"$hangPy`" `"$hangPid`"" -Encoding ascii
  $r = Preflight $system @('-VsWhere', $vswhereHang, '-ProbeSeconds', '2')
  $hung = if (Test-Path $hangPid) { [int](Get-Content $hangPid) } else { 0 }
  Check ($r.Code -eq 1 -and $r.Text -match 'vswhere at .* gave no answer within 2 seconds and was stopped' -and $hung -and -not (Get-Process -Id $hung -ErrorAction Ignore)) "a hung vswhere fails at -ProbeSeconds and is stopped (pid $hung)"
  Remove-Item -LiteralPath $hangPid -ErrorAction Ignore
  $killed = Start-Process $pwsh -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $work 'killed.txt') -ArgumentList '-NoProfile', '-File', "`"$(Join-Path $repo 'scripts\campaign-preflight.ps1')`"", '-SearchPath', "`"$system`"", '-Only', 'cmake', '-VsWhere', "`"$vswhereHang`"", '-ProbeSeconds', '600'
  $hung = 0
  foreach ($i in 1..150) { if (Test-Path $hangPid) { $hung = [int]("$(Get-Content $hangPid -ErrorAction Ignore)" -replace '\D', ''); if ($hung) { break } }; Start-Sleep -Milliseconds 200 }
  Stop-Process -Id $killed.Id -Force  # as a closed window or a kill ends it: no script of its own runs
  $left = $true
  foreach ($i in 1..50) { if (-not ($hung -and (Get-Process -Id $hung -ErrorAction Ignore))) { $left = $false; break }; Start-Sleep -Milliseconds 200 }
  Check ($hung -and -not $left) "a preflight killed while a probe hangs leaves no probe running (pid $hung)"
  if ($hung) { Stop-Process -Id $hung -Force -ErrorAction Ignore }
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
  Check ($r.Code -eq 2 -and $r.Text -match 'the checks are space, owner, lock') 'an unknown check name exits 2 and lists the checks, the ones that start no process first'
  $r = Preflight $system @('-Only', 'bosun')
  Check ($r.Code -eq 1 -and $r.Text -match 'bosun is not on the PATH a run launches with' -and $r.Text -match 'fix: install it with `pwsh scripts/install.ps1`') 'no bosun fails with its fix'
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
  Check ($r.Code -eq 1 -and $r.Text -match "gpt-6.2-astra didn't reply READY" -and $r.Text -match 'codex exec -m gpt-6.2-astra') 'a reviewer that does not reply fails with its fix'
  $d = Tool 'codex-good' 'codex' "if `"%1`"==`"login`" (echo Logged in using ChatGPT& exit /b 0)`r`necho READY"
  $r = Preflight "$d;$system" @('-Only', 'codex')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   codex signed in, and gpt-6.2-astra replied') 'a signed-in codex whose reviewer replies passes'

  # Claude, as a run's session would sign in. The fake reports a token sign-in only when the token reaches it,
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
  # The day belongs to the token it was recorded for (D00 T03 §23): a record written for another token is no day for
  # this one, whether that day would pass or fail, and one written before fingerprints counts as none.
  . (Join-Path $repo 'scripts\lib\token-record.ps1')
  $stored = (Get-Item $claudeKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN')
  $other = Get-TokenFingerprint 'tok-another-456'
  foreach ($day in [datetime]::UtcNow.ToString('yyyy-MM-dd'), [datetime]::UtcNow.AddDays(-350).ToString('yyyy-MM-dd')) {
    Set-Content -LiteralPath $tokenRecord -Value (@{ issued = $day; token = $other } | ConvertTo-Json -Compress)
    $r = Preflight "$d;$system" $claude
    Check ($r.Code -eq 1 -and $r.Text -match 'for another token' -and $r.Text -match '-RecordOnly -IssuedOn' -and $r.Text -notmatch $stored) "a record for another token, made on $day, is no day for the stored one"
  }
  Set-Content -LiteralPath $tokenRecord -Value (@{ issued = [datetime]::UtcNow.ToString('yyyy-MM-dd') } | ConvertTo-Json -Compress)
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match "isn't recorded for it") 'a record written before fingerprints is no day either'
  # A day after today, though recorded for this token, is refused with the same remedy.
  Set-Content -LiteralPath $tokenRecord -Value (@{ issued = [datetime]::UtcNow.AddDays(5).ToString('yyyy-MM-dd'); token = (Get-TokenFingerprint $stored) } | ConvertTo-Json -Compress)
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 1 -and $r.Text -match 'is after today' -and $r.Text -match '-RecordOnly -IssuedOn') 'a recorded day after today is refused, naming how to correct it'
  Set-Content -LiteralPath $tokenRecord -Value (@{ issued = [datetime]::UtcNow.ToString('yyyy-MM-dd'); token = (Get-TokenFingerprint $stored) } | ConvertTo-Json -Compress)
  $r = Preflight "$d;$system" $claude
  Check ($r.Code -eq 0) 'and the same day recorded for the stored token passes'
  # Removal reports a part it couldn't do: the token goes, and a locked issue record is named, not claimed removed.
  $held = [IO.File]::Open($tokenRecord, 'Open', 'Read', 'None')
  try { $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -Remove; $code = $LASTEXITCODE } finally { $held.Dispose() }
  Check ($code -eq 1 -and ($out -join ' ') -match 'issue day is still at' -and $null -eq (Get-Item $claudeKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN', $null)) 'a removal that leaves the issue record says so'
  'tok-ok-123' | & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord | Out-Null
  $env:CLAUDE_CODE_OAUTH_TOKEN = 'from-this-shell'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\store-claude-token.ps1') -Key $claudeKey -Record $tokenRecord -Remove | Out-Null
  $r = Preflight "$d;$system" $claude
  Remove-Item Env:CLAUDE_CODE_OAUTH_TOKEN
  Check ($r.Code -eq 1 -and $r.Text -match 'in [23] days') 'a token only this shell has is not what a new window gets, so it does not count'
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
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   main of [\w.-]+/[\w.-]+ is protected: force pushes and deletion refused, admins included' -and $r.Text -notmatch 'method DELETE' -and @($r.Text -split "`n" | Where-Object { $_ -like 'ok   main of*' }).Count -eq 1) 'full protection passes in one short line, naming the repository, the undo left to the help (D00 T03 §25)'
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

  # D00 T03 §25. The output read as a stream, each line stamped as it arrives while the preflight runs, so what is on
  # screen when is measured, not inferred from the final text.
  function Stream([string]$path, [string[]]$extra, [int]$deadline = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($a in @('-NoProfile', '-File', (Join-Path $repo 'scripts\campaign-preflight.ps1'), '-SearchPath', $path) + $extra) { $start.ArgumentList.Add($a) }
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $seen = [Collections.Generic.List[object]]::new()
    try {
      $null = $process.StandardError.ReadToEndAsync()
      # A deadline of its own: a stalled preflight is a failed test, never a test that waits for ever.
      $pending = $process.StandardOutput.ReadLineAsync()
      while ($clock.Elapsed.TotalSeconds -lt $deadline) {
        if (-not $pending.Wait(250)) { continue }
        if ($null -eq $pending.Result) { break }
        $seen.Add([pscustomobject]@{ At = $clock.Elapsed.TotalSeconds; Line = $pending.Result })
        $pending = $process.StandardOutput.ReadLineAsync()
      }
      $done = $process.WaitForExit(5000)
      [pscustomobject]@{ Code = $(if ($done) { $process.ExitCode } else { -1 }); Lines = $seen }
    } finally { if (-not $process.HasExited) { & taskkill /T /F /PID $process.Id 2>&1 | Out-Null } }
  }
  function When($run, [string]$pattern) { $hit = $run.Lines | Where-Object { $_.Line -match $pattern } | Select-Object -First 1; if ($hit) { $hit.At } else { 999 } }
  $slowGit = Tool 'git-slow' 'git' "`"%SystemRoot%\System32\PING.EXE`" $marker >nul"
  $slowGh = Tool 'gh-slow' 'gh' "`"%SystemRoot%\System32\PING.EXE`" $marker >nul"
  $noOwner = Join-Path $work 'no-owner.md'
  try {
    $run = Stream "$slowGit;$slowGh;$system" @('-Only', 'protection,tree,owner', '-ProbeSeconds', '3', '-Acceptance', $noOwner)
  } finally { Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore } }
  $ownerAt = When $run '^FAIL no owner record'
  Check ($ownerAt -lt 5 -and $ownerAt -lt (When $run '^checking tree\.\.\.') -and (When $run '^checking tree') -lt (When $run '^checking protection')) ("in -Only protection,tree,owner the owner failure is on screen at {0:N1} s, before the slow local and remote probes start" -f $ownerAt)
  # The completion review: the same target on the default invocation, every check, not only a chosen few.
  $full = Stream $system @('-Acceptance', $noOwner, '-ProbeSeconds', '5') 300
  $fullOwnerAt = When $full '^FAIL no owner record'
  Check ($fullOwnerAt -lt 5 -and (When $full '^checking space') -lt $fullOwnerAt -and $fullOwnerAt -lt (When $full '^checking tree')) ("on a full default run the owner failure is on screen at {0:N1} s, before any probe starts" -f $fullOwnerAt)
  # An owner record that exists but is held open by another process is said so, not taken for a missing confirmation.
  $lockedOwner = Join-Path $work 'owner-locked.md'
  [IO.File]::WriteAllText($lockedOwner, "- Extra usage: off 2026-11-01`n- Reboot check: passed 2026-11-01`n")
  $holder = [IO.File]::Open($lockedOwner, 'Open', 'Read', 'None')
  try { $r = Preflight $system @('-Only', 'owner', '-Acceptance', $lockedOwner) } finally { $holder.Dispose() }
  Check ($r.Code -eq 1 -and $r.Text -match "the owner record at .*owner-locked\.md can't be read" -and $r.Text -match 'close whatever holds' -and $r.Text -notmatch 'the owner confirms it') 'a locked owner record is said to be unreadable, with what to do'
  $listedOrder = (& $pwsh -NoProfile -File (Join-Path $repo 'scripts/campaign-preflight.ps1') -Only nope 2>&1 | Out-String)
  Check ($listedOrder -match 'the checks are space, owner, lock, tree') 'the default order starts with the checks that start no process'
  Check ((When $run '^checking owner\.\.\.') -lt $ownerAt) 'a check is named before its own result'
  # An instant check after a slow one: a clock for the whole run would give it the slow one's time too.
  try { $timed = Stream "$slowGit;$system" @('-Only', 'tree,hold', '-ProbeSeconds', '3', '-HoldRecord', (Join-Path $work 'no-hold.json')) }
  finally { Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore } }
  $secs = { param($pattern) $hit = $timed.Lines | Where-Object { $_.Line -match $pattern } | Select-Object -First 1; if ($hit -and $hit.Line -match '\((\d+\.\d) s\)$') { [double]$Matches[1] } else { -1 } }
  $treeSecs = & $secs '^FAIL .*(git|tree|status)'; $holdSecs = & $secs '^ok   no run holds the machine now'
  Check ($treeSecs -ge 2 -and $holdSecs -ge 0 -and $holdSecs -lt 1) "each result carries its own check's duration (tree $treeSecs s, then hold $holdSecs s)"
  # The protection read says the host, the repository and the limit before it ends.
  $origin = (git -C $repo remote get-url origin).Trim()
  $null = $origin -match '([^/:]+)[/:]([^/]+)/([^/]+?)(?:\.git)?/?$'
  $askPattern = '^\s+asking ' + [regex]::Escape($Matches[1]) + ' for the branch protection of ' + [regex]::Escape("$($Matches[2])/$($Matches[3])") + '''s main, up to 3 s$'
  try { $asking = Stream "$slowGh;$gitHere;$system" @('-Only', 'protection', '-ProbeSeconds', '3') }
  finally { Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore } }
  $askAt = When $asking $askPattern; $endAt = When $asking 'gave no answer within 3 seconds'
  Check ($askAt -lt 999 -and $endAt -lt 999 -and $askAt -lt $endAt - 2) ("the protection read names its host, repository and limit {0:N1} s before it ends" -f ($endAt - $askAt))

  # Each protection state's fix, run as printed against a recording gh: the body it sends, the read-back's target.
  $wantHost, $wantPath = $Matches[1], "repos/$($Matches[2])/$($Matches[3])/branches/main/protection"
  $recorder = Join-Path $work 'gh-recorder'
  New-Item -ItemType Directory -Force $recorder | Out-Null
  # Stdin is read only for a call that sends a body (--input -): a read would otherwise wait on an open console.
  Set-Content (Join-Path $recorder 'gh.cmd') "@echo off`r`necho %*>>`"$recorder\args.txt`"`r`necho %* | findstr /c:`"--input -`" >nul && findstr `"^`" >>`"$recorder\stdin.txt`"`r`necho {}" -Encoding ascii
  function Run-Printed([string]$command) {
    Remove-Item -LiteralPath "$recorder\args.txt", "$recorder\stdin.txt" -ErrorAction Ignore
    $env:Path = "$recorder;$env:Path"
    try { & $pwsh -NoProfile -Command $command 2>&1 | Out-Null } finally { $env:Path = $env:Path.Substring($recorder.Length + 1) }
    [pscustomobject]@{ Args = @(Get-Content -LiteralPath "$recorder\args.txt" -ErrorAction Ignore); Body = (Get-Content -LiteralPath "$recorder\stdin.txt" -Raw -ErrorAction Ignore) }
  }
  # A fix as printed: its sentence, then each command on a line of its own, copied as it stands (round 2).
  function Fix-Of($r) {
    $ls = @($r.Text -split "`n"); $at = -1
    for ($k = 0; $k -lt $ls.Count; $k++) { if ($ls[$k] -match '^\s+fix: ') { $at = $k; break } }
    if ($at -lt 0) { return [pscustomobject]@{ Head = ''; Commands = @(); Text = '' } }
    $cmds = @(); for ($k = $at + 1; $k -lt $ls.Count -and $ls[$k] -match '^ {10}\S'; $k++) { $cmds += $ls[$k].Trim() }
    $head = $ls[$at] -replace '^\s+fix: ', ''
    [pscustomobject]@{ Head = $head; Commands = $cmds; Text = (@($head) + $cmds) -join "`n" }
  }
  $r = Preflight "$(Protection 'gh-absent' '{"message":"Branch not protected","status":"404"}' 1);$system" @('-Only', 'protection')
  $fix = Fix-Of $r
  $put = Run-Printed $fix.Commands[0]; $back = Run-Printed $fix.Commands[1]
  Check ($fix.Head -eq 'turn it on, then read it back, one command per line (the header help says how to undo it):' -and $fix.Commands.Count -eq 2) "an absent protection's fix is a sentence and two command lines ($($fix.Commands.Count))"
  $sent = try { $put.Body | ConvertFrom-Json -ErrorAction Stop } catch { $null }
  Check ($put.Args[0] -eq "api --hostname $wantHost --method PUT $wantPath --input -" -and $sent.enforce_admins -eq $true -and $sent.allow_force_pushes -eq $false -and $sent.allow_deletions -eq $false) "an absent protection's PUT, run as printed, sends the whole body to this origin's main ($($put.Args[0]))"
  Check ($back.Args[0] -like "api --hostname $wantHost $wantPath --jq *") 'its read-back, run as printed, names the same host, repository and branch'
  $asked = (Get-Content -LiteralPath (Join-Path $work 'gh-absent.asked') -Raw).Trim()
  Check ($asked -notmatch 'PUT|DELETE|POST') 'the preflight itself only reads the protection'
  # The completion review: a 404 is absence only when GitHub says so with that status; the same words on another
  # status get a diagnosis and no write.
  $r = Preflight "$(Protection 'gh-contradicts' 'gh: Branch not protected (HTTP 403)' 1);$system" @('-Only', 'protection')
  Check ($r.Code -eq 1 -and $r.Text -notmatch 'method PUT' -and (Fix-Of $r).Commands -contains "gh auth status --hostname $wantHost") "'Branch not protected' on a 403 is no confirmed absence: a diagnosis, no write"
  # Only admins exempt: the enforce_admins endpoint, which changes nothing else (round 1).
  $adminsOnly = [ordered]@{ url = 'x'; required_status_checks = @{ strict = $true; contexts = @('build'); checks = @(@{ context = 'build'; app_id = 42 }) }; enforce_admins = @{ enabled = $false }; allow_force_pushes = @{ enabled = $false }; allow_deletions = @{ enabled = $false } } | ConvertTo-Json -Depth 6 -Compress
  $r = Preflight "$(Protection 'gh-admins-only' $adminsOnly);$system" @('-Only', 'protection')
  $fix = Fix-Of $r
  $post = Run-Printed $fix.Commands[0]; $back = Run-Printed $fix.Commands[1]
  Check ($post.Args[0] -eq "api --hostname $wantHost --method POST $wantPath/enforce_admins" -and -not $post.Body -and $back.Args[0] -like "api --hostname $wantHost $wantPath/enforce_admins *") "admins alone exempt are held by the enforce_admins endpoint, with no body ($($post.Args[0]))"
  # Force pushes or deletion allowed: no endpoint changes only those, and a whole-rule PUT would restate the rest (an app
  # pin, an unpinned check, rules the preflight could not read), so the mend is by hand, whatever else the rule holds.
  foreach ($case in @(
      @{ Name = 'gh-forced'; Rule = [ordered]@{ url = 'x'; required_status_checks = @{ strict = $true; checks = @(@{ context = 'build'; app_id = 42 }, @{ context = 'lint'; app_id = $null }) }; enforce_admins = @{ enabled = $true }; allow_force_pushes = @{ enabled = $true }; allow_deletions = @{ enabled = $false } } },
      @{ Name = 'gh-deletable-unreadable'; Rule = [ordered]@{ url = 'x'; required_status_checks = 'not an object'; enforce_admins = @{ enabled = $false }; allow_force_pushes = @{ enabled = $false }; allow_deletions = @{ enabled = $true } } },
      @{ Name = 'gh-actors'; Rule = [ordered]@{ url = 'x'; required_pull_request_reviews = @{ dismissal_restrictions = @{ teams = @(@{ slug = 'release' }) } }; enforce_admins = @{ enabled = $true }; allow_force_pushes = @{ enabled = $true }; allow_deletions = @{ enabled = $false } } },
      @{ Name = 'gh-restricted'; Rule = [ordered]@{ url = 'x'; restrictions = @{ users = @(@{ login = 'someone' }) }; enforce_admins = @{ enabled = $false }; allow_force_pushes = @{ enabled = $true }; allow_deletions = @{ enabled = $false } } })) {
    $r = Preflight "$(Protection $case.Name ($case.Rule | ConvertTo-Json -Depth 6 -Compress));$system" @('-Only', 'protection')
    $fix = Fix-Of $r
    Check ($r.Text -match 'partial: ' -and $fix.Head -match "^on https://$([regex]::Escape($wantHost))/\S+/settings/branches, edit main's rule .*leaving its other rules as they are" -and $fix.Commands.Count -eq 0 -and $r.Text -notmatch 'method PUT') "a partial protection allowing force pushes or deletion ($($case.Name -replace '^gh-', '')) is mended by hand, never by a PUT"
  }
  foreach ($case in @(@{ Name = 'gh-refused'; Answer = 'gh: Not Found (HTTP 404)'; Code = 1 }, @{ Name = 'gh-garbled'; Answer = 'not json'; Code = 0 })) {
    $r = Preflight "$(Protection $case.Name $case.Answer $case.Code);$system" @('-Only', 'protection')
    Check ($r.Code -eq 1 -and (Fix-Of $r).Commands -contains "gh auth status --hostname $wantHost" -and $r.Text -notmatch 'method PUT') "protection that cannot be read ($($case.Name -replace '^gh-', '')) gets a diagnosis and no write"
  }
  $r = Preflight "$slowGh;$gitHere;$system" @('-Only', 'protection', '-ProbeSeconds', '2')
  Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -like "*$marker*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Ignore }
  Check ($r.Text -match 'gave no answer' -and $r.Text -notmatch 'method PUT') 'a protection read that times out gets a diagnosis and no write'
  # An origin whose host or repository GitHub would not spell so gets no command at all, and gh is asked nothing.
  $odd = Join-Path $work 'odd-origin'
  if (-not (Test-Path $odd)) { git init -q -b main $odd; git -C $odd remote add origin 'https://github.com/owner/re$(po).git' }
  $r = Preflight "$(Protection 'gh-odd' (Shape));$system" @('-Only', 'protection', '-Repo', $odd)
  Check ($r.Code -eq 1 -and $r.Text -match 'does not name a GitHub host and repository as GitHub spells them' -and -not (Test-Path -LiteralPath (Join-Path $work 'gh-odd.asked'))) 'a malformed origin gets no command, and gh is asked nothing'
  $help = Get-Content (Join-Path $repo 'scripts/campaign-preflight.ps1') -TotalCount 12 | Out-String
  Check ($help -match 'created on a branch that had none is undone with: gh api --hostname <host> --method DELETE repos/<owner>/<repo>/branches/main/protection') 'the header help says how to undo protection the fix created, and only that'

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
  Set-Content (Join-Path $clone 'scripts\run-guard.py') "import json, os, sys`nhow = os.environ.get('BOSUN_TEST_GUARD', 'ok')`nif how == 'crash': sys.exit(1)`nprint(json.dumps({'state': 'unreadable' if how == 'unreadable' else 'inactive', 'state_error': 'JSONDecodeError', 'hook_failures': [{'at': 'now', 'error': 'OSError(disk full)'}] if how == 'hook' else []}))" -Encoding utf8
  git -C $clone add -A; git -C $clone -c user.name=t -c user.email=t@t commit -q -m seed; git -C $clone push -q origin main 2>&1 | Out-Null
  $repoArgs = @('-Only', 'tree,pushed,ci,guard', '-Repo', $clone)
  $r = Preflight $tools $repoArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   clean tree' -and $r.Text -match 'is main on the remote' -and $r.Text -match 'ok   CI green' -and $r.Text -match 'ok   run guard healthy') 'a clean, pushed repo with green CI and a healthy guard passes'
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
  # Round 2 of D00 T03 §27: a start the gate already asked for may be running, so the fix is to wait, never a second start.
  $env:BOSUN_TEST_CI_NOTE = "start not answered: build.yml (the start of build.yml got no clear answer (timed out), so it may be running and won't be started again for this commit; if no run appears: gh workflow run build.yml --ref main)"
  $r = Preflight $tools @('-Only', 'ci', '-Repo', $clone)
  Check ($r.Code -eq 1 -and $r.Text -match 'fix: wait for the run the gate already asked for \(`gh run list --commit [0-9a-f]{40}`\)' -and $r.Text -notmatch 'fix: start a run') 'an unanswered start is waited for, with a lookup naming the commit, never started a second time'
  Remove-Item Env:BOSUN_TEST_CI, Env:BOSUN_TEST_CI_NOTE
  foreach ($c in @(@{ How = 'crash'; Says = 'gave no status' }, @{ How = 'unreadable'; Says = "can't read its state: JSONDecodeError" }, @{ How = 'hook'; Says = 'hook failed open.*disk full' })) {
    $env:BOSUN_TEST_GUARD = $c.How
    $r = Preflight $tools @('-Only', 'guard', '-Repo', $clone)
    Check ($r.Code -eq 1 -and $r.Text -match $c.Says -and $r.Text -match 'fix: run `python scripts/run-guard.py status`' -and $r.Text -notmatch 'watchdog|install') "a guard that reads '$($c.How)' fails with its fix, and names no watchdog"
  }
  Remove-Item Env:BOSUN_TEST_GUARD

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
  # D00 T03 §28: the hold is the run's own power request, said by its holder; a record an older version left still
  # holds the power plan and is put back by hand; the lock the request cannot stop is the owner's screen saver setting.
  $holdRecord = Join-Path $work 'campaign-hold.json'
  $runHold = Join-Path $work 'run-hold.json'
  $holdArgs = @('-Only', 'hold', '-HoldRecord', $holdRecord, '-RunHold', $runHold)
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 0 -and $r.Text -match "ok   no run holds the machine now; the run's launcher holds it awake while its writer runs") 'with no run, the hold check says what the launcher will do'
  $holder = [Diagnostics.ProcessStartInfo]::new($pwsh)
  foreach ($a in '-NoProfile', '-NonInteractive', '-File', (Join-Path $repo 'scripts\lib\hold-awake.ps1'), '-ParentPid', "$PID", '-Record', $runHold) { $holder.ArgumentList.Add($a) }
  $holder.UseShellExecute = $false; $holder.CreateNoWindow = $true; $holder.RedirectStandardInput = $true; $holder.RedirectStandardOutput = $true
  $held = [Diagnostics.Process]::Start($holder)
  try {
    $null = $held.StandardOutput.ReadLine()
    $r = Preflight $system $holdArgs
    Check ($r.Code -eq 0 -and $r.Text -match "ok   the machine is held awake by run $($held.Id)") 'a live run''s holder that holds passes, named'
  } finally { $held.StandardInput.Close(); $null = $held.WaitForExit(3000); if (-not $held.HasExited) { & taskkill /T /F /PID $held.Id 2>&1 | Out-Null } }
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   no run holds the machine now') 'a released hold (a timed one ended, or its writer returned) passes as no run holding'
  # A failed holder lives until its writer returns: failing while it does, passing once it is released (round 1 of
  # D00 T03 §28), even with the launcher's window still open.
  $env:BOSUN_TEST_HOLD_FAIL = '1'
  try { $held = [Diagnostics.Process]::Start($holder) } finally { Remove-Item Env:BOSUN_TEST_HOLD_FAIL }
  try {
    $null = $held.StandardOutput.ReadLine()
    $r = Preflight $system $holdArgs
    Check ($r.Code -eq 1 -and $r.Text -match "the run's hold failed: SetThreadExecutionState refused the request") 'a live run whose holder failed fails with its reason'
  } finally { $held.StandardInput.Close(); $null = $held.WaitForExit(3000); if (-not $held.HasExited) { & taskkill /T /F /PID $held.Id 2>&1 | Out-Null } }
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   no run holds the machine now') 'a failed hold whose writer returned passes, its shell still open'
  # A pid Windows gave to another process (here this test's own, whose start does not match) is not the holder.
  [IO.File]::WriteAllText($runHold, (@{ pid = $PID; started = '2020-01-01T00:00:00.0000000Z'; parent = $PID; acquired = $true } | ConvertTo-Json))
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   no run holds the machine now') 'a record whose pid now names another process reads as no run holding'
  # A run's record that cannot be read fails with what to move aside, never as no run.
  foreach ($bad in '{ not json', '"just text"', '{"pid": 123}', "{`"pid`": $PID, `"started`": `"soon`", `"acquired`": true}") {
    [IO.File]::WriteAllText($runHold, $bad)
    $r = Preflight $system $holdArgs
    Check ($r.Code -eq 1 -and $r.Text -match "the run's hold record at .* can't be read" -and $r.Text -match 'Move-Item') "an unreadable run hold record ($bad) fails with what to move aside"
  }
  Remove-Item -LiteralPath $runHold
  # A record an older version left: put back by hand; one that cannot be read: moved aside.
  . (Join-Path $repo 'scripts\lib\campaign.ps1')
  $legacy = [ordered]@{ Scheme = '381b4222-f694-41f0-9685-ff5bb260df2e'; Power = @($HoldPower | ForEach-Object { [ordered]@{ Sub = $_.Sub; Setting = $_.Setting; AC = 600; DC = 300 } }); Lock = '1' }
  [IO.File]::WriteAllText($holdRecord, ($legacy | ConvertTo-Json -Depth 5))
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 1 -and $r.Text -match 'a hold record an older version left is at' -and $r.Text.Contains("pwsh scripts/campaign-hold.ps1 -Restore -Record '$holdRecord'")) 'a record an older version left fails with the manual restore of that record'
  # The restore keeps the targets the check was given, each quoted as pasted.
  $odd = Join-Path $work "it's-hold.json"
  [IO.File]::Copy($holdRecord, $odd, $true)
  $r = Preflight $system @('-Only', 'hold', '-HoldRecord', $odd, '-RunHold', $runHold, '-PowerCfg', 'C:\fake\powercfg.exe', '-LockKey', 'HKCU:\Software\BosunTest\lock')
  Check ($r.Text.Contains("-Record '$($odd.Replace("'", "''"))' -PowerCfg 'C:\fake\powercfg.exe' -LockKey 'HKCU:\Software\BosunTest\lock'")) 'the restore command keeps the record, power tool and lock key it was given, quoted'
  [IO.File]::WriteAllText($odd, '{ not json')
  $r = Preflight $system @('-Only', 'hold', '-HoldRecord', $odd, '-RunHold', $runHold)
  Check ($r.Text.Contains("Move-Item -LiteralPath '$($odd.Replace("'", "''"))' -Destination")) 'a path holding a quote is quoted in the move-aside command'
  Remove-Item -LiteralPath $odd
  [IO.File]::WriteAllText($holdRecord, '{ not json')
  $r = Preflight $system $holdArgs
  Check ($r.Code -eq 1 -and $r.Text -match "a hold record an older version left can't be read" -and $r.Text -match 'Move-Item -LiteralPath') 'an unreadable record fails with what to move aside'
  Remove-Item -LiteralPath $holdRecord
  # The screen saver that locks on resume: the owner's setting, refused; an inactive or a non-secure one passes.
  Set-ItemProperty -LiteralPath $lockKey -Name ScreenSaveTimeOut -Value '600'
  foreach ($case in @(@{ Active = '1'; Secure = '1'; Fails = $true }, @{ Active = '0'; Secure = '1'; Fails = $false }, @{ Active = '1'; Secure = '0'; Fails = $false })) {
    Set-ItemProperty -LiteralPath $lockKey -Name ScreenSaveActive -Value $case.Active
    Set-ItemProperty -LiteralPath $lockKey -Name ScreenSaverIsSecure -Value $case.Secure
    $r = Preflight $system @('-Only', 'lock', '-LockKey', $lockKey, '-InactivityKey', $lockKey, '-LockPolicyKey', "$lockKey\Policy")
    if ($case.Fails) { Check ($r.Code -eq 1 -and $r.Text -match 'the screen saver locks the desktop on resume' -and $r.Text -match "On resume, display logon screen") 'a screen saver that locks on resume fails with the owner''s fix' }
    else { Check ($r.Code -eq 0 -and $r.Text -match 'no screen saver that locks on resume') "a screen saver active $($case.Active), secure $($case.Secure) passes" }
  }  # A managed account's policy wins over the desktop's own values (round 1 of D00 T03 §28); the test key's teardown
  # removes it.
  Set-ItemProperty -LiteralPath $lockKey -Name ScreenSaveActive -Value '0'
  New-Item -Path "$lockKey\Policy" -Force | Out-Null
  Set-ItemProperty -LiteralPath "$lockKey\Policy" -Name ScreenSaveActive -Value '1'
  Set-ItemProperty -LiteralPath "$lockKey\Policy" -Name ScreenSaverIsSecure -Value '1'
  $r = Preflight $system @('-Only', 'lock', '-LockKey', $lockKey, '-InactivityKey', $lockKey, '-LockPolicyKey', "$lockKey\Policy")
  Check ($r.Code -eq 1 -and $r.Text -match 'the screen saver locks the desktop on resume' -and $r.Text -match 'a policy sets ScreenSaveActive, ScreenSaverIsSecure' -and $r.Text -match 'Group Policy') 'a screen saver lock enforced by policy fails, naming the policy and who can change it'
  # A zero timeout never starts the saver, so it never locks.
  Set-ItemProperty -LiteralPath "$lockKey\Policy" -Name ScreenSaveTimeOut -Value '0'
  $r = Preflight $system @('-Only', 'lock', '-LockKey', $lockKey, '-InactivityKey', $lockKey, '-LockPolicyKey', "$lockKey\Policy")
  Check ($r.Code -eq 0 -and $r.Text -match 'no screen saver that locks on resume') 'a secure screen saver with a zero timeout passes, since it never starts'
  # Nor does one with no timeout set anywhere (Microsoft KB 2616727; round 5 of D00 T03 §28).
  $fresh = "$lockKey\Fresh"
  New-Item -Path $fresh -Force | Out-Null
  Set-ItemProperty -LiteralPath $fresh -Name ScreenSaveActive -Value '1'
  Set-ItemProperty -LiteralPath $fresh -Name ScreenSaverIsSecure -Value '1'
  $r = Preflight $system @('-Only', 'lock', '-LockKey', $fresh, '-InactivityKey', $lockKey, '-LockPolicyKey', "$fresh\NoPolicy")
  Check ($r.Code -eq 0 -and $r.Text -match 'no screen saver that locks on resume') 'a secure screen saver with no timeout set passes, since it never starts'
  Set-ItemProperty -LiteralPath "$lockKey\Policy" -Name ScreenSaveActive -Value '0'


  $policy = "HKCU:\Software\BosunTest\preflight-$PID\AU"
  $store = Join-Path $work 'tasks.json'
  $bin = Join-Path $work 'bin'
  $dotnetDir = Join-Path $work 'dotnet-home'
  New-Item -ItemType Directory -Force $dotnetDir | Out-Null
  $sdkList = Join-Path $dotnetDir 'sdks.txt'
  Set-Content $sdkList '11.0.100-rc.2.26504.105 [x]'
  Set-Content (Join-Path $dotnetDir 'dotnet.cmd') "@type `"$sdkList`"" -Encoding ascii
  $adminRecord = Join-Path $work 'admin.json'
  $pending = "HKCU:\Software\BosunTest\preflight-$PID\RebootRequired"
  $place = @('-PolicyKey', $policy, '-TaskStore', $store, '-BinDir', $bin, '-DotnetDir', $dotnetDir, '-AdminRecord', $adminRecord, '-PendingRestartKey', $pending)
  $setupPlace = @('-PolicyKey', $policy, '-TaskStore', $store, '-BinDir', $bin, '-DotnetDir', $dotnetDir, '-Record', $adminRecord)
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match "admin setup isn't in place" -and $r.Text -match 'NoAutoRebootWithLoggedOnUsers is absent' -and $r.Text -match "(?m)^\s+pwsh '[^']*campaign-admin-setup\.ps1' -Apply$") 'no admin setup fails with its fix, the command on a line of its own, its path quoted'
  # D00 T03 §25: a failing run ends with one ordered list, this account's fixes, then an elevated shell's, then what only
  # the owner confirms, each fix once however many failures share it, and the command that reruns the same checks with
  # this run's options, which finds the same failure when run as printed.
  $spaced = Join-Path $work 'a checkout with spaces'
  if (-not (Test-Path $spaced)) { git init -q -b main $spaced }
  $drive = [IO.Path]::GetPathRoot($spaced)
  $fixed = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object Name)
  $second = if ($fixed.Count -ge 2) { @($fixed | Where-Object { $_ -ne $drive })[0] } else { $drive }
  $r = Preflight $system (@('-Only', 'owner,setup,space', '-Repo', $spaced, '-MinFreeGB', '1000000000', '-FreeDrives', "$drive,$drive,$second", '-Acceptance', (Join-Path $work 'no-owner.md')) + $place)
  $list = $r.Text.Substring([Math]::Max(0, $r.Text.IndexOf('check(s) failed')))
  $groups = [ordered]@{}; $current = $null
  foreach ($l in $list -split "`n") {
    if ($l -match '^  (with this account|in an elevated shell|only the owner can confirm):$') { $current = $Matches[1]; $groups[$current] = @() }
    elseif ($current -and $l -match '^    \d+\. (.+)$') { $groups[$current] += $Matches[1] }
    elseif ($current -and $l -match '^ {9}(\S.*)$' -and @($groups[$current]).Count) { $last = @($groups[$current]).Count - 1; $all = @($groups[$current]); $all[$last] += " | $($Matches[1])"; $groups[$current] = $all }
  }
  Check ($r.Code -eq 1 -and @($groups.Keys) -join '|' -eq 'with this account|in an elevated shell|only the owner can confirm' -and (@($groups['with this account']) -join '|') -eq (@("free space on $drive") + @(if ($second -ne $drive) { "free space on $second" }) -join '|') -and @($groups['in an elevated shell']).Count -eq 1 -and $groups['in an elevated shell'][0] -match "^in an elevated shell: \| pwsh '[^']*campaign-admin-setup\.ps1' -Apply$" -and @($groups['only the owner can confirm']).Count -eq 1) "a failing run ends with each fix once, in its group, in order ($(($groups.Keys | ForEach-Object { "${_}: $($groups[$_] -join ' / ')" }) -join ' || '))"
  $listLines = @($list -split "`n"); $at = [array]::IndexOf($listLines, 'then run it again:')
  $recheck = if ($at -ge 0 -and $at + 1 -lt $listLines.Count) { $listLines[$at + 1].Trim() } else { '' }  # the line as printed, nothing stripped
  Check ($recheck -match "-Repo '[^']*a checkout with spaces'" -and $recheck -match "-Only 'space,owner,setup'" -and $recheck -match "-MinFreeGB '1000000000'" -and $recheck -match "-FreeDrives '$([regex]::Escape("$drive,$drive,$second"))'") "the recheck command carries this run's options ($recheck)"
  Push-Location $repo
  try { $again = & $pwsh -NoProfile -Command $recheck 2>&1 | Out-String } finally { Pop-Location }  # read-only: the preflight changes nothing
  Check ($again -match "(?m)^FAIL drive $([regex]::Escape($drive)) has .* under the .* GB the campaign needs" -and $again -match "(?m)^FAIL drive $([regex]::Escape($second)) has ") "run as printed, the recheck finds the same space failure on the same checkout ($(($again -split "`n" | Select-Object -First 6) -join ' | '))"
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Apply @setupPlace | Out-Null
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

  # The bodies the task runs, the record that undoes the setup, and what still restarts the machine (D00 T03 §18).
  $body = Join-Path $bin 'lib\campaign.ps1'
  $bodyKept = [IO.File]::ReadAllBytes($body)
  Remove-Item -LiteralPath $body
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'lib\\campaign\.ps1 is missing from' -and $r.Text -match "campaign-admin-setup\.ps1' -Apply") 'a missing task body fails with -Apply as its fix'
  [IO.File]::WriteAllBytes($body, $bodyKept + [Text.Encoding]::ASCII.GetBytes("# changed`r`n"))
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'lib\\campaign\.ps1 in .* differs from this checkout') 'a task body that differs from this checkout fails'
  [IO.File]::WriteAllBytes($body, $bodyKept)
  $recordKept = [IO.File]::ReadAllText($adminRecord)
  Remove-Item -LiteralPath $adminRecord
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match "record at .* is missing, so the update values from before the setup are lost" -and $r.Text -match "records them as unknown" -and $r.Text -match "returns Windows' defaults") 'a missing record says the originals are lost and what -Apply and -Restore then do'
  [IO.File]::WriteAllText($adminRecord, '')
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'is not a readable record \(it holds nothing\)') 'an empty record fails, never read as no record'
  [IO.File]::WriteAllText($adminRecord, '{"PolicyKey": "x"}')
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'is not a complete admin record \(Update is missing\)' -and $r.Text -match 'Move-Item -LiteralPath') 'an incomplete record fails with the field that is wrong and the steps that recover'
  # The steps it prints, followed to the end: move the record aside, apply, and the setup passes; restore returns defaults.
  Move-Item -LiteralPath $adminRecord -Destination "$adminRecord.damaged"
  $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Apply @setupPlace 2>&1
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 0 -and ($out -join ' ') -match 'the originals are not known' -and (Get-Content -LiteralPath $adminRecord -Raw | ConvertFrom-Json).Originals -eq 'unknown') 'after the printed steps the setup passes, its record saying the originals are unknown'
  $out = & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Restore @setupPlace 2>&1
  $back = (Get-Item -LiteralPath $policy).GetValueNames()
  Check ($LASTEXITCODE -eq 0 -and $back -notcontains 'NoAutoRebootWithLoggedOnUsers' -and $back -notcontains 'AUOptions') "and restore then returns Windows' defaults ($(($out | Select-Object -Last 1)))"
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Apply @setupPlace | Out-Null
  Remove-Item -LiteralPath "$adminRecord.damaged" -ErrorAction Ignore
  $recordKept = [IO.File]::ReadAllText($adminRecord)
  $other = $recordKept | ConvertFrom-Json
  $other.BinDir = Join-Path $work 'elsewhere'
  [IO.File]::WriteAllText($adminRecord, ($other | ConvertTo-Json -Depth 5))
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'is for other targets \(BinDir ') 'a record for other targets fails'
  [IO.File]::WriteAllText($adminRecord, $recordKept)
  $deadlineKey = Split-Path $policy
  foreach ($case in @(@('SetComplianceDeadline', 1), @('ConfigureDeadlineForQualityUpdates', 2), @('ConfigureDeadlineForFeatureUpdates', 7))) {
    New-ItemProperty -LiteralPath $deadlineKey -Name $case[0] -Value $case[1] -PropertyType DWord -Force | Out-Null
    $r = Preflight $system (@('-Only', 'setup') + $place)
    Check ($r.Code -eq 1 -and $r.Text -match "an update deadline is set .*$($case[0])=$($case[1])" -and $r.Text -match 'Not Configured' -and $r.Text -match '(?s)in an elevated shell:\s+\d+\. set Windows Update''s') "a deadline set by $($case[0]) fails with how to clear it, listed under an elevated shell, since its owning check says so (round 2 of D00 T03 §25)"
    if ($case[0] -eq 'ConfigureDeadlineForQualityUpdates') {
      New-ItemProperty -LiteralPath $deadlineKey -Name ConfigureDeadlineNoAutoReboot -Value 1 -PropertyType DWord -Force | Out-Null
      $r = Preflight $system (@('-Only', 'setup') + $place)
      Check ($r.Code -eq 1 -and $r.Text -match 'an update deadline is set') 'a deadline with ConfigureDeadlineNoAutoReboot still fails, since a passed deadline restarts anyway'
      Remove-ItemProperty -LiteralPath $deadlineKey -Name ConfigureDeadlineNoAutoReboot
    }
    Remove-ItemProperty -LiteralPath $deadlineKey -Name $case[0]
  }
  New-Item -Path $pending -Force | Out-Null
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match 'an update restart is pending' -and $r.Text -match 'restart now, then run the preflight again') 'a pending restart fails with "restart now"'
  Remove-Item -LiteralPath $pending
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 0 -and $r.Text -match 'no update deadline set, no restart pending' -and $r.Text -match 'its bodies as in this checkout') "with none of them the pass line says what was checked ($(($r.Text -split "`n" | Select-Object -First 1)))"

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
