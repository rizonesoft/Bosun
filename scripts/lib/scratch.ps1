# Shared scratch-folder helper for PowerShell tests (D00 T07 §3).
# Dot-source this file and call New-BosunScratchFolder -Name '<test-name>'.
#
# Resolves BOSUN_SCRATCH when set, non-whitespace and an existing directory; otherwise falls back to the system
# temporary folder. Once a root is selected, a child creation failure throws naming that root and never falls back
# to system temp. The caller removes the returned directory in its cleanup block.

function Get-BosunScratchRoot {
  $raw = $env:BOSUN_SCRATCH
  if (-not [string]::IsNullOrWhiteSpace($raw)) {
    $trimmed = $raw.Trim()
    if (Test-Path -LiteralPath $trimmed -PathType Container) {
      return [IO.Path]::GetFullPath($trimmed)
    }
  }
  return [IO.Path]::GetTempPath()
}

function New-BosunScratchFolder {
  param(
    [string]$Name = 'test'
  )
  if ([string]::IsNullOrEmpty($Name) -or $Name -eq '.' -or $Name -eq '..' -or $Name -notmatch '^[A-Za-z0-9._-]+$') {
    throw "New-BosunScratchFolder: invalid name '$Name' (allowed: A-Z, a-z, 0-9, ., _, -)"
  }
  $root = Get-BosunScratchRoot
  $target = Join-Path $root ("bosun-$Name-" + [guid]::NewGuid().ToString('N'))
  try {
    $null = [IO.Directory]::CreateDirectory($target)
    return $target
  } catch {
    throw "New-BosunScratchFolder: could not create scratch folder under '$root': $($_.Exception.Message)"
  }
}
