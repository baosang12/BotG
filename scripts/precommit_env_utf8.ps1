# Enforce UTF-8 (no BOM) environment then run precommit_check.ps1
$ErrorActionPreference = 'Stop'

# Console & process encodings
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$env:PYTHONIOENCODING = 'utf-8'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'

# Ensure code page 65001 for child processes
try { chcp 65001 | Out-Null } catch {}

# Run original precommit
$script = Join-Path $PSScriptRoot 'precommit_check.ps1'
if (!(Test-Path $script)) { throw "Missing $script" }
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $script
