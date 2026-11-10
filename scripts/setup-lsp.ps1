# Sets up code intelligence for Claude Code in this repo: the language servers and the plugins that start them.
# Run once per machine; a second run changes nothing and says so.
#
#   pwsh scripts/setup-lsp.ps1          install what is missing
#   pwsh scripts/setup-lsp.ps1 -Check   report only; exit 1 when something is missing
#
# C and C++ use clangd from Visual Studio's LLVM component (found through vswhere, added to the user PATH), C# uses
# csharp-ls (a .NET global tool) and Python uses pyright (an npm global package). The plugins are the official ones,
# enabled for this repo in .claude/settings.json, so each machine only has to install them. clangd reads each file's
# flags from build/compile_commands.json, which `pwsh scripts/build.ps1` writes.
param(
  [switch]$Check,
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$marketplace = 'claude-plugins-official'
$plugins = 'clangd-lsp', 'csharp-lsp', 'pyright-lsp'
$missing = 0
function Say([string]$state, [string]$what) { "{0,-9} {1}" -f $state, $what }
function Need([string]$what, [string]$fix) {
  Say 'missing' "$what ($fix)"
  $script:missing++
}
# Whether a tool is on this shell's PATH or on the user PATH a new shell would get.
function Find([string]$tool) {
  $found = Get-Command $tool -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  if ($found) { return $found.Source }
  foreach ($dir in ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ })) {
    foreach ($ext in '.exe', '.cmd') {
      $at = Join-Path $dir "$tool$ext"
      if (Test-Path -LiteralPath $at) { return $at }
    }
  }
}

# --- clangd -----------------------------------------------------------------------------------------------------
if (Find 'clangd') {
  Say 'ok' "clangd at $(Find 'clangd')"
} else {
  $llvm = $null
  if (Test-Path -LiteralPath $VsWhere) {
    foreach ($vs in @(& $VsWhere -products '*' -sort -property installationPath 2>$null | Where-Object { $_ })) {
      $at = Join-Path $vs 'VC\Tools\Llvm\x64\bin'
      if (Test-Path -LiteralPath (Join-Path $at 'clangd.exe')) { $llvm = $at; break }
    }
  }
  if (-not $llvm) {
    Need 'clangd' "add the 'C++ Clang tools for Windows' component in the Visual Studio Installer, then run this again"
  } elseif ($Check) {
    Need 'clangd on PATH' "it is at $llvm; run this without -Check to add that folder to your user PATH"
  } else {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User') ?? ''
    [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $llvm).TrimStart(';'), 'User')
    $env:Path = "$env:Path;$llvm"
    Say 'added' "$llvm to your user PATH, for clangd"
  }
}

# --- csharp-ls and pyright --------------------------------------------------------------------------------------
$servers = @(
  @{ Tool = 'csharp-ls'; With = 'dotnet'; Install = @('tool', 'install', '--global', 'csharp-ls') },
  @{ Tool = 'pyright-langserver'; With = 'npm'; Install = @('install', '--global', 'pyright') }
)
foreach ($s in $servers) {
  if (Find $s.Tool) { Say 'ok' "$($s.Tool) at $(Find $s.Tool)"; continue }
  $how = "$($s.With) $($s.Install -join ' ')"
  if ($Check) { Need $s.Tool "run this without -Check, or ``$how``"; continue }
  $with = Find $s.With
  if (-not $with) { Need $s.Tool "$($s.With) is not installed, and it installs $($s.Tool)"; continue }
  & $with @($s.Install) | Select-Object -Last 2
  if ($LASTEXITCODE -or -not (Find $s.Tool)) { Need $s.Tool "``$how`` did not install it"; continue }
  Say 'installed' "$($s.Tool) at $(Find $s.Tool)"
}

# --- The plugins ------------------------------------------------------------------------------------------------
$claude = Find 'claude'
if (-not $claude) {
  Need 'claude' 'install Claude Code, then run this again'
} else {
  Push-Location -LiteralPath $repo
  try {
    $installed = (& $claude plugin list 2>&1) -join "`n"
    foreach ($p in $plugins) {
      if ($installed -match [regex]::Escape("$p@$marketplace")) { Say 'ok' "plugin $p"; continue }
      if ($Check) { Need "plugin $p" 'run this without -Check'; continue }
      if (((& $claude plugin marketplace list 2>&1) -join "`n") -notmatch [regex]::Escape($marketplace)) {
        & $claude plugin marketplace add "anthropics/$marketplace" | Select-Object -Last 1
      }
      & $claude plugin install "$p@$marketplace" --scope project | Select-Object -Last 1
      if ($LASTEXITCODE) { Need "plugin $p" "``claude plugin install $p@$marketplace --scope project`` failed" } else { Say 'installed' "plugin $p" }
    }
  } finally { Pop-Location }
}

if ($missing) { "$missing missing"; exit 1 }
'code intelligence is set up: start a new Claude Code session to load it'
