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

  $d = Tool 'wsl-none' 'wsl' "echo There is no distribution with the supplied name.`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  Check ($r.Code -eq 1 -and $r.Text -match "WSL Ubuntu doesn't start" -and $r.Text -match 'wsl --install -d Ubuntu') 'a missing WSL distro fails with its fix'
  $d = Tool 'wsl-nomount' 'wsl' "if `"%4`"==`"true`" exit /b 0`r`nexit /b 1"
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  Check ($r.Code -eq 1 -and $r.Text -match "can't see this repo at /mnt/d/" -and $r.Text -match 'wsl --shutdown') 'a WSL that cannot see /mnt/d fails with its fix'
  $d = Tool 'wsl-good' 'wsl' 'exit /b 0'
  $r = Preflight "$d;$system" @('-Only', 'wsl')
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   WSL Ubuntu sees /mnt/d/') 'a WSL that sees the repo passes'

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
  $gitDir = Split-Path (Get-Command git -CommandType Application | Select-Object -First 1).Source
  $tools = "$gitDir;$pythonDir;$system"
  $origin = Join-Path $work 'origin.git'
  $clone = Join-Path $work 'clone'
  $other = Join-Path $work 'other'
  git init -q --bare -b main $origin
  git clone -q $origin $clone 2>&1 | Out-Null
  New-Item -ItemType Directory -Force (Join-Path $clone 'scripts') | Out-Null
  # Stand-ins for the repo's own CI and guard scripts, answering with the exit code the test sets.
  Set-Content (Join-Path $clone 'scripts\ci_status.py') "import os, sys`ncode = int(os.environ.get('BOSUN_TEST_CI', '0'))`nprint('build.yml: faked')`nif code == 5: print('to get a real run: gh workflow run build.yml --ref main')`nsys.exit(code)" -Encoding utf8
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
  Remove-Item Env:BOSUN_TEST_CI
  $env:BOSUN_TEST_GUARD = '1'
  $r = Preflight $tools @('-Only', 'guard', '-Repo', $clone)
  Remove-Item Env:BOSUN_TEST_GUARD
  Check ($r.Code -eq 1 -and $r.Text -match "the watchdog isn't healthy" -and $r.Text -match 'run-guard.py status --verify') 'an unhealthy guard fails with its fix'

  # --- The machine ---
  $drive = [IO.Path]::GetPathRoot($work)
  $r = Preflight $system @('-Only', 'space', '-FreeDrives', $drive, '-MinFreeGB', '1000000000')
  Check ($r.Code -eq 1 -and $r.Text -match 'GB free, under the' -and $r.Text -match 'fix: free space on') 'a free space shortfall fails with its fix'
  $r = Preflight $system @('-Only', 'space', '-FreeDrives', $drive, '-MinFreeGB', '0')
  Check ($r.Code -eq 0) 'enough free space passes'

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
  Remove-Item Env:BOSUN_FAKE_POWERCFG

  $policy = "HKCU:\Software\BosunTest\preflight-$PID\AU"
  $store = Join-Path $work 'tasks.json'
  $bin = Join-Path $work 'bin'
  $dotnetDir = Join-Path $work 'dotnet-home'
  New-Item -ItemType Directory -Force $dotnetDir | Out-Null
  $sdkList = Join-Path $dotnetDir 'sdks.txt'
  Set-Content $sdkList '11.0.100-rc.2.26504.105 [x]'
  Set-Content (Join-Path $dotnetDir 'dotnet.cmd') "@type `"$sdkList`"" -Encoding ascii
  $place = @('-PolicyKey', $policy, '-TaskStore', $store, '-BinDir', $bin, '-DotnetDir', $dotnetDir, '-Vhdx', 'R:\test.vhdx')
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 1 -and $r.Text -match "admin setup isn't in place" -and $r.Text -match 'NoAutoRebootWithLoggedOnUsers is absent' -and $r.Text -match 'campaign-admin-setup.ps1 -Apply') 'no admin setup fails with its fix'
  & $pwsh -NoProfile -File (Join-Path $repo 'scripts\campaign-admin-setup.ps1') -Apply -Record (Join-Path $work 'admin.json') @place | Out-Null
  $r = Preflight $system (@('-Only', 'setup') + $place)
  Check ($r.Code -eq 0 -and $r.Text -match 'ok   admin setup in place') 'admin setup in place passes'
  $good = [IO.File]::ReadAllText($store)
  $drifts = @(
    @{ Name = 'disabled'; Field = 'Enabled'; Value = $false; Says = 'disabled' },
    @{ Name = 'a changed action'; Field = 'Arguments'; Value = '-File C:\elsewhere.ps1'; Says = 'arguments are' },
    @{ Name = 'another principal'; Field = 'User'; Value = 'DerickPayne'; Says = 'runs as DerickPayne' },
    @{ Name = 'a changed trigger'; Field = 'Triggers'; Value = @('Daily'); Says = 'triggers are Daily' }
  )
  foreach ($x in $drifts) {
    $all = $good | ConvertFrom-Json
    $all.'Bosun attach D'.($x.Field) = $x.Value
    Set-Content $store ($all | ConvertTo-Json -Depth 5)
    $r = Preflight $system (@('-Only', 'setup') + $place)
    Check ($r.Code -eq 1 -and $r.Text -match "task 'Bosun attach D' $([regex]::Escape($x.Says))") "a task with $($x.Name) fails"
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
  Remove-Item -Recurse -Force $work -ErrorAction Ignore
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
