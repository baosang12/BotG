Describe "postrun_collect path normalization" {
  It "trims trailing separator and space without throwing" {
    $tmp = Join-Path $env:TEMP "botg_test_postrun"
    $run = Join-Path $tmp "run_x"
    $l1Dir = Join-Path $run "l1"
    
    # Clean and create test directory structure
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $l1Dir | Out-Null
    
    # Create minimal required files for postrun_collect.ps1
    $requiredFiles = @(
      'orders.csv',
      'trade_closes.log', 
      'run_metadata.json',
      'telemetry.csv',
      'risk_snapshots.csv'
    )
    
    foreach ($file in $requiredFiles) {
      New-Item -Path (Join-Path $run $file) -ItemType File -Force | Out-Null
      if ($file -eq 'orders.csv') {
        Set-Content -Path (Join-Path $run $file) -Value "symbol,timestamp_submit" -Encoding utf8
      } elseif ($file -eq 'run_metadata.json') {
        Set-Content -Path (Join-Path $run $file) -Value '{"start":"2025-10-24T00:00:00Z"}' -Encoding utf8
      } elseif ($file -eq 'trade_closes.log') {
        Set-Content -Path (Join-Path $run $file) -Value "# empty" -Encoding utf8
      } else {
        Set-Content -Path (Join-Path $run $file) -Value "" -Encoding utf8
      }
    }
    
    # Test with trailing backslash and space
    $env:RUN_DIR = "$run\ "
    $artifactsDir = Join-Path $tmp "artifacts"
    
    # This should not throw the TrimEnd error
    { 
      & powershell -NoProfile -File "scripts\postrun_collect.ps1" -RunDir $env:RUN_DIR -ArtifactsDir $artifactsDir
    } | Should Not Throw
    
    # Cleanup
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  }
  
  It "handles path with trailing forward slash" {
    $tmp = Join-Path $env:TEMP "botg_test_postrun2"
    $run = Join-Path $tmp "run_y"
    
    # Clean and create test directory structure
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    
    # Create minimal required files
    $requiredFiles = @(
      'orders.csv',
      'trade_closes.log',
      'run_metadata.json', 
      'telemetry.csv',
      'risk_snapshots.csv'
    )
    
    foreach ($file in $requiredFiles) {
      New-Item -Path (Join-Path $run $file) -ItemType File -Force | Out-Null
      if ($file -eq 'orders.csv') {
        Set-Content -Path (Join-Path $run $file) -Value "symbol,timestamp_submit" -Encoding utf8
      } elseif ($file -eq 'run_metadata.json') {
        Set-Content -Path (Join-Path $run $file) -Value '{"start":"2025-10-24T00:00:00Z"}' -Encoding utf8
      } elseif ($file -eq 'trade_closes.log') {
        Set-Content -Path (Join-Path $run $file) -Value "# empty" -Encoding utf8
      } else {
        Set-Content -Path (Join-Path $run $file) -Value "" -Encoding utf8
      }
    }
    
    # Test with trailing forward slash (Unix-style)
    $testPath = $run.Replace('\', '/') + '/'
    $artifactsDir = Join-Path $tmp "artifacts2"
    
    { 
      & powershell -NoProfile -File "scripts\postrun_collect.ps1" -RunDir $testPath -ArtifactsDir $artifactsDir
    } | Should Not Throw
    
    # Cleanup
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  }
}
