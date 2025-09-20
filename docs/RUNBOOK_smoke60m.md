# RUNBOOK: Smoke 60m Production System

## Overview

The BotG Smoke 60m system provides production-hardened testing for 60-minute trading scenarios with FIFO reconstruction and comprehensive reporting.

## Quick Reference Commands

### Quick Commands via ops.ps1

```powershell
# 1) MergeOnly run gần nhất
. .\scripts\ops.ps1; Invoke-Smoke60mMergeLatest

# 2) MergeOnly run cụ thể
. .\scripts\ops.ps1; Invoke-Smoke60mMergeExisting -ExistingRoot "D:\botg\runs\paper_smoke_60m_v2_YYYYMMDD_HHMMSS"

# 3) Full 4×15'
. .\scripts\ops.ps1; Invoke-Smoke60mRun -Segments 4

# 4) Xem báo cáo + thống kê
. .\scripts\ops.ps1; Show-Smoke60mReport
. .\scripts\ops.ps1; Show-PhaseStats
```

## Self-test (không cần 60')

```powershell
# 1) Nạp hàm
. .\scripts\ops.ps1

# 2) MergeOnly run gần nhất (nếu có)
Invoke-Smoke60mMergeLatest

# 3) Tự test nhanh
.\scripts\ops_selftest.ps1
```

Kỳ vọng:

* Có `RUN_ROOT`, có `orders_merged.csv`, có `report_60m.md`.
* Nếu có dữ liệu FILL → `FIFO: OK`.
* Nếu chỉ header → `NO_DATA_ROWS` (không lỗi).

## Bảo trì

```powershell
# Dọn runs cũ (14 ngày)
. .\scripts\ops.ps1; Clean-OldRuns -Days 14

# Mở thư mục run mới nhất
. .\scripts\ops.ps1; Open-SmokeFolder

# Kiểm tra nhanh hệ thống
. .\scripts\ops.ps1; Invoke-Smoke60mMergeLatest; Show-PhaseStats
```

### 1. Full 4×15m Run (Production)

```powershell
# Navigate to BotG root
cd "D:\OneDrive\Tài liệu\cAlgo\Sources\Robots\BotG"

# Run complete 4-segment test (60 minutes total)
.\scripts\run_smoke_60m_wrapper_v2.ps1 -Segments 4 -OutRoot "D:\botg\runs" -LogPath "D:\botg\logs"
```

### 2. MergeOnly - Latest Run

```powershell
# Merge and analyze the most recent run without re-running segments
.\scripts\run_smoke_60m_wrapper_v2.ps1 -MergeOnly -OutRoot "D:\botg\runs" -LogPath "D:\botg\logs"
```

### 3. MergeOnly - Specific Run

```powershell
# Merge a specific existing run
.\scripts\run_smoke_60m_wrapper_v2.ps1 -MergeOnly -ExistingRoot "D:\botg\runs\paper_smoke_60m_v2_YYYYMMDD_HHMMSS"
```

### 4. Quick Health Check

```powershell
# Get the latest run and show summary
$root = (Get-ChildItem "D:\botg\runs\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc | Select-Object -First 1).FullName
Get-Content (Join-Path $root 'report_60m.md')
```

### 5. Phase Statistics

```powershell
# Detailed phase and latency analysis
$root = (Get-ChildItem "D:\botg\runs\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc | Select-Object -First 1).FullName
$merged = Join-Path $root 'orders_merged.csv'
$csv = Import-Csv $merged
"PHASES: " + (($csv | Group-Object phase | % { "$($_.Name)=$($_.Count)" }) -join '; ')
$f = $csv | ? { $_.phase -eq 'FILL' }
if($f){ $m = $f | Measure-Object latency_ms -Average -Minimum -Maximum; "FILL: $($f.Count) | latency avg=$([int]$m.Average) min=$([int]$m.Minimum) max=$([int]$m.Maximum)" }
```

## System Architecture

### Segment Patterns
The system supports dual segment naming patterns:
- **New format**: `seg01`, `seg02`, `seg03`, `seg04`
- **Legacy format**: `segment_1`, `segment_2`, `segment_3`, `segment_4`

Priority: Legacy format (`segment_X`) takes precedence over new format (`segXX`) when both exist.

### File Structure
```
D:\botg\runs\paper_smoke_60m_v2_YYYYMMDD_HHMMSS\
├── seg01\                          # Segment 1 (or segment_1)
│   └── telemetry\
│       └── orders.csv              # Trading orders for this segment
├── seg02\                          # Segment 2 (or segment_2)
│   └── telemetry\
│       └── orders.csv
├── seg03\                          # Segment 3 (or segment_3)
│   └── telemetry\
│       └── orders.csv
├── seg04\                          # Segment 4 (or segment_4)
│   └── telemetry\
│       └── orders.csv
├── orders_merged.csv               # Merged data from all segments
├── closed_trades_fifo_reconstructed.csv  # FIFO-reconstructed trades
├── fifo_stdout.log                 # Python FIFO process output
├── fifo_stderr.log                 # Python FIFO process errors
└── report_60m.md                   # Summary report
```

## Configuration

### Parameters

| Parameter | Description | Default | Example |
|-----------|-------------|---------|---------|
| `-Segments` | Number of 15-minute segments to run | 4 | `-Segments 4` |
| `-OutRoot` | Base directory for run outputs | Required | `-OutRoot "D:\botg\runs"` |
| `-LogPath` | Directory for telemetry logs | Required | `-LogPath "D:\botg\logs"` |
| `-MergeOnly` | Skip segment execution, only merge existing data | false | `-MergeOnly` |
| `-ExistingRoot` | Specific run directory for MergeOnly mode | "" | `-ExistingRoot "D:\botg\runs\paper_smoke_60m_v2_20250920_151811"` |

### Schema Requirements

**Required CSV Columns:**
- `status`, `reason`, `latency_ms`, `price_requested`, `price_filled`, `size_requested`, `size_filled`

**Optional CSV Columns (warnings if missing):**
- `take_profit`, `requested_units`, `level`, `risk_R_usd`

## FIFO Processing

### Python Script Requirements
- **File**: `reconstruct_closed_trades_sqlite.py`
- **Raw docstring**: Uses `r"""` to prevent string escaping issues
- **Warning suppression**: Automatically suppresses SyntaxWarning messages
- **Encoding**: UTF-8-sig support for BOM handling
- **Exit codes**: Proper SystemExit handling for PowerShell integration

### FIFO Logic
1. **Header-only CSV**: Skip FIFO processing, report `FIFO: N/A`
2. **Data present**: Execute Python reconstruction, report `FIFO: OK` or `FIFO: FAILED`
3. **Missing script**: Report `MISSING: reconstruct_closed_trades_sqlite.py`

### FIFO Output
- **Success**: Creates `closed_trades_fifo_reconstructed.csv`
- **Logs**: Captures stdout/stderr to `fifo_stdout.log` and `fifo_stderr.log`
- **Error handling**: Graceful failure with detailed error messages

## Production Notes

### Prerequisites
- **No cTrader required**: System uses simulation/paper mode
- **Python environment**: Must have Python accessible via `python` command
- **PowerShell 5.1+**: Compatible with Windows PowerShell constraints
- **Disk space**: ~100MB per run for telemetry and logs

### Performance
- **Full run time**: ~60 minutes for 4×15m segments
- **MergeOnly time**: ~10-30 seconds depending on data volume
- **Typical data size**: 10k-50k records per run

### Unicode Support
- **Paths**: Full Unicode path support (e.g., "Tài liệu")
- **BOM handling**: Automatic BOM detection and removal
- **Encoding**: UTF-8 throughout the pipeline

## Testing

### Run Pester Tests
```powershell
# Install Pester if needed
Install-Module Pester -Force -Scope CurrentUser

# Run all tests
Invoke-Pester -Path ".\tests" -Output Detailed

# Run specific test file
Invoke-Pester -Path ".\tests\csv_merge.Tests.ps1" -Output Detailed
Invoke-Pester -Path ".\tests\wrapper_v2.Tests.ps1" -Output Detailed
```

### Test Coverage
- **CSV merge**: BOM handling, header validation, Unicode paths
- **Segment discovery**: Both naming patterns, priority logic
- **FIFO processing**: Header-only vs data scenarios
- **Error handling**: Missing files, invalid headers, process failures
- **Report generation**: Latency statistics, phase breakdown

## Troubleshooting

### Common Issues

**1. "No orders.csv found"**
```
# Check segment directories exist and contain telemetry/orders.csv
ls "D:\botg\runs\paper_smoke_60m_v2_*\seg*"
```

**2. "FIFO failed"**
```
# Check Python script exists and is executable
Test-Path ".\reconstruct_closed_trades_sqlite.py"
python --version

# Check FIFO error logs
Get-Content "D:\botg\runs\paper_smoke_60m_v2_*\fifo_stderr.log"
```

**3. "Header validation failed"**
```
# Check CSV headers match V3 schema
$merged = "D:\botg\runs\paper_smoke_60m_v2_*\orders_merged.csv"
Get-Content $merged -TotalCount 1
```

**4. "MergeOnly but no run found"**
```
# List available runs
Get-ChildItem "D:\botg\runs\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc
```

### Performance Optimization

**1. Faster MergeOnly**
```powershell
# Use specific ExistingRoot instead of latest discovery
.\scripts\run_smoke_60m_wrapper_v2.ps1 -MergeOnly -ExistingRoot "D:\botg\runs\paper_smoke_60m_v2_20250920_151811"
```

**2. Parallel Analysis**
```powershell
# Run multiple analysis commands in parallel background jobs
Start-Job { & .\scripts\run_smoke_60m_wrapper_v2.ps1 -MergeOnly }
```

## CI Integration

### Pre-commit Hook (Optional)
```powershell
# scripts\precommit_check.ps1
Invoke-Pester .\tests -Passthru | Tee-Object -Variable TestResults
if ($TestResults.FailedCount -gt 0) {
    Write-Error "Tests failed. Commit blocked."
    exit 1
}
```

### Automation Example
```powershell
# Daily smoke test automation
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "D:\botg\automation\smoke_60m_$timestamp.log"

try {
    .\scripts\run_smoke_60m_wrapper_v2.ps1 -Segments 4 -OutRoot "D:\botg\runs" -LogPath "D:\botg\logs" | 
        Tee-Object -FilePath $logFile
    
    # Check if FIFO succeeded
    $latestRun = Get-ChildItem "D:\botg\runs\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    $report = Get-Content (Join-Path $latestRun.FullName "report_60m.md") -Raw
    
    if ($report -match "FIFO: OK") {
        Write-Host "✅ Daily smoke test PASSED" -ForegroundColor Green
    } else {
        Write-Warning "❌ Daily smoke test had FIFO issues"
    }
} catch {
    Write-Error "❌ Daily smoke test FAILED: $_"
}
```

## Appendix

### Version History
- **v2**: Production hardening, dual segment patterns, robust FIFO processing
- **v1**: Initial implementation (deprecated)

### Dependencies
- PowerShell 5.1+
- Python 3.x with pandas, sqlite3
- Pester module (for testing)

### Contact
See `PR_CREATION_GUIDE.md` for contribution guidelines.