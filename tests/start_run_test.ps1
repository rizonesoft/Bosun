# scripts/start-run.ps1: the shortcut it writes starts the run from this checkout, and a dry run changes nothing.
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
  $out = & $pwsh -NoProfile -File $script -InstallShortcut -ShortcutDir $work
  $link = Join-Path $work 'Bosun unattended run.lnk'
  Check ($LASTEXITCODE -eq 0 -and (Test-Path $link)) "the shortcut is written where asked ($out)"
  $read = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
  Check ($read.WorkingDirectory -eq $repo) 'it starts in this checkout, wherever that is'
  Check ($read.Arguments -match '-NoExit' -and $read.Arguments.Contains("`"$script`"")) 'it runs this checkout''s own start script and keeps the window'
  Check ((Split-Path $read.TargetPath -Leaf) -match '^pwsh') 'it runs PowerShell 7'

  $state = Join-Path $work 'state.json'
  $env:RUN_GUARD_STATE = $state
  # With nothing but Windows on PATH, as on a CI runner with no Claude Code: a dry run starts nothing, so it needs none.
  $path = $env:Path
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -DryRun) -join "`n" } finally { $env:Path = $path }
  Remove-Item Env:RUN_GUARD_STATE
  Check ($LASTEXITCODE -eq 0 -and -not (Test-Path $state)) 'a dry run leaves the run state alone'
  Check ($out -match 'run-guard\.py start --session ([0-9a-f-]{36}) --permission-mode bypassPermissions --model opus' -and $out -match "claude --session-id $($Matches[1]) --dangerously-skip-permissions --model opus `"/goal ") 'the guard is told the same session id and model the session starts with, in bypass mode, with its goal'
  Check ($out -notmatch 'claude-opus-\d') 'the model is the alias for the newest Opus, never one pinned version'
  Check ($out -match '100% complete' -and $out -match 'query next') 'the goal names the finish line'

  # The Antigravity run: its own shortcut, every permission auto-approved, and the goal as its opening prompt.
  & $pwsh -NoProfile -File $script -Agent antigravity -InstallShortcut -ShortcutDir $work | Out-Null
  $agyLink = Join-Path $work 'Bosun unattended run (Antigravity).lnk'
  Check ((Test-Path $agyLink) -and (New-Object -ComObject WScript.Shell).CreateShortcut($agyLink).Arguments -match ' -Agent antigravity$') 'the Antigravity shortcut starts this script with -Agent antigravity'
  $env:Path = Join-Path $env:SystemRoot 'System32'
  try { $out = (& $pwsh -NoProfile -File $script -Agent antigravity -DryRun) -join "`n" } finally { $env:Path = $path }
  Check ($LASTEXITCODE -eq 0 -and $out -match 'agy --dangerously-skip-permissions --effort medium --prompt-interactive "Finish the Bosun') 'Antigravity starts with every prompt off, at medium effort, with the goal'
  Check ($out -match 'query next' -and $out -match 'wait for it in this session' -and $out -notmatch 'run guard resumes') 'its goal keeps the finish line and waits in-session, since no guard holds it'

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
    Check ($out -match 'ARGS=--session-id [0-9a-f-]{36} --dangerously-skip-permissions --model opus "/goal test goal"') 'the session starts on the newest Opus with every prompt off and its goal'
    Check ($saved.model -eq 'opus' -and $saved.permission_mode -eq 'bypassPermissions' -and $saved.active) 'the guard keeps the model for the sessions it resumes'
    Remove-Item $state -ErrorAction Ignore
    $env:CLAUDE_CODE_OAUTH_TOKEN = 'tok-in-this-window'
    $out = (& $pwsh -NoProfile -File $script -UserEnvKey $envKey -Goal 'test goal' -Model sonnet) -join "`n"
    Check ($out -match 'sign-in: the long-lived token this window already has' -and $out -match 'TOKEN=set' -and $out -notmatch 'tok-in-this-window') 'a window that has the token uses it as it is'
    Check ($out -match '--model sonnet "/goal') '-Model starts another model'
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
