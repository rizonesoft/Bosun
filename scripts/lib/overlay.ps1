# The one lookup order for an owner value (D00 T05 §1). Dot-source this file and call Get-BosunOverlayValue.
#
# A value comes from, in order:
#   1. the tool's own environment variable (BOSUN_ plus the key in upper case, or the -Variable given), for a
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
      $sharing = $reason -is [IO.IOException] -and ($reason.HResult -band 0xFFFF) -in 32, 33
      if (-not $sharing -or $attempt -ge 5) { throw "${Path}: cannot be read ($($reason.Message))" }
      Start-Sleep -Milliseconds 100
    }
  }
  # Strict UTF-8: a file saved in another encoding is refused, never decoded into a changed value.
  $start = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
  try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, $start, $bytes.Length - $start) }
  catch [Text.DecoderFallbackException] { throw "${Path}: is not valid UTF-8; save it as UTF-8" }
  # Keys compare as TOML compares them: exactly, so vault_item and VAULT_ITEM are two keys.
  $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
  $number = 0
  foreach ($line in ($text -split "`r?`n")) {
    $number++
    $where = "${Path}:$number"
    # TOML allows no control character but tab anywhere in a line, a lone carriage return included.
    if ($line -match '[\x00-\x08\x0A-\x1F\x7F]') { throw "${where}: a control character is not allowed (only tab is)" }
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    if ($trimmed.StartsWith('[')) { throw "${where}: a table header is not allowed; the overlay config is flat key = 'value' lines" }
    if ($trimmed -notmatch '^([A-Za-z0-9_-]+)\s*=\s*(.*)$') { throw "${where}: expected key = `"value`"" }
    $key = $Matches[1]
    $rest = $Matches[2]
    if ($rest -eq '' -or ($rest[0] -ne '"' -and $rest[0] -ne "'")) {
      throw "${where}: the value of $key must be a quoted string (arrays, numbers, booleans and dates are not allowed)"
    }
    if ($rest.StartsWith('"""') -or $rest.StartsWith("'''")) { throw "${where}: multi-line strings are not allowed" }
    $parsed = ConvertFrom-BosunOverlayString -Text $rest -Where $where
    if ($parsed[1].Trim() -ne '' -and -not $parsed[1].TrimStart().StartsWith('#')) {
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
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_]+$')][string]$Key,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$Variable,
    [string]$Root
  )
  if (-not $Variable) { $Variable = 'BOSUN_' + $Key.ToUpperInvariant() }
  # The checkout that holds this file (scripts/lib/..), never the caller's working directory.
  if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
  # One absolute path for every use: .NET resolves a relative path against the process folder, PowerShell against its
  # own location, and the two differ after Set-Location.
  $Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)

  $fromVariable = [Environment]::GetEnvironmentVariable($Variable)
  if ($fromVariable) {
    return [pscustomobject]@{ Value = $fromVariable; Source = 'variable'; Message = "$Key from `$env:$Variable" }
  }
  $config = Join-Path $Root '.private' 'config.toml'
  if (Test-Path -LiteralPath $config -PathType Leaf) {
    $values = Read-BosunOverlayConfig -Path $config
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
