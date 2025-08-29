<#
Postrun audit runner: collects git info, artifacts, smoke/reconstruct metrics, telemetry zip analysis, security scan, acceptance gates, and writes reports under path_issues/ with prefix copilot_report_<ts>_*
Compatible with Windows PowerShell 5.1.
#>
param(
  [string]$RootPath,
  [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function New-StepLog([string]$outDir, [string]$ts) { return (Join-Path -Path $outDir -ChildPath ("agent_steps_{0}.log" -f $ts)) }
function Append-Step([string]$stepsLog, [string]$step, [string]$status, [string]$notes=$null, [string]$artifact=$null) {
  $o = @{ step=$step; status=$status }
  if ($notes) { $o.notes = $notes }
  if ($artifact) { $o.artifact = $artifact }
  ($o | ConvertTo-Json -Compress) | Add-Content -LiteralPath $stepsLog
}

function Ensure-Dir([string]$p) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Force -Path $p | Out-Null } }

function Get-LatestFile([string]$glob) {
  $files = Get-ChildItem -Path $glob -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
  if ($files) { return $files[0].FullName } else { return $null }
}

function Safe-ReadJson([string]$path) { if ($path -and (Test-Path -LiteralPath $path)) { return (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json) } return $null }

function Hash-File([string]$path) { if (-not (Test-Path -LiteralPath $path)) { return $null } (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function Analyze-OrdersCsv([string]$ordersCsv) {
  $res = @{ total_rows=0; headers=$null; requests=0; acks=0; fills=0; missing_price_filled=0; missing_size_filled=0; missing_latency_ms=0; fill_rate=null }
  if (-not (Test-Path -LiteralPath $ordersCsv)) { return $res }
  $rows = Import-Csv -LiteralPath $ordersCsv
  if ($rows) {
    $res.headers = ($rows[0].PSObject.Properties.Name)
    foreach ($r in $rows) {
      $res.total_rows++
      switch ($r.phase) { 'REQUEST' { $res.requests++ } 'ACK' { $res.acks++ } 'FILL' { $res.fills++; if (-not $r.price_filled) { $res.missing_price_filled++ }; if (-not $r.size_filled) { $res.missing_size_filled++ }; if (-not $r.latency_ms) { $res.missing_latency_ms++ } } default { } }
    }
    if ($res.requests -gt 0) { $res.fill_rate = [math]::Round(($res.fills*100.0)/$res.requests, 2) }
  }
  return $res
}

function Sum-PnlFromTradeCloses([string]$logPath) {
  $sum = 0.0; $count = 0
  if (-not (Test-Path -LiteralPath $logPath)) { return @{ rows=0; sum_pnl=0.0 } }
  $lines = Get-Content -LiteralPath $logPath
  foreach ($ln in $lines) {
    if ($ln -match 'pnl=([-+]?\d+(?:\.\d+)?)') { $sum += [double]$Matches[1]; $count++ }
  }
  return @{ rows=$count; sum_pnl=$sum }
}

try {
  $root = if ($RootPath) { $RootPath } else { (Get-Location).Path }
  Set-Location -LiteralPath $root
  $outDir = Join-Path -Path $root -ChildPath 'path_issues'
  Ensure-Dir $outDir
  $ts = Get-Date -Format 'yyyyMMdd_HHmmss'
  Set-Content -LiteralPath (Join-Path -Path $outDir -ChildPath '.ts') -Value $ts
  $stepsLog = New-StepLog -outDir $outDir -ts $ts
  Append-Step -stepsLog $stepsLog -step 'init' -status 'ok' -notes 'start postrun audit' -artifact $outDir

  # 1) Git
  $insideGit = $false
  try { $insideGit = (git rev-parse --is-inside-work-tree) -eq 'true' } catch { $insideGit = $false }
  if ($insideGit) {
    $gitStatusFile = Join-Path $outDir ("copilot_report_{0}_git_status.txt" -f $ts)
    git --no-pager status --porcelain | Out-File -Encoding UTF8 -FilePath $gitStatusFile
    Append-Step $stepsLog 'git-status' 'ok' $null $gitStatusFile

    $gitLogFile = Join-Path $outDir ("copilot_report_{0}_git_log.txt" -f $ts)
    git --no-pager log -n 50 --pretty=format:'%H|%an|%ae|%ad|%s' | Out-File -Encoding UTF8 -FilePath $gitLogFile
    Append-Step $stepsLog 'git-log' 'ok' $null $gitLogFile

    $grepFile = Join-Path $outDir ("copilot_changes_{0}_commits.txt" -f $ts)
    git --no-pager log --pretty=format:'%H|%an|%ae|%ad|%s' --grep='copilot' --grep='postrun' --grep='fix(postrun)' | Out-File -Encoding UTF8 -FilePath $grepFile
    Append-Step $stepsLog 'git-grep' 'ok' $null $grepFile

    $commitLines = @(); if (Test-Path -LiteralPath $grepFile) { $commitLines = Get-Content -LiteralPath $grepFile }
    $filesList = @()
    $commitObjs = @()
    foreach ($line in $commitLines) {
      if ($line -match '^(?<sha>[0-9a-f]{40})\|(?<an>[^|]*)\|(?<ae>[^|]*)\|(?<ad>[^|]*)\|(?<s>.*)$') {
        $sha=$Matches['sha']; $an=$Matches['an']; $ae=$Matches['ae']; $ad=$Matches['ad']; $msg=$Matches['s']
        $diffPath = Join-Path $outDir ("copilot_changes_{0}_{1}.diff" -f $ts, $sha)
        git --no-pager show $sha | Out-File -Encoding UTF8 -FilePath $diffPath
        Append-Step $stepsLog 'git-show' 'ok' $null $diffPath
        $names = (git --no-pager show $sha --name-only --pretty=format:'') | Where-Object { $_ -and $_.Trim().Length -gt 0 }
        $commitObjs += [pscustomobject]@{ sha=$sha; author=$an; email=$ae; date=$ad; subject=$msg; files=$names; diff_path=$diffPath }
        foreach ($n in $names) {
          $full = Join-Path -Path $root -ChildPath $n
          $exists = Test-Path -LiteralPath $full
          $sha256 = $null; $size=0; $mtime=$null
          if ($exists) { $h=Get-FileHash -LiteralPath $full -Algorithm SHA256; $sha256=$h.Hash; $fi=Get-Item -LiteralPath $full; $size=$fi.Length; $mtime=$fi.LastWriteTimeUtc.ToString('o') }
          $filesList += [pscustomobject]@{ file=$n; last_commit_sha=$sha; current_sha256=$sha256; last_modified_iso=$mtime; size_bytes=$size; exists=$exists }
        }
      }
    }
    $filesJsonFile = Join-Path $outDir ("copilot_changes_{0}_files.json" -f $ts)
    $filesList | ConvertTo-Json -Depth 6 | Out-File -Encoding UTF8 -FilePath $filesJsonFile
    Append-Step $stepsLog 'git-files-json' 'ok' $null $filesJsonFile

    $statusLines = Get-Content -LiteralPath $gitStatusFile
    if ($statusLines -and $statusLines.Length -gt 0) {
      $uncommittedDiff = Join-Path $outDir ("copilot_uncommitted_{0}.diff" -f $ts)
      git --no-pager diff | Out-File -Encoding UTF8 -FilePath $uncommittedDiff
      Append-Step $stepsLog 'git-diff' 'ok' $null $uncommittedDiff
    } else { Append-Step $stepsLog 'git-diff' 'ok' 'clean' }
  } else {
    Append-Step $stepsLog 'git' 'blocked' 'not a git repo'
    $commitObjs = @(); $filesList=@()
  }

  # 2) Changes markdown/json
  $changesJson = [pscustomobject]@{ commits=$commitObjs; files=$filesList }
  $changesJsonPath = Join-Path $outDir ("copilot_changes_{0}.json" -f $ts)
  $changesJson | ConvertTo-Json -Depth 8 | Out-File -Encoding UTF8 -FilePath $changesJsonPath
  $changesMdPath = Join-Path $outDir ("copilot_changes_{0}.md" -f $ts)
  $md = @()
  foreach ($c in $commitObjs) {
    $md += ("### Commit {0}" -f $c.sha)
    $md += ("- Author: {0} <{1}>" -f $c.author, $c.email)
    $md += ("- Date: {0}" -f $c.date)
    $md += ("- Message: {0}" -f $c.subject)
    if ($c.files) { $md += "- Files:"; foreach ($f in $c.files) { $md += ("  - {0}" -f $f) } }
    $md += ("- Diff: {0}" -f $c.diff_path)
    $md += ""
  }
  if ($filesList.Count -gt 0) { $md += "## File hashes"; foreach ($f in $filesList) { $md += ("- {0} | sha256={1} | size={2}" -f $f.file, $f.current_sha256, $f.size_bytes) } }
  $md | Out-File -Encoding UTF8 -FilePath $changesMdPath
  Append-Step $stepsLog 'changes' 'ok' $null $changesMdPath

  # 3) Artifacts & smoke
  $artifactRecords = @(); $missing = @()
  $targets = @(
    'path_issues/pre_run_readiness_*.json',
    'path_issues/agent_steps_*.log',
    'path_issues/smoke_summary_*.json',
    'path_issues/reconstruct_report.json',
    'path_issues/closed_trades_fifo_reconstructed.csv',
    'path_issues/slip_latency_percentiles.json',
    'path_issues/slippage_hist.png',
    'path_issues/latency_percentiles.png',
    'path_issues/latest_zip.txt'
  )
  foreach ($pat in $targets) {
    $list = Get-ChildItem -Path $pat -ErrorAction SilentlyContinue
    if (-not $list) { $missing += $pat; continue }
    foreach ($fi in $list) {
      $artifactRecords += [pscustomobject]@{
        path = $fi.FullName
        sha256 = (Hash-File $fi.FullName)
        size_bytes = $fi.Length
        mtime_iso = $fi.LastWriteTimeUtc.ToString('o')
      }
    }
  }
  $artifactsJsonPath = Join-Path $outDir ("copilot_artifacts_{0}.json" -f $ts)
  [pscustomobject]@{ found=$artifactRecords; missing=$missing } | ConvertTo-Json -Depth 6 | Out-File -Encoding UTF8 -FilePath $artifactsJsonPath
  Append-Step $stepsLog 'artifacts' 'ok' $null $artifactsJsonPath

  $smokeSummaryPath = Get-LatestFile 'path_issues\smoke_summary_*.json'
  $smokeObj = Safe-ReadJson $smokeSummaryPath
  $smokeParsed = $null
  if ($smokeObj) {
    $smokeParsed = [pscustomobject]@{
      ts = $smokeObj.ts
      run_dir = $smokeObj.run_dir
      smoke_zip = $smokeObj.smoke_zip
      trades = $smokeObj.trades
      total_pnl = $smokeObj.total_pnl
      orphan_after = $smokeObj.orphan_after
      acceptance = $smokeObj.acceptance
      ordersRequested = $null
      ordersFilled = $null
      fill_rate = $null
      fill_count = $null
      errors = @()
      warnings = @()
    }
  }
  $smokeParsedPath = Join-Path $outDir ("copilot_smoke_parsed_{0}.json" -f $ts)
  ($smokeParsed | ConvertTo-Json -Depth 6) | Out-File -Encoding UTF8 -FilePath $smokeParsedPath
  Append-Step $stepsLog 'smoke-parse' 'ok' $null $smokeParsedPath

  $reconPath = 'path_issues\reconstruct_report.json'
  $reconObj = Safe-ReadJson $reconPath
  $reconParsed = $null
  if ($reconObj) {
    $reconParsed = [pscustomobject]@{
      orphan_after = $reconObj.orphan_after
      request_count = $reconObj.request_count
      fill_count = $reconObj.fill_count
      closed_trades_count = $reconObj.closed_trades_count
      unmatched_orders_count = $reconObj.unmatched_orders_count
      sum_pnl = $reconObj.sum_pnl
    }
  }
  $reconParsedPath = Join-Path $outDir ("copilot_reconstruct_parsed_{0}.json" -f $ts)
  ($reconParsed | ConvertTo-Json -Depth 6) | Out-File -Encoding UTF8 -FilePath $reconParsedPath
  Append-Step $stepsLog 'reconstruct-parse' 'ok' $null $reconParsedPath

  # 4) Telemetry zip analysis
  $zip = $ZipPath
  $latestZipTxt = 'path_issues\latest_zip.txt'
  if (-not $zip -and (Test-Path -LiteralPath $latestZipTxt)) { $zip = (Get-Content -Raw -LiteralPath $latestZipTxt).Trim() }
  if (-not $zip -and $smokeParsed -and $smokeParsed.smoke_zip -and (Test-Path -LiteralPath $smokeParsed.smoke_zip)) { $zip = $smokeParsed.smoke_zip }

  $telemetryJsonPath = Join-Path $outDir ("copilot_telemetry_analysis_{0}.json" -f $ts)
  $telemetryMdPath = Join-Path $outDir ("copilot_telemetry_analysis_{0}.md" -f $ts)
  if ($zip -and (Test-Path -LiteralPath $zip)) {
    $tmpDir = Join-Path $root ("tmp\copilot_telemetry_{0}" -f $ts)
    Ensure-Dir $tmpDir
    try { Expand-Archive -LiteralPath $zip -DestinationPath $tmpDir -Force } catch { }
    $ordersCsv = Join-Path $tmpDir 'orders.csv'
    $telemetryCsv = Join-Path $tmpDir 'telemetry.csv'
    $tradeCloses = Join-Path $tmpDir 'trade_closes.log'
    $ordersInfo = Analyze-OrdersCsv $ordersCsv
    $pnlInfo = Sum-PnlFromTradeCloses $tradeCloses
    $topFiles = (Get-ChildItem -Path $tmpDir | Select-Object -First 20 | ForEach-Object { $_.Name })
    $telemetryObj = [pscustomobject]@{
      zip = $zip
      extracted_dir = $tmpDir
      top_files = $topFiles
      orders = $ordersInfo
      trade_closes = $pnlInfo
    }
    $telemetryObj | ConvertTo-Json -Depth 8 | Out-File -Encoding UTF8 -FilePath $telemetryJsonPath
    $mdt = @("# Telemetry analysis", "- Zip: $zip", "- Extracted: $tmpDir", "- Top files: " + ($topFiles -join ', '), "- Orders: requests=$($ordersInfo.requests) fills=$($ordersInfo.fills) fill_rate=$($ordersInfo.fill_rate)% missing_price_filled=$($ordersInfo.missing_price_filled) missing_size_filled=$($ordersInfo.missing_size_filled) missing_latency_ms=$($ordersInfo.missing_latency_ms)", "- Trade closes: rows=$($pnlInfo.rows) sum_pnl=$($pnlInfo.sum_pnl)")
    $mdt | Out-File -Encoding UTF8 -FilePath $telemetryMdPath
    Append-Step $stepsLog 'telemetry' 'ok' $null $telemetryJsonPath
  } else {
    Set-Content -LiteralPath $telemetryJsonPath -Value (ConvertTo-Json @{ note = 'zip not available'; zip=$zip })
    Set-Content -LiteralPath $telemetryMdPath -Value "Zip not available: $zip"
    Append-Step $stepsLog 'telemetry' 'blocked' 'zip not available' $telemetryJsonPath
  }

  # 5) Security / config drift
  $securityHitsPath = Join-Path $outDir ("copilot_acl_changes_{0}.txt" -f $ts)
  $patterns = 'UPLOAD_URL','Invoke-RestMethod','Invoke-WebRequest','curl','icacls','Set-Acl'
  try { Select-String -Path (Join-Path $root '*') -Pattern $patterns -SimpleMatch -Recurse -ErrorAction SilentlyContinue | Out-File -Encoding UTF8 -FilePath $securityHitsPath } catch { '' | Out-File -Encoding UTF8 -FilePath $securityHitsPath }
  Append-Step $stepsLog 'security-scan' 'ok' $null $securityHitsPath

  # 6) Acceptance gates
  $buildStatus = $null
  $buildOut = Get-LatestFile 'path_issues\build_and_test_output.txt'
  if ($buildOut) { $buildStatus = if ((Select-String -Path $buildOut -Pattern 'PASS' -SimpleMatch -Quiet)) { 'PASS' } else { 'UNKNOWN' } }
  $smokeStatus = 'UNKNOWN'
  $fillRate = $null; $fills=$null; $requests=$null; if ($telemetryJsonPath -and (Test-Path $telemetryJsonPath)) { $t=Safe-ReadJson $telemetryJsonPath; if ($t -and $t.orders) { $requests=$t.orders.requests; $fills=$t.orders.fills; $fillRate=$t.orders.fill_rate } }
  $orphanAfter = $null; if ($smokeParsed) { $orphanAfter = $smokeParsed.orphan_after }
  if ($orphanAfter -ne $null -and $fillRate -ne $null) { $smokeStatus = if (($orphanAfter -eq 0) -and ($fillRate -ge 95)) { 'PASS' } else { 'FAIL' } }
  $reconStatus = 'UNKNOWN'; $unmatchedPct = $null
  if ($reconParsed -and $reconParsed.request_count) { if ($reconParsed.request_count -gt 0) { $unmatchedPct = [math]::Round((100.0 * [double]$reconParsed.unmatched_orders_count) / [double]$reconParsed.request_count, 3) } $reconStatus = if ($unmatchedPct -ne $null -and $unmatchedPct -le 1.0) { 'PASS' } else { 'FAIL' } }
  $loggingStatus = 'UNKNOWN'
  if ($fills -ne $null) { $missingAll = 0; if ($t.orders) { $missingAll = [int]$t.orders.missing_price_filled + [int]$t.orders.missing_size_filled + [int]$t.orders.missing_latency_ms }
    $good = $fills - [math]::Max([int]$t.orders.missing_price_filled,[int]$t.orders.missing_size_filled)
    $pct = if ($fills -gt 0) { [math]::Round(100.0*$good/$fills,2) } else { $null }
    if ($pct -ne $null) { $loggingStatus = if ($pct -ge 99) { 'PASS' } else { 'PARTIAL' } }
  }
  $acceptance = [pscustomobject]@{
    build = $buildStatus
    smoke = $smokeStatus
    reconstruct = $reconStatus
    logging = $loggingStatus
    metrics = @{ requests=$requests; fills=$fills; fill_rate=$fillRate; orphan_after=$orphanAfter; unmatched_pct=$unmatchedPct }
    verdict = if (($buildStatus -eq 'PASS') -and ($smokeStatus -eq 'PASS') -and ($reconStatus -eq 'PASS') -and ($loggingStatus -eq 'PASS')) { 'PASS' } else { 'PARTIAL' }
  }
  $acceptPath = Join-Path $outDir ("copilot_acceptance_{0}.json" -f $ts)
  $acceptance | ConvertTo-Json -Depth 6 | Out-File -Encoding UTF8 -FilePath $acceptPath
  Append-Step $stepsLog 'acceptance' 'ok' $null $acceptPath

  # 7) Action items (lightweight)
  $actionsMd = @('# Actionable items', '## High priority', '- Verify git commits tagged with copilot/postrun and review diffs saved under path_issues.', '- Keep artifacts and zip for audit trail.','## Medium/Low','- Consider enriching smoke_summary with ordersRequested/filled to avoid later enrichment.')
  $actionsPath = Join-Path $outDir ("copilot_action_items_{0}.md" -f $ts)
  $actionsMd | Out-File -Encoding UTF8 -FilePath $actionsPath
  Append-Step $stepsLog 'actions' 'ok' $null $actionsPath

  # 8) Final reports
  $reportJson = [pscustomobject]@{
    ts = $ts
    changes = @{ json=$changesJsonPath; md=$changesMdPath }
    git = @{ status = if ($insideGit) { 'inside' } else { 'outside' } }
    artifacts = $artifactsJsonPath
    smoke_parsed = $smokeParsedPath
    reconstruct_parsed = $reconParsedPath
    telemetry = @{ json=$telemetryJsonPath; md=$telemetryMdPath }
    acceptance = $acceptPath
    actions = $actionsPath
  }
  $reportJsonPath = Join-Path $outDir ("copilot_report_{0}.json" -f $ts)
  $reportJson | ConvertTo-Json -Depth 8 | Out-File -Encoding UTF8 -FilePath $reportJsonPath

  $reportMd = @('# Copilot Postrun Report', "- Timestamp: $ts", ("- Root: {0}" -f $root), ("- Inside Git: {0}" -f $insideGit), '', '## Sections', "- Changes: $changesMdPath", "- Artifacts: $artifactsJsonPath", "- Telemetry: $telemetryMdPath", "- Acceptance: $acceptPath", "- Actions: $actionsPath")
  $reportMdPath = Join-Path $outDir ("copilot_report_{0}.md" -f $ts)
  $reportMd | Out-File -Encoding UTF8 -FilePath $reportMdPath
  Append-Step $stepsLog 'report' 'ok' $null $reportMdPath

  Write-Host ("REPORT_JSON=" + $reportJsonPath)
  Write-Host ("REPORT_MD=" + $reportMdPath)
}
catch {
  Write-Error $_
  exit 1
}
