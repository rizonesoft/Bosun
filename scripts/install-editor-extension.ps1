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
  The editor CLIs to use, as a list or separated by ';'; by default each of code, code-insiders and antigravity-ide that is
  on the PATH.

.PARAMETER UserDataDir
  An editor started with --user-data-dir (a portable or isolated one): passed to the CLI, and its profiles read there.

.PARAMETER ExtensionsDir
  An editor started with --extensions-dir: passed to the CLI.
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$PackOnly,
    [string]$OutFile = '',
    [string[]]$Cli = @(),
    [string]$UserDataDir = '',
    [string]$ExtensionsDir = ''
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

# -Cli takes several, as a list or one string with ';' between them (a -File call can't pass a list).
$Cli = @($Cli | ForEach-Object { $_ -split ';' } | Where-Object { $_ })
$clis = if ($Cli) { $Cli } else { @('code', 'code-insiders', 'antigravity-ide') | Where-Object { Get-Command $_ -CommandType Application, ExternalScript -ErrorAction Ignore } }
if (-not $clis) { 'no VS Code-family editor CLI was found (code, code-insiders, antigravity-ide), so nothing was installed'; exit 1 }

# An editor's profiles (D03 T02 §5): VS Code keeps extensions per profile, and a profile whose useDefaultFlags.extensions
# is true uses the default profile's. The registry is %APPDATA%\<nameShort>\User\globalStorage\storage.json, nameShort
# from the product.json of the CLI's own install ("Code", "Code - Insiders"). No registry is a fresh editor (the
# default profile alone); one that can't be read leaves the other profiles unknown, which is said.
function Find-Product([string]$cli) {
    $command = Get-Command $cli -ErrorAction Ignore
    $path = if ($command -and $command.Source) { $command.Source } else { $cli }
    $folder = Split-Path $path
    $install = Split-Path $folder
    $candidates = @((Join-Path $folder 'product.json'), (Join-Path $install 'resources\app\product.json'))
    if ($install -and (Test-Path -LiteralPath $install)) {
        $candidates += @(Get-ChildItem -LiteralPath $install -Directory -ErrorAction Ignore | ForEach-Object { Join-Path $_.FullName 'resources\app\product.json' })
    }
    $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Get-Profiles([string]$cli) {
    $product = Find-Product $cli
    # A CLI with no install beside it (a wrapper, a stand-in) has no profile registry Bosun can find: its default profile
    # is all there is to do, and that is said, not failed.
    if (-not $product) { return [pscustomobject]@{ Independent = @(); Inheriting = @(); Problem = $null; Note = 'no product.json beside it, so its default profile alone is covered' } }
    try { $name = (Get-Content -LiteralPath $product -Raw | ConvertFrom-Json).nameShort } catch { $name = $null }
    if (-not $name) { return [pscustomobject]@{ Independent = @(); Inheriting = @(); Problem = "$product names no nameShort, so only its default profile is covered"; Note = $null } }
    $data = if ($UserDataDir) { $UserDataDir } else { Join-Path $env:APPDATA $name }
    $storage = Join-Path $data 'User\globalStorage\storage.json'
    if (-not (Test-Path -LiteralPath $storage)) { return [pscustomobject]@{ Independent = @(); Inheriting = @(); Problem = $null; Note = $null } }
    try {
        $registry = Get-Content -LiteralPath $storage -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $listed = if ($registry.PSObject.Properties['userDataProfiles']) { $registry.userDataProfiles } else { @() }
        $profiles = @($listed | Where-Object { $_ -and $_.PSObject.Properties['name'] -and $_.name })
    } catch {
        return [pscustomobject]@{ Independent = @(); Inheriting = @(); Problem = "$storage could not be read ($($_.Exception.Message)), so its other profiles are not covered"; Note = $null }
    }
    $inherits = { param($p)
        $flags = $p.PSObject.Properties['useDefaultFlags']
        $flags -and $flags.Value -and $flags.Value.PSObject.Properties['extensions'] -and $flags.Value.extensions -eq $true }
    [pscustomobject]@{
        Independent = @($profiles | Where-Object { -not (& $inherits $_) } | ForEach-Object name)
        Inheriting = @($profiles | Where-Object { & $inherits $_ } | ForEach-Object name)
        Problem = $null
        Note = $null
    }
}

$failed = 0
foreach ($c in $clis) {
    $profiles = Get-Profiles $c
    if ($profiles.Problem) { $failed++; "${c}: $($profiles.Problem)" }
    if ($profiles.Note) { "${c}: $($profiles.Note)" }
    # The default profile through the unqualified CLI, then each profile with extensions of its own.
    foreach ($target in @($null) + $profiles.Independent) {
        $scope = @(if ($UserDataDir) { '--user-data-dir', $UserDataDir }) + @(if ($ExtensionsDir) { '--extensions-dir', $ExtensionsDir }) +
            @(if ($target) { '--profile', $target })
        $where = if ($target) { "${c} (profile $target)" } else { "${c}" }
        $output = if ($Uninstall) { & $c --uninstall-extension $id @scope 2>&1 } else { & $c --install-extension $vsix --force @scope 2>&1 }
        $code = $LASTEXITCODE
        # The editor's own listing is the proof either way, so a listing that fails proves nothing: an uninstall is done
        # only when the listing ran and no longer names it (an editor that never had it may refuse the uninstall itself).
        $listing = @(& $c --list-extensions @scope 2>$null)
        $listExit = $LASTEXITCODE
        $listed = $listing -contains $id
        $ok = $listExit -eq 0 -and $(if ($Uninstall) { -not $listed } else { $code -eq 0 -and $listed })
        if ($listExit -ne 0) { $output = @($output) + "its extension list could not be read (exit $listExit)" }
        if ($ok) {
            if ($Uninstall) { "${where}: $id removed; a window open now keeps reporting until it is reloaded or closed, as VS Code runs a removed extension until then" } else { "${where}: $id installed; VS Code starts it in the windows already open, and each writes its record under %LOCALAPPDATA%\Bosun\editor-windows" }
        } else {
            $failed++
            "${where}: $(if ($Uninstall) { 'could not remove' } else { 'could not install' }) $id (exit $code): $(($output | Out-String).Trim())"
        }
    }
    foreach ($inheriting in $profiles.Inheriting) { "${c} (profile $inheriting): uses the default profile's extensions, so it is covered by the default" }
}
if ($failed) { 'run this again once the editor answers: it does what is still missing and leaves what is done' }
exit $(if ($failed) { 1 } else { 0 })
