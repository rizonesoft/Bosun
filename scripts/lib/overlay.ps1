# The one lookup order for an owner value (D00 T05 §1). Dot-source this file and call Get-BosunOverlayValue.
#
# A value comes from, in order:
#   1. the tool's own environment variable (BOSUN_ plus the key in upper case with - as _, or the -Variable given), for a
#      one-off override; an empty variable counts as unset;
#   2. .private/config.toml in the checkout (the private overlay, rizonetech/Bosun-private), when it holds the key
#      with a non-empty value;
#   3. otherwise a setup-required result whose Message names the variable and the key.
# The file is opened only when the variable is unset, so a broken file never defeats an override.
#
# config.toml is read as the flat subset the overlay needs: blank lines, `#` comment lines, and
#   key = "basic string"   (escapes \" \\ \b \t \n \f \r \uXXXX \UXXXXXXXX)
#   key = 'literal string'
# each optionally followed by a `#` comment. Values are data: nothing in them is expanded. Any other line (a table
# header, an array, a number, a duplicate key) is refused with the file, the line and the reason, and the refusal
# never quotes the line, since a value on it may be a secret. Keys are case-sensitive, a raw control character other than
# tab is refused, and the file must be UTF-8. A file that exists but cannot be read, or is open for writing by another
# process for more than half a second, is refused too.

function ConvertFrom-BosunOverlayString([string]$Text, [string]$Where) {
  # Parses the quoted value at the start of $Text. Returns @(value, rest of the line).
  $quote = $Text[0]
  if ($quote -eq "'") {
    $end = $Text.IndexOf("'", 1)
    if ($end -lt 0) { throw "${Where}: the literal string has no closing quote" }
    return @($Text.Substring(1, $end - 1), $Text.Substring($end + 1))
  }
  $value = [Text.StringBuilder]::new()
  $i = 1
  while ($i -lt $Text.Length) {
    $c = $Text[$i]
    if ($c -eq '"') { return @($value.ToString(), $Text.Substring($i + 1)) }
    if ($c -ne '\') { [void]$value.Append($c); $i++; continue }
    if ($i + 1 -ge $Text.Length) { break }
    $e = $Text[$i + 1]
    $simple = @{ [char]'"' = '"'; [char]'\' = '\'; [char]'b' = "`b"; [char]'t' = "`t"; [char]'n' = "`n"; [char]'f' = "`f"; [char]'r' = "`r" }
    if ($simple.ContainsKey($e)) { [void]$value.Append($simple[$e]); $i += 2; continue }
    $digits = if ($e -ceq 'u') { 4 } elseif ($e -ceq 'U') { 8 } else { 0 }
    if ($digits -eq 0) { throw "${Where}: unknown escape in a basic string (allowed: \"" \\ \b \t \n \f \r \uXXXX \UXXXXXXXX)" }
    $hex = if ($i + 2 + $digits -le $Text.Length) { $Text.Substring($i + 2, $digits) } else { '' }
    if ($hex -notmatch '^[0-9A-Fa-f]+$' -or $hex.Length -ne $digits) { throw "${Where}: a \u escape needs 4 hex digits and a \U escape 8" }
    $code = [Convert]::ToInt64($hex, 16)
    if ($code -gt 0x10FFFF -or ($code -ge 0xD800 -and $code -le 0xDFFF)) { throw "${Where}: a \u or \U escape names no Unicode scalar value" }
    [void]$value.Append([char]::ConvertFromUtf32($code))
    $i += 2 + $digits
  }
  throw "${Where}: the basic string has no closing quote"
}

function Read-BosunOverlayConfig([string]$Path) {
  # Reads the whole file before returning anything, so a malformed line anywhere refuses the file.
  # Shared for reading only: a file another process has open for writing (a save in progress) is not read half
  # written. A save is short, so a sharing refusal is tried again a few times before the file is refused.
  # The loop is bounded by its attempts, and a successful read (an empty file included) ends it.
  $bytes = $null
  for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
      $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
      try {
        $buffer = [IO.MemoryStream]::new()
        $stream.CopyTo($buffer)
        $bytes = $buffer.ToArray()
      } finally { $stream.Dispose() }
      break
    } catch {
      $reason = $_.Exception.InnerException ?? $_.Exception
      # No file (or no .private folder) is the one case that is not an error: the caller reports setup-required.
      if ($reason -is [IO.FileNotFoundException] -or $reason -is [IO.DirectoryNotFoundException]) { return $null }
      $sharing = $reason -is [IO.IOException] -and ($reason.HResult -band 0xFFFF) -in 32, 33
      if (-not $sharing -or $attempt -ge 5) { throw "${Path}: cannot be read ($($reason.Message))" }
      Start-Sleep -Milliseconds 100
    }
  }
  # Strict UTF-8, line by line: a file saved in another encoding is refused at the line that holds the bad bytes,
  # never decoded into a changed value.
  $start = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
  $utf8 = [Text.UTF8Encoding]::new($false, $true)
  $lines = [Collections.Generic.List[string]]::new()
  while ($start -le $bytes.Length) {
    $end = [Array]::IndexOf($bytes, [byte]0x0A, $start)
    if ($end -lt 0) { $end = $bytes.Length }
    $length = $end - $start
    # A carriage return ends a line only as part of CRLF; a lone one (a file cut after its CR) stays and is refused.
    if ($end -lt $bytes.Length -and $length -gt 0 -and $bytes[$end - 1] -eq 0x0D) { $length-- }
    try { $lines.Add($utf8.GetString($bytes, $start, $length)) }
    catch [Text.DecoderFallbackException] { throw "${Path}:$($lines.Count + 1): is not valid UTF-8; save the file as UTF-8" }
    $start = $end + 1
  }
  # Keys compare as TOML compares them: exactly, so vault_item and VAULT_ITEM are two keys.
  $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
  # TOML whitespace is space and tab only; a no-break space or any other Unicode space is not trimmed away.
  $blank = [char[]](' ', "`t")
  $number = 0
  foreach ($line in $lines) {
    $number++
    $where = "${Path}:$number"
    # TOML allows no control character but tab anywhere in a line, a lone carriage return included.
    if ($line -match '[\x00-\x08\x0A-\x1F\x7F]') { throw "${where}: a control character is not allowed (only tab is)" }
    $trimmed = $line.Trim($blank)
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    if ($trimmed.StartsWith('[')) { throw "${where}: a table header is not allowed; the overlay config is flat key = 'value' lines" }
    if ($trimmed -cnotmatch '^([A-Za-z0-9_-]+)[ \t]*=[ \t]*(.*)$') { throw "${where}: expected key = `"value`"" }
    $key = $Matches[1]
    $rest = $Matches[2]
    if ($rest -eq '' -or ($rest[0] -ne '"' -and $rest[0] -ne "'")) {
      throw "${where}: the value of $key must be a quoted string (arrays, numbers, booleans and dates are not allowed)"
    }
    if ($rest.StartsWith('"""') -or $rest.StartsWith("'''")) { throw "${where}: multi-line strings are not allowed" }
    $parsed = ConvertFrom-BosunOverlayString -Text $rest -Where $where
    if ($parsed[1].Trim($blank) -ne '' -and -not $parsed[1].TrimStart($blank).StartsWith('#')) {
      throw "${where}: only a # comment may follow the value of $key"
    }
    if ($values.ContainsKey($key)) { throw "${where}: $key is set twice" }
    $values[$key] = $parsed[0]
  }
  # A dictionary is a collection, so it is returned whole rather than as its entries.
  ,$values
}

function Get-BosunOverlayValue {
  param(
    # The same key grammar as the file's, so every key the file accepts can be asked for.
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Key,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$Variable,
    [string]$Root,
    # Read the variable alone, never .private/config.toml: for a value that names or checks the overlay itself, which
    # the overlay's own file must not vouch for.
    [switch]$VariableOnly
  )
  # A hyphen cannot be in a variable name, so overlay-repo reads BOSUN_OVERLAY_REPO.
  if (-not $Variable) { $Variable = 'BOSUN_' + $Key.ToUpperInvariant().Replace('-', '_') }
  # The variable alone is enough: it is read before the root is looked at, so a root that cannot be resolved (a
  # disconnected drive) never defeats an override.
  $fromVariable = [Environment]::GetEnvironmentVariable($Variable)
  if ($fromVariable) {
    return [pscustomobject]@{ Value = $fromVariable; Source = 'variable'; Message = "$Key from `$env:$Variable" }
  }
  if ($VariableOnly) {
    return [pscustomobject]@{ Value = $null; Source = 'setup-required'; Message = "$Key is not set: set the $Variable environment variable" }
  }
  # The checkout that holds this file (scripts/lib/..), never the caller's working directory.
  if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
  # One absolute path for every use: .NET resolves a relative path against the process folder, PowerShell against its
  # own location, and the two differ after Set-Location.
  $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
  $config = Join-Path $Root '.private' 'config.toml'
  # Opened directly, not probed first: only a missing file is setup-required, and a folder named config.toml or a
  # path that cannot be read is refused with its reason.
  $values = Read-BosunOverlayConfig -Path $config
  if ($null -ne $values) {
    if ($values.ContainsKey($Key) -and $values[$Key] -ne '') {
      return [pscustomobject]@{ Value = $values[$Key]; Source = 'file'; Message = "$Key from $config" }
    }
  }
  [pscustomobject]@{
    Value   = $null
    Source  = 'setup-required'
    Message = "$Key is not set: set the $Variable environment variable, or add $Key = `"...`" to .private/config.toml (the private overlay)"
  }
}

# --- the overlay's own clone and its source (D00 T05 §2), shared by scripts/setup-clone.ps1 and scripts/sync.ps1 ---

function Get-OverlayOrigin([string]$folder) {
  $value = git -C $folder config --get remote.origin.url 2>$null
  if ($LASTEXITCODE) { $null } else { "$value" }
}
# Whether a clone's origin is the source asked for: the GitHub URL of owner/name in HTTPS or SSH form, the same
# resolved local path, or the same URL.
function Test-OverlayOrigin([string]$origin, [hashtable]$source) {
  if (-not $origin) { return $false }
  switch ($source.Kind) {
    'github' {
      $name = [regex]::Escape($source.Value)
      return $origin -match "^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)$name(\.git)?/?$"
    }
    'path' {
      if (-not [IO.Path]::IsPathRooted($origin)) { return $false }
      # Windows folders compare without case, as Windows opens them; elsewhere a path's case is part of its name.
      $compare = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
      return [string]::Equals([IO.Path]::GetFullPath($origin).TrimEnd('\', '/'), [IO.Path]::GetFullPath($source.Value).TrimEnd('\', '/'), $compare)
    }
    default { return $origin.TrimEnd('/') -ceq $source.Value.TrimEnd('/') }
  }
}
# The source forms setup accepts. Anything else is refused without repeating the value, which may hold a credential.
function Get-OverlaySource([string]$value) {
  if ($value -match '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$') { return @{ Kind = 'github'; Value = $value } }
  if ($value -match '^https://') {
    $uri = $null
    # No user information, query or fragment: each can carry a token, which git would keep in .private/.git/config.
    if ([Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment -and $value -notmatch '[\s?#@]') { return @{ Kind = 'url'; Value = $value } }
    return $null
  }
  # An SSH user name is allowed, a password (user:secret@) is not.
  if ($value -match '^ssh://([A-Za-z0-9._-]+@)?[A-Za-z0-9.-]+(:\d+)?/[^\s?#]+$' -or $value -match '^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s?#:@][^\s?#@]*$') { return @{ Kind = 'url'; Value = $value } }
  if (-not $value.StartsWith('-') -and [IO.Path]::IsPathFullyQualified($value) -and (Test-Path -LiteralPath $value -PathType Container)) {
    return @{ Kind = 'path'; Value = (Resolve-Path -LiteralPath $value).ProviderPath }
  }
  $null
}

# The one rule for what counts as the overlay's own clone, in setup, sync and scripts/hooks/post-merge: .private and its .git
# are real folders at their own paths (no link, junction, or .git file of a linked worktree or submodule), and git
# reports that .git as both the clone's git folder and its common one. Anything else may belong to another checkout,
# whose config would take the mark: it is never marked or pulled.
function Get-OverlayCloneFault([string]$folder) {
  foreach ($path in $folder, (Join-Path $folder '.git')) {
    $entry = Get-Item -LiteralPath $path -Force -ErrorAction Ignore
    if (-not $entry) { return '.private is a folder that is not a git checkout' }
    if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { return "$(if ($path -eq $folder) { '.private' } else { '.private/.git' }) is a link to another folder" }
    if (-not $entry.PSIsContainer) { return "$(if ($path -eq $folder) { '.private is a file' } else { '.private is a linked worktree or submodule' }), not a clone of its own" }
  }
  $own = [IO.Path]::GetFullPath((Join-Path $folder '.git')).TrimEnd('\', '/')
  foreach ($ask in '--absolute-git-dir', '--git-common-dir') {
    $said = "$(git -C $folder rev-parse --path-format=absolute $ask 2>$null)"
    if ($LASTEXITCODE -or -not $said -or [IO.Path]::GetFullPath($said).TrimEnd('\', '/') -ne $own) { return '.private is not a git clone of its own' }
  }
  $null
}

# --- the overlay's agent rules (D00 T05 §5) ---
# Claude Code reads the overlay's rules through an import kept in the checkout's ignored CLAUDE.local.md, between two
# marker lines, only while .private is the overlay's own clone (Get-OverlayCloneFault), marked by setup and holding
# AGENTS.md: an unrelated folder at .private is never trusted. Setup, scripts/hooks/post-merge and scripts/sync.ps1 call
# this after their work, so the import follows the overlay as it gains or loses its rules. Any other text in the file
# is kept byte for byte, a file left empty is removed, and a write is a temporary file renamed over the old one.
# Returns what it did: 'added', 'removed' or 'unchanged'.
$script:BosunRulesStart = '<!-- bosun-overlay-rules: written by scripts/setup-clone.ps1; do not edit between these lines -->'
$script:BosunRulesEnd = '<!-- /bosun-overlay-rules -->'
function Update-BosunAgentRules([string]$Repo) {
  $private = Join-Path $Repo '.private'
  $wanted = (Test-Path -LiteralPath (Join-Path $private 'AGENTS.md') -PathType Leaf) -and -not (Get-OverlayCloneFault $private) -and
    ("$(git -C $private config --local --get bosun.overlay 2>$null)" -eq 'true')
  $file = Join-Path $Repo 'CLAUDE.local.md'
  $text = if (Test-Path -LiteralPath $file -PathType Leaf) { [IO.File]::ReadAllText($file) } else { '' }
  # What this function wrote is taken out line by line, never by a span between markers: every marker line, and the
  # import line that follows an opening marker. A block whose closing line was lost therefore loses its import too,
  # and every other line, the owner's own, is kept as written, line breaks included.
  $kept = [Text.StringBuilder]::new()
  $afterStart = $false
  foreach ($line in [regex]::Split($text, '(?<=\n)')) {
    if (-not $line) { continue }
    $bare = $line.TrimEnd("`r", "`n")
    if ($bare -ceq $script:BosunRulesStart) { $afterStart = $true; continue }
    if ($bare -ceq $script:BosunRulesEnd) { $afterStart = $false; continue }
    if ($afterStart -and $bare -ceq '@.private/AGENTS.md') { $afterStart = $false; continue }
    $afterStart = $false
    [void]$kept.Append($line)
  }
  $without = $kept.ToString()
  # The block goes first, each of its lines ending in its own line break, so the owner's text after it is never
  # touched (a last line without a line break stays so) and taking the block out gives the file back byte for byte.
  $new = if ($wanted) { "$($script:BosunRulesStart)`n@.private/AGENTS.md`n$($script:BosunRulesEnd)`n$without" } else { $without }
  if ($new -ceq $text) { return 'unchanged' }
  if (-not $new.Trim()) { Remove-Item -LiteralPath $file -Force; return 'removed' }
  # The temporary file is ignored as CLAUDE.local.md is (.gitignore: CLAUDE.local.md*), and removed if the rename fails.
  $temp = "$file.tmp-$PID"
  try {
    [IO.File]::WriteAllText($temp, $new, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $file -Force
  } finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction Ignore }
  }
  if ($wanted) { 'added' } else { 'removed' }
}