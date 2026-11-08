# Put Visual Studio's CMake on the user PATH, or take it off again. With no switch it only previews. -Apply adds the
# folder at the end, keeping every entry already there, and records what it added; -Undo removes only what it added.
# Each change is read back from the registry before the script says it's done.
param(
  [switch]$Apply,
  [switch]$Undo,
  [string]$Folder,
  [string]$Variable = 'Path',
  [string]$Record = (Join-Path $env:LOCALAPPDATA 'Bosun\path-changes.json'),
  [string]$VsWhere = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$ErrorActionPreference = 'Stop'
if ($Apply -and $Undo) { throw 'give -Apply or -Undo, not both' }

$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
function Read-Value {
  # Unexpanded, so an entry such as %USERPROFILE%\bin is kept as written.
  $key.GetValue($Variable, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}
function Write-Value([string]$value) {
  $kind = if ($key.GetValueNames() -contains $Variable) { $key.GetValueKind($Variable) } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
  $key.SetValue($Variable, $value, $kind)
  Add-Type -Namespace Bosun -Name Env -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);' -ErrorAction Ignore
  $result = [UIntPtr]::Zero
  [void][Bosun.Env]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)  # new shells see it
}
function Entries([string]$value) { @($value -split ';' | Where-Object { $_ -ne '' }) }
function Same([string]$a, [string]$b) { $a.TrimEnd('\') -ieq $b.TrimEnd('\') }
function Load-Record {
  if (-not (Test-Path $Record)) { return @{} }
  $data = Get-Content $Record -Raw | ConvertFrom-Json -AsHashtable
  if ($data -isnot [hashtable]) { throw "$Record has the wrong shape" }
  $data
}
function Save-Record([hashtable]$data) {
  New-Item -ItemType Directory -Force (Split-Path $Record) | Out-Null
  $tmp = "$Record.$PID.tmp"
  $data | ConvertTo-Json -Depth 4 | Set-Content $tmp -Encoding utf8
  Move-Item $tmp $Record -Force
}

if ($Undo) {
  $data = Load-Record
  $added = @($data[$Variable])
  if (-not $added) { "nothing to undo: this script added nothing to the user $Variable"; exit 0 }
  $before = Entries (Read-Value)
  $after = @($before | Where-Object { $e = $_; -not ($added | Where-Object { Same $_ $e }) })
  Write-Value ($after -join ';')
  $check = Entries (Read-Value)
  if (($check -join ';') -ne ($after -join ';')) { throw "the user $Variable didn't read back as written" }
  $data.Remove($Variable)
  Save-Record $data
  "removed from the user ${Variable}: $($added -join ', ')"
  exit 0
}

if (-not $Folder) {
  if (-not (Test-Path $VsWhere)) { throw "vswhere isn't at ${VsWhere}: give -Folder, or install Visual Studio with the C++ CMake tools" }
  $vs = & $VsWhere -latest -products '*' -property installationPath | Select-Object -First 1
  if (-not $vs) { throw 'vswhere found no Visual Studio: give -Folder' }
  $Folder = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
}
if (-not (Test-Path (Join-Path $Folder 'cmake.exe'))) { throw "there's no cmake.exe in $Folder" }

$before = Entries (Read-Value)
if ($before | Where-Object { Same $_ $Folder }) { "already on the user ${Variable}: $Folder"; exit 0 }
if (-not $Apply) { "would add to the end of the user ${Variable}: $Folder (run again with -Apply)"; exit 0 }

$after = @($before) + $Folder
Write-Value ($after -join ';')
$check = Entries (Read-Value)
if (($check -join ';') -ne ($after -join ';')) { throw "the user $Variable didn't read back as written" }
$data = Load-Record
$data[$Variable] = @(@($data[$Variable]) | Where-Object { $_ }) + $Folder
Save-Record $data
"added to the user ${Variable}: $Folder (undo with -Undo)"
