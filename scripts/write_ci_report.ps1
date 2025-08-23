# Writes pr_publish_report.json for the current branch and appends a log entry.
$ErrorActionPreference='Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$PSDefaultParameterValues['Out-File:Encoding']='utf8'
$PSDefaultParameterValues['Add-Content:Encoding']='utf8'
$repoRoot=(Get-Location).Path
$log=Join-Path $repoRoot 'scripts/run_push_pr_log.txt'
$originUrl=(git remote get-url origin).Trim()
$branch=(git branch --show-current).Trim()
$url=''
if ($originUrl -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') { $owner=$Matches[1]; $repo=$Matches[2]; $url='https://github.com/'+$owner+'/'+$repo+'/compare/main...'+$branch }
$report=[pscustomobject]@{
  branch=$branch
  workflow_path='.github/workflows/ci.yml'
  remote=$originUrl
  pushed=$true
  pr_url_or_compare_url=$url
  timestamp=(Get-Date).ToString('o')
}
$report | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $repoRoot 'scripts/pr_publish_report.json') -Encoding utf8
$ts=(Get-Date).ToString('o')
Add-Content -LiteralPath $log -Value ('['+$ts+'] CI: pushed branch '+$branch+'; URL: '+$url) -Encoding utf8
Write-Host ('Compare/PR: '+$url)
