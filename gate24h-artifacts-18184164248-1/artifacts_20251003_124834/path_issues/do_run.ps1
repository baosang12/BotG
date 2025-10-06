Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
& "$PSScriptRoot\copilot_report_runner.ps1" -Ts $ts
