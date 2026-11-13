# Tests scripts/capture-window.ps1 (D02 T02 §9) as far as it can be tested with no desktop: its own self-test, which
# checks the pair writer, the covered-window rule, and that what a launch started is stopped as a tree while a process
# of the same name is left alone. Capturing a real window needs a desktop session: that is D02 T02 §10.
$ErrorActionPreference = 'Stop'
$script = Join-Path (Split-Path $PSScriptRoot) 'scripts/capture-window.ps1'
$text = (& pwsh -NoProfile -File $script -SelfTest 2>&1 | Out-String)
$code = $LASTEXITCODE
$text.TrimEnd()
if ($code -ne 0 -or $text -notmatch '(?m)^self-test: all pass\s*$') { "capture-window's self-test failed (exit $code)"; exit 1 }
'all passed'
