# scripts/start-run.ps1: the shortcuts it writes start the run from this checkout, each with its runner's icon; a dry
# run changes nothing; and when the launcher's window process ends, nothing it started keeps running.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts\start-run.ps1'
$pwsh = (Get-Process -Id $PID).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-start-run-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$failed = 0
function Check([bool]$ok, [string]$what) {
  if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ }
}
try {
  # One call writes a launcher for each runner, and removes the names the same launchers had before.
  $icons = Join-Path $work 'icons'
  $earlier = 'Bosun unattended run.lnk', 'Bosun unattended run (Antigravity).lnk', 'Bosun Carbon unattended run.lnk'
  foreach ($name in $earlier) { Set-Content -LiteralPath (Join-Path $work $name) -Value 'an earlier shortcut' }
  $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work -IconDir $icons) -join "`n"
  $link = Join-Path $work 'Bosun Claude campaign.lnk'
  $carbonLink = Join-Path $work 'Bosun Carbon campaign.lnk'
  Check ($LASTEXITCODE -eq 0 -and (Test-Path $link) -and (Test-Path $carbonLink)) "both shortcuts are written where asked ($out)"
  Check (-not ($earlier | Where-Object { Test-Path -LiteralPath (Join-Path $work $_) })) 'the shortcuts'' earlier names are removed'
  $shell = New-Object -ComObject WScript.Shell
  $read = $shell.CreateShortcut($link)
  $carbon = $shell.CreateShortcut($carbonLink)
  Check ($read.WorkingDirectory -eq $repo -and $carbon.WorkingDirectory -eq $repo) 'each starts in this checkout, wherever that is'
  Check ($read.Arguments -match '-NoExit' -and $read.Arguments.TrimEnd().EndsWith("`"$script`"")) 'the Claude shortcut runs this checkout''s own start script and keeps the window'
  Check ($carbon.Arguments -match '-NoExit' -and $carbon.Arguments.EndsWith("`"$script`" -Agent antigravity")) 'the Carbon shortcut starts the same script with -Agent antigravity'
  Check ((Split-Path $read.TargetPath -Leaf) -match '^pwsh') 'it runs PowerShell 7'
  Check ($read.Description -eq 'Start the unattended Bosun campaign in Claude Code' -and $carbon.Description -match 'Google Antigravity on Gemini 4 Carbon$') 'each says which runner it starts'
  # Each carries its own badged icon, rendered for this PC outside the repository (needs scripts/requirements-icons.txt).
  $claudeIcon = Join-Path $icons 'bosun-campaign-claude.ico'
  $carbonIcon = Join-Path $icons 'bosun-campaign-carbon.ico'
  Check ($read.IconLocation -eq "$claudeIcon,0" -and $carbon.IconLocation -eq "$carbonIcon,0" -and (Test-Path $claudeIcon) -and (Test-Path $carbonIcon)) "each shortcut names its own icon file ($($read.IconLocation); $($carbon.IconLocation))"
  $hashes = @($claudeIcon, $carbonIcon, (Join-Path $repo 'resources\bosun.ico') | Where-Object { Test-Path $_ } | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
  Check (@($hashes | Select-Object -Unique).Count -eq 3) 'the two badges differ from each other and from the plain Bosun icon'
  # An icon folder that can't be made: the shortcuts are still written, with the plain icon and a notice.
  $blocked = Join-Path $work 'not-a-folder'
  Set-Content -LiteralPath $blocked -Value 'a file where the icon folder would go'
  $plainDir = Join-Path $work 'plain'
  New-Item -ItemType Directory -Force $plainDir | Out-Null
  $out = (& $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $plainDir -IconDir $blocked) -join "`n"
  $fallback = $shell.CreateShortcut((Join-Path $plainDir 'Bosun Claude campaign.lnk'))
  Check ($LASTEXITCODE -eq 0 -and $fallback.IconLocation -eq "$(Join-Path $repo 'resources\bosun.ico'),0" -and $out -match 'could not be rendered') "a badge that can't be rendered leaves the plain Bosun icon and says so ($($fallback.IconLocation))"

  $state = Join-Path $work 'state.json'
  $env:RUN_GUARD_STATE = $state
  # With nothing but Windows on PATH, as on a CI runner with no Claude Code: a dry run starts nothing, so it needs none.
  $path = $env:Path
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -DryRun) -join "`n" } finally { $env:Path = $path }
  Remove-Item Env:RUN_GUARD_STATE
  Check ($LASTEXITCODE -eq 0 -and -not (Test-Path $state)) 'a dry run leaves the run state alone'
  Check ($out -match '(?m)run-guard\.py start --session ([0-9a-f-]{36})\s*$' -and $out -match "claude --session-id $($Matches[1]) --dangerously-skip-permissions --model opus --effort medium `"/goal ") 'the guard is told the same session id the session starts with, in bypass mode, at medium effort, with its goal'
  Check ($out -notmatch 'claude-opus-\d') 'the model is the alias for the newest Opus, never one pinned version'
  Check ($out -match '100% complete' -and $out -match 'query next') 'the goal names the finish line'
  Check ($out -notmatch 'guard resumes' -and $out -match 'own heartbeat takes the run up again') 'the goal promises no resume: the session''s own heartbeat carries a wait'

  # The Antigravity run: every permission auto-approved, on Gemini 4 Carbon, and the goal as its opening prompt.
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -Agent antigravity -DryRun) -join "`n" } finally { $env:Path = $path }
  Check ($LASTEXITCODE -eq 0 -and $out -match 'agy --dangerously-skip-permissions --effort medium --model gemini-4-carbon-medium --prompt-interactive "Finish the Bosun') 'Antigravity starts with every prompt off, at medium effort, on Gemini 4 Carbon, with the goal'
  Check ($out -match 'query next' -and $out -match 'wait for it in this session' -and $out -notmatch 'heartbeat takes') 'its goal keeps the finish line and waits in-session, since no guard holds it'

  # A real start against a fake claude, a test registry key and a state file of its own: nothing real is started.
  $fake = Join-Path $work 'fake'
  New-Item -ItemType Directory -Force $fake | Out-Null
  Set-Content (Join-Path $fake 'claude.cmd') "@echo off`r`nif defined CLAUDE_CODE_OAUTH_TOKEN (echo TOKEN=set) else (echo TOKEN=none)`r`necho ARGS=%*" -Encoding ascii
  $envKey = "HKCU:\Software\BosunStartRunTest-$PID"
  New-Item -Path $envKey -Force | Out-Null
  Set-ItemProperty -LiteralPath $envKey -Name 'CLAUDE_CODE_OAUTH_TOKEN' -Value 'tok-start-run-test'
  $had = $env:CLAUDE_CODE_OAUTH_TOKEN
  $env:RUN_GUARD_STATE = $state
  $env:Path = "$fake;$path"
  try {
    $env:CLAUDE_CODE_OAUTH_TOKEN = $null
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal') -join "`n"
    $code = $LASTEXITCODE
    $saved = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json }
    Check ($code -eq 0 -and $out -match 'sign-in: the long-lived token from your user variables' -and $out -match 'TOKEN=set') 'a window without the token gets it from the user''s variables'
    Check ($out -notmatch 'tok-start-run-test') 'and the token is never printed'
    Check ($out -match 'ARGS=--session-id [0-9a-f-]{36} --dangerously-skip-permissions --model opus --effort medium "/goal test goal"') 'the session starts on the newest Opus at medium effort, whatever the user''s own default, with every prompt off and its goal'
    $kept = @($saved.PSObject.Properties.Name)
    Check ($saved.active -and $saved.session_id -match '^[0-9a-f-]{36}$' -and -not ($kept | Where-Object { $_ -in 'model', 'permission_mode', 'child' })) "the guard holds the run for that session and keeps nothing a resume would need ($($kept -join ', '))"
    Remove-Item $state -ErrorAction Ignore
    $env:CLAUDE_CODE_OAUTH_TOKEN = 'tok-in-this-window'
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal' -Model sonnet) -join "`n"
    Check ($out -match 'sign-in: the long-lived token this window already has' -and $out -match 'TOKEN=set' -and $out -notmatch 'tok-in-this-window') 'a window that has the token uses it as it is'
    Check ($out -match '--model sonnet --effort medium "/goal') '-Model starts another model'
    Remove-Item $state -ErrorAction Ignore
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal' -Effort low) -join "`n"
    Check ($out -match '--model opus --effort low "/goal') '-Effort starts another effort'

    # The window closes: the launcher's process ends, and nothing it started keeps running. The stand-in claude
    # starts a process of its own and both wait; each ends by itself after two minutes whatever happens here.
    Remove-Item $state -ErrorAction Ignore
    $tree = Join-Path $work 'tree'
    New-Item -ItemType Directory -Force $tree | Out-Null
    $pids = Join-Path $tree 'pids.txt'
    Set-Content (Join-Path $tree 'linger.ps1') @'
param($pids)
$child = Start-Process (Get-Process -Id $PID).Path -NoNewWindow -PassThru -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120'
Set-Content -LiteralPath $pids -Value "$PID $($child.Id)"
Start-Sleep 120
'@
    Set-Content (Join-Path $tree 'claude.cmd') "@`"$pwsh`" -NoProfile -File `"%~dp0linger.ps1`" `"$pids`"" -Encoding ascii
    $env:Path = "$tree;$path"
    $launcher = Start-Process $pwsh -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $tree 'out.txt') -ArgumentList '-NoProfile', '-File', "`"$script`"", '-UserEnvKey', "`"$envKey`"", '-Goal', 'tree-test'
    $started = @()
    try {
      # Read only once it is whole: the file exists a moment before its line is written.
      $line = ''
      foreach ($i in 1..300) {
        $line = if (Test-Path -LiteralPath $pids) { "$(Get-Content -LiteralPath $pids -Raw -ErrorAction Ignore)".Trim() } else { '' }
        if ($line -match '^\d+ \d+$') { break }
        Start-Sleep -Milliseconds 200
      }
      $started = @(if ($line -match '^\d+ \d+$') { $line -split ' ' | ForEach-Object { [int]$_ } })
      $before = @($started | Where-Object { Get-Process -Id $_ -ErrorAction Ignore })
      Check ($started.Count -eq 2 -and $before.Count -eq 2 -and -not $launcher.HasExited) "the launcher's session and the process it started are running ($($started -join ', '))"
      Stop-Process -Id $launcher.Id -Force  # as closing the window ends the launcher: no script of its own gets to run
      $left = $started
      foreach ($i in 1..75) { $left = @($started | Where-Object { Get-Process -Id $_ -ErrorAction Ignore }); if (-not $left) { break }; Start-Sleep -Milliseconds 200 }
      Check ($started.Count -eq 2 -and -not $left) "when the launcher ends, its whole process tree ends with it (still running: $($left -join ', '))"
    } finally {
      # Whatever is left is stopped and waited for, so nothing outlives the test or holds its folder open.
      $ids = @($launcher.Id) + $started
      foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction Ignore }
      foreach ($i in 1..50) { if (-not ($ids | Where-Object { Get-Process -Id $_ -ErrorAction Ignore })) { break }; Start-Sleep -Milliseconds 200 }
    }
  } finally {
    $env:CLAUDE_CODE_OAUTH_TOKEN = $had
    $env:Path = $path
    Remove-Item Env:RUN_GUARD_STATE
    Remove-Item -LiteralPath $envKey -Recurse -Force
  }

  # The one-time import from the vault, against a fake `op`: the token lands in the given key and is never printed.
  $vaultKey = "HKCU:\Software\BosunStartRunVault-$PID"
  $record = Join-Path $work 'claude-token.json'
  $store = Join-Path $repo 'scripts\store-claude-token.ps1'
  Set-Content (Join-Path $fake 'op.cmd') "@echo off`r`nif `"%1 %2`"==`"read op://Vault/item/credential`" (echo tok-from-vault& exit /b 0)`r`nif `"%1 %2`"==`"read op://Vault/nodate/credential`" (echo tok-no-date& exit /b 0)`r`nif `"%1 %2 %3`"==`"item get item`" (echo {`"created_at`":`"2026-03-05T08:04:52Z`"}& exit /b 0)`r`necho not signed in 1>&2`r`nexit /b 1" -Encoding ascii
  try {
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record -IssuedOn 2026-11-10) -join "`n"
    Check ($LASTEXITCODE -eq 0 -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-from-vault' -and $out -notmatch 'tok-from-vault') '-FromVault stores the vault''s token without printing it'
    Check ((Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-11-10') 'and records the day it was made'
    & $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record | Out-Null
    Check ((Get-Content $record -Raw | ConvertFrom-Json).issued -eq '2026-03-05') 'with no day given, the day is the one the vault item was created, never today'
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/nodate/credential' -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 0 -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-no-date' -and -not (Test-Path $record) -and $out -match '-RecordOnly -IssuedOn') 'a vault item with no readable day stores the token with no day, and drops the old token''s'
    & $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/item/credential' -Key $vaultKey -Record $record -IssuedOn 2026-11-10 | Out-Null
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $fake 'op.cmd') -VaultItem 'op://Vault/other/credential' -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 1 -and $out -match 'could not read the token from the vault' -and (Get-Item $vaultKey).GetValue('CLAUDE_CODE_OAUTH_TOKEN') -eq 'tok-from-vault') 'a vault that can''t be read changes nothing and says so'
    $out = (& $pwsh -NoProfile -File $store -FromVault -Op (Join-Path $work 'no-op.exe') -Key $vaultKey -Record $record) -join "`n"
    Check ($LASTEXITCODE -eq 1 -and $out -match 'is not on PATH') 'a missing 1Password CLI is named'
  } finally {
    Remove-Item -LiteralPath $vaultKey -Recurse -Force -ErrorAction Ignore
  }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed failed"; exit 1 }
'all passed'
