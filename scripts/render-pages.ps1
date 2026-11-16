# Writes a picture of every view the panel registers, in each state and both themes, for a review to look at.
#
#   pwsh scripts/render-pages.ps1                 write build/renders/
#   pwsh scripts/render-pages.ps1 -Out <folder>   write somewhere else
#
# The pictures are drawn off screen (no window opens, no desktop is needed) by the render test in
# tests/Bosun.Panel.Tests/Render: one PNG per view, state and theme, named <view>.<state>.<theme>.png, and an
# index.json naming each with its size and the commit it was drawn from. A view is added to the pictures by adding
# it to RenderCatalog.cs.
#
# What an earlier run left is removed before anything is built, and the index is written last, only when every
# picture was drawn: so a folder with an index.json is one whole, current run, and after a build that fails or a
# view that can't be drawn there is no index and no old picture to mistake for a new one. A picture or a file that is
# not a render (a reviewer's screenshot.png, their notes) is left where it is and changes nothing.
#
# Exit 0 when every picture was written; the build's or the test's own exit code when it failed; 1 when the test
# passed but left no index.
param(
  [string]$Out = (Join-Path (Split-Path $PSScriptRoot) 'build/renders'),
  # The project that holds the render test. For tests/render_pages_test.ps1, which names one that doesn't build.
  [string]$Project = (Join-Path (Split-Path $PSScriptRoot) 'tests/Bosun.Panel.Tests/Bosun.Panel.Tests.csproj')
)
$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
$index = Join-Path $Out 'index.json'

# The same rule as RenderCatalog.Clear: the index first, then the pictures; nothing else in the folder is touched.
if (Test-Path -LiteralPath $Out -PathType Container) {
  # An index that can't be removed (something holds it open) would be left saying an old run is current: the run
  # stops here, before a picture is touched. A file that isn't there is nothing to remove.
  foreach ($name in 'index.json', 'index.json.tmp') {
    $old = Join-Path $Out $name
    if (-not (Test-Path -LiteralPath $old)) { continue }
    try { Remove-Item -LiteralPath $old -Force }
    catch { "render-pages: $old can't be removed ($($_.Exception.Message)), so nothing was changed; close what holds it and run again"; exit 1 }
  }
  Get-ChildItem -LiteralPath $Out -File | Where-Object { $_.Name -cmatch '^[a-z0-9-]+\.[a-z0-9-]+\.(dark|light|highcontrast)\.png(\.tmp)?$' } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

& dotnet build $Project -nologo -maxCpuCount:2 -v:quiet
if ($LASTEXITCODE -ne 0) { "render-pages: the build failed (exit $LASTEXITCODE), so nothing was drawn; $Out holds no renders"; exit $LASTEXITCODE }

$before = $env:BOSUN_RENDER_DIR
$env:BOSUN_RENDER_DIR = $Out
try {
  & dotnet test --project $Project --no-build --filter-method '*Every_registered_view_is_written_in_each_state_and_theme_with_an_index'
  $code = $LASTEXITCODE
} finally { $env:BOSUN_RENDER_DIR = $before }
if ($code -ne 0) {
  # The test can fail after its index was written (a check that reads the finished folder): a failed run leaves none.
  foreach ($name in 'index.json', 'index.json.tmp') {
    $left = Join-Path $Out $name
    if (-not (Test-Path -LiteralPath $left)) { continue }
    try { Remove-Item -LiteralPath $left -Force }
    catch { "render-pages: the render test failed (exit $code) and $left can't be removed ($($_.Exception.Message)): it is NOT a current run; close what holds it and run again"; exit $code }
  }
  "render-pages: the render test failed (exit $code); $Out holds no index, so nothing in it is a current run"
  exit $code
}
if (-not (Test-Path -LiteralPath $index -PathType Leaf)) { "render-pages: the test passed but wrote no $index"; exit 1 }

$renders = @((Get-Content -LiteralPath $index -Raw | ConvertFrom-Json).renders)
"render-pages: $($renders.Count) pictures in $Out"
exit 0
