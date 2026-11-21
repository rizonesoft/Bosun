# The CI secrets job's self-test (D00 T05 §6): the history scan it runs, `pwsh scripts/leak-scan.ps1 -All`, must find
# a secret that is only in the history. A scratch repository holds a token built at run time in one commit, removed
# again in the next, and in a merge resolution only; the same command, run there, must exit 1 naming each introducing
# commit and its file, with the token nowhere in its output. A scan that fails for another reason (exit 1 with no
# finding, or any other exit) fails this test. The token never reaches a commit of this repository.
#
#   pwsh tests/ci/secret_history_selftest.ps1      (the secrets job in .github/workflows/plan.yml runs it)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot)
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } elseif ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-secret-history-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM'
$saved = @{}; foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..2]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
$letters = [char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
# Built at run time, so no token-shaped literal is ever committed.
$token = 'gh' + 'p_' + (-join (1..36 | ForEach-Object { $letters | Get-Random }))
try {
  git config --global user.name 'Self Test'; git config --global user.email 'self-test@example.invalid'; git config --global init.defaultBranch main
  $repo = Join-Path $work 'history'
  git init -q $repo
  New-Item -ItemType Directory -Path (Join-Path $repo 'scripts') | Out-Null
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/leak-scan.ps1') -Destination (Join-Path $repo 'scripts/leak-scan.ps1')
  function Save([string]$file, [string]$text) { [IO.File]::WriteAllText((Join-Path $repo $file), $text, [Text.UTF8Encoding]::new($false)) }
  Save 'notes.txt' "notes`n"; git -C $repo add -A; git -C $repo commit -q -m base
  Save 'config.txt' "token = $token`n"; git -C $repo add -A; git -C $repo commit -q -m 'add config'
  $introduced = "$(git -C $repo rev-parse HEAD)"
  git -C $repo rm -q config.txt; git -C $repo commit -q -m 'remove config'
  git -C $repo checkout -q -b side; Save 'side.txt' "side`n"; git -C $repo add -A; git -C $repo commit -q -m side
  git -C $repo checkout -q main; Save 'main.txt' "main`n"; git -C $repo add -A; git -C $repo commit -q -m main
  git -C $repo merge -q --no-commit --no-ff side 2>$null
  if ($LASTEXITCODE) { throw 'the fixture merge could not start, so the merge-resolution case would not be a merge' }
  Save 'resolved.txt' "token = $token`n"; git -C $repo add -A; git -C $repo commit -q -m 'merge side'
  $merged = "$(git -C $repo rev-parse HEAD)"
  Check 'the resolution commit is a merge, with two parents' (@((git -C $repo rev-list --parents -n 1 $merged) -split ' ').Count -eq 3)
  git -C $repo rm -q resolved.txt; git -C $repo commit -q -m 'tidy'
  Check 'the scratch history holds the token behind its tip only' (-not (Test-Path -LiteralPath (Join-Path $repo 'config.txt')) -and -not (Test-Path -LiteralPath (Join-Path $repo 'resolved.txt')))

  $said = & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'scripts/leak-scan.ps1') -All 2>&1 | Out-String; $code = $LASTEXITCODE
  # The output is checked for the token before it is shown, so a redaction that fails never puts the token in the log.
  $leaked = $said.Contains($token) -or $said.Contains($token.Substring(4, 12))
  if ($leaked) { "the scan's output is withheld: it holds the synthetic token" } else { "--- the scan's own output, as CI shows it ---"; $said.TrimEnd() }
  Check 'the history scan exits 1' ($code -eq 1) "(exit $code)"
  Check 'it names the commit that added the token, and its file' (-not $leaked -and $said -match "$($introduced.Substring(0, 12)): config\.txt:1: secret \(github-pat\)")
  Check 'it names the merge whose resolution added the token, and its file' ($said -match "$($merged.Substring(0, 12)): resolved\.txt:1: secret \(github-pat\)")
  Check 'and nothing of the token is in its output' (-not $leaked)
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
