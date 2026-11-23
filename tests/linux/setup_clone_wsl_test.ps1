# Tests scripts/setup-clone.ps1 -CheckPrePush on Linux and WSL (D00 T07 §6): verifies pre-push hook, disk execute bit,
# scripts/leak-scan.ps1, pwsh on PATH, and runnable gitleaks together, with Linux release-archive install guidance
# (never winget). Runs on Linux/WSL only (CI linux job and `wsl -d Ubuntu --cd . pwsh -NoProfile -File ...`).
$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { 'this test runs on Linux/WSL; tests/setup_clone_test.ps1 covers Windows'; exit 1 }
$repoRoot = Split-Path (Split-Path $PSScriptRoot)
$setup = Join-Path $repoRoot 'scripts/setup-clone.ps1'
. (Join-Path $repoRoot 'scripts/lib/scratch.ps1')
$work = New-BosunScratchFolder -Name 'setup-clone-wsl'
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX', 'HOME', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'PATH'
$saved = @{}
foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..6]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }

$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$pwshExe = (Get-Process -Id $PID).Path
$shExe = (Get-Command sh -CommandType Application | Select-Object -First 1).Source
$chmodExe = (Get-Command chmod -CommandType Application | Select-Object -First 1).Source

$binNoGl = Join-Path $work 'bin-no-gl'
$binWithGl = Join-Path $work 'bin-with-gl'
$binBadGl = Join-Path $work 'bin-bad-gl'
$binNoPwsh = Join-Path $work 'bin-no-pwsh'
New-Item -ItemType Directory -Path $binNoGl, $binWithGl, $binBadGl, $binNoPwsh | Out-Null
foreach ($dir in $binNoGl, $binWithGl, $binBadGl) {
  New-Item -ItemType SymbolicLink -Path (Join-Path $dir 'git') -Target $gitExe | Out-Null
  New-Item -ItemType SymbolicLink -Path (Join-Path $dir 'pwsh') -Target $pwshExe | Out-Null
  New-Item -ItemType SymbolicLink -Path (Join-Path $dir 'sh') -Target $shExe | Out-Null
}
New-Item -ItemType SymbolicLink -Path (Join-Path $binNoPwsh 'git') -Target $gitExe | Out-Null
New-Item -ItemType SymbolicLink -Path (Join-Path $binNoPwsh 'sh') -Target $shExe | Out-Null

$glOk = Join-Path $binWithGl 'gitleaks'
Set-Content -LiteralPath $glOk -Value "#!/bin/sh`necho 8.30.1`nexit 0" -NoNewline
& $chmodExe +x $glOk
$glOk2 = Join-Path $binNoPwsh 'gitleaks'
Set-Content -LiteralPath $glOk2 -Value "#!/bin/sh`necho 8.30.1`nexit 0" -NoNewline
& $chmodExe +x $glOk2
$glBad = Join-Path $binBadGl 'gitleaks'
Set-Content -LiteralPath $glBad -Value "#!/bin/sh`necho broken >&2`nexit 2" -NoNewline
& $chmodExe +x $glBad

$env:HOME = Join-Path $work 'home'; New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'

function Invoke-Check([string]$clone, [string]$pathVal) {
  $oldPath = $env:PATH
  try {
    $env:PATH = $pathVal
    $out = & $pwshExe -NoProfile -File $setup -Repo $clone -CheckPrePush 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $out }
  } finally { $env:PATH = $oldPath }
}

try {
  $env:PATH = $binNoGl
  git config --global user.name 'Setup Linux Test'
  git config --global user.email 'setup-linux@example.invalid'
  git config --global init.defaultBranch main

  $clone = Join-Path $work 'clone'
  git init -q $clone
  New-Item -ItemType Directory -Path (Join-Path $clone 'scripts/hooks') | Out-Null
  foreach ($h in 'pre-commit', 'post-merge', 'pre-push') {
    $hp = Join-Path $clone "scripts/hooks/$h"
    Set-Content -LiteralPath $hp -Value "#!/bin/sh`nexit 0" -NoNewline
    & $chmodExe +x $hp
  }
  Set-Content -LiteralPath (Join-Path $clone 'scripts/leak-scan.ps1') -Value '# stub'
  git -C $clone add scripts/hooks/pre-commit scripts/hooks/post-merge scripts/hooks/pre-push scripts/leak-scan.ps1
  git -C $clone update-index --chmod=+x scripts/hooks/pre-commit scripts/hooks/post-merge scripts/hooks/pre-push

  # 1) core.hooksPath unset and wrong (including wrong case) -> refused without mutating clone
  $idxBefore = (Get-FileHash -LiteralPath (Join-Path $clone '.git/index') -Algorithm SHA256).Hash
  $r = Invoke-Check $clone $binWithGl
  $afterUnset = git -C $clone config --local --get core.hooksPath 2>$null
  Check 'Linux -CheckPrePush refuses unset core.hooksPath without mutating the clone' ($r.Exit -eq 1 -and
    $r.Text -match 'core\.hooksPath' -and -not $afterUnset -and
    (Get-FileHash -LiteralPath (Join-Path $clone '.git/index') -Algorithm SHA256).Hash -eq $idxBefore) "(exit $($r.Exit): $($r.Text))"

  git -C $clone config --local core.hooksPath 'Scripts/Hooks'
  $rCase = Invoke-Check $clone $binWithGl
  Check 'Linux -CheckPrePush refuses wrong-case core.hooksPath' ($rCase.Exit -eq 1 -and $rCase.Text -match 'Scripts/Hooks') "(exit $($rCase.Exit): $($rCase.Text))"

  git -C $clone config --local core.hooksPath 'scripts/hooks'
  $cfgBefore = Get-Content -LiteralPath (Join-Path $clone '.git/config') -Raw

  # 2) gitleaks missing -> refuses naming gitleaks and Linux release archive, never winget
  $r = Invoke-Check $clone $binNoGl
  Check 'Linux -CheckPrePush refuses missing gitleaks with Linux release-archive install, not winget' ($r.Exit -eq 1 -and
    $r.Text -match 'gitleaks is not installed' -and $r.Text -match 'gitleaks_8\.30\.1_linux_x64\.tar\.gz' -and
    $r.Text -notmatch 'winget') "(exit $($r.Exit): $($r.Text))"

  # 3) gitleaks exits non-zero -> refuses
  $r = Invoke-Check $clone $binBadGl
  Check 'Linux -CheckPrePush refuses when gitleaks exits non-zero' ($r.Exit -eq 1 -and $r.Text -match 'gitleaks exited 2') "(exit $($r.Exit): $($r.Text))"

  # 4) pwsh missing from PATH (set inside child so pwsh startup cannot re-inject $PSHOME) -> refuses
  $noPwshOut = & $pwshExe -NoProfile -Command "`$env:PATH = '$binNoPwsh'; & '$setup' -Repo '$clone' -CheckPrePush" 2>&1 | Out-String
  $noPwshExit = $LASTEXITCODE
  Check 'Linux -CheckPrePush refuses when pwsh is not on PATH' ($noPwshExit -eq 1 -and $noPwshOut -match 'pwsh is not on PATH') "(exit ${noPwshExit}: $noPwshOut)"

  # 5) pre-push missing disk execute bit -> refuses with chmod +x remedy
  & $chmodExe -x (Join-Path $clone 'scripts/hooks/pre-push')
  $r = Invoke-Check $clone $binWithGl
  & $chmodExe +x (Join-Path $clone 'scripts/hooks/pre-push')
  Check 'Linux -CheckPrePush refuses when pre-push lacks disk execute bit' ($r.Exit -eq 1 -and $r.Text -match 'chmod \+x') "(exit $($r.Exit): $($r.Text))"

  # 5b) pre-push non-executable in git index -> refuses
  git -C $clone update-index --chmod=-x scripts/hooks/pre-push
  $rMode = Invoke-Check $clone $binWithGl
  git -C $clone update-index --chmod=+x scripts/hooks/pre-push
  Check 'Linux -CheckPrePush refuses when pre-push is not executable in git index' ($rMode.Exit -eq 1 -and $rMode.Text -match 'update-index --chmod=\+x') "(exit $($rMode.Exit): $($rMode.Text))"

  # 5c) pre-push untracked (present on disk) -> refuses
  git -C $clone rm -q --cached scripts/hooks/pre-push
  $rUntracked = Invoke-Check $clone $binWithGl
  git -C $clone add scripts/hooks/pre-push; git -C $clone update-index --chmod=+x scripts/hooks/pre-push
  Check 'Linux -CheckPrePush refuses when pre-push is untracked' ($rUntracked.Exit -eq 1 -and $rUntracked.Text -match 'not tracked by git') "(exit $($rUntracked.Exit): $($rUntracked.Text))"

  # 6) scripts/leak-scan.ps1 missing -> refuses
  Remove-Item -LiteralPath (Join-Path $clone 'scripts/leak-scan.ps1') -Force
  $r = Invoke-Check $clone $binWithGl
  Set-Content -LiteralPath (Join-Path $clone 'scripts/leak-scan.ps1') -Value '# stub'
  Check 'Linux -CheckPrePush refuses when scripts/leak-scan.ps1 is missing' ($r.Exit -eq 1 -and $r.Text -match 'leak-scan\.ps1 is missing') "(exit $($r.Exit): $($r.Text))"

  # 6b) git missing on Linux -> refuses with Linux guidance, never Git for Windows
  $noGitDir = Join-Path $work 'bin-no-git'
  New-Item -ItemType Directory -Path $noGitDir | Out-Null
  $noGitOut = & $pwshExe -NoProfile -Command "`$env:PATH = '$noGitDir'; & '$setup' -Repo '$clone' -CheckPrePush" 2>&1 | Out-String
  $noGitExit = $LASTEXITCODE
  Check 'Linux missing-git refusal gives Linux guidance, never Git for Windows' ($noGitExit -eq 1 -and $noGitOut -match 'git is not on PATH' -and $noGitOut -notmatch 'Git for Windows') "(exit ${noGitExit}: $noGitOut)"

  # 7) All present -> single pass, exit 0, zero mutations (config and index hash preserved)
  $idxBeforeOk = (Get-FileHash -LiteralPath (Join-Path $clone '.git/index') -Algorithm SHA256).Hash
  $r = Invoke-Check $clone $binWithGl
  Check 'Linux -CheckPrePush passes with one line when hook, pwsh, and gitleaks are ready' ($r.Exit -eq 0 -and
    $r.Text -match 'pre-push: ok' -and $r.Text -match '8\.30\.1' -and
    (Get-Content -LiteralPath (Join-Path $clone '.git/config') -Raw) -ceq $cfgBefore -and
    (Get-FileHash -LiteralPath (Join-Path $clone '.git/index') -Algorithm SHA256).Hash -eq $idxBeforeOk) "(exit $($r.Exit): $($r.Text))"
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
