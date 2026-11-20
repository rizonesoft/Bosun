# Tests Get-BosunOverlayValue (scripts/lib/overlay.ps1), the one lookup order for an owner value: the BOSUN_*
# variable, then .private/config.toml, then setup-required. The helper is copied into a temporary checkout and
# called from another working directory without -Root, so a helper that read the caller's folder would fail here.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\scripts\lib\overlay.ps1'
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$dir = Join-Path $scratch ("bosun-overlay-test-" + [guid]::NewGuid())
$checkout = Join-Path $dir 'checkout'
$elsewhere = Join-Path $dir 'elsewhere'
New-Item -ItemType Directory -Path (Join-Path $checkout 'scripts\lib'), $elsewhere | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $checkout 'scripts\lib\overlay.ps1')
. (Join-Path $checkout 'scripts\lib\overlay.ps1')
$private = Join-Path $checkout '.private'
$config = Join-Path $private 'config.toml'
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "ok   $name" } else { "FAIL $name $detail"; $script:failed++ }
}
function Set-Config([string]$Text) {
  New-Item -ItemType Directory -Force -Path $private | Out-Null
  [IO.File]::WriteAllText($config, $Text, [Text.UTF8Encoding]::new($false))
}
function Get-Refusal([scriptblock]$Call) {
  try { & $Call | Out-Null; $null } catch { $_.Exception.Message }
}
$saved = @{}
foreach ($name in 'BOSUN_VAULT_ITEM', 'BOSUN_OTHER_NAME') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
function Clear-Vars { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $null) } }

Push-Location $elsewhere
try {
  Clear-Vars
  # A decoy config in the working directory: a helper that read the caller's folder would find it.
  New-Item -ItemType Directory -Path (Join-Path $elsewhere '.private') | Out-Null
  Set-Content -LiteralPath (Join-Path $elsewhere '.private\config.toml') -Value 'vault_item = "from the working directory"'

  $r = Get-BosunOverlayValue -Key vault_item
  Check 'no .private at all is setup-required' ($r.Source -eq 'setup-required' -and $null -eq $r.Value) "($($r.Source) $($r.Value))"
  Check 'setup-required names the variable and the key' ($r.Message -match 'BOSUN_VAULT_ITEM' -and $r.Message -match 'vault_item') $r.Message

  New-Item -ItemType Directory -Path $private | Out-Null
  $r = Get-BosunOverlayValue -Key vault_item
  Check '.private without config.toml is setup-required' ($r.Source -eq 'setup-required') $r.Source

  Set-Config "# owner values`nvault_item = `"op://vault/item/field`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'the file is read from the checkout, not the working directory' ($r.Source -eq 'file' -and $r.Value -eq 'op://vault/item/field') "($($r.Source) $($r.Value))"

  $env:BOSUN_VAULT_ITEM = 'from the variable'
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'the variable wins over the file' ($r.Source -eq 'variable' -and $r.Value -eq 'from the variable') "($($r.Source) $($r.Value))"

  $env:BOSUN_OTHER_NAME = 'from the custom variable'
  $r = Get-BosunOverlayValue -Key vault_item -Variable BOSUN_OTHER_NAME
  Check 'a custom -Variable beats the derived one' ($r.Source -eq 'variable' -and $r.Value -eq 'from the custom variable') "($($r.Source) $($r.Value))"

  $env:BOSUN_VAULT_ITEM = ''
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'an empty variable counts as unset' ($r.Source -eq 'file' -and $r.Value -eq 'op://vault/item/field') "($($r.Source) $($r.Value))"
  Clear-Vars

  # An empty file holds no key: setup-required, at once (round 2 found the read retried an empty file forever).
  Set-Config ''
  # In a child process, so a hang is stopped (with its whole tree) at 10 s and fails the case instead of the run.
  $probe = Join-Path $dir 'empty-probe.ps1'
  $answer = Join-Path $dir 'empty-probe.txt'
  Set-Content -LiteralPath $probe -Value "param(`$lib, `$out) . `$lib; (Get-BosunOverlayValue -Key vault_item).Source | Set-Content -LiteralPath `$out"
  $child = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $probe, (Join-Path $checkout 'scripts\lib\overlay.ps1'), $answer -NoNewWindow -PassThru
  $done = $child.WaitForExit(10000)
  if (-not $done) { taskkill /T /F /PID $child.Id | Out-Null; $child.WaitForExit() }
  $source = if (Test-Path -LiteralPath $answer) { (Get-Content -LiteralPath $answer -Raw).Trim() } else { '' }
  Check 'an empty file is setup-required, without hanging' ($done -and $source -eq 'setup-required') "($(if ($done) { $source } else { 'still running after 10 s, stopped' }))"

  Set-Config "other = `"x`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'a key missing from the file is setup-required' ($r.Source -eq 'setup-required' -and $null -eq $r.Value) $r.Source

  Set-Config "vault_item = `"`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'an empty value in the file is setup-required' ($r.Source -eq 'setup-required' -and $null -eq $r.Value -and $r.Message -match 'BOSUN_VAULT_ITEM') "($($r.Source) $($r.Value))"

  Set-Config 'vault_item = "a\"b\\c\td\ne\u00e9\U0001F600"'
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'basic-string escapes are decoded' ($r.Value -eq "a`"b\c`td`ne$([char]0xE9)$([char]::ConvertFromUtf32(0x1F600))") $r.Value

  Set-Config "vault_item = 'C:\no\escapes \n here'"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'a literal string is taken as written' ($r.Value -eq 'C:\no\escapes \n here') $r.Value

  Set-Config 'vault_item = "$env:PATH $(1+1) # = still data"   # a trailing comment'
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'values are data, and a trailing comment is allowed' ($r.Value -eq '$env:PATH $(1+1) # = still data') $r.Value

  $refused = [ordered]@{
    'a table header'         = "[section]`nvault_item = `"x`""
    'an array'               = 'vault_item = ["x"]'
    'a number'               = 'vault_item = 3'
    'a bare word'            = 'vault_item = x'
    'a duplicate key'        = "vault_item = `"secret-one`"`nvault_item = `"secret-two`""
    'text after the value'   = 'vault_item = "x" y'
    'an unknown escape'      = 'vault_item = "secret\qb"'
    'an upper-case escape'   = 'vault_item = "a\Bb"'
    'a short \u escape'      = 'vault_item = "a\u12"'
    'an unclosed string'     = 'vault_item = "secret-value'
    'a multi-line string'    = 'vault_item = """x"""'
    'a line with no ='       = 'vault_item'
  }
  foreach ($case in $refused.GetEnumerator()) {
    Set-Config $case.Value
    $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
    $line = if ($case.Key -eq 'a duplicate key') { 2 } else { 1 }
    Check "$($case.Key) is refused with the file and line" ($message -and $message.Contains("${config}:$line")) "($message)"
    Check "$($case.Key) refusal quotes no value" ($message -and $message -notmatch 'secret') "($message)"
  }

  Set-Config "vault_item = `"good`"`nbroken line with secret-value`n"
  $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
  Check 'a malformed line after the requested key refuses the file' ($message -and $message.Contains("${config}:2") -and $message -notmatch 'secret') "($message)"

  $env:BOSUN_VAULT_ITEM = 'override'
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'the variable still answers when the file is malformed' ($r.Source -eq 'variable' -and $r.Value -eq 'override') "($($r.Source) $($r.Value))"
  Clear-Vars

  Set-Config "vault_item = `"good`"`n"
  $lock = [IO.File]::Open($config, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
  try {
    $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
    Check 'a locked file is refused with its path and the reason' ($message -and $message.Contains($config) -and $message -match 'cannot be read') "($message)"
  } finally { $lock.Dispose() }

  # A save in progress: another handle has the file open for writing, so the lookup refuses rather than read it
  # half written.
  $writer = [IO.File]::Open($config, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
  try {
    $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
    Check 'a file open for writing elsewhere is refused, not read half written' ($message -and $message -match 'cannot be read') "($message)"
  } finally { $writer.Dispose() }
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'the same file reads once the writer has closed it' ($r.Source -eq 'file' -and $r.Value -eq 'good') "($($r.Source) $($r.Value))"

  New-Item -ItemType Directory -Force -Path $private | Out-Null
  [IO.File]::WriteAllBytes($config, [byte[]](([Text.Encoding]::ASCII.GetBytes("vault_item = 'caf")) + 0xE9 + [byte][char]"'"))
  $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
  Check 'a file that is not UTF-8 is refused, not decoded into a changed value' ($message -and $message -match 'not valid UTF-8') "($message)"

  [IO.File]::WriteAllBytes($config, [byte[]](@(0xEF, 0xBB, 0xBF) + [Text.Encoding]::UTF8.GetBytes("vault_item = 'caf$([char]0xE9)'")))
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'a UTF-8 file with a byte order mark reads' ($r.Value -eq "caf$([char]0xE9)") $r.Value

  Set-Config "VAULT_ITEM = `"other`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'keys are case-sensitive' ($r.Source -eq 'setup-required') "($($r.Source) $($r.Value))"
  Set-Config "VAULT_ITEM = `"other`"`nvault_item = `"mine`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'two spellings of a key are two keys' ($r.Source -eq 'file' -and $r.Value -eq 'mine') "($($r.Source) $($r.Value))"

  $controls = [ordered]@{ 'a raw carriage return in a basic string' = "vault_item = `"secret`rvalue`""; 'a raw carriage return in a literal string' = "vault_item = 'secret`rvalue'"; 'a raw control character' = "vault_item = `"secret$([char]1)value`"" }
  foreach ($case in $controls.GetEnumerator()) {
    Set-Config $case.Value
    $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
    Check "$($case.Key) is refused with the file and line" ($message -and $message.Contains("${config}:1") -and $message -notmatch 'secret') "($message)"
  }
  Set-Config "vault_item = `"tab`there`"`n"
  $r = Get-BosunOverlayValue -Key vault_item
  Check 'a raw tab is allowed' ($r.Value -eq "tab`there") $r.Value

  foreach ($escape in '\UFFFFFFFF', '\U00110000', '\uD800') {
    Set-Config "vault_item = `"$escape`""
    $message = Get-Refusal { Get-BosunOverlayValue -Key vault_item }
    Check "$escape is refused with the file and line" ($message -and $message.Contains("${config}:1") -and $message -match 'Unicode scalar') "($message)"
  }

  $message = Get-Refusal { Get-BosunOverlayValue -Key 'bad key' }
  Check 'a key that is not a plain name is refused' ([bool]$message) ''
} finally {
  Pop-Location
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
  Remove-Item -LiteralPath $dir -Recurse -Force
}

if ($failed) { "$failed failed"; exit 1 }
'all passed'
