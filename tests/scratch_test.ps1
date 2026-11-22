# Tests scripts/lib/scratch.ps1 (D00 T07 §3): BOSUN_SCRATCH resolution, fallback to system temp when unset/empty/
# missing/file, uniqueness, traversal name refusal, child-creation failure leaving system temp untouched, and cleanup.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
. (Join-Path $repo 'scripts/lib/scratch.ps1')

$failed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
  if ($Ok) { "ok   $Name" } else { "FAIL $Name $Detail"; $script:failed++ }
}

function Same-Dir([string]$A, [string]$B) {
  $na = [IO.Path]::GetFullPath($A).TrimEnd('\', '/')
  $nb = [IO.Path]::GetFullPath($B).TrimEnd('\', '/')
  return $na.Equals($nb, [StringComparison]::OrdinalIgnoreCase)
}

$savedScratch = $env:BOSUN_SCRATCH
$sysTemp = [IO.Path]::GetTempPath()
$created = [Collections.Generic.List[string]]::new()
$lockedDir = $null

try {
  # Create a fake BOSUN_SCRATCH root using the helper itself before we override BOSUN_SCRATCH.
  $fakeRoot = New-BosunScratchFolder -Name 'scratch-test-root'
  $created.Add($fakeRoot)

  # 1. Existing BOSUN_SCRATCH directory is honoured, named for the test, and unique across calls.
  $env:BOSUN_SCRATCH = $fakeRoot
  $a = New-BosunScratchFolder -Name 'alpha'
  $created.Add($a)
  $b = New-BosunScratchFolder -Name 'alpha'
  $created.Add($b)
  Check 'existing BOSUN_SCRATCH holds the created folder' ((Test-Path -LiteralPath $a -PathType Container) -and (Same-Dir (Split-Path $a -Parent) $fakeRoot) -and ((Split-Path $a -Leaf) -like 'bosun-alpha-*')) "($a)"
  Check 'two calls with the same name return distinct folders' ($a -ne $b -and (Test-Path -LiteralPath $b -PathType Container)) "($a vs $b)"

  # 2. Unset and empty/whitespace BOSUN_SCRATCH fall back to system temp.
  Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore
  $u = New-BosunScratchFolder -Name 'unset'
  $created.Add($u)
  Check 'unset BOSUN_SCRATCH falls back to system temp' ((Test-Path -LiteralPath $u -PathType Container) -and (Same-Dir (Split-Path $u -Parent) $sysTemp)) "($u vs $sysTemp)"

  $env:BOSUN_SCRATCH = '   '
  $w = New-BosunScratchFolder -Name 'blank'
  $created.Add($w)
  Check 'whitespace BOSUN_SCRATCH falls back to system temp' ((Test-Path -LiteralPath $w -PathType Container) -and (Same-Dir (Split-Path $w -Parent) $sysTemp)) "($w vs $sysTemp)"

  # 3. Missing directory and file path fall back to system temp.
  $env:BOSUN_SCRATCH = Join-Path $fakeRoot 'does-not-exist'
  $m = New-BosunScratchFolder -Name 'missing'
  $created.Add($m)
  Check 'missing BOSUN_SCRATCH path falls back to system temp' ((Test-Path -LiteralPath $m -PathType Container) -and (Same-Dir (Split-Path $m -Parent) $sysTemp)) "($m vs $sysTemp)"

  $notADir = Join-Path $fakeRoot 'plain-file.txt'
  Set-Content -LiteralPath $notADir -Value 'x'
  $env:BOSUN_SCRATCH = $notADir
  $f = New-BosunScratchFolder -Name 'file'
  $created.Add($f)
  Check 'file BOSUN_SCRATCH path falls back to system temp' ((Test-Path -LiteralPath $f -PathType Container) -and (Same-Dir (Split-Path $f -Parent) $sysTemp)) "($f vs $sysTemp)"

  # 4. Unsafe names (path separators, traversal, empty, trailing newline) are refused.
  $env:BOSUN_SCRATCH = $fakeRoot
  foreach ($bad in @('', '.', '..', '../escape', 'a\b', 'a/b', 'bad name', "alpha`n")) {
    $threw = $false
    try { $null = New-BosunScratchFolder -Name $bad } catch { $threw = $true }
    Check "unsafe name '$($bad -replace "`n", '\n')' is refused" $threw
  }

  # 4b. Relative BOSUN_SCRATCH resolves against PowerShell's current location (Push-Location), not the process CWD.
  $subA = Join-Path $fakeRoot 'loc-a'
  $subB = Join-Path $fakeRoot 'loc-b'
  $null = New-Item -ItemType Directory -Path $subA, $subB
  Push-Location -LiteralPath $subA
  try {
    $env:BOSUN_SCRATCH = '..\loc-b'
    $rel = New-BosunScratchFolder -Name 'rel'
    $created.Add($rel)
    Check 'relative BOSUN_SCRATCH resolves against PowerShell location' ((Test-Path -LiteralPath $rel -PathType Container) -and (Same-Dir (Split-Path $rel -Parent) $subB)) "($rel vs $subB)"
  } finally { Pop-Location }

  # 5. Child-creation failure inside a selected existing scratch root throws naming the root and leaves system temp untouched.
  $lockedDir = Join-Path $fakeRoot 'unwritable'
  $null = New-Item -ItemType Directory -Path $lockedDir
  $acl = Get-Acl -LiteralPath $lockedDir
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
  $denyRule = [Security.AccessControl.FileSystemAccessRule]::new(
    $identity,
    [Security.AccessControl.FileSystemRights]::CreateDirectories,
    [Security.AccessControl.AccessControlType]::Deny
  )
  $acl.AddAccessRule($denyRule)
  Set-Acl -LiteralPath $lockedDir -AclObject $acl

  $env:BOSUN_SCRATCH = $lockedDir
  $beforeTemp = @(Get-ChildItem -LiteralPath $sysTemp -Filter 'bosun-failcase-*' -ErrorAction Ignore | Select-Object -ExpandProperty FullName)
  $returned = $null
  $errText = ''
  try {
    $returned = New-BosunScratchFolder -Name 'failcase'
  } catch {
    $errText = $_.Exception.Message
  }
  $afterTemp = @(Get-ChildItem -LiteralPath $sysTemp -Filter 'bosun-failcase-*' -ErrorAction Ignore | Select-Object -ExpandProperty FullName)
  $newInTemp = @($afterTemp | Where-Object { $_ -notin $beforeTemp })
  Check 'child creation failure throws naming selected root, returns nothing, and leaves system temp untouched' (
    $null -eq $returned -and $errText -like "*$lockedDir*" -and $newInTemp.Count -eq 0
  ) "(returned='$returned', err='$errText', newInTemp=$($newInTemp.Count))"

  # 6. Bracketed parent cleanup removes the exact allocated child when addressed with -LiteralPath.
  $bracketParent = Join-Path $fakeRoot 'Scratch [CI]'
  $null = New-Item -ItemType Directory -Path $bracketParent
  $env:BOSUN_SCRATCH = $bracketParent
  $bracketChild = New-BosunScratchFolder -Name 'brackets'
  $created.Add($bracketChild)
  $hadChild = Test-Path -LiteralPath $bracketChild -PathType Container
  Remove-Item -LiteralPath $bracketChild -Recurse -Force
  Check 'bracketed parent cleanup removes the exact allocated child' ($hadChild -and -not (Test-Path -LiteralPath $bracketChild)) "($bracketChild)"

  # 7. Caller cleanup removes the folder.
  Remove-Item -LiteralPath $a -Recurse -Force
  Check 'caller cleanup removes the created folder' (-not (Test-Path -LiteralPath $a))
} finally {
  if ($lockedDir -and (Test-Path -LiteralPath $lockedDir)) {
    try {
      $acl = Get-Acl -LiteralPath $lockedDir
      $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
      $denyRule = [Security.AccessControl.FileSystemAccessRule]::new(
        $identity,
        [Security.AccessControl.FileSystemRights]::CreateDirectories,
        [Security.AccessControl.AccessControlType]::Deny
      )
      $null = $acl.RemoveAccessRule($denyRule)
      Set-Acl -LiteralPath $lockedDir -AclObject $acl
    } catch {
      Write-Warning "cleanup could not restore ACL on ${lockedDir}: $($_.Exception.Message)"
    }
  }
  if ($null -eq $savedScratch) { Remove-Item Env:BOSUN_SCRATCH -ErrorAction Ignore } else { $env:BOSUN_SCRATCH = $savedScratch }
  foreach ($p in $created) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Ignore }
}

if ($failed) { "$failed check(s) failed"; exit 1 }
'all scratch helper checks passed'
