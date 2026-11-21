# The leak guard on Linux and WSL without gitleaks (D00 T05 §4): a real push through scripts/hooks/pre-push is
# refused with the release-archive install, never winget's, and the destination's refs read back unchanged. Runs on
# Linux only (the CI linux job, or WSL): tests/leak_scan_test.ps1 covers Windows.
$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { 'this test runs on Linux; tests/leak_scan_test.ps1 covers Windows'; exit 1 }
$repoRoot = Split-Path (Split-Path $PSScriptRoot)
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-leak-scan-linux-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# Isolated as the Windows suite is: no inherited repository, its own home and git configuration, and a PATH of one
# private folder holding links to git, pwsh and sh only, so gitleaks is off it wherever it is installed.
foreach ($name in 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX') { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwshExe = (Get-Process -Id $PID).Path
$shExe = (Get-Command sh -CommandType Application | Select-Object -First 1).Source
$bin = Join-Path $work 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
foreach ($tool in @{ git = $gitExe; pwsh = $pwshExe; sh = $shExe }.GetEnumerator()) { New-Item -ItemType SymbolicLink -Path (Join-Path $bin $tool.Key) -Target $tool.Value | Out-Null }
$savedPath = $env:PATH
$env:HOME = Join-Path $work 'home'; New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
try {
  $env:PATH = $bin
  git config --global user.name 'Leak Test'; git config --global user.email 'leak-test@example.invalid'
  git config --global init.defaultBranch main

  $seed = Join-Path $work 'seed'
  git init -q $seed
  New-Item -ItemType Directory -Path (Join-Path $seed 'scripts/hooks') | Out-Null
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/leak-scan.ps1') -Destination (Join-Path $seed 'scripts/leak-scan.ps1')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/hooks/pre-push') -Destination (Join-Path $seed 'scripts/hooks/pre-push')
  Set-Content -LiteralPath (Join-Path $seed 'notes.txt') -Value 'notes'
  git -C $seed add -A; git -C $seed update-index --chmod=+x scripts/hooks/pre-push; git -C $seed commit -q -m seed
  $bare = Join-Path $work 'remote.git'
  git clone -q --bare $seed $bare 2>$null
  $checkout = Join-Path $work 'checkout'
  git clone -q $bare $checkout 2>$null
  git -C $checkout config core.hooksPath scripts/hooks
  Set-Content -LiteralPath (Join-Path $checkout 'more.txt') -Value 'clean'
  git -C $checkout add -A; git -C $checkout commit -q -m more
  $before = (git --git-dir=$bare for-each-ref --format='%(refname) %(objectname)') -join "`n"

  $text = git -C $checkout push origin main 2>&1 | Out-String; $code = $LASTEXITCODE
  $after = (git --git-dir=$bare for-each-ref --format='%(refname) %(objectname)') -join "`n"
  Check 'without gitleaks the push is refused' ($code -ne 0 -and $text -match 'leak-scan: push refused: gitleaks is not installed') "(exit ${code}: $text)"
  Check 'with the release-archive install, not winget' ($text -match 'gitleaks_8\.30\.1_linux_x64\.tar\.gz' -and $text -match 'gitleaks_8\.30\.1_checksums\.txt' -and $text -notmatch 'winget') "($text)"
  Check 'and the destination refs read back unchanged' ($after -eq $before) "($after)"
} finally {
  $env:PATH = $savedPath
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
