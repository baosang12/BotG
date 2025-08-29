[CmdletBinding()]
param(
  [string]$OutFile = 'path_issues/findings_paths_to_fix.txt'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path '.').Path
$outPath = Join-Path $repo $OutFile
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null

$patterns = @(
  'Tài Liệu','Tài liệu','Tài','Tài','Tài liệu','TàiLieu','TàiLieu','TaiLieu'
)

$results = @()
Get-ChildItem -Recurse -File | ForEach-Object {
  $file = $_.FullName
  try {
    $content = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
  } catch { return }
  $lineNo = 0
  foreach ($line in $content -split "`n") {
    $lineNo++
    foreach ($p in $patterns) {
      if ($line -like "*${p}*") {
        $results += "${file}`t${lineNo}`t${line.Trim()}"
        break
      }
    }
  }
}

Set-Content -LiteralPath $outPath -Value ($results -join "`r`n") -Encoding UTF8
Write-Host "Wrote: $outPath" -ForegroundColor Cyan
