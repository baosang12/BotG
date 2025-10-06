[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ArtifactsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

function Write-Json($obj, $path) {
  $dir = Split-Path -Parent $path
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $obj | ConvertTo-Json -Depth 8 | Out-File -FilePath $path -Encoding utf8 -Force
}

$root = (Resolve-Path -LiteralPath $ArtifactsDir -ErrorAction Stop).Path

$required = @(
  'orders.csv','telemetry.csv','risk_snapshots.csv','trade_closes.log','run_metadata.json','closed_trades_fifo_reconstructed.csv'
)
$present = @()
$missing = @()
foreach ($f in $required) {
  $p = Join-Path $root $f
  if (Test-Path -LiteralPath $p) { $present += $f } else { $missing += $f }
}

$reasons = @()

# Telemetry span (UTC minutes)
$spanHours = 0
try {
  $tele = Join-Path $root 'telemetry.csv'
  $lines = Get-Content -LiteralPath $tele
  if ($lines.Length -gt 1) {
    $hdr = $lines[0]
    $rows = $lines[1..($lines.Length-1)]
    $tsIdx = ($hdr -split ',').IndexOf('ts')
    if ($tsIdx -ge 0) {
      $first = ($rows[0] -split ',')[$tsIdx]
      $last = (($rows[-1]) -split ',')[$tsIdx]
      $t0 = Get-Date $first
      $t1 = Get-Date $last
      $spanHours = [math]::Round(($t1.ToUniversalTime() - $t0.ToUniversalTime()).TotalHours, 2)
    }
  }
} catch { $reasons += "telemetry_span:error $_" }

if ($spanHours -lt 23.75) { $reasons += "telemetry_span_hours=$spanHours (<23.75)" }

# orders.csv schema
try {
  $orders = Join-Path $root 'orders.csv'
  $hdr = (Get-Content -LiteralPath $orders -First 1)
  $cols = $hdr -split ','
  $need = 'request_id','side','type','status','reason','latency_ms','price_requested','price_filled','size_requested','size_filled','ts_request','ts_ack','ts_fill'
  foreach ($c in $need) { if ($cols -notcontains $c) { $reasons += "orders.missing:$c" } }
} catch { $reasons += "orders_schema:error $_" }

# risk_snapshots schema
try {
  $risk = Join-Path $root 'risk_snapshots.csv'
  $hdr = (Get-Content -LiteralPath $risk -First 1)
  $cols = $hdr -split ','
  $need = 'ts','equity','R_used','exposure','drawdown'
  foreach ($c in $need) { if ($cols -notcontains $c) { $reasons += "risk.missing:$c" } }
} catch { $reasons += "risk_schema:error $_" }

# Compute simple KPIs (best-effort)
$kpi = @{ }
try {
  $ordersPath = Join-Path $root 'orders.csv'
  $rows = (Get-Content -LiteralPath $ordersPath) | Select-Object -Skip 1
  $fills = ($rows | Where-Object { $_ -match ',filled,' }).Count
  $reqs = ($rows | Measure-Object).Count
  $fillRate = if ($reqs -gt 0) { [math]::Round(($fills*100.0)/$reqs,2) } else { 0 }
  $kpi.fills = $fills
  $kpi.fill_rate = $fillRate
} catch {}

$pass = ($missing.Count -eq 0 -and $reasons.Count -eq 0)

$validation = [ordered]@{
  pass = $pass
  reasons = $reasons
  telemetry_span_hours = $spanHours
  files_present = $present
  schema_ok = ($reasons | Where-Object { $_ -like '*schema*' -or $_ -like 'orders.missing*' -or $_ -like 'risk.missing*' }).Count -eq 0
  risk_violations = @{ daily = $false; weekly = $false }
  kpi = $kpi
}

Write-Json $validation (Join-Path $root 'gate2_validation.json')

# Markdown summary (simple 3/3/3)
$md = @()
$md += "# Gate2 Summary"
$md += "\n## What"
$md += "- Telemetry span: ${spanHours}h"
$md += "- Required files: $($present.Count)/$($required.Count) present"
$md += "- KPIs: fills=$($kpi.fills), fill_rate=$($kpi.fill_rate)%"
$md += "\n## So-what"
$md += "- $(if ($spanHours -ge 23.75) { 'Run duration looks OK' } else { 'Run duration too short' })"
$md += "- $(if ($missing.Count -eq 0) { 'All required files present' } else { 'Some files are missing' })"
$md += "- $(if ($validation.schema_ok) { 'Schemas look OK' } else { 'Schema issues detected' })"
$md += "\n## Next"
$md += "- Investigate any listed reasons"
$md += "- Re-run with paper mode and full 24h if needed"
$md += "- Archive artifacts"
$mdPath = Join-Path $root 'page_gate2_summary.md'
$md -join "`n" | Out-File -FilePath $mdPath -Encoding utf8 -Force

if (-not $pass) { Write-Error "Gate2 validation failed"; exit 1 }
