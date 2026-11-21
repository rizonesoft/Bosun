# Tests how agents get the overlay's owner-only rules (D00 T05 §5): the public rule files carry the pointer and no
# private reference, machine path or plain import of the overlay; scripts/overlay_rules_hook.py trusts only the marked
# overlay's own clone and its own AGENTS.md and hands those rules to a Claude Code session as SessionStart context,
# and nothing otherwise; and setup's printed undo of the mark works
# from any folder. Every case runs in temporary checkouts.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ('bosun-agent-rules-test-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}

# --- the tracked rule files ---
$agents = [IO.File]::ReadAllText((Join-Path $repoRoot 'AGENTS.md'))
$claude = [IO.File]::ReadAllText((Join-Path $repoRoot 'CLAUDE.md'))
Check 'AGENTS.md points every agent at .private/AGENTS.md, behind the mark, on the clone''s own file' ($agents -match '\.private/AGENTS\.md' -and $agents -match 'bosun\.overlay' -and $agents -match 'not a link or junction' -and $agents -match 'linked worktree' -and $agents -match 'not a link to a file elsewhere' -and $agents -match 'overlay_rules_hook\.py')
foreach ($file in 'GEMINI.md', 'CODEX.md') {
  $text = [IO.File]::ReadAllText((Join-Path $repoRoot $file))
  Check "$file defers to AGENTS.md and leaves the pointer to it" ($text -match '\bAGENTS\.md\b' -and $text -notmatch '\.private/AGENTS\.md')
}
# A plain import of the overlay would load whatever folder sits at .private, mark or not.
Check 'CLAUDE.md imports nothing from .private' ($claude -notmatch '(?m)^\s*@\S*\.private')
# A machine path: a drive-rooted folder (C:\Users\..., D:/...), or a home folder on Linux or under WSL's /mnt.
$machinePath = '(?i)\b[A-Z]:[\\/][^\s`)]|/home/[a-z_][a-z0-9_-]*/|/mnt/[a-z]/users/'
foreach ($pair in @(@('AGENTS.md', $agents), @('CLAUDE.md', $claude))) {
  Check "$($pair[0]) holds no op:// reference" ($pair[1] -notmatch 'op://')
  $hit = [regex]::Match($pair[1], $machinePath)
  Check "$($pair[0]) holds no machine path" (-not $hit.Success) "(at: $($hit.Value))"
}
# The vault and account names are private strings: where this checkout has its overlay, the rule files hold none of
# the deny list's entries (read from it, never written here; D00 T05 §7). Counts only are shown.
$denyFile = Join-Path $repoRoot '.private\deny-list.txt'
if (Test-Path -LiteralPath $denyFile -PathType Leaf) {
  $entries = @([IO.File]::ReadAllLines($denyFile) | Where-Object { -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  foreach ($pair in @(foreach ($ruleFile in 'AGENTS.md', 'CLAUDE.md', 'GEMINI.md', 'CODEX.md') { ,@($ruleFile, [IO.File]::ReadAllText((Join-Path $repoRoot $ruleFile))) })) {
    $found = @($entries | Where-Object { $pair[1].IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
    Check "$($pair[0]) holds none of the overlay's $($entries.Count) deny-list entries" ($entries.Count -gt 0 -and $found -eq 0) "($found found)"
  }
} else { 'note: no .private/deny-list.txt in this checkout, so the rule files are not checked against it' }
$settings = Get-Content -LiteralPath (Join-Path $repoRoot '.claude\settings.json') -Raw | ConvertFrom-Json
$starts = @($settings.hooks.SessionStart | ForEach-Object { $_.hooks } | ForEach-Object { $_.command })
Check 'Claude Code runs the overlay rules hook at every session start' (@($starts | Where-Object { $_ -match 'scripts/overlay_rules_hook\.py' }).Count -eq 1) "($($starts -join ' | '))"

# --- isolation for the checkouts below ---
$saved = @{}
$changed = 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_PREFIX', 'HOME', 'USERPROFILE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM', 'BOSUN_OVERLAY_REPO', 'CLAUDE_PROJECT_DIR'
foreach ($name in $changed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
foreach ($name in $changed[0..6] + 'CLAUDE_PROJECT_DIR') { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore }
$env:HOME = Join-Path $work 'home'; $env:USERPROFILE = $env:HOME; New-Item -ItemType Directory -Path $env:HOME | Out-Null
$env:GIT_CONFIG_GLOBAL = Join-Path $work 'gitconfig'; $env:GIT_CONFIG_NOSYSTEM = '1'
git config --global user.name 'Rules Test'; git config --global user.email 'rules-test@example.invalid'; git config --global init.defaultBranch main
$pwshExe = (Get-Process -Id $PID).Path
$hook = Join-Path $repoRoot 'scripts\overlay_rules_hook.py'
$pythonExe = (Get-Command python, python3 -CommandType Application -ErrorAction Ignore | Select-Object -First 1).Source
$rulesText = "# Owner rules`n`n- The canary word is HERON.`n"
function New-Checkout([string]$name, [switch]$Unmarked, [switch]$NoRules) {
  $checkout = Join-Path $work $name
  git init -q $checkout
  $private = Join-Path $checkout '.private'
  git init -q $private
  if (-not $NoRules) { [IO.File]::WriteAllText((Join-Path $private 'AGENTS.md'), $rulesText) }
  if (-not $Unmarked) { git -C $private config --local bosun.overlay true }
  $checkout
}
# The rules the hook hands a session for a checkout, or $null when it hands none.
function Get-Rules([string]$checkout) {
  $r = Invoke-Hook $checkout
  if (-not $r.Out.Trim()) { return $null }
  $context = ($r.Out | ConvertFrom-Json).hookSpecificOutput.additionalContext
  $context.Substring($context.IndexOf("`n`n") + 2)
}
function Invoke-Hook([string]$checkout) {
  $out = Join-Path $work 'hook-out.txt'; $err = Join-Path $work 'hook-err.txt'
  $env:CLAUDE_PROJECT_DIR = $checkout
  try { $p = Start-Process -FilePath $pythonExe -ArgumentList "`"$hook`"" -RedirectStandardOutput $out -RedirectStandardError $err -NoNewWindow -PassThru -Wait }
  finally { Remove-Item -LiteralPath Env:CLAUDE_PROJECT_DIR -ErrorAction Ignore }
  [pscustomobject]@{ Exit = $p.ExitCode; Out = [IO.File]::ReadAllText($out); Err = [IO.File]::ReadAllText($err) }
}

try {
  # --- what is trusted ---
  $marked = New-Checkout 'marked'
  Check 'the marked overlay''s own AGENTS.md is read as written' ((Get-Rules $marked) -ceq $rulesText)
  Check 'an unmarked overlay is not trusted' ($null -eq (Get-Rules (New-Checkout 'unmarked' -Unmarked)))
  Check 'a marked overlay without AGENTS.md has no rules' ($null -eq (Get-Rules (New-Checkout 'norules' -NoRules)))
  $absent = Join-Path $work 'absent'; git init -q $absent
  Check 'a checkout without .private has no rules' ($null -eq (Get-Rules $absent))
  $plain = Join-Path $work 'plain'; git init -q $plain
  New-Item -ItemType Directory -Path (Join-Path $plain '.private') | Out-Null
  [IO.File]::WriteAllText((Join-Path $plain '.private\AGENTS.md'), $rulesText)
  Check 'a plain folder at .private is not trusted' ($null -eq (Get-Rules $plain))
  $joined = Join-Path $work 'joined'; git init -q $joined
  New-Item -ItemType Junction -Path (Join-Path $joined '.private') -Target (Join-Path $marked '.private') | Out-Null
  Check 'a junction at .private to another checkout''s marked overlay is not trusted' ($null -eq (Get-Rules $joined))
  # An empty .git folder in .private makes git answer for the checkout around it; that checkout's own mark must not count.
  $hollow = Join-Path $work 'hollow'; git init -q $hollow
  New-Item -ItemType Directory -Path (Join-Path $hollow '.private\.git') -Force | Out-Null
  [IO.File]::WriteAllText((Join-Path $hollow '.private\AGENTS.md'), $rulesText)
  git -C $hollow config --local bosun.overlay true
  Check 'a .private whose .git is not its own repository is not trusted' ($null -eq (Get-Rules $hollow))
  $linked = New-Checkout 'linked' -NoRules
  $elsewhere = Join-Path $work 'elsewhere.md'; [IO.File]::WriteAllText($elsewhere, $rulesText)
  New-Item -ItemType SymbolicLink -Path (Join-Path $linked '.private\AGENTS.md') -Target $elsewhere | Out-Null
  Check 'an AGENTS.md linked to a file outside the clone is not trusted' ($null -eq (Get-Rules $linked))
  $bom = New-Checkout 'bom' -NoRules
  [IO.File]::WriteAllText((Join-Path $bom '.private\AGENTS.md'), $rulesText, [Text.UTF8Encoding]::new($true))
  Check 'a file saved with a byte-order mark reads as its text' ((Get-Rules $bom) -ceq $rulesText)

  # --- the SessionStart hook, run as Claude Code runs it ---
  $r = Invoke-Hook $marked
  $context = try { ($r.Out | ConvertFrom-Json).hookSpecificOutput } catch { $null }
  Check 'for the marked overlay the hook hands the rules over as SessionStart context' ($r.Exit -eq 0 -and $context.hookEventName -eq 'SessionStart' -and $context.additionalContext.EndsWith($rulesText) -and $context.additionalContext -match '\.private/AGENTS\.md') "(exit $($r.Exit): $($r.Out) $($r.Err))"
  foreach ($case in @(@('unmarked', (Join-Path $work 'unmarked')), @('a junction', $joined), @('a linked file', $linked), @('no .private', $absent))) {
    $r = Invoke-Hook $case[1]
    Check "for $($case[0]) the hook gives nothing and exits 0" ($r.Exit -eq 0 -and -not $r.Out.Trim()) "(exit $($r.Exit): $($r.Out))"
  }
  $lock = [IO.File]::Open((Join-Path $marked '.private\AGENTS.md'), 'Open', 'Read', 'None')
  try { $r = Invoke-Hook $marked } finally { $lock.Dispose() }
  Check 'rules that cannot be read are a note, never a blocked session' ($r.Exit -eq 0 -and -not $r.Out.Trim() -and $r.Err -match 'could not be read') "(exit $($r.Exit): $($r.Out) $($r.Err))"
  Check 'and the hook writes nothing into the checkout' (-not (Test-Path -LiteralPath (Join-Path $marked 'CLAUDE.local.md')) -and @(Get-ChildItem -LiteralPath $marked -Force | Where-Object { $_.Name -notin '.git', '.private' }).Count -eq 0)

  # --- setup's printed undo of the mark, run from another folder, undoes it in the checkout setup was given ---
  $source = Join-Path $work 'overlay-source.git'
  git init -q --bare $source
  $staging = Join-Path $work 'overlay-staging'; git clone -q $source $staging 2>$null
  [IO.File]::WriteAllText((Join-Path $staging 'AGENTS.md'), $rulesText); git -C $staging add -A; git -C $staging commit -q -m rules; git -C $staging push -q origin main 2>$null
  $target = Join-Path $work 'target'
  git init -q $target
  Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts') -Destination (Join-Path $target 'scripts') -Recurse
  foreach ($hookName in 'pre-commit', 'post-merge', 'pre-push') { git -C $target update-index --add --chmod=+x "scripts/hooks/$hookName" 2>$null }
  git clone -q $source (Join-Path $target '.private') 2>$null
  $env:BOSUN_OVERLAY_REPO = $source
  $said = & $pwshExe -NoProfile -File (Join-Path $target 'scripts\setup-clone.ps1') -Repo $target 2>&1 | Out-String
  $undo = ([regex]::Match($said, 'to undo the mark only \(the checkout stays as it is\): (.+)')).Groups[1].Value.Trim()
  Check 'setup marks the overlay and prints an undo naming that checkout''s .private' ($said -match 'marked it as one' -and $undo.Contains((Join-Path $target '.private'))) "($said)"
  Push-Location -LiteralPath $work
  try { Invoke-Expression $undo | Out-Null } finally { Pop-Location }
  Check 'the undo run as printed from another folder removes that checkout''s mark' ("$(git -C (Join-Path $target '.private') config --local --get bosun.overlay)" -ne 'true' -and $null -eq (Get-Rules $target))
} finally {
  foreach ($name in $changed) { if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction Ignore } else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] } }
  Remove-Item -LiteralPath $work -Recurse -Force
}
if ($failed) { "$failed check(s) failed"; exit 1 }
'all checks passed'
