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

    # D03 T02 §5: each editor's own profile registry. A stand-in editor is an install folder holding product.json (its
    # nameShort names its data folder) and a CLI that keeps what is installed, per profile, as files, and logs each call.
    $appData = Join-Path $work 'appdata'
    function New-Editor([string]$name, [string]$nameShort, $profiles) {
        $install = Join-Path $work $name
        New-Item -ItemType Directory -Force -Path (Join-Path $install 'bin'), (Join-Path $install 'state') | Out-Null
        @{ nameShort = $nameShort } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $install 'bin\product.json')
        $state = Join-Path $install 'state'
        # %1 the verb; the profile follows --profile wherever it is. "mode-lie" in the state folder makes an install exit 0
        # without installing.
        $cli = Join-Path $install 'bin\code.cmd'
        Set-Content -LiteralPath $cli -Encoding ascii -Value (@(
            '@echo off'
            "set S=$state"
            'echo %*>>"%S%\calls.log"'
            'set V=%~1'
            'set P=default'
            ':args'
            'if "%~1"=="" goto run'
            'if "%~1"=="--profile" set P=%~2'
            'shift'
            'goto args'
            ':run'
            'if "%V%"=="--list-extensions" ( if exist "%S%\%P%" echo bosun.bosun-windows'
            '  exit /b 0 )'
            'if "%V%"=="--install-extension" ( if exist "%S%\mode-lie" exit /b 0'
            '  type nul > "%S%\%P%"'
            '  exit /b 0 )'
            'if "%V%"=="--uninstall-extension" ( if not exist "%S%\%P%" exit /b 1'
            '  del "%S%\%P%"'
            '  exit /b 0 )'
            'exit /b 9'
        ) -join "`r`n")
        if ($null -ne $profiles) {
            $folder = Join-Path $appData "$nameShort\User\globalStorage"
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            if ($profiles -is [string]) { Set-Content -LiteralPath (Join-Path $folder 'storage.json') -Value $profiles }
            else { @{ userDataProfiles = @($profiles) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $folder 'storage.json') }
        }
        [pscustomobject]@{ Cli = $cli; State = $state }
    }
    function Installed($editor) { @(Get-ChildItem -LiteralPath $editor.State -File | Where-Object Name -notin 'calls.log', 'mode-lie' | ForEach-Object Name | Sort-Object) -join ',' }
    function Run-Installer([string[]]$extra) {
        $before = $env:APPDATA
        $env:APPDATA = $appData
        try { & pwsh -NoProfile -File (Join-Path $repo 'scripts\install-editor-extension.ps1') -OutFile $vsix @extra 2>&1 } finally { $env:APPDATA = $before }
    }
    $stable = New-Editor 'stable' 'StandinCode' @(
        @{ name = 'Work'; location = 'w1' },
        @{ name = 'Agents'; location = 'builtin/agents'; useDefaultFlags = @{ extensions = $true } })
    $insiders = New-Editor 'insiders' 'StandinCode - Insiders' @(
        @{ name = 'Night'; location = 'n1'; useDefaultFlags = @{ settings = $true } },
        @{ name = 'Shared'; location = 's1'; useDefaultFlags = @{ extensions = $true } })
    $said = Run-Installer @('-Cli', "$($stable.Cli);$($insiders.Cli)")
    Check "two editors' defaults and independent profiles are installed, never an inheriting one ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Installed $stable) -eq 'default,Work' -and (Installed $insiders) -eq 'default,Night') "$(Installed $stable) / $(Installed $insiders)`n$($said | Out-String)"
    Check 'an inheriting profile is said to be covered by the default' (($said -join "`n") -match 'profile Agents\): uses the default profile''s extensions' -and ($said -join "`n") -match 'profile Shared\): uses the default')
    $said = Run-Installer @('-Uninstall', '-Cli', "$($stable.Cli);$($insiders.Cli)")
    Check "and uninstalled from each the same way ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Installed $stable) -eq '' -and (Installed $insiders) -eq '') ($said | Out-String)

    # No registry: a fresh editor is its default profile alone. A malformed one installs the default, says the rest
    # can't be read, and exits 1.
    $fresh = New-Editor 'fresh' 'StandinFresh' $null
    $said = Run-Installer @('-Cli', $fresh.Cli)
    Check "an editor with no profile registry is its default profile alone ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Installed $fresh) -eq 'default') ($said | Out-String)
    $damaged = New-Editor 'damaged' 'StandinDamaged' '{"userDataProfiles": ['
    $said = Run-Installer @('-Cli', $damaged.Cli)
    Check "a registry that can't be read installs the default, says so, and exits 1 ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and (Installed $damaged) -eq 'default' -and ($said -join "`n") -match 'could not be read.*other profiles are not covered') ($said | Out-String)

    # A CLI that says it installed while its listing disagrees is not done; a second run completes what the first left.
    $liar = New-Editor 'liar' 'StandinLiar' @(@{ name = 'Work'; location = 'w1' })
    Set-Content -LiteralPath (Join-Path $liar.State 'mode-lie') -Value 'x'
    $said = Run-Installer @('-Cli', "$($liar.Cli);$($fresh.Cli)")
    Check "an install its listing doesn't show is not done, and the other editor still is ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and (Installed $liar) -eq '' -and ($said -join "`n") -match 'could not install bosun\.bosun-windows \(exit 0\)' -and ($said -join "`n") -match 'run this again') ($said | Out-String)
    Remove-Item -LiteralPath (Join-Path $liar.State 'mode-lie')
    $said = Run-Installer @('-Cli', $liar.Cli)
    Check "a second run completes it ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Installed $liar) -eq 'default,Work') ($said | Out-String)

    # Round 1 of D03 T02 §5. A profile name the CLI's command shell would change is refused, never passed through it.
    $odd = New-Editor 'odd' 'StandinOdd' @(@{ name = 'QA-%USERNAME%'; location = 'q1' }, @{ name = 'Plain'; location = 'p1' })
    $said = Run-Installer @('-Cli', $odd.Cli)
    Check "a profile named with % is refused and said, the others done ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and (Installed $odd) -eq 'default,Plain' -and ($said -join "`n") -match 'profile QA-%USERNAME%\): not done, because its name holds a character') ($said | Out-String)
    # A wrapper with no product.json beside it, given -UserDataDir, reads that editor's registry.
    $isolated = Join-Path $work 'isolated'
    New-Item -ItemType Directory -Force -Path (Join-Path $isolated 'User\globalStorage') | Out-Null
    @{ userDataProfiles = @(@{ name = 'Work'; location = 'w1' }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $isolated 'User\globalStorage\storage.json')
    $wrapped = New-Editor 'wrapped' 'StandinWrapped' $null
    Remove-Item -LiteralPath (Join-Path (Split-Path $wrapped.Cli) 'product.json')
    $said = Run-Installer @('-Cli', $wrapped.Cli, '-UserDataDir', $isolated)
    Check "-UserDataDir names the registry even for a wrapper with no product.json ($LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Installed $wrapped) -eq 'default,Work') "$(Installed $wrapped)`n$($said | Out-String)"
    # An editor that can't be run is said, and the next one is still done.
    $later = New-Editor 'later' 'StandinLater' $null
    $said = Run-Installer @('-Cli', "$(Join-Path $work 'no-such-editor.cmd');$($later.Cli)")
    Check "an editor that can't be run is said and the next is still done ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and (Installed $later) -eq 'default' -and ($said -join "`n") -match 'no-such-editor\.cmd: could not be run') ($said | Out-String)
    # An inheriting profile is covered only when the default was.
    $lyingInherit = New-Editor 'lying-inherit' 'StandinLyingInherit' @(@{ name = 'Shared'; location = 's1'; useDefaultFlags = @{ extensions = $true } })
    Set-Content -LiteralPath (Join-Path $lyingInherit.State 'mode-lie') -Value 'x'
    $said = Run-Installer @('-Cli', $lyingInherit.Cli)
    Check "an inheriting profile is not called covered when the default was not done ($LASTEXITCODE)" ($LASTEXITCODE -eq 1 -and ($said -join "`n") -match 'profile Shared\): uses the default profile''s extensions, which were not done' -and ($said -join "`n") -notmatch 'so it is covered by the default') ($said | Out-String)
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore
}
if ($failures) { "$failures check(s) failed"; exit 1 }
'editor extension: all checks passed'
