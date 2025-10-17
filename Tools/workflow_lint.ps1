param(
  [string]$WorkflowsDir = ".github/workflows",
  [string]$OutputDir = "path_issues/triage/workflow_lint_${env:GITHUB_RUN_ID}"
)
$ErrorActionPreference = "Stop"
$issues = @()

function Add-Issue([string]$file, [string]$rule, [string]$msg){
  $issues += [pscustomobject]@{ file=$file; rule=$rule; message=$msg }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$files = Get-ChildItem -Path $WorkflowsDir -File -Recurse -Include *.yml, *.yaml |
  Where-Object { $_.Name -notmatch '^gate24h.*' }

foreach($f in $files){
  $name = $f.FullName
  $text = Get-Content $name -Raw -Encoding UTF8

  # Rule 4: No BOM
  $bytes = [System.IO.File]::ReadAllBytes($name)
  if($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF){
    Add-Issue $name "NoBOM" "File has UTF-8 BOM"
  }

  # Rule 1: timeout-minutes per job
  if($text -notmatch '(?ms)jobs:\s*.+'){
    Add-Issue $name "TimeoutPerJob" "No jobs block found"
  } else {
    $jobBlocks = [regex]::Matches($text, '(?ms)^[ \t]*[A-Za-z0-9_-]+:\s*\r?\n(?:[ \t].*\r?\n)+', [System.Text.RegularExpressions.RegexOptions]::Multiline)
    foreach($jb in $jobBlocks){
      $block = $jb.Value
      if($block -notmatch '^\s*timeout-minutes:\s*\d+' ){
        Add-Issue $name "TimeoutPerJob" "Job missing timeout-minutes"
      }
    }
  }

  # Rule 2: upload-artifact@v4 must have if: always()
  $uploadMatches = [regex]::Matches($text, '(?mi)uses:\s*actions/upload-artifact@v4')
  foreach($m in $uploadMatches){
    $prefix = $text.Substring([Math]::Max($m.Index-500,0), [Math]::Min(500,$m.Index))
    $after  = $text.Substring($m.Index, [Math]::Min(300, $text.Length-$m.Index))
    $block  = ($prefix + $after)
    if($block -notmatch '(?mi)^\s*if:\s*always\(\)\s*$'){
      Add-Issue $name "IfAlwaysOnUpload" "upload-artifact@v4 without if: always()"
    }
  }

  # Rule 3: Only actions/setup-python@v5
  if($text -match '(?mi)uses:\s*actions/setup-python@v(?!5)\d+'){
    Add-Issue $name "SetupPythonV5Only" "Only actions/setup-python@v5 is allowed"
  }

  # Rule 5: ASCII-only in echo commands
  $echoMatches = [regex]::Matches($text, '(?mi)^\s*run:\s*(.+)$')
  foreach($em in $echoMatches){
    $line = $em.Groups[1].Value
    if($line -match '(?i)\becho\b' -and $line -match '[^\x00-\x7F]'){
      Add-Issue $name "ASCIIOnlyEcho" "Non-ASCII characters found in echo"
    }
  }

  # Rule 6: concurrency -> cancel-in-progress: true
  $concurrencyBlocks = [regex]::Matches($text, '(?ms)^\s*concurrency:\s*\r?\n(?:\s+.*\r?\n)+')
  foreach($cb in $concurrencyBlocks){
    if($cb.Value -notmatch '(?mi)^\s*cancel-in-progress:\s*true\s*$'){
      Add-Issue $name "ConcurrencyCancelInProgress" "concurrency without cancel-in-progress: true"
    }
  }
}

$reportPath = Join-Path $OutputDir "lint_report.txt"
$reportJson = Join-Path $OutputDir "lint_report.json"

if($issues.Count -eq 0){
  "LINT PASS - all workflows clean" | Out-File $reportPath -Encoding ASCII
  @() | ConvertTo-Json | Out-File $reportJson -Encoding ASCII
  exit 0
}else{
  "LINT FAIL - $($issues.Count) issue(s) found" | Out-File $reportPath -Encoding ASCII
  $issues | ConvertTo-Json -Depth 5 | Out-File $reportJson -Encoding ASCII
  $issues | ForEach-Object { "$($_.file) [$($_.rule)]: $($_.message)" } | Out-File (Join-Path $OutputDir "violations.txt") -Encoding ASCII
  exit 1
}