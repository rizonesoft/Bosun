# Tests Install-SwapFile (scripts/lib/swap-file.ps1): an exe that is running is replaced without stopping it.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\swap-file.ps1')
$work = Join-Path ([IO.Path]::GetTempPath()) "bosun-swap-test-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$failed = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:failed++ } }
$proc = $null
try {
  # A running copy of a real exe stands in for a `bosun run` in flight.
  $target = Join-Path $work 'tool.exe'
  Copy-Item (Join-Path $env:SystemRoot 'System32\ping.exe') $target
  $proc = Start-Process $target -ArgumentList '-n', '30', '127.0.0.1' -WindowStyle Hidden -PassThru
  Start-Sleep -Milliseconds 300
  $source = Join-Path $work 'source.exe'
  Copy-Item (Join-Path $env:SystemRoot 'System32\where.exe') $source
  Install-SwapFile $source $target
  Check ((Get-FileHash $target).Hash -eq (Get-FileHash $source).Hash) 'the new exe takes the name while the old one runs'
  Check (-not $proc.HasExited) 'the running job is not stopped'
  Check (@(Get-ChildItem $work -Filter 'tool.exe.old-*').Count -eq 1) 'the running image is kept aside'
  $proc.Kill(); $proc.WaitForExit()
  Install-SwapFile $source $target
  Check (@(Get-ChildItem $work -Filter 'tool.exe.old-*').Count -eq 0) 'old images are removed once nothing holds them'
  Check (-not (Test-Path "$target.new")) 'no staging file is left'
} finally {
  if ($proc -and -not $proc.HasExited) { $proc.Kill() }
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failed) { exit 1 }
