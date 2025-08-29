$ErrorActionPreference='Stop'
$pi = Join-Path (Get-Location).Path 'path_issues'
$latest = Get-ChildItem -LiteralPath $pi -Filter 'copilot_report_*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if(-not $latest){ Write-Host 'NO_REPORT'; exit 0 }
$ts = ($latest.BaseName -replace '^copilot_report_','')
$md = $latest.FullName
$json = Join-Path $pi ("copilot_report_${ts}.json")
$acc = Join-Path $pi ("copilot_acceptance_${ts}.json")
Write-Host ("REPORT_TS=" + $ts)
Write-Host ("REPORT_MD=" + $md)
Write-Host ("REPORT_JSON_EXISTS=" + (Test-Path -LiteralPath $json))
if(Test-Path -LiteralPath $json){ Write-Host ("REPORT_JSON=" + $json) }
Write-Host ("ACCEPTANCE_JSON_EXISTS=" + (Test-Path -LiteralPath $acc))
if(Test-Path -LiteralPath $acc){ $a=Get-Content -LiteralPath $acc -Raw | ConvertFrom-Json; Write-Host ("ACCEPTANCE_SMOKE=" + $a.smoke) }
