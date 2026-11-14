# Tests scripts/render-pages.ps1 (D02 T02 §9): it writes the sample view's four pictures and their index, and a run
# that can't finish leaves nothing that reads as a current run.
#
# Each case writes into a scratch folder of its own (-Out), never build/renders. The builds are of one test project
# that the gate has already built, so they are quick and need no queue slot.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'scripts/render-pages.ps1'
$scratch = if ($env:BOSUN_SCRATCH -and (Test-Path -LiteralPath $env:BOSUN_SCRATCH -PathType Container)) { $env:BOSUN_SCRATCH } else { [IO.Path]::GetTempPath() }
$work = Join-Path $scratch ("bosun-render-pages-test-" + [guid]::NewGuid())
$failed = 0
function Check([string]$Name, [bool]$Ok) { if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failed++ } }
function Names([string]$Folder) { @(Get-ChildItem -LiteralPath $Folder -File | Sort-Object Name | ForEach-Object Name) -join ',' }

New-Item -ItemType Directory -Path $work | Out-Null
try {
  # A build that fails: the old pictures and their index are gone before the build starts, and stay gone.
  $out = Join-Path $work 'broken'
  New-Item -ItemType Directory -Path $out | Out-Null
  Set-Content -LiteralPath (Join-Path $out 'queue.normal.dark.png') 'a picture from an earlier run'
  Set-Content -LiteralPath (Join-Path $out 'index.json') '{"renders":[{"file":"queue.normal.dark.png"}]}'
  Set-Content -LiteralPath (Join-Path $out 'notes.txt') 'not a render'
  $broken = Join-Path $work 'Broken.csproj'
  Set-Content -LiteralPath $broken '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="missing.cs" /></ItemGroup></Project>'
  $text = (& pwsh -NoProfile -File $script -Out $out -Project $broken 2>&1 | Out-String)
  $code = $LASTEXITCODE
  Check "a build that fails stops the run with the build's exit code ($code)" ($code -ne 0 -and $text -match 'the build failed')
  Check "and leaves no old picture and no index, only what wasn't a render ($(Names $out))" ((Names $out) -eq 'notes.txt')

  # An index held open can't be removed: the run stops before it touches a picture, so the old run is still whole.
  $out = Join-Path $work 'held'
  New-Item -ItemType Directory -Path $out | Out-Null
  Set-Content -LiteralPath (Join-Path $out 'queue.normal.dark.png') 'a picture from an earlier run'
  Set-Content -LiteralPath (Join-Path $out 'index.json') '{"renders":[{"file":"queue.normal.dark.png"}]}'
  $hold = [IO.File]::Open((Join-Path $out 'index.json'), 'Open', 'Read', 'None')
  try {
    $text = (& pwsh -NoProfile -File $script -Out $out -Project $broken 2>&1 | Out-String)
    $code = $LASTEXITCODE
  } finally { $hold.Dispose() }
  Check "an index that can't be removed stops the run (exit $code) saying so" ($code -eq 1 -and $text -match "index\.json can't be removed" -and $text -notmatch 'the build failed')
  Check "and the earlier run is left whole ($(Names $out))" ((Names $out) -eq 'index.json,queue.normal.dark.png')

  # The real project: the sample view in two states and two themes, and the index naming them.
  # A picture that isn't a render (a reviewer's own screenshot) and their notes sit beside them, and are left alone:
  # they neither fail the run nor go (the completion review: the run failed on any other PNG in the folder).
  $out = Join-Path $work 'real'
  New-Item -ItemType Directory -Path $out | Out-Null
  Set-Content -LiteralPath (Join-Path $out 'screenshot.png') 'a reviewer''s own picture'
  Set-Content -LiteralPath (Join-Path $out 'notes.txt') 'a reviewer''s notes'
  $text = (& pwsh -NoProfile -File $script -Out $out 2>&1 | Out-String)
  $code = $LASTEXITCODE
  Check "the real catalogue is written (exit $code)" ($code -eq 0 -and $text -match "render-pages: \d+ pictures in ")
  if ($code -ne 0) { $text }
  Check 'a picture and notes that are not renders are still there, as they were' ((Get-Content -LiteralPath (Join-Path $out 'screenshot.png') -Raw -ErrorAction Ignore) -match 'own picture' -and (Get-Content -LiteralPath (Join-Path $out 'notes.txt') -Raw -ErrorAction Ignore) -match 'notes')
  Remove-Item -LiteralPath (Join-Path $out 'screenshot.png'), (Join-Path $out 'notes.txt') -Force
  $names = Names $out
  foreach ($file in 'sample.normal.dark.png', 'sample.normal.light.png', 'sample.empty.dark.png', 'sample.empty.light.png', 'index.json') {
    Check "$file is there" ((",$names,").Contains(",$file,"))
  }
  $index = Get-Content -LiteralPath (Join-Path $out 'index.json') -Raw | ConvertFrom-Json
  $pngs = @(Get-ChildItem -LiteralPath $out -Filter '*.png' | Sort-Object Name | ForEach-Object Name)
  Check "the index names every picture and no other ($($pngs.Count))" ((@($index.renders.file | Sort-Object) -join ',') -eq ($pngs -join ','))
  Check 'each picture is a PNG of 1360 x 860' (-not (@($index.renders | Where-Object { $_.width -ne 1360 -or $_.height -ne 860 })) -and -not ($pngs | Where-Object {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $out $_)); -not ($bytes.Length -gt 1000 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) }))
  Check 'the index says which commit the pictures are of' ($index.commit -match '^([0-9a-f]{40}|unknown)$')

  # A render test that fails after its index was written (a check that reads the finished folder): the run fails, and
  # the index goes, so the failed run can't be read as a current one. A stand-in `dotnet`, first on the path, builds
  # nothing, writes an index as the test would have, and fails.
  $out = Join-Path $work 'late'
  $fake = Join-Path $work 'fake-dotnet'
  New-Item -ItemType Directory -Path $out, $fake | Out-Null
  Set-Content -LiteralPath (Join-Path $fake 'dotnet.cmd') -Encoding ascii @'
@echo off
if "%1"=="build" exit /b 0
echo {"renders":[]}> "%BOSUN_RENDER_DIR%\index.json"
exit /b 3
'@
  $path = $env:PATH
  $env:PATH = "$fake;$path"
  try {
    $text = (& pwsh -NoProfile -File $script -Out $out 2>&1 | Out-String)
    $code = $LASTEXITCODE
  } finally { $env:PATH = $path }
  Check "a render test that fails after writing its index fails the run with its exit code ($code)" ($code -eq 3 -and $text -match 'the render test failed')
  Check "and leaves no index ($(Names $out))" ((Names $out) -eq '')

  # Renders stay out of Git: the default folder is under build/, which is ignored.
  $ignored = (& git -C $repo check-ignore 'build/renders/sample.normal.dark.png' | Out-String).Trim()
  Check 'build/renders is ignored by Git' ($ignored -eq 'build/renders/sample.normal.dark.png')
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Ignore }
if ($failed) { "$failed check(s) failed"; exit 1 }
'all passed'
