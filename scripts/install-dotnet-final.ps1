# The body of the `Bosun dotnet final` task, which admin setup registers to run as SYSTEM daily and at startup. It
# installs the final .NET 11 SDK into the folder the watchdog's dotnet.exe lives in, once Microsoft ships it, then
# removes its own task. Until then it changes nothing: `dotnet-install.ps1 -Channel 11.0 -Quality GA` would install a
# release candidate today, so the version comes from `latest-sdk` in the 11.0 releases.json, and only one with no
# prerelease suffix is installed. Runs under Windows PowerShell 5.1.
param(
  [Parameter(Mandatory)][string]$InstallDir,
  [string]$TaskName = 'Bosun dotnet final',
  [string]$TaskStore = '',
  [string]$Metadata = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json',
  [string]$Installer = 'https://dot.net/v1/dotnet-install.ps1',
  [string]$Dotnet = '',
  [string]$Log = (Join-Path $env:ProgramData 'Bosun\logs\dotnet-final.log')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\campaign.ps1')
if (-not $Dotnet) { $Dotnet = Join-Path $InstallDir 'dotnet.exe' }

function Say([string]$text) {
  $dir = Split-Path $Log
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
  Add-Content -LiteralPath $Log -Value "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) $text" -Encoding UTF8
  $text
}

function Sdks { ((& $Dotnet --list-sdks 2>&1) | Out-String) }

function Read-Text([string]$source) {
  if ($source -match '^https?://') { return (Invoke-WebRequest -UseBasicParsing -Uri $source -TimeoutSec 120).Content }
  [IO.File]::ReadAllText($source)
}

try {
  if (Test-FinalSdk (Sdks)) {
    Say "a final 11.0.1xx SDK is already in $InstallDir; removing $TaskName"
    Unregister-CampaignTask $TaskName $TaskStore
    exit 0
  }
  $latest = (Read-Text $Metadata | ConvertFrom-Json).'latest-sdk'
  if ($latest -notmatch '^11\.0\.1\d\d$') {
    Say "latest 11.0 SDK is $latest, a prerelease, nothing changed"
    exit 0
  }
  $script = $Installer
  if ($Installer -match '^https?://') {
    $script = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$PID.ps1"
    Invoke-WebRequest -UseBasicParsing -Uri $Installer -OutFile $script -TimeoutSec 120
  }
  Say "installing SDK $latest into $InstallDir"
  $out = & $WindowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script -Version $latest -InstallDir $InstallDir 2>&1 | Out-String
  $code = $LASTEXITCODE
  if ($code -ne 0) {
    Say "install of $latest failed (exit $code), keeping $TaskName for the next run: $($out.Trim() -replace '\s+', ' ')"
    exit 1
  }
  # Only the SDK list of that dotnet.exe counts: an installer that says it worked isn't proof.
  if (-not (Test-FinalSdk (Sdks))) {
    Say "after installing $latest, $Dotnet --list-sdks shows no final 11.0.1xx SDK; keeping $TaskName"
    exit 1
  }
  Say "installed $latest; removing $TaskName"
  Unregister-CampaignTask $TaskName $TaskStore
  exit 0
} catch {
  Say "failed, keeping $TaskName for the next run: $($_.Exception.Message)"
  exit 1
}
