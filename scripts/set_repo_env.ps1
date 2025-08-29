[CmdletBinding()]
param(
  [string]$BotGRoot = $env:BOTG_ROOT
)

if (-not $BotGRoot -or $BotGRoot.Trim().Length -eq 0) {
  try { $BotGRoot = (Resolve-Path ".").Path } catch { $BotGRoot = (Get-Location).Path }
}

$env:BOTG_ROOT = $BotGRoot
Write-Host ("[ENV] BOTG_ROOT={0}" -f $env:BOTG_ROOT) -ForegroundColor Cyan
