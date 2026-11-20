# A Release build leaves only the shipped tools in bin/, and the installer takes them from there.
param([string]$Repo = "$PSScriptRoot\..")
$ErrorActionPreference = 'Stop'
$fail = 0
function Check($ok, $name) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }

$bin = Join-Path $Repo 'bin'
$release = Join-Path $Repo 'build\Release'
$names = if (Test-Path $bin) { @(Get-ChildItem $bin -Recurse -File | ForEach-Object Name | Sort-Object) } else { @() }
Check (($names -join ',') -ceq 'bosun-logger.exe,bosun.exe') "bin holds exactly the two tools (got: $($names -join ','))"
foreach ($tool in 'bosun.exe', 'bosun-logger.exe') {
  $built = Join-Path $release $tool
  $copied = Join-Path $bin $tool
  $same = (Test-Path $built) -and (Test-Path $copied) -and ((Get-FileHash $built).Hash -eq (Get-FileHash $copied).Hash)
  Check $same "bin\$tool matches the Release build"
}
Check (Test-Path (Join-Path $release 'test_core.exe')) 'test binaries stay in build'

$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo 'scripts\install.ps1'), [ref]$tokens, [ref]$errors)
$default = ($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Build' }).DefaultValue.Extent.Text
Check ($default -match '\\bin"$') "install.ps1 defaults to bin (got: $default)"
# D03 T02 §6: the logger is given the Windows log folder, found through the drive mounts on each sample; a guest path
# translated once at install (wslpath) would be stranded when the mounts move.
$logs = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$logsWsl' }, $true) | Select-Object -First 1
Check ($logs -and $logs.Right.Extent.Text -notmatch 'wslpath' -and $logs.Right.Extent.Text -match "LOCALAPPDATA") "install.ps1 hands the logger the Windows log folder (got: $($logs.Right.Extent.Text))"

git -C $Repo check-ignore -q bin/bosun.exe
Check ($LASTEXITCODE -eq 0) 'bin is ignored by Git'

if ($fail) { "$fail failed"; exit 1 }
'bin_output_test ok'
