param([Parameter(Mandatory=$true)][string]$Ts)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Get-Location).Path
$pi = Join-Path -Path $root -ChildPath 'path_issues'
New-Item -ItemType Directory -Force -Path $pi | Out-Null

function Write-Step($step,$status,$notes,$artifact){
  $obj=[pscustomobject]@{step=$step;status=$status;notes=$notes;artifact=$artifact}
  $log=Join-Path -Path $pi -ChildPath ("agent_steps_${Ts}.log")
  ($obj|ConvertTo-Json -Compress) | Add-Content -LiteralPath $log -Encoding UTF8
}
function Save-Text($path,$text){ $dir = Split-Path -Parent $path; if ($dir){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }; Set-Content -LiteralPath $path -Value $text -Encoding UTF8 }
function Save-Json($path,$obj){ $json = $obj | ConvertTo-Json -Depth 10; Save-Text -path $path -text $json }
function FileMeta([string]$p){ if(Test-Path -LiteralPath $p){ $fi = Get-Item -LiteralPath $p; $sha=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash; return [pscustomobject]@{path=$p; exists=$true; size_bytes=$fi.Length; mtime_iso=$fi.LastWriteTimeUtc.ToString('o'); sha256=$sha} } else { return [pscustomobject]@{path=$p; exists=$false} } }

# 1) Git checks
try {
  $inside = $false; try { $inside = ((& git rev-parse --is-inside-work-tree 2>$null) -eq 'true') } catch { $inside=$false }
  if($inside){
    $gitStatusPath = Join-Path -Path $pi -ChildPath ("copilot_report_${Ts}_git_status.txt")
    $gitLogPath = Join-Path -Path $pi -ChildPath ("copilot_report_${Ts}_git_log.txt")
    $grepListPath = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}_commits.txt")
    (& git --no-pager status --porcelain) | Out-File -FilePath $gitStatusPath -Encoding utf8
    (& git --no-pager log -n 50 --pretty=format:'%H|%an|%ae|%ad|%s') | Out-File -FilePath $gitLogPath -Encoding utf8
    $grepList = & git --no-pager log --pretty=format:'%H|%an|%ae|%ad|%s' --grep='copilot' --grep='postrun' --grep='fix(postrun)'
    $grepList | Out-File -FilePath $grepListPath -Encoding utf8
    $shas = @(); if($grepList){ $shas = $grepList | ForEach-Object { ($_ -split '\|')[0] } | Where-Object { $_ -and $_ -match '^[0-9a-f]{7,}$' } | Select-Object -Unique }
    $filesMeta = @();
    foreach($sha in $shas){
      $diffPath = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}_${sha}.diff")
      (& git --no-pager show $sha) | Out-File -FilePath $diffPath -Encoding utf8 -Width 4096
      $names = & git --no-pager show $sha --name-only --pretty=format:'' | Where-Object { $_ -ne '' } | Sort-Object -Unique
      foreach($f in $names){
        $full = Join-Path -Path $root -ChildPath $f
        $meta = FileMeta -p $full
        $meta | Add-Member -NotePropertyName last_commit_sha -NotePropertyValue $sha
        $filesMeta += $meta
      }
    }
    $filesMetaPath = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}_files.json")
    Save-Json -path $filesMetaPath -obj $filesMeta

    $statusText = Get-Content -LiteralPath $gitStatusPath -Raw
    if($statusText.Trim().Length -gt 0){
      $uncDiffPath = Join-Path -Path $pi -ChildPath ("copilot_uncommitted_${Ts}.diff")
      (& git --no-pager diff) | Out-File -FilePath $uncDiffPath -Encoding utf8
      $uncListPath = Join-Path -Path $pi -ChildPath ("copilot_uncommitted_${Ts}.txt")
      $statusText | Out-File -FilePath $uncListPath -Encoding utf8
    }
    Write-Step 'git' 'ok' 'git info collected' $gitStatusPath
  } else {
    $fsList = @()
    $targets = @('scripts','Harness','Program.cs','path_issues')
    foreach($t in $targets){ $p = Join-Path -Path $root -ChildPath $t; if(Test-Path -LiteralPath $p){ Get-ChildItem -LiteralPath $p -Recurse -File | ForEach-Object { $fi=$_; $sha=(Get-FileHash -LiteralPath $fi.FullName -Algorithm SHA256).Hash; $fsList += [pscustomobject]@{path=$fi.FullName; size_bytes=$fi.Length; mtime_iso=$fi.LastWriteTimeUtc.ToString('o'); sha256=$sha} } } }
    $fsOut = Join-Path -Path $pi -ChildPath ("copilot_changes_fs_${Ts}.json"); Save-Json -path $fsOut -obj $fsList
    Write-Step 'git' 'ok' 'no git; filesystem snapshot captured' $fsOut
  }
} catch { Write-Step 'git' 'fail' $_.ToString() '' }

# 2) Changes summary
try {
  $changesMd = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}.md")
  $changesJson = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}.json")
  $summary = @()
  $cobjs = @()
  $grepListFile = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}_commits.txt")
  if (Test-Path -LiteralPath $grepListFile) {
    $commits = Get-Content -LiteralPath $grepListFile
    foreach($c in $commits){ if(-not $c){ continue }; $p=$c -split '\|',5; $sha=$p[0]; $author=$p[1]; $email=$p[2]; $date=$p[3]; $msg=$p[4];
      $files = & git --no-pager show $sha --name-only --pretty=format:'' | Where-Object { $_ -ne '' }
      $diffRel = "path_issues/" + ("copilot_changes_${Ts}_${sha}.diff")
      $summary += "- $sha | $author <$email> | $date | $msg"
      $summary += "  files: " + ($files -join ', ')
      $summary += "  diff: $diffRel"
      $cobjs += [pscustomobject]@{sha=$sha;author=$author;email=$email;date=$date;message=$msg;files=$files;diff_path=$diffRel}
    }
    Save-Text -path $changesMd -text ("# Copilot changes`n`n" + ($summary -join "`n"))
    Save-Json -path $changesJson -obj $cobjs
  } else { Save-Text -path $changesMd -text '# No matching commits found'; Save-Json -path $changesJson -obj @() }
  Write-Step 'changes' 'ok' 'summaries written' $changesMd
} catch { Write-Step 'changes' 'fail' $_.ToString() '' }

# 3) Artifacts & smoke
try {
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
  $records = @(); $missing = @()
  foreach($pat in $targets){ $items = Get-ChildItem -LiteralPath $pi -Filter $pat | Sort-Object LastWriteTime -Descending; if($items -and $items.Count -ge 1){ $item=$items[0]; $records += FileMeta -p $item.FullName } else { $missing += $pat } }
  # latest_zip referenced path
  $latestTxt = Join-Path -Path $pi -ChildPath 'latest_zip.txt'
  if (Test-Path -LiteralPath $latestTxt) { $zp = (Get-Content -LiteralPath $latestTxt -Raw).Trim(); if($zp){ $records += FileMeta -p $zp } }
  $artPath = Join-Path -Path $pi -ChildPath ("copilot_artifacts_${Ts}.json"); Save-Json -path $artPath -obj ([pscustomobject]@{files=$records;missing=$missing})

  # smoke summary parse
  $smoke = Get-ChildItem -LiteralPath $pi -Filter 'smoke_summary_*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if($smoke){ $sj = Get-Content -LiteralPath $smoke.FullName -Raw | ConvertFrom-Json; $parsed = [pscustomobject]@{path=$smoke.FullName; ts=$sj.ts; trades=$sj.trades; total_pnl=$sj.total_pnl; orphan_after=$sj.orphan_after; acceptance=$sj.acceptance; ordersRequested=$sj.ordersRequested; ordersFilled=$sj.ordersFilled; fill_rate=$sj.fill_rate; fill_count=$sj.fill_count; warnings=$sj.warnings; errors=$sj.errors }; $outP = Join-Path -Path $pi -ChildPath ("copilot_smoke_parsed_${Ts}.json"); Save-Json -path $outP -obj $parsed }

  # reconstruct report parse
  $recon = Join-Path -Path $pi -ChildPath 'reconstruct_report.json'
  if(Test-Path -LiteralPath $recon){ $rj = Get-Content -LiteralPath $recon -Raw | ConvertFrom-Json; $rparsed = [pscustomobject]@{ orphan_after=$rj.orphan_after; request_count=$rj.request_count; fill_count=$rj.fill_count; closed_trades_count=$rj.closed_trades_count; unmatched_orders_count=$rj.unmatched_orders_count; sum_pnl=$rj.sum_pnl }; $outR = Join-Path -Path $pi -ChildPath ("copilot_reconstruct_parsed_${Ts}.json"); Save-Json -path $outR -obj $rparsed }

  Write-Step 'artifacts' 'ok' 'artifact inventory created' $artPath
} catch { Write-Step 'artifacts' 'fail' $_.ToString() '' }

# 4) Telemetry ZIP analysis
try {
  $zipPath = $null
  $latestTxt = Join-Path -Path $pi -ChildPath 'latest_zip.txt'
  if (Test-Path -LiteralPath $latestTxt) { $zipPath = (Get-Content -LiteralPath $latestTxt -Raw).Trim() }
  if (-not $zipPath) {
    $artRoot = 'D:\botg\logs\artifacts'
    if (Test-Path -LiteralPath $artRoot) {
      $latestRun = Get-ChildItem -LiteralPath $artRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
      if ($latestRun) {
        $zipPath = Join-Path -Path $latestRun.FullName -ChildPath ($latestRun.Name + '.zip')
        if (-not (Test-Path -LiteralPath $zipPath)) { $z=Get-ChildItem -LiteralPath $latestRun.FullName -Filter '*.zip' | Select-Object -First 1; if($z){ $zipPath = $z.FullName } }
      }
    }
  }
  $teleJson = Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json")
  $teleMd = Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.md")
  if($zipPath -and (Test-Path -LiteralPath $zipPath)){
    $tmpRoot = Join-Path -Path $root -ChildPath ("tmp/copilot_telemetry_${Ts}")
    New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $tmpRoot -Force
    $orders = Join-Path -Path $tmpRoot -ChildPath 'orders.csv'
  # telemetry.csv path (optional)
  # $telemetry = Join-Path -Path $tmpRoot -ChildPath 'telemetry.csv'
    $closes = Join-Path -Path $tmpRoot -ChildPath 'trade_closes.log'
    $ordersCount=0;$phases=@{};$fillsMissingPrice=0;$fillsMissingSize=0;$fillsMissingLatency=0;$requests=0;$fills=0
    $rows = @()
    if (Test-Path -LiteralPath $orders) {
      $rows = Import-Csv -LiteralPath $orders
      $ordersCount = $rows.Count
      foreach($row in $rows){ $ph=$row.phase; if(-not $ph){ continue }; if(-not $phases.ContainsKey($ph)){ $phases[$ph]=0 }; $phases[$ph]++;
        if($ph -eq 'REQUEST'){ $requests++ }
        elseif($ph -eq 'FILL'){
          $fills++
          if(-not $row.price_filled){ $fillsMissingPrice++ }
          if(-not $row.size_filled){ $fillsMissingSize++ }
          if(-not $row.latency_ms){ $fillsMissingLatency++ }
        }
      }
    }
    $fillRate = if($requests -gt 0){ [math]::Round(($fills*100.0)/$requests,2) } else { 0 }
    $slipTop = @()
    if ($rows.Count -gt 0) {
      $slipTop = $rows | Where-Object { $_.phase -eq 'FILL' -and $_.slippage } | ForEach-Object { [pscustomobject]@{order_id=$_.order_id; ts=$_.timestamp_iso; slippage=[double]$_.slippage } } | Sort-Object { [math]::Abs($_.slippage) } -Descending | Select-Object -First 10
    }
    $pnlSum = 0.0
    if (Test-Path -LiteralPath $closes) {
      $lines = Get-Content -LiteralPath $closes
      foreach($ln in $lines){ $m = [regex]::Match($ln, 'pnl=([+-]?[0-9\.]+)'); if($m.Success){ $pnlSum += [double]$m.Groups[1].Value } }
    }
    $teleObj = [pscustomobject]@{
      zip_path = $zipPath
      orders_count = $ordersCount
      phases = $phases
      requests = $requests
      fills = $fills
      fill_rate = $fillRate
      fills_missing_price_filled = $fillsMissingPrice
      fills_missing_size_filled = $fillsMissingSize
      fills_missing_latency_ms = $fillsMissingLatency
      pnl_sum_from_closes = $pnlSum
      slippage_top10 = $slipTop
    }
    Save-Json -path $teleJson -obj $teleObj
    $md = "# Telemetry analysis`n`n- Zip: $zipPath`n- Orders: $ordersCount`n- Requests: $requests, Fills: $fills, Fill rate: $fillRate%`n- Missing on FILL: price=$fillsMissingPrice, size=$fillsMissingSize, latency_ms=$fillsMissingLatency`n- PnL (sum from trade_closes): $pnlSum`n`n## Top 10 slippage (abs)`n" + (($slipTop | ForEach-Object { "- $($_.ts) | $($_.order_id) | slippage=$($_.slippage)" }) -join "`n")
    Save-Text -path $teleMd -text $md
    Write-Step 'telemetry' 'ok' 'zip analyzed' $teleJson
  } else {
    Save-Json -path $teleJson -obj ([pscustomobject]@{error='zip not found'; tried=$zipPath})
    Write-Step 'telemetry' 'blocked' 'zip not found' $teleJson
  }
} catch { Write-Step 'telemetry' 'fail' $_.ToString() '' }

# 5) Security/config drift
try {
  $findings = @()
  $aclNotes = @()
  $targets = @('.github/workflows','scripts','Harness','runbook_postrun.md')
  foreach($t in $targets){ $p = Join-Path -Path $root -ChildPath $t; if(Test-Path -LiteralPath $p){ Get-ChildItem -LiteralPath $p -Recurse -File | ForEach-Object {
      $f=$_.FullName; $text = Get-Content -LiteralPath $f -Raw -ErrorAction SilentlyContinue
      if($text){
        if($text -match 'Invoke-RestMethod|Invoke-WebRequest|curl|UPLOAD_URL'){ $findings += [pscustomobject]@{file=$f; issue='network_upload_api_reference'} }
        if($text -match 'icacls|Set-Acl'){ $aclNotes += "[$f] matched ACL command" }
      }
    } } }
  $secJson = Join-Path -Path $pi -ChildPath ("copilot_security_${Ts}.json"); Save-Json -path $secJson -obj $findings
  if($aclNotes.Count -gt 0){ $aclTxt = Join-Path -Path $pi -ChildPath ("copilot_acl_changes_${Ts}.txt"); Save-Text -path $aclTxt -text ($aclNotes -join "`n") }
  Write-Step 'security' 'ok' 'security scan done' $secJson
} catch { Write-Step 'security' 'fail' $_.ToString() '' }

# 6) Acceptance gates
try {
  $buildTxt = Join-Path -Path $pi -ChildPath 'build_and_test_output.txt'
  $buildStatus = 'UNKNOWN'
  if(Test-Path -LiteralPath $buildTxt){ $c = Get-Content -LiteralPath $buildTxt -Raw; if($c -match 'PASS'){ $buildStatus='PASS' } elseif($c -match 'FAIL'){ $buildStatus='FAIL' } }
  $smokeParsed = Get-ChildItem -LiteralPath $pi -Filter ('copilot_smoke_parsed_' + $Ts + '.json') | Select-Object -First 1
  $orphan = $null; $fillRate = $null
  if($smokeParsed){ $sp = Get-Content -LiteralPath $smokeParsed.FullName -Raw | ConvertFrom-Json; $orphan=$sp.orphan_after; $fillRate=$sp.fill_rate }
  if(-not $fillRate){ if(Test-Path -LiteralPath (Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json"))){ $ta = Get-Content -LiteralPath (Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json")) -Raw | ConvertFrom-Json; $fillRate = $ta.fill_rate } }
  $reconParsed = Get-ChildItem -LiteralPath $pi -Filter ('copilot_reconstruct_parsed_*.json') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $unmatched = $null; $requests = $null
  if($reconParsed){ $rp = Get-Content -LiteralPath $reconParsed.FullName -Raw | ConvertFrom-Json; $unmatched=$rp.unmatched_orders_count; $requests=$rp.request_count }
  $reconGate = 'UNKNOWN'; if(($null -ne $unmatched) -and ($null -ne $requests) -and ($requests -gt 0)){ $rate=($unmatched*100.0/$requests); $reconGate = $(if($rate -le 1.0){'PASS'}else{'FAIL'}) }
  $logCompleteness = 'UNKNOWN'; $completeRate=$null
  if(Test-Path -LiteralPath (Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json"))){ $ta = Get-Content -LiteralPath (Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json")) -Raw | ConvertFrom-Json; if($ta.fills -gt 0){ $completeRate = [math]::Round((100.0 * ($ta.fills - [int]$ta.fills_missing_price_filled - [int]$ta.fills_missing_size_filled - [int]$ta.fills_missing_latency_ms) / $ta.fills),2); $logCompleteness = $(if($completeRate -ge 99.0){'PASS'}else{'PARTIAL'}) } }
  $smokeGate = 'UNKNOWN'; if(($null -ne $orphan) -and ($null -ne $fillRate)){ $smokeGate = $(if(($orphan -eq 0) -and ($fillRate -ge 95.0)){'PASS'}else{'FAIL'}) }
  $acc = [pscustomobject]@{ build=$buildStatus; smoke=$smokeGate; reconstruct=$reconGate; logging=$logCompleteness; fill_rate=$fillRate; orphan_after=$orphan; unmatched=$unmatched; requests=$requests; logging_complete_rate=$completeRate }
  $accPath = Join-Path -Path $pi -ChildPath ("copilot_acceptance_${Ts}.json"); Save-Json -path $accPath -obj $acc
  Write-Step 'acceptance' 'ok' 'gates evaluated' $accPath
} catch { Write-Step 'acceptance' 'fail' $_.ToString() '' }

# 7) Action items
try {
  $ai = @()
  $ai += '# High priority'
  $ai += '- Harden nested PowerShell invocations: always use call operator (&) with fully-qualified paths; avoid -File with Unicode paths.'
  $ai += '- Add schema checks for orders.csv (required columns: phase, order_id, price_filled, size_filled, latency_ms).'
  $ai += ''
  $ai += '# Medium'
  $ai += '- CI: ensure smoke artifacts are attached and a single aggregated status is posted.'
  $ai += '- Record SHA256 for produced zips in latest_zip.txt.'
  $ai += ''
  $ai += '# Commands'
  $ai += '```powershell'
  $ai += '# Re-run smoke'
  $ai += 'powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_smoke.ps1 -Seconds 120 -ArtifactPath D:\botg\logs\artifacts -FillProbability 1.0 -DrainSeconds 10 -UseSimulation'
  $ai += '# Re-run this report'
  $ai += ("powershell -NoProfile -ExecutionPolicy Bypass -File .\path_issues\copilot_report_runner.ps1 -Ts ${Ts}")
  $ai += '```'
  $aiPath = Join-Path -Path $pi -ChildPath ("copilot_action_items_${Ts}.md"); Save-Text -path $aiPath -text ($ai -join "`n")
  Write-Step 'actions' 'ok' 'action items written' $aiPath
} catch { Write-Step 'actions' 'fail' $_.ToString() '' }

# 8) Final report
try {
  $mdPath = Join-Path -Path $pi -ChildPath ("copilot_report_${Ts}.md")
  $jsonPath = Join-Path -Path $pi -ChildPath ("copilot_report_${Ts}.json")
  $accPath = Join-Path -Path $pi -ChildPath ("copilot_acceptance_${Ts}.json")
  $artPath = Join-Path -Path $pi -ChildPath ("copilot_artifacts_${Ts}.json")
  $teleMd = Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.md")
  $teleJs = Join-Path -Path $pi -ChildPath ("copilot_telemetry_analysis_${Ts}.json")
  $changesMd = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}.md")
  $changesJs = Join-Path -Path $pi -ChildPath ("copilot_changes_${Ts}.json")
  $aiPath = Join-Path -Path $pi -ChildPath ("copilot_action_items_${Ts}.md")
  $md = @()
  $md += '# Postrun Report'
  $md += "- Timestamp: ${Ts}"
  $accSmoke = 'UNKNOWN'; if(Test-Path -LiteralPath $accPath){ $accObj = Get-Content -LiteralPath $accPath -Raw | ConvertFrom-Json; $accSmoke = $accObj.smoke }
  $md += ("- Acceptance (smoke): " + $accSmoke)
  $md += ''
  $md += '## Changes'
  if(Test-Path -LiteralPath $changesMd){ $md += (Get-Content -LiteralPath $changesMd) }
  $md += ''
  $md += '## Artifacts'
  if(Test-Path -LiteralPath $artPath){ $md += (Get-Content -LiteralPath $artPath -Raw) }
  $md += ''
  $md += '## Telemetry analysis'
  if(Test-Path -LiteralPath $teleMd){ $md += (Get-Content -LiteralPath $teleMd -Raw) }
  $md += ''
  $md += '## Action items'
  if(Test-Path -LiteralPath $aiPath){ $md += (Get-Content -LiteralPath $aiPath -Raw) }
  Save-Text -path $mdPath -text ($md -join "`n")
  $summary = [pscustomobject]@{ ts=$Ts; acceptance=(if(Test-Path -LiteralPath $accPath){ Get-Content -LiteralPath $accPath -Raw | ConvertFrom-Json } else { $null }); artifacts=(if(Test-Path -LiteralPath $artPath){ Get-Content -LiteralPath $artPath -Raw | ConvertFrom-Json } else { $null }); telemetry=(if(Test-Path -LiteralPath $teleJs){ Get-Content -LiteralPath $teleJs -Raw | ConvertFrom-Json } else { $null }); changes=(if(Test-Path -LiteralPath $changesJs){ Get-Content -LiteralPath $changesJs -Raw | ConvertFrom-Json } else { $null }) }
  Save-Json -path $jsonPath -obj $summary
  Write-Host ("REPORT_MD=" + $mdPath)
  Write-Host ("REPORT_JSON=" + $jsonPath)
  Write-Step 'report' 'ok' 'final report written' $mdPath
} catch { Write-Step 'report' 'fail' $_.ToString() '' }
