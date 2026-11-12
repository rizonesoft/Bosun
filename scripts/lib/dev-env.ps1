# The Visual Studio compiler environment for scripts/build.ps1, loaded once and replayed from a cache after that.
#
# Loading the environment (Enter-VsDevShell) is most of what a build with nothing to do costs. Import-DevEnv keeps
# what that load changed in build/dev-env.json and applies it straight away the next time.
#
# What is stored is only what the load itself did, never a value the calling shell brought with it:
#   - a variable the load created, or replaced outright, is stored whole;
#   - a list variable the load added to (PATH, LIB, LIBPATH, INCLUDE and the like, entries separated by `;`) is
#     stored as the entries it put in front and the entries it put behind. A replay adds those around whatever the
#     variable holds in the shell doing the replay, so one shell's own LIB or PATH never reaches another's build.
# A variable the load left alone is not stored at all.
#
# The cache is used only while all of this holds: it parses, it is this script's (schema_version 2), its key equals
# the key given (the Visual Studio installation path and version), and the compiler folder it recorded still has
# cl.exe. Anything else loads the environment again and rewrites the cache. A cache that cannot be written costs a
# notice, not the build.

function Get-DevEnvSnapshot {
  $snapshot = @{}
  foreach ($item in Get-ChildItem Env:) { $snapshot[$item.Name] = $item.Value }
  $snapshot
}

function Split-DevEnvList([string]$Value) { @(($Value -split ';') | Where-Object { $_ }) }

# The stored cache when it can be used with this key, else $null.
function Read-DevEnvCache([string]$Cache, [string]$Key) {
  if (-not (Test-Path -LiteralPath $Cache -PathType Leaf)) { return $null }
  try { $data = Get-Content -LiteralPath $Cache -Raw | ConvertFrom-Json -AsHashtable } catch { return $null }
  if ($data -isnot [Collections.IDictionary] -or $data['schema_version'] -ne 2 -or $data['key'] -cne $Key) { return $null }
  if ($data['variables'] -isnot [Collections.IDictionary] -or $data['lists'] -isnot [Collections.IDictionary] -or -not $data['compiler_dir']) { return $null }
  if (-not (Test-Path -LiteralPath (Join-Path $data['compiler_dir'] 'cl.exe'))) { return $null }
  $data
}

# Loads the compiler environment into this process: from the cache when it holds for $Key, else by running $Load
# and storing what it changed. Prints one line saying which.
function Import-DevEnv {
  param(
    [Parameter(Mandatory)][string]$Cache,
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][scriptblock]$Load
  )
  $data = Read-DevEnvCache $Cache $Key
  if ($data) {
    foreach ($name in $data['variables'].Keys) { Set-Item -LiteralPath "Env:$name" -Value ([string]$data['variables'][$name]) }
    foreach ($name in $data['lists'].Keys) {
      $current = Split-DevEnvList ([Environment]::GetEnvironmentVariable($name))
      $entries = @($data['lists'][$name]['prepend']) + $current + @($data['lists'][$name]['append']) | Where-Object { $_ }
      Set-Item -LiteralPath "Env:$name" -Value ($entries -join ';')
    }
    'developer environment: from cache'
    return
  }

  $before = Get-DevEnvSnapshot
  & $Load | Out-Null
  $after = Get-DevEnvSnapshot
  $variables = [ordered]@{}
  $lists = [ordered]@{}
  foreach ($name in ($after.Keys | Sort-Object)) {
    if ($before.ContainsKey($name) -and $before[$name] -ceq $after[$name]) { continue }
    $old = if ($before.ContainsKey($name)) { Split-DevEnvList $before[$name] } else { @() }
    $new = Split-DevEnvList $after[$name]
    # A list the load added to still holds every entry it had. Anything else was created or replaced outright.
    if (-not $old.Count -or ($old | Where-Object { $new -notcontains $_ })) { $variables[$name] = $after[$name]; continue }
    # Entries the load added, split at the first entry that was there before: in front of it, or behind.
    $prepend = @(); $append = @(); $seenOld = $false
    foreach ($entry in $new) {
      if ($old -contains $entry) { $seenOld = $true; continue }
      if ($seenOld) { $append += $entry } else { $prepend += $entry }
    }
    $lists[$name] = [ordered]@{ prepend = $prepend; append = $append }
  }
  $compiler = Get-Command cl -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  'developer environment: loaded'
  if (-not $compiler) { return }

  $json = ([ordered]@{
      schema_version = 2
      key            = $Key
      compiler_dir   = Split-Path $compiler.Source
      variables      = $variables
      lists          = $lists
    } | ConvertTo-Json -Depth 5) + "`n"
  $temp = "$Cache.tmp-$PID"
  try {
    New-Item -ItemType Directory -Path (Split-Path $Cache) -Force | Out-Null
    [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $Cache, $true)
  } catch {
    "developer environment: could not be cached in ${Cache} ($($_.Exception.Message)); the next build loads it again"
  } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction Ignore } }
}
