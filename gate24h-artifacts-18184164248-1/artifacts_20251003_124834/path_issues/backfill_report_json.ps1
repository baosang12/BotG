param(
  [string]$Ts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Get-Location).Path
$pi = Join-Path -Path $root -ChildPath 'path_issues'
New-Item -ItemType Directory -Force -Path $pi | Out-Null

function Save-Json([string]$path, $obj){
  $json = $obj | ConvertTo-Json -Depth 10
  Set-Content -LiteralPath $path -Value $json -Encoding UTF8
}

function FileMeta([string]$p){
  if(Test-Path -LiteralPath $p){
    $fi = Get-Item -LiteralPath $p
    $sha=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
    return [pscustomobject]@{path=$p; exists=$true; size_bytes=$fi.Length; mtime_iso=$fi.LastWriteTimeUtc.ToString('o'); sha256=$sha}
  }
  else { return [pscustomobject]@{path=$p; exists=$false} }
}

if(-not $Ts){
  $latestMd = Get-ChildItem -LiteralPath $pi -Filter 'copilot_report_*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if(-not $latestMd){ throw 'No copilot_report_*.md found to infer timestamp' }
  $Ts = ($latestMd.BaseName -replace '^copilot_report_','')
}

# 1) Artifacts inventory
$targets = @(
  'pre_run_readiness_*.json',
  'agent_steps_*.log',
  'smoke_summary_*.json',
  'reconstruct_report.json',
  'closed_trades_fifo_reconstructed.csv',
  'slip_latency_percentiles.json',
  'slippage_hist.png',
  'latency_percentiles.png',
  'latest_zip.txt'
)
$records=@(); $missing=@()
foreach($pat in $targets){
  $items = @(Get-ChildItem -LiteralPath $pi -Filter $pat -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
  if($items -and $items.Length -ge 1){ $records += FileMeta -p $items[0].FullName }
  else { $missing += $pat }
}
$latestTxt = Join-Path -Path $pi -ChildPath 'latest_zip.txt'
if (Test-Path -LiteralPath $latestTxt){
  $zp = (Get-Content -LiteralPath $latestTxt -Raw).Trim()
  if($zp){ $records += FileMeta -p $zp }
}
$artPath = Join-Path -Path $pi -ChildPath ("copilot_artifacts_${Ts}.json")
Save-Json -path $artPath -obj ([pscustomobject]@{files=$records;missing=$missing})

# 2) Acceptance gates
$buildTxt = Join-Path -Path $pi -ChildPath 'build_and_test_output.txt'
$buildStatus = 'UNKNOWN'
if(Test-Path -LiteralPath $buildTxt){ $c = Get-Content -LiteralPath $buildTxt -Raw; if($c -match 'PASS'){ $buildStatus='PASS' } elseif($c -match 'FAIL'){ $buildStatus='FAIL' } }

$teleJs = Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json")
$fillRate=$null; $fills=$null; $missP=$null; $missS=$null; $missL=$null
if(Test-Path -LiteralPath $teleJs){ $ta = Get-Content -LiteralPath $teleJs -Raw | ConvertFrom-Json; $fillRate=$ta.fill_rate; $fills=$ta.fills; $missP=$ta.fills_missing_price_filled; $missS=$ta.fills_missing_size_filled; $missL=$ta.fills_missing_latency_ms }

$orphan=$null
$smokeSum = Get-ChildItem -LiteralPath $pi -Filter 'smoke_summary_*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if($smokeSum){ $sj = Get-Content -LiteralPath $smokeSum.FullName -Raw | ConvertFrom-Json; $orphan=$sj.orphan_after }

$reconPath = Join-Path -Path $pi -ChildPath 'reconstruct_report.json'
$unmatched=$null; $requests=$null
if(Test-Path -LiteralPath $reconPath){
  $rj = Get-Content -LiteralPath $reconPath -Raw | ConvertFrom-Json
  # Try multiple schema variants
  if($rj.PSObject.Properties.Name -contains 'unmatched_orders_count'){ $unmatched = $rj.unmatched_orders_count }
  elseif($rj.PSObject.Properties.Name -contains 'unmatched_ids'){ $unmatched = @($rj.unmatched_ids).Length }
  if($rj.PSObject.Properties.Name -contains 'request_count'){ $requests = $rj.request_count }
  elseif(($null -ne $fills) -and ($rj.PSObject.Properties.Name -contains 'matched_count')){ $requests = [int]$rj.matched_count }
}

$reconGate = 'UNKNOWN'
if(($null -ne $unmatched) -and ($null -ne $requests) -and ($requests -gt 0)){
  $rate=($unmatched*100.0/$requests)
  $reconGate = $(if($rate -le 1.0){'PASS'}else{'FAIL'})
}

$logCompleteness = 'UNKNOWN'; $completeRate=$null
if(($null -ne $fills) -and ($fills -gt 0)){
  $completeRate = [math]::Round((100.0 * ($fills - [int]$missP - [int]$missS - [int]$missL) / $fills),2)
  $logCompleteness = $(if($completeRate -ge 99.0){'PASS'}else{'PARTIAL'})
}

$smokeGate = 'UNKNOWN'
if(($null -ne $orphan) -and ($null -ne $fillRate)){
  $smokeGate = $(if(($orphan -eq 0) -and ($fillRate -ge 95.0)){'PASS'}else{'FAIL'})
}

$acc = [pscustomobject]@{ build=$buildStatus; smoke=$smokeGate; reconstruct=$reconGate; logging=$logCompleteness; fill_rate=$fillRate; orphan_after=$orphan; unmatched=$unmatched; requests=$requests; logging_complete_rate=$completeRate }
$accPath = Join-Path -Path $pi -ChildPath ("copilot_acceptance_${Ts}.json")
Save-Json -path $accPath -obj $acc

# 3) Final summary JSON
$changesJs = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}.json")
$tele = $null; if(Test-Path -LiteralPath $teleJs){ $tele = Get-Content -LiteralPath $teleJs -Raw | ConvertFrom-Json }
$changes = $null; if(Test-Path -LiteralPath $changesJs){ $changes = Get-Content -LiteralPath $changesJs -Raw | ConvertFrom-Json }
$art = $null; if(Test-Path -LiteralPath $artPath){ $art = Get-Content -LiteralPath $artPath -Raw | ConvertFrom-Json }
$summary = [pscustomobject]@{ ts=$Ts; acceptance=$acc; artifacts=$art; telemetry=$tele; changes=$changes }
$reportJson = Join-Path -Path $pi -ChildPath ("copilot_report_${Ts}.json")
Save-Json -path $reportJson -obj $summary

Write-Host ("REPORT_JSON=" + $reportJson)
Write-Host ("ACCEPTANCE_JSON=" + $accPath)
