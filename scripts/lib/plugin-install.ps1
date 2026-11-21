# Whether a Claude Code plugin is installed for a checkout, from `claude plugin list --json`, used by setup-lsp.ps1.
#
# A plugin enabled only in the repo's .claude/settings.json is listed but not loaded until it is installed for this
# checkout's folder: Claude Code records project installs by path, so a checkout that moved (R:\src\Bosun to
# D:\src\Bosun on 2026-11-10) lost clangd and csharp-ls while the plain listing still named them.
function Test-PluginInstalled([string]$ListJson, [string]$Id, [string]$Repo) {
  $want = [IO.Path]::GetFullPath($Repo).TrimEnd('\', '/')
  foreach ($entry in @($ListJson | ConvertFrom-Json)) {
    if ($entry.id -ne $Id) { continue }
    if ($entry.scope -eq 'user') { return $true }
    if ($entry.projectPath -and [string]::Equals([IO.Path]::GetFullPath($entry.projectPath).TrimEnd('\', '/'), $want, [StringComparison]::OrdinalIgnoreCase)) { return $true }
  }
  return $false
}
