<# scripts/run_reconcile_fix.ps1 #>
$ErrorActionPreference = 'Continue'

$art = ".\artifacts\telemetry_run_20250819_154459"
$py = "python"

Write-Host "1) Run make_closes_from_reconstructed.py" -ForegroundColor Cyan
try {
  & $py .\scripts\make_closes_from_reconstructed.py 2>&1 | Tee-Object -FilePath (Join-Path $art 'make_closes_run.log')
}
catch {
  $_ | Out-File -FilePath (Join-Path $art 'make_closes_error.log') -Encoding utf8
  Write-Host "make_closes_from_reconstructed failed; see make_closes_error.log" -ForegroundColor Red
  exit 3
}

Write-Host "2) Inspect reconcile.py for --closes expected format" -ForegroundColor Cyan
try {
  Select-String -Path .\scripts\reconcile.py -Pattern "closes" -Context 2,2 | Tee-Object -FilePath (Join-Path $art 'reconcile_inspect_closes.txt')
}
catch {
}

Write-Host "3) Try running reconcile with CSV closes (cleaned)" -ForegroundColor Cyan
$closed = Join-Path $art 'closed_trades_fifo_reconstructed_cleaned.csv'
$closes_csv = Join-Path $art 'trade_closes_like_from_reconstructed.csv'
$recon_out = Join-Path $art 'reconcile_fix_report.json'

if ((Test-Path $closed) -and (Test-Path $closes_csv)) {
  Write-Host "Running reconcile.py with CSV closes..."
  try {
    & $py .\scripts\reconcile.py --closed $closed --closes $closes_csv --risk (Join-Path $art 'risk_snapshots.csv') 2>&1 | Tee-Object -FilePath $recon_out
  }
  catch {
    $_ | Out-File -FilePath (Join-Path $art 'reconcile_err.txt') -Encoding utf8
  }
  Write-Host "Reconcile attempted; output saved to $recon_out"
}
else {
  Write-Host "Missing cleaned closed or closes CSV, aborting reconcile attempt" -ForegroundColor Yellow
}

Write-Host "4) If reconcile produced closes_sum=0, retry with JSONL closes" -ForegroundColor Cyan
$cs = $null
if (Test-Path $recon_out) {
  try {
    $tmp = Get-Content $recon_out -Raw | ConvertFrom-Json
    if ($tmp) { $cs = $tmp.closes_sum }
  } catch {}
}
if ([string]$cs -eq '0' -or [string]$cs -eq '0.0') {
  Write-Host "closes_sum appears to be 0 - trying JSONL fallback"
  $closes_jsonl = Join-Path $art 'trade_closes_like_from_reconstructed.jsonl'
  if (Test-Path $closes_jsonl) {
    try {
      & $py .\scripts\reconcile.py --closed $closed --closes $closes_jsonl --risk (Join-Path $art 'risk_snapshots.csv') 2>&1 | Tee-Object -FilePath (Join-Path $art 'reconcile_fix_report_jsonl.json')
    } catch {
      $_ | Out-File -FilePath (Join-Path $art 'reconcile_err.txt') -Encoding utf8
    }
    Write-Host "Reconcile retried with JSONL"
  } else {
    Write-Host "JSONL closes not found: $closes_jsonl" -ForegroundColor Yellow
  }
} else {
  Write-Host "reconcile_fix_report.json does not show closes_sum=0 (or file missing)" -ForegroundColor Green
}

Write-Host "5) Run compute_fill_breakdown on a 10k sample of orders.csv" -ForegroundColor Cyan
$orders = Join-Path $art 'orders.csv'
$sample = Join-Path $art 'orders_sample_10k.csv'
if (Test-Path $orders) {
  try {
    Get-Content $orders -TotalCount 1 | Out-File -FilePath $sample -Encoding utf8
    Get-Content $orders | Select-Object -Skip 1 -First 10000 | Add-Content -Path $sample -Encoding utf8
    Write-Host "Created sample at $sample"
  }
  catch {
  }
  $compute_log = Join-Path $art 'compute_fill_breakdown_sample.log'
  if (Test-Path .\scripts\compute_fill_breakdown.ps1) {
    Write-Host "Running compute_fill_breakdown.ps1 on sample"
    try {
      & .\scripts\compute_fill_breakdown.ps1 -OrdersCsv $sample -OutDir $art -Verbose 2>&1 | Tee-Object -FilePath $compute_log
    }
    catch {
      $_ | Tee-Object -FilePath $compute_log
    }
  }
  elseif (Test-Path .\scripts\compute_fill_breakdown.py) {
    Write-Host "Running compute_fill_breakdown.py (python) on sample"
    & $py -u .\scripts\compute_fill_breakdown.py --orders $sample --outdir $art 2>&1 | Tee-Object -FilePath $compute_log
  }
  else {
    Write-Host "No compute_fill_breakdown script found in scripts/" -ForegroundColor Yellow
  }
}
else {
  Write-Host "orders.csv not found: $orders" -ForegroundColor Yellow
}

# Final summary
$logs = @()
foreach ($f in 'make_closes_run.log','reconcile_fix_report.json','reconcile_fix_report_jsonl.json','reconcile_err.txt','compute_fill_breakdown_sample.log','reconcile_inspect_closes.txt') {
  $p = Join-Path $art $f
  if (Test-Path $p) { $logs += $p }
}

# Compose summary
$closed_sum = $null
$closes_sum = $null
try {
  $obj = Get-Content (Join-Path $art 'reconcile_fix_report.json') -Raw | ConvertFrom-Json
  $closed_sum = $obj.closed_sum
  $closes_sum = $obj.closes_sum
}
catch {
}
if (-not $closes_sum) {
  try {
    $obj = Get-Content (Join-Path $art 'reconcile_fix_report_jsonl.json') -Raw | ConvertFrom-Json
    $closed_sum = $obj.closed_sum
    $closes_sum = $obj.closes_sum
  }
  catch {
  }
}

$cleaned = 0
if (Test-Path (Join-Path $art 'closed_trades_fifo_reconstructed_cleaned.csv')) {
  try {
    $cleaned = (Import-Csv (Join-Path $art 'closed_trades_fifo_reconstructed_cleaned.csv')).Count
  }
  catch {
  }
}
$dup_count = 0
if (Test-Path (Join-Path $art 'duplicate_groups_from_reconstructed.csv')) {
  try {
    $dup_count = (Import-Csv (Join-Path $art 'duplicate_groups_from_reconstructed.csv')).Count
  }
  catch {
  }
}

$diff = $null
if (($null -ne $closed_sum) -and ($null -ne $closes_sum)) {
  $diff = [double]$closed_sum - [double]$closes_sum
}

$summary = @{ cleaned_rows = $cleaned; duplicate_groups_count = $dup_count; closed_sum = $closed_sum; closes_sum = $closes_sum; diff = $diff; outputs = $logs }
$summaryTxt = Join-Path $art 'auto_reconcile_summary.txt'
$summary | ConvertTo-Json -Depth 5 | Tee-Object -FilePath $summaryTxt

Write-Host ($summary | ConvertTo-Json -Depth 5)
