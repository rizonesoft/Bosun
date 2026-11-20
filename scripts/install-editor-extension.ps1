<#
.SYNOPSIS
  Packs Bosun's window reporter (editors/bosun-windows) as a VSIX and installs it into each VS Code-family editor found.

.DESCRIPTION
  The window reporter tells Bosun which folders each open editor window holds (D03 T02 §4): VS Code keeps no record of
  its open windows outside itself, so the Instances page can only place an editor by the folder it was launched from
  until the reporter runs in it. The VSIX is built here (the zip of [Content_Types].xml, extension.vsixmanifest and
  extension/), so no packaging tool is needed. Each editor's own CLI installs it (`--install-extension`); the script
  never starts, restarts or reloads an editor.

.PARAMETER Uninstall
  Removes the reporter from each editor found instead.

.PARAMETER PackOnly
  Builds the VSIX and installs nothing.

.PARAMETER OutFile
  Where the VSIX is written; build\editor-extension\bosun-windows-<version>.vsix by default.

.PARAMETER Cli
  The editor CLIs to use; by default each of code, code-insiders and antigravity-ide that is on the PATH.
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$PackOnly,
    [string]$OutFile = '',
    [string[]]$Cli = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$repo = Split-Path $PSScriptRoot
$source = Join-Path $repo 'editors\bosun-windows'
$package = Get-Content -LiteralPath (Join-Path $source 'package.json') -Raw | ConvertFrom-Json
$id = "$($package.publisher).$($package.name)"
# The files the extension runs from: never its tests.
$files = @('package.json', 'extension.js', 'record.js')

function Escape-Xml([string]$text) { [Security.SecurityElement]::Escape($text) }

function New-Vsix([string]$path) {
    $folder = Split-Path $path
    if ($folder) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Language="en-US" Id="$(Escape-Xml $package.name)" Version="$(Escape-Xml $package.version)" Publisher="$(Escape-Xml $package.publisher)" />
    <DisplayName>$(Escape-Xml $package.displayName)</DisplayName>
    <Description xml:space="preserve">$(Escape-Xml $package.description)</Description>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$(Escape-Xml $package.engines.vscode)" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="$(Escape-Xml ($package.extensionKind -join ','))" />
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
  </Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
  </Assets>
</PackageManifest>
"@
    $types = '<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension=".json" ContentType="application/json" /><Default Extension=".js" ContentType="application/javascript" /><Default Extension=".vsixmanifest" ContentType="text/xml" /></Types>'
    # Written beside its place and moved in whole, so an install never reads a half-written package.
    $temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            function Add-Text([string]$name, [string]$text) {
                $entry = $zip.CreateEntry($name)
                $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
                try { $writer.Write($text) } finally { $writer.Dispose() }
            }
            Add-Text '[Content_Types].xml' $types
            Add-Text 'extension.vsixmanifest' $manifest
            foreach ($file in $files) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $source $file), "extension/$file") }
        } finally { $zip.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    $path
}

if (-not $OutFile) { $OutFile = Join-Path $repo "build\editor-extension\bosun-windows-$($package.version).vsix" }
$vsix = if ($Uninstall) { $null } else { New-Vsix $OutFile }
if ($vsix) { "packed $vsix" }
if ($PackOnly) { exit 0 }

$clis = if ($Cli) { $Cli } else { @('code', 'code-insiders', 'antigravity-ide') | Where-Object { Get-Command $_ -CommandType Application, ExternalScript -ErrorAction Ignore } }
if (-not $clis) { 'no VS Code-family editor CLI was found (code, code-insiders, antigravity-ide), so nothing was installed'; exit 1 }

$failed = 0
foreach ($c in $clis) {
    $output = if ($Uninstall) { & $c --uninstall-extension $id 2>&1 } else { & $c --install-extension $vsix --force 2>&1 }
    $code = $LASTEXITCODE
    # The editor's own listing is the proof either way, so a listing that fails proves nothing: an uninstall is done only
    # when the listing ran and no longer names it (an editor that never had it may refuse the uninstall itself).
    $listing = @(& $c --list-extensions 2>$null)
    $listExit = $LASTEXITCODE
    $listed = $listing -contains $id
    $ok = $listExit -eq 0 -and $(if ($Uninstall) { -not $listed } else { $code -eq 0 -and $listed })
    if ($listExit -ne 0) { $output = @($output) + "its extension list could not be read (exit $listExit)" }
    if ($ok) {
        if ($Uninstall) { "${c}: $id removed" } else { "${c}: $id installed; VS Code starts it in the windows already open, and each writes its record under %LOCALAPPDATA%\Bosun\editor-windows" }
    } else {
        $failed++
        "${c}: $(if ($Uninstall) { 'could not remove' } else { 'could not install' }) $id (exit $code): $(($output | Out-String).Trim())"
    }
}
exit $(if ($failed) { 1 } else { 0 })
