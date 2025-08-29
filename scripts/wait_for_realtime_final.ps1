[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$OutBase,
  [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function PathJoin([string]$a,[string]$b){ return [System.IO.Path]::Combine($a,$b) }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$finalPath = PathJoin $OutBase 'final_report.json'

while ((Get-Date) -lt $deadline) {
  if (Test-Path -LiteralPath $finalPath) {
    try {
      $content = Get-Content -LiteralPath $finalPath -Raw -ErrorAction Stop
      $content | Write-Output
      exit 0
    } catch {
      Start-Sleep -Seconds 2
    }
  }
  Start-Sleep -Seconds 3
}

Write-Output 'FINAL_NOT_READY'
exit 3
