# The Visual Studio compiler environment for scripts/build.ps1, loaded once and replayed from a cache after that.
#
# Loading the environment (Enter-VsDevShell) is most of what a build with nothing to do costs. Import-DevEnv keeps
# what that load changed in build/dev-env.json and applies it straight away the next time.
#
# What is stored is only what the load itself added or changed: each variable it set, and for PATH the entries it
# put in front and behind, never the whole PATH and never a variable the load left alone. So a replay cannot bring
# back a stale or unrelated value from the shell that wrote the cache.
#
# The cache is used only while all of this holds: it parses, it is this script's (schema_version 1), its key equals
# the key given (the Visual Studio installation path and version), and the compiler folder it recorded still has
# cl.exe. Anything else loads the environment again and rewrites the cache. A cache that cannot be written costs a
# notice, not the build.

function Get-DevEnvSnapshot {
  $snapshot = @{}
  foreach ($item in Get-ChildItem Env:) { $snapshot[$item.Name] = $item.Value }
  $snapshot
}

function Split-DevEnvPath([string]$Value) { @(($Value -split ';') | Where-Object { $_ }) }

# The stored cache when it can be used with this key, else $null.
function Read-DevEnvCache([string]$Cache, [string]$Key) {
  if (-not (Test-Path -LiteralPath $Cache -PathType Leaf)) { return $null }
  try { $data = Get-Content -LiteralPath $Cache -Raw | ConvertFrom-Json -AsHashtable } catch { return $null }
  if ($data -isnot [Collections.IDictionary] -or $data['schema_version'] -ne 1 -or $data['key'] -cne $Key) { return $null }
  if ($data['variables'] -isnot [Collections.IDictionary] -or -not $data['compiler_dir']) { return $null }
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
    $env:PATH = (@($data['path_prepend']) + (Split-DevEnvPath $env:PATH) + @($data['path_append']) | Where-Object { $_ }) -join ';'
    'developer environment: from cache'
    return
  }

  $before = Get-DevEnvSnapshot
  & $Load | Out-Null
  $after = Get-DevEnvSnapshot
  $variables = [ordered]@{}
  foreach ($name in ($after.Keys | Sort-Object)) {
    if ($name -ieq 'PATH') { continue }
    if (-not $before.ContainsKey($name) -or $before[$name] -cne $after[$name]) { $variables[$name] = $after[$name] }
  }
  # PATH entries the load added, split at the first entry that was there before: in front of it, or behind.
  $old = Split-DevEnvPath $before['PATH']
  $prepend = @(); $append = @(); $seenOld = $false
  foreach ($entry in (Split-DevEnvPath $after['PATH'])) {
    if ($old -contains $entry) { $seenOld = $true; continue }
    if ($seenOld) { $append += $entry } else { $prepend += $entry }
  }
  $compiler = Get-Command cl -CommandType Application -ErrorAction Ignore | Select-Object -First 1
  'developer environment: loaded'
  if (-not $compiler) { return }

  $json = ([ordered]@{
      schema_version = 1
      key            = $Key
      compiler_dir   = Split-Path $compiler.Source
      variables      = $variables
      path_prepend   = $prepend
      path_append    = $append
    } | ConvertTo-Json -Depth 4) + "`n"
  $temp = "$Cache.tmp-$PID"
  try {
    New-Item -ItemType Directory -Path (Split-Path $Cache) -Force | Out-Null
    [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $Cache, $true)
  } catch {
    "developer environment: could not be cached in ${Cache} ($($_.Exception.Message)); the next build loads it again"
  } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction Ignore } }
}
