# The window reporter's package (D03 T02 §4): scripts/install-editor-extension.ps1 -PackOnly builds a VSIX whose
# manifest names the extension as its package.json does, holds the files the extension runs from and nothing else.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$repo = Split-Path $PSScriptRoot
$scratch = if ($env:BOSUN_SCRATCH) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch "editor-extension-test-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { "ok   $what" } else { $script:failures++; "FAIL $what"; if ($detail) { "     | $detail" } }
}

try {
    # The record the extension keeps, with a stand-in vscode (editors/bosun-windows/test).
    if (Get-Command node -CommandType Application -ErrorAction Ignore) {
        $said = & node --test (Join-Path $repo 'editors/bosun-windows/test/*.test.js') 2>&1
        # Some ran, and none failed: a glob that matched nothing is no pass.
        Check "the extension's own tests pass under node --test ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and ($said -join "`n") -match 'pass [1-9]' -and ($said -join "`n") -match 'fail 0') (($said | Select-Object -Last 15) -join "`n")
    } else {
        Check 'node is on the PATH, to run the extension''s own tests' $false
    }

    $vsix = Join-Path $work 'reporter.vsix'
    $said = & pwsh -NoProfile -File (Join-Path $repo 'scripts\install-editor-extension.ps1') -PackOnly -OutFile $vsix 2>&1
    Check "-PackOnly exits 0 ($LASTEXITCODE)" ($LASTEXITCODE -eq 0) ($said | Out-String)
    Check 'the VSIX is written where asked' (Test-Path -LiteralPath $vsix)
    Check 'no temporary file is left beside it' (@(Get-ChildItem -LiteralPath $work -Filter '*.tmp').Count -eq 0)

    $package = Get-Content -LiteralPath (Join-Path $repo 'editors\bosun-windows\package.json') -Raw | ConvertFrom-Json
    $zip = [IO.Compression.ZipFile]::OpenRead($vsix)
    try {
        $names = @($zip.Entries | ForEach-Object FullName | Sort-Object)
        $want = @('[Content_Types].xml', 'extension.vsixmanifest', 'extension/extension.js', 'extension/package.json', 'extension/record.js') | Sort-Object
        Check 'it holds the manifest, the content types and the three files the extension runs from, and no test' (($names -join '|') -eq ($want -join '|')) ($names -join ', ')
        function Text([string]$name) { $r = [IO.StreamReader]::new($zip.GetEntry($name).Open()); try { $r.ReadToEnd() } finally { $r.Dispose() } }
        [xml]$manifest = Text 'extension.vsixmanifest'
        $identity = $manifest.PackageManifest.Metadata.Identity
        Check 'the manifest names the package.json identity' ($identity.Id -eq $package.name -and $identity.Publisher -eq $package.publisher -and $identity.Version -eq $package.version) "$($identity.Publisher).$($identity.Id) $($identity.Version)"
        $properties = @{}; foreach ($p in $manifest.PackageManifest.Metadata.Properties.Property) { $properties[$p.Id] = $p.Value }
        Check 'the manifest says it is a UI extension for the package''s engine' ($properties['Microsoft.VisualStudio.Code.ExtensionKind'] -eq 'ui' -and $properties['Microsoft.VisualStudio.Code.Engine'] -eq $package.engines.vscode)
        Check 'the manifest points at its package.json' ($manifest.PackageManifest.Assets.Asset.Path -eq 'extension/package.json')
        Check 'the packed record.js is the checkout''s' ((Text 'extension/record.js') -eq (Get-Content -LiteralPath (Join-Path $repo 'editors\bosun-windows\record.js') -Raw))
        $declared = @(([xml](Text '[Content_Types].xml')).Types.Default.Extension)
        $undeclared = @($names | Where-Object { $_ -ne '[Content_Types].xml' } | ForEach-Object { [IO.Path]::GetExtension($_) } | Sort-Object -Unique | Where-Object { $_ -notin $declared })
        Check 'the content types cover every packed file' ($undeclared.Count -eq 0) "undeclared: $($undeclared -join ', ')"
    } finally { $zip.Dispose() }

    # A CLI that fails to install is said, with exit 1, and nothing claims success.
    $fake = Join-Path $work 'fake-code.cmd'
    Set-Content -LiteralPath $fake -Value "@echo off`r`nif ""%1""==""--list-extensions"" exit /b 0`r`necho the editor refused`r`nexit /b 3" -Encoding ascii
    $said = & pwsh -NoProfile -File (Join-Path $repo 'scripts\install-editor-extension.ps1') -OutFile $vsix -Cli $fake 2>&1
    Check "an editor that refuses is said, with exit 1 ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and ($said -join "`n") -match 'could not install bosun\.bosun-windows \(exit 3\): the editor refused') ($said | Out-String)

    # Round 1: an uninstall whose editor can't list its extensions is not proven, so it is never said to be done.
    $broken = Join-Path $work 'broken-code.cmd'
    Set-Content -LiteralPath $broken -Value "@echo off`r`necho the editor is broken`r`nexit /b 5" -Encoding ascii
    $said = & pwsh -NoProfile -File (Join-Path $repo 'scripts\install-editor-extension.ps1') -Uninstall -Cli $broken 2>&1
    Check "an uninstall that can't be verified is said, with exit 1 ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and ($said -join "`n") -match 'could not remove bosun\.bosun-windows' -and ($said -join "`n") -notmatch ' removed') ($said | Out-String)
    $gone = Join-Path $work 'gone-code.cmd'
    Set-Content -LiteralPath $gone -Value "@echo off`r`nif ""%1""==""--list-extensions"" exit /b 0`r`necho not installed`r`nexit /b 1" -Encoding ascii
    $said = & pwsh -NoProfile -File (Join-Path $repo 'scripts\install-editor-extension.ps1') -Uninstall -Cli $gone 2>&1
    Check "an uninstall from an editor that lists none is done ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and ($said -join "`n") -match 'bosun\.bosun-windows removed') ($said | Out-String)
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failures) { "$failures check(s) failed"; exit 1 }
'editor extension: all checks passed'
