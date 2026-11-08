# The body of the `Bosun attach D` task, which admin setup registers to run as SYSTEM at startup: D: is the ReFS
# volume inside a VHDX, and nothing else attaches it at boot. Attaching one that is already attached is skipped, so
# this is harmless when Windows has done it. Runs under Windows PowerShell 5.1.
param(
  [Parameter(Mandatory)][string]$Path,
  [string]$Log = (Join-Path $env:ProgramData 'Bosun\logs\attach-vhdx.log')
)
$ErrorActionPreference = 'Stop'
function Say([string]$text) {
  $dir = Split-Path $Log
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
  Add-Content -LiteralPath $Log -Value "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) $text" -Encoding UTF8
}
try {
  # The disk holding the VHDX may come up a little after the task starts.
  for ($i = 0; $i -lt 30 -and -not (Test-Path -LiteralPath $Path); $i++) { Start-Sleep -Seconds 2 }
  if ((Get-DiskImage -ImagePath $Path).Attached) { Say "$Path already attached"; exit 0 }
  Mount-DiskImage -ImagePath $Path | Out-Null
  $letters = (Get-DiskImage -ImagePath $Path | Get-Disk | Get-Partition | Where-Object DriveLetter).DriveLetter -join ','
  Say "attached $Path as $letters"
} catch {
  Say "failed to attach ${Path}: $($_.Exception.Message)"
  exit 1
}
