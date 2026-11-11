# Replaces an installed exe that may be running, used by scripts/install.ps1.
#
# Every `bosun run` another repo has in flight holds bosun.exe open, so copying over it fails. Windows lets a running
# exe be renamed, though: the new file is copied next to it, the running one is renamed aside, and the new one takes
# its name. A job already running keeps its own image to the end, and the next `bosun run` starts the new one. Old
# images are removed once nothing holds them.
function Install-SwapFile([string]$Source, [string]$Target) {
  $dir = Split-Path $Target
  $name = Split-Path $Target -Leaf
  Remove-SwappedFile $Target

  $new = "$Target.new"
  Copy-Item -LiteralPath $Source -Destination $new -Force
  if (Test-Path -LiteralPath $Target) {
    $aside = "$Target.old-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))"
    Move-Item -LiteralPath $Target -Destination $aside
    try { Remove-Item -LiteralPath $aside -Force -ErrorAction Stop }
    catch [System.UnauthorizedAccessException], [System.IO.IOException] { }  # running: removed by a later install
  }
  Move-Item -LiteralPath $new -Destination $Target
}

# Removes the images Install-SwapFile set aside for $Target once nothing runs them; one still running stays for later.
function Remove-SwappedFile([string]$Target) {
  Get-ChildItem -LiteralPath (Split-Path $Target) -Filter "$(Split-Path $Target -Leaf).old-*" -ErrorAction Ignore | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }
    catch [System.UnauthorizedAccessException], [System.IO.IOException] { }  # still running: removed by a later install
  }
}