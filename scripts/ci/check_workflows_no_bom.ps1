Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot "..\.."
$wf = Get-ChildItem (Join-Path $root ".github\workflows") -File -Include *.yml,*.yaml -Recurse
$violations = @()

foreach($f in $wf){
  $bytes = [IO.File]::ReadAllBytes($f.FullName)
  if($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF){
    $violations += "BOM detected: $($f.FullName)"
  }
  $txt = [Text.Encoding]::UTF8.GetString($bytes)
  if($txt -match "workflow_di\s*\r?\n\s*spatch:" -or
     $txt -match "desc\s*\r?\n\s*ription:" -or
     $txt -match "sou\s*\r?\n\s*rce"){
    $violations += "Split header token detected: $($f.FullName)"
  }
}

if($violations.Count){
  $violations | ForEach-Object { Write-Error $_ }
  exit 1
}else{
  "No BOM/split issues found in .github/workflows" | Write-Host
}
