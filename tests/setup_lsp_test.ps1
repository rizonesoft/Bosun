# Tests Test-PluginInstalled (scripts/lib/plugin-install.ps1): a plugin counts as installed for a checkout only when
# Claude Code recorded it for that folder or for the user, not when it is merely listed for another folder.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\lib\plugin-install.ps1')
$failed = 0
function Case([string]$name, [object[]]$entries, [bool]$want) {
  $json = ConvertTo-Json -InputObject @($entries) -Depth 3
  $got = Test-PluginInstalled -ListJson $json -Id 'clangd-lsp@claude-plugins-official' -Repo 'D:\src\Bosun'
  if ($got -ne $want) { "FAIL $name (got $got, want $want)"; $script:failed++ } else { "ok   $name" }
}
$id = 'clangd-lsp@claude-plugins-official'
Case 'installed for this checkout' @(@{ id = $id; scope = 'project'; projectPath = 'D:\src\Bosun' }) $true
Case 'path compared without case or trailing slash' @(@{ id = $id; scope = 'project'; projectPath = 'd:\src\bosun\' }) $true
Case 'installed for the user' @(@{ id = $id; scope = 'user' }) $true
Case 'only installed for the old checkout' @(@{ id = $id; scope = 'project'; projectPath = 'R:\src\Bosun' }) $false
Case 'another plugin for this checkout' @(@{ id = 'pyright-lsp@claude-plugins-official'; scope = 'project'; projectPath = 'D:\src\Bosun' }) $false
Case 'nothing installed' @() $false
if ($failed) { exit 1 }
