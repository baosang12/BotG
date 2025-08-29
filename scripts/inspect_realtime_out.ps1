[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$OutBase,
  [switch]$ShowLogs
)

$ErrorActionPreference = 'Continue'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

if (-not (Test-Path -LiteralPath $OutBase)) {
  Write-Output ("MISSING_OUTBASE: " + $OutBase)
  exit 2
}

$items = Get-ChildItem -LiteralPath $OutBase -Force -ErrorAction SilentlyContinue | Select-Object Name, FullName, Length, LastWriteTime
$json = $items | ConvertTo-Json -Depth 4
Write-Output $json

if ($ShowLogs) {
  foreach ($pat in @('run_smoke_*.log','run_smoke_*.err.log','daemon_ack.json','final_report.json')) {
    $files = Get-ChildItem -LiteralPath $OutBase -Filter $pat -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    foreach ($f in $files) {
      Write-Output ("--- FILE: " + $f.FullName + " (" + $f.Length + " bytes) ---")
      try { Get-Content -LiteralPath $f.FullName -TotalCount 200 } catch { Write-Output ("<read-error: " + $_.Exception.Message + ">") }
    }
  }
}
