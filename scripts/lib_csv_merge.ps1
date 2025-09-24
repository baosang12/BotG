#requires -Version 5.1
Set-StrictMode -Version Latest

function Remove-Bom {
    param([string]$s)
    if ($null -eq $s) { return $s }
    return $s.TrimStart([char]0xFEFF).Trim()
}

function Resolve-OrdersCsv {
    param([Parameter(Mandatory)][string]$segDir)
    # Priority order: direct -> telemetry subfolder -> recursive search
    $p1 = Join-Path -Path $segDir -ChildPath 'orders.csv'
    if (Test-Path -LiteralPath $p1) { return (Resolve-Path -LiteralPath $p1).Path }
    
    $tele = Join-Path -Path $segDir -ChildPath 'telemetry'
    $p2 = Join-Path -Path $tele -ChildPath 'orders.csv'
    if (Test-Path -LiteralPath $p2) { return (Resolve-Path -LiteralPath $p2).Path }
    
    $found = Get-ChildItem -LiteralPath $segDir -Recurse -File -Filter 'orders.csv' -ErrorAction SilentlyContinue |
             Select-Object -ExpandProperty FullName -First 1
    if ($found) { return $found }
    return $null
}

function Get-LineCount {
    param([Parameter(Mandatory)][string]$path)
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    try {
        $lines = Get-Content -LiteralPath $path -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($lines) { return $lines.Count } else { return 0 }
    } catch {
        return 0
    }
}

function Write-StubOrders {
    param([Parameter(Mandatory)][string]$out)
    # Harness V3 header with all required and optional columns
    $header = @(
        'ts_iso','epoch_ms','phase','order_id','side','action','type',
        'status','reason','latency_ms','price_requested','price_filled',
        'size_requested','size_filled','sl','tp','price_exec',
        'theoretical_lots','theoretical_units','requestedVolume','filledSize',
        'slippage','brokerMsg','client_order_id','host','session',
        'take_profit','requested_units','level','risk_R_usd'
    ) -join ','
    
    Set-Content -LiteralPath $out -Value $header -Encoding UTF8
    Write-Host "STUB_ORDERS_WRITTEN: $out (header only)"
}

function Assert-HeaderSchema {
    param([Parameter(Mandatory)][string]$header)
    $headerCols = ($header -split ',') | ForEach-Object { Remove-Bom $_ }
    
    # Required columns - throw if missing
    $required = @('status','reason','latency_ms','price_requested','price_filled','size_requested','size_filled')
    $missing = @($required | Where-Object { $_ -notin $headerCols })
    if ($missing.Count -gt 0) {
        throw "SCHEMA_ERROR: Missing required columns: $($missing -join ', ')"
    }
    
    # Optional columns - warn if missing
    $optional = @('take_profit','requested_units','level','risk_R_usd')
    $missingOptional = @($optional | Where-Object { $_ -notin $headerCols })
    if ($missingOptional.Count -gt 0) {
        Write-Warning "Missing optional columns: $($missingOptional -join ', ')"
    }
    
    Write-Host "HEADER_VALIDATION: PASSED"
}

function Merge-Orders {
    param(
        [string[]]$ordersFiles,
        [Parameter(Mandatory)][string]$mergedOut
    )
    
    # Handle empty or null input
    if (-not $ordersFiles -or $ordersFiles.Count -eq 0) {
        Write-Host "No orders.csv files provided. Writing stub..."
        Write-StubOrders -out $mergedOut
        return
    }
    
    # Filter to existing files with content
    $validFiles = $ordersFiles | Where-Object { 
        (Test-Path -LiteralPath $_) -and ((Get-LineCount $_) -ge 1)
    }
    
    if (-not $validFiles -or $validFiles.Count -eq 0) {
        Write-Host "All orders.csv files empty or missing. Writing stub..."
        Write-StubOrders -out $mergedOut
        return
    }
    
    # Use first valid file for header
    $firstFile = $validFiles[0]
    $rawHeader = Get-Content -LiteralPath $firstFile -TotalCount 1 -Encoding UTF8
    $header = Remove-Bom $rawHeader
    
    # Validate schema
    Assert-HeaderSchema -header $header
    
    # Initialize merged file with header
    Set-Content -LiteralPath $mergedOut -Value $header -Encoding UTF8
    $totalDataLines = 0
    
    foreach ($src in $validFiles) {
        $lines = Get-Content -LiteralPath $src -Encoding UTF8
        if ($lines.Count -lt 2) { 
            Write-Warning "Skipping file with no data rows: $src"
            continue 
        }
        
        # Check header consistency
        $srcHeader = Remove-Bom $lines[0]
        if ($srcHeader -ne $header) { 
            Write-Warning "Header mismatch, SKIPPING: $src"
            continue 
        }
        
        # Append data rows (skip header)
        $dataLines = $lines[1..($lines.Count-1)]
        $dataLines | Add-Content -LiteralPath $mergedOut -Encoding UTF8
        $totalDataLines += $dataLines.Count
        Write-Host "Merged from $src`: $($dataLines.Count) data rows"
    }
    
    Write-Host "MERGED_CSV: $mergedOut ($totalDataLines total data rows)"
}

function Write-RunReport {
    param(
        [Parameter(Mandatory)][string]$runRoot,
        [Parameter(Mandatory)][string]$mergedOut,
        [Parameter(Mandatory)][string]$closedOut
    )
    
    $lineCount = Get-LineCount $mergedOut
    
    # Handle empty or header-only CSV
    if ($lineCount -lt 2) {
        $phaseStat = "no data"
        $fills = @()
        $latency = "no FILL"
        $fifoStatus = "N/A"
    } else {
        # Parse CSV and analyze
        $csv = @(Import-Csv -LiteralPath $mergedOut -Encoding UTF8)
        if ($csv.Count -gt 0) {
            $phaseGroups = $csv | Group-Object phase
            $phaseStat = ($phaseGroups | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join '; '
            $fills = $csv | Where-Object { $_.phase -eq 'FILL' }
        } else {
            $phaseStat = "no data"
            $fills = @()
        }
        
        # Calculate latency statistics for FILL records
        if ($fills.Count -gt 0) {
            $m = $fills | Measure-Object -Property latency_ms -Average -Minimum -Maximum
            $latency = "latency_ms avg=$([int]$m.Average) min=$([int]$m.Minimum) max=$([int]$m.Maximum)"
        } else {
            $latency = "no FILL"
        }
        
        # Check FIFO status
        $fifoStatus = if (Test-Path -LiteralPath $closedOut) { "OK" } else { "N/A" }
    }
    
    # Generate markdown report
    $mdLines = @(
        "# Smoke 60m Report",
        "**RUN_ROOT:** $runRoot",
        "**MERGED_CSV:** $(Split-Path $mergedOut -Leaf)",
        "**PHASES:** $phaseStat",
        "**FILL:** $($fills.Count) ($latency)",
        "**FIFO:** $fifoStatus"
    )
    
    $reportPath = Join-Path -Path $runRoot -ChildPath 'report_60m.md'
    Set-Content -LiteralPath $reportPath -Value ($mdLines -join "`r`n") -Encoding UTF8
    
    # Console output for CI/automation
    Write-Host "RUN_ROOT: $runRoot"
    Write-Host "FILL: $($fills.Count)"
    Write-Host "FIFO: $fifoStatus"
}

# Legacy function compatibility - keep existing names that might be in use
function Test-MinHeaders {
    param([Parameter(Mandatory)][string]$mergedOut)
    $hdr = (Get-Content -LiteralPath $mergedOut -TotalCount 1 -Encoding UTF8)
    Assert-HeaderSchema -header $hdr
}