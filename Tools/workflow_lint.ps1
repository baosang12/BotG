<# tools/workflow_lint.ps1 - ASCII only #>
param(
  [string]$WorkflowsDir = ".github/workflows",
  [string]$OutputDir = "path_issues/triage/workflow_lint_" + (Get-Date -f yyyyMMdd_HHmmss)
)

function New-Dir($p){ if(-not (Test-Path $p)){ [void](New-Item -ItemType Directory -Force -Path $p) } }
function Read-Utf8NoBom($file){ [System.IO.File]::ReadAllText($file,[System.Text.UTF8Encoding]::new($false)) }
function Has-Bom($file){ $fs=[System.IO.File]::OpenRead($file); try{ if($fs.Length -lt 3){return $false}; $b=New-Object byte[] 3; [void]$fs.Read($b,0,3); return ($b[0]-eq0xEF -and $b[1]-eq0xBB -and $b[2]-eq0xBF) } finally{ $fs.Dispose() } }

New-Dir $OutputDir
$issues=@(); $ok=@()
$files = Get-ChildItem -LiteralPath $WorkflowsDir -Filter *.yml -File | Sort-Object Name
if($files.Count -eq 0){ Write-Host "No workflow files found."; exit 0 }

foreach($f in $files){
  $name=$f.Name; $text=Read-Utf8NoBom $f.FullName; $lines=$text -split "`r?`n"

  # Skip gate24h* files entirely
  if($name -match '^gate24h'){ continue }

  if(Has-Bom $f.FullName){ $issues += [pscustomobject]@{file=$name; rule="NoBOM"; msg="File has UTF-8 BOM"} }
  if($text -match '(?im)^\s*-\s*run:\s*echo\s*"[^"]*[^\x00-\x7F][^"]*"'){
    $issues += [pscustomobject]@{file=$name; rule="AsciiEcho"; msg="Unicode found in echo command"}
  }

  $pyHits = Select-String -InputObject $text -Pattern 'uses:\s*actions/setup-python@v(\d+)' -AllMatches
  foreach($m in $pyHits.Matches){
    if([int]$m.Groups[1].Value -ne 5){
      $issues += [pscustomobject]@{file=$name; rule="PythonV5"; msg="actions/setup-python not @v5"}
    }
  }

  for($i=0;$i -lt $lines.Count;$i++){
    if($lines[$i] -match 'uses:\s*actions/upload-artifact@v4'){
      $win = ($i-10)..($i-1) | Where-Object { $_ -ge 0 } | ForEach-Object { $lines[$_] }
      $hasIfAlways = $false
      foreach($l in $win){ if($l -match '^\s*if:\s*always\(\)\s*$'){ $hasIfAlways=$true; break } }
      if(-not $hasIfAlways){
        $issues += [pscustomobject]@{file=$name; rule="IfAlways"; msg="upload-artifact@v4 without if: always()"}
      }
    }
  }

  if($text -match '(?s)concurrency:\s*[\r\n]+.*?cancel-in-progress:\s*true'){
    # ok
  } elseif($text -match '(?m)^\s*concurrency:\s*$'){
    $issues += [pscustomobject]@{file=$name; rule="ConcurrencyCancel"; msg="concurrency present but no cancel-in-progress: true"}
  }

  if($name -notmatch '^gate24h'){
    $jobsMatch = [regex]::Match($text,'(?m)^jobs:\s*$')
    if($jobsMatch.Success){
      $jobsSection = $text.Substring($jobsMatch.Index + $jobsMatch.Length)
      $jobMatches = [regex]::Matches($jobsSection,'(?m)^  ([A-Za-z0-9\-_]+):\s*$')
      foreach($jm in $jobMatches){
        $jobName=$jm.Groups[1].Value; $start=$jm.Index+$jm.Length; $tail=$jobsSection.Substring($start)
        $next=[regex]::Match($tail,'(?m)^  [A-Za-z0-9\-_]+:\s*$')
        $block= if($next.Success){ $tail.Substring(0,$next.Index) } else { $tail }
        if($block -notmatch '(?m)^\s{4}timeout-minutes:\s*\d+\s*$'){
          $issues += [pscustomobject]@{file=$name; rule="TimeoutPerJob"; msg="job '$jobName' missing timeout-minutes"}
        }
      }
    }
  }

  if(-not ($issues | Where-Object { $_.file -eq $name })){ $ok += $name }
}

$reportTxt = Join-Path $OutputDir "lint_report.txt"
$reportJson= Join-Path $OutputDir "lint_report.json"

if($issues.Count -gt 0){
  $issues | Sort-Object file,rule | Format-Table -AutoSize | Out-String | Set-Content -Path $reportTxt -Encoding utf8
  $issues | ConvertTo-Json -Depth 5 | Set-Content -Path $reportJson -Encoding utf8
  Write-Host "LINT FAIL - $($issues.Count) issue(s). See $reportTxt" -ForegroundColor Red
  exit 1
}else{
  "All checks passed on {0} workflows`n- {1}" -f $ok.Count, ($ok -join ", ") | Set-Content -Path $reportTxt -Encoding utf8
  @{"ok"=$ok} | ConvertTo-Json | Set-Content -Path $reportJson -Encoding utf8
  Write-Host "LINT PASS - $($ok.Count) workflows clean." -ForegroundColor Green
  exit 0
}