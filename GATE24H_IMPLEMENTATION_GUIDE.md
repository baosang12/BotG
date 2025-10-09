# HƯỚNG DẪN HOÀN THIỆN GATE24H PAPER MODE

## PHẦN 1: Config chế độ paper (TODO 1)

### 1.1. Cập nhật TelemetryConfig.cs

**File:** `BotG/Telemetry/TelemetryConfig.cs`

**Thêm sau dòng `public ExecutionConfig Execution`:**
```csharp
    public FeesConfig Fees { get; set; } = new FeesConfig();
    public SpreadConfig Spread { get; set; } = new SpreadConfig();
    public SlippageConfig Slippage { get; set; } = new SlippageConfig();
    public string MarketSource { get; set; } = "live_feed";
```

**Thêm trước `}` cuối file (sau class ExecutionConfig):**
```csharp
    public class FeesConfig
    {
        public double CommissionPerLot { get; set; } = 7.0;
    }

    public class SpreadConfig
    {
        public double PipsBase { get; set; } = 0.1;
    }

    public class SlippageConfig
    {
        public string Mode { get; set; } = "random";
        public double RangePips { get; set; } = 0.1;
        public int Seed { get; set; } = 42;
    }
```

**Thêm env variable overrides trong method `Load()` sau dòng `var envFlush`:**
```csharp
                var envSimEnabled = Environment.GetEnvironmentVariable("SIM_ENABLED");
                var envCommission = Environment.GetEnvironmentVariable("COMMISSION_PER_LOT");
                var envSpread = Environment.GetEnvironmentVariable("SPREAD_PIPS");
                var envSlipRange = Environment.GetEnvironmentVariable("SLIP_RANGE_PIPS");
                var envSlipSeed = Environment.GetEnvironmentVariable("SLIP_SEED");
```

**Thêm sau dòng `if (int.TryParse(envFlush...`:**
```csharp
                // Override simulation config
                if (!string.IsNullOrWhiteSpace(envSimEnabled) && bool.TryParse(envSimEnabled, out var simEnabled))
                {
                    cfg.Simulation.Enabled = simEnabled;
                }
                
                // Override fees/spread/slippage
                if (double.TryParse(envCommission, out var commission)) cfg.Fees.CommissionPerLot = commission;
                if (double.TryParse(envSpread, out var spread)) cfg.Spread.PipsBase = spread;
                if (double.TryParse(envSlipRange, out var slipRange)) cfg.Slippage.RangePips = slipRange;
                if (int.TryParse(envSlipSeed, out var slipSeed)) cfg.Slippage.Seed = slipSeed;
```

### 1.2. config.runtime.json đã OK ✅

---

## PHẦN 2: Wiring Gate24h workflow (TODO 2)

**File:** `.github/workflows/gate24h.yml`

**Thêm step tạo timestamp sau step "Checkout":**
```yaml
    - name: Create timestamp
      id: ts
      shell: powershell
      run: |
        $ts = Get-Date -Format "yyyyMMdd_HHmmss"
        "ts=$ts" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
        $runDir = "D:\botg\runs\gate24h_$ts"
        "RUN_DIR=$runDir" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
        New-Item -ItemType Directory -Force -Path $runDir | Out-Null
        Write-Host "Created run directory: $runDir"
```

**Thêm env variables cho job gate24h:**
```yaml
    env:
      BOTG_MODE: paper
      SIM_ENABLED: false
      COMMISSION_PER_LOT: 7.0
      SPREAD_PIPS: 0.1
      SLIP_RANGE_PIPS: 0.1
      SLIP_SEED: 42
      BOTG_LOG_PATH: ${{ env.RUN_DIR }}
```

**Sửa step "Run supervised 24h gate" để truyền logPath:**
```yaml
    - name: Run supervised 24h gate
      shell: powershell
      run: |
        # Chạy bot với logPath từ RUN_DIR
        # Thêm --logPath "$env:RUN_DIR" vào lệnh chạy bot
```

**Thêm step postrun sau khi bot chạy xong:**
```yaml
    - name: Postrun collect and validate
      if: always()
      shell: powershell
      run: |
        & scripts\postrun_collect.ps1 -RunDir "$env:RUN_DIR"
```

**Sửa step upload artifact:**
```yaml
    - name: Upload gate24h artifacts
      if: always()
      uses: actions/upload-artifact@v4
      with:
        name: artifacts_${{ steps.ts.outputs.ts }}
        path: ${{ env.RUN_DIR }}/**/*
        retention-days: 30
```

---

## PHẦN 3: Logging bắt buộc (TODO 3)

**File:** `BotG/ExecutionModule.cs` (hoặc OrderLifecycleLogger.cs)

**Tại mỗi giai đoạn REQUEST/ACK/FILL, thêm các cột:**

### Schema orders.csv cần có:
```
timestamp_iso,phase,order_id,symbol,side,type,qty,status,
latency_ms,price_requested,price_filled,requested_lots,
theoretical_lots,slippage_pips,commission,spread_cost
```

**Ví dụ log REQUEST:**
```csharp
var latencyMs = (DateTime.UtcNow - requestTime).TotalMilliseconds;
var record = $"{timestamp_iso},REQUEST,{order_id},{symbol},{side},{type},{qty},PENDING," +
             $"{latencyMs:F2},{price_requested:F5},0,{requested_lots:F2},{theoretical_lots:F2}," +
             $"0,{commission:F2},{spread_cost:F2}";
WriteToOrderLog(record);
```

---

## PHẦN 4: Risk snapshot 60s timer (TODO 4)

**File:** `BotG/RiskManager/RiskManager.cs`

**Thêm timer trong constructor:**
```csharp
private System.Timers.Timer? _riskSnapshotTimer;

public RiskManager(...)
{
    // ... existing code ...
    
    // Setup 60s risk snapshot timer
    _riskSnapshotTimer = new System.Timers.Timer(60000); // 60 seconds
    _riskSnapshotTimer.Elapsed += OnRiskSnapshotTimer;
    _riskSnapshotTimer.AutoReset = true;
    _riskSnapshotTimer.Start();
}

private void OnRiskSnapshotTimer(object? sender, System.Timers.ElapsedEventArgs e)
{
    try
    {
        var snapshot = CreateRiskSnapshot();
        WriteRiskSnapshot(snapshot);
    }
    catch (Exception ex)
    {
        // Log error
    }
}

private void WriteRiskSnapshot(RiskSnapshot snapshot)
{
    var line = $"{snapshot.Timestamp:o},{snapshot.Equity:F2},{snapshot.Balance:F2}," +
               $"{snapshot.Margin:F2},{snapshot.RiskState},{snapshot.DailyR:F2},{snapshot.DailyPct:F4}";
    
    var filePath = Path.Combine(_config.LogPath, _config.RiskSnapshotFile);
    File.AppendAllText(filePath, line + Environment.NewLine);
}
```

**Schema risk_snapshots.csv:**
```
timestamp,equity,balance,margin,risk_state,daily_r,daily_pct
```

**Tính drawdown từ đỉnh equity:**
```csharp
private double _peakEquity = 10000.0;

private RiskSnapshot CreateRiskSnapshot()
{
    var currentEquity = GetCurrentEquity();
    if (currentEquity > _peakEquity) _peakEquity = currentEquity;
    
    var drawdown = (_peakEquity - currentEquity) / _peakEquity * 100;
    var dailyR = (currentEquity - _startDayEquity) / 10.0; // R = $10
    
    return new RiskSnapshot
    {
        Timestamp = DateTime.UtcNow,
        Equity = currentEquity,
        Balance = GetBalance(),
        Margin = GetUsedMargin(),
        RiskState = DetermineRiskState(),
        DailyR = dailyR,
        DailyPct = (currentEquity - _startDayEquity) / _startDayEquity * 100
    };
}
```

---

## PHẦN 5: Reconstruct FIFO script (TODO 5)

**File:** `path_issues/reconstruct_fifo.ps1`

```powershell
param(
    [Parameter(Mandatory=$true)]
    [string]$RunDir
)

$ErrorActionPreference = "Stop"

Write-Host "Reconstructing FIFO trades from: $RunDir"

# Check required files
$ordersFile = Join-Path $RunDir "orders.csv"
$closesFile = Join-Path $RunDir "trade_closes.log"
$metaFile = Join-Path $RunDir "run_metadata.json"

if (-not (Test-Path $ordersFile)) {
    Write-Error "Missing orders.csv"
    exit 1
}

# Call Python script to do actual reconstruction
python path_issues\reconstruct_fifo.py `
    --orders "$ordersFile" `
    --closes "$closesFile" `
    --output "$RunDir\closed_trades_fifo_reconstructed.csv"

if ($LASTEXITCODE -ne 0) {
    Write-Error "FIFO reconstruction failed"
    exit 1
}

Write-Host "✓ FIFO reconstruction complete"
```

**File:** `path_issues/reconstruct_fifo.py`

```python
import pandas as pd
import argparse
from pathlib import Path

def reconstruct_fifo(orders_path, closes_path, output_path):
    """Reconstruct closed trades using FIFO matching"""
    
    # Read orders
    orders = pd.read_csv(orders_path)
    
    # Filter FILL phase only
    fills = orders[orders['phase'] == 'FILL'].copy()
    
    # Group by symbol and match BUY/SELL pairs using FIFO
    closed_trades = []
    
    for symbol in fills['symbol'].unique():
        symbol_fills = fills[fills['symbol'] == symbol].sort_values('timestamp_iso')
        
        buy_queue = []
        sell_queue = []
        
        for _, fill in symbol_fills.iterrows():
            if fill['side'] == 'BUY':
                buy_queue.append(fill)
            else:
                sell_queue.append(fill)
            
            # Match FIFO
            while buy_queue and sell_queue:
                buy = buy_queue.pop(0)
                sell = sell_queue.pop(0)
                
                qty = min(buy['qty'], sell['qty'])
                pnl = (sell['price_filled'] - buy['price_filled']) * qty
                
                holding_minutes = (pd.to_datetime(sell['timestamp_iso']) - 
                                 pd.to_datetime(buy['timestamp_iso'])).total_seconds() / 60
                
                closed_trades.append({
                    'timestamp': sell['timestamp_iso'],
                    'order_id': sell['order_id'],
                    'open_time': buy['timestamp_iso'],
                    'close_time': sell['timestamp_iso'],
                    'symbol': symbol,
                    'side': 'BUY',  # Position side
                    'qty': qty,
                    'open_price': buy['price_filled'],
                    'close_price': sell['price_filled'],
                    'pnl': pnl,
                    'holding_minutes': holding_minutes,
                    'mae_pips': 0.0,  # TODO: Calculate from tick data
                    'mfe_pips': 0.0   # TODO: Calculate from tick data
                })
    
    # Write output
    df = pd.DataFrame(closed_trades)
    df.to_csv(output_path, index=False)
    
    print(f"✓ Reconstructed {len(closed_trades)} closed trades")
    
    if len(closed_trades) == 0:
        print("⚠ Warning: No closed trades found")
        return 1
    
    return 0

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--orders', required=True)
    parser.add_argument('--closes', required=False)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    
    exit(reconstruct_fifo(args.orders, args.closes, args.output))
```

---

## PHẦN 6: Schema guard & packaging (TODO 6)

**File:** `path_issues/validate_artifacts.py`

```python
import json
import csv
from pathlib import Path
import argparse
import sys

REQUIRED_FILES = [
    'orders.csv',
    'telemetry.csv',
    'risk_snapshots.csv',
    'trade_closes.log',
    'run_metadata.json',
    'closed_trades_fifo_reconstructed.csv'
]

REQUIRED_COLUMNS = {
    'orders.csv': ['timestamp_iso', 'phase', 'order_id', 'symbol', 'side', 'status'],
    'telemetry.csv': ['timestamp_iso'],
    'risk_snapshots.csv': ['timestamp', 'equity', 'balance'],
    'closed_trades_fifo_reconstructed.csv': ['timestamp', 'order_id', 'pnl']
}

def validate_artifacts(run_dir):
    run_dir = Path(run_dir)
    errors = []
    warnings = []
    
    # Check required files exist
    for req_file in REQUIRED_FILES:
        file_path = run_dir / req_file
        if not file_path.exists():
            errors.append(f"Missing required file: {req_file}")
    
    # Check schemas
    for csv_file, req_cols in REQUIRED_COLUMNS.items():
        file_path = run_dir / csv_file
        if file_path.exists():
            try:
                with open(file_path, 'r', encoding='utf-8') as f:
                    reader = csv.DictReader(f)
                    headers = reader.fieldnames or []
                    
                    missing_cols = [col for col in req_cols if col not in headers]
                    if missing_cols:
                        errors.append(f"{csv_file}: Missing columns {missing_cols}")
                    
                    # Check row count
                    row_count = sum(1 for _ in reader)
                    if csv_file == 'risk_snapshots.csv' and row_count < 1300:
                        warnings.append(f"risk_snapshots.csv has only {row_count} rows (expected ≥1300 for 24h)")
                    
            except Exception as e:
                errors.append(f"Error reading {csv_file}: {e}")
    
    # Check run_metadata.json
    meta_path = run_dir / 'run_metadata.json'
    if meta_path.exists():
        try:
            with open(meta_path, 'r') as f:
                meta = json.load(f)
                
                if meta.get('mode') != 'paper':
                    errors.append(f"run_metadata.json: mode is '{meta.get('mode')}', expected 'paper'")
                
                if meta.get('simulation', {}).get('enabled', True):
                    errors.append("run_metadata.json: simulation.enabled is true, expected false")
                    
        except Exception as e:
            errors.append(f"Error reading run_metadata.json: {e}")
    
    # Print summary
    summary = {
        'status': 'PASS' if not errors else 'FAIL',
        'errors': errors,
        'warnings': warnings,
        'files_checked': len(REQUIRED_FILES),
        'run_dir': str(run_dir)
    }
    
    print(json.dumps(summary, indent=2))
    
    return 0 if not errors else 1

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--dir', required=True, help='Run directory to validate')
    args = parser.parse_args()
    
    sys.exit(validate_artifacts(args.dir))
```

**File:** `scripts/postrun_collect.ps1`

```powershell
param(
    [Parameter(Mandatory=$true)]
    [string]$RunDir
)

$ErrorActionPreference = "Stop"

Write-Host "=== Postrun Collection & Validation ==="
Write-Host "Run Dir: $RunDir"

# Step 1: Reconstruct FIFO trades
Write-Host "`n[1/3] Reconstructing FIFO trades..."
& path_issues\reconstruct_fifo.ps1 -RunDir $RunDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "FIFO reconstruction failed"
    exit 1
}

# Step 2: Validate artifacts
Write-Host "`n[2/3] Validating artifacts..."
python path_issues\validate_artifacts.py --dir $RunDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Artifact validation failed"
    exit 1
}

# Step 3: Create analysis summary stub if not exists
Write-Host "`n[3/3] Creating analysis summary..."
$summaryPath = Join-Path $RunDir "analysis_summary_stats.json"
if (-not (Test-Path $summaryPath)) {
    @{
        "generated_at" = (Get-Date -Format "o")
        "run_dir" = $RunDir
        "kpi" = @{
            "total_trades" = 0
            "win_rate" = 0.0
            "avg_pnl" = 0.0
        }
    } | ConvertTo-Json -Depth 5 | Out-File -FilePath $summaryPath -Encoding UTF8
}

Write-Host "`n✓ Postrun collection complete"
```

---

## PHẦN 7: Unit tests & CI (TODO 7)

**File:** `Tests/SchemaGuardTests.cs`

```csharp
using System.IO;
using Xunit;
using Telemetry;

namespace Tests
{
    public class SchemaGuardTests
    {
        [Fact]
        public void OrdersCsv_Should_Have_Required_Headers()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "botg_test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            
            var config = new TelemetryConfig { LogPath = tempDir };
            var logger = new OrderLifecycleLogger(config);
            
            // Act
            logger.LogRequest(/* params */);
            logger.Flush();
            
            // Assert
            var ordersPath = Path.Combine(tempDir, "orders.csv");
            Assert.True(File.Exists(ordersPath));
            
            var header = File.ReadLines(ordersPath).First();
            Assert.Contains("timestamp_iso", header);
            Assert.Contains("order_id", header);
            Assert.Contains("symbol", header);
            Assert.Contains("side", header);
            Assert.Contains("latency_ms", header);
            Assert.Contains("price_requested", header);
            Assert.Contains("price_filled", header);
            
            // Cleanup
            Directory.Delete(tempDir, true);
        }
        
        [Fact]
        public void RiskSnapshotsCsv_Should_Have_Required_Headers()
        {
            // Similar test for risk_snapshots.csv
        }
    }
}
```

**File:** `.github/workflows/ci.yml` - thêm job:

```yaml
  schema-selftest:
    runs-on: [self-hosted, Windows]
    steps:
    - name: Checkout
      uses: actions/checkout@v4
      
    - name: Create sample artifacts
      shell: powershell
      run: |
        $testDir = "D:\botg\test_artifacts"
        New-Item -ItemType Directory -Force -Path $testDir | Out-Null
        
        # Create minimal sample files
        "timestamp_iso,phase,order_id,symbol,side,status" | Out-File "$testDir\orders.csv" -Encoding utf8
        "timestamp_iso,value" | Out-File "$testDir\telemetry.csv" -Encoding utf8
        "timestamp,equity,balance,margin,risk_state,daily_r,daily_pct" | Out-File "$testDir\risk_snapshots.csv" -Encoding utf8
        "trade closed" | Out-File "$testDir\trade_closes.log" -Encoding utf8
        "timestamp,order_id,pnl" | Out-File "$testDir\closed_trades_fifo_reconstructed.csv" -Encoding utf8
        '{"mode":"paper","simulation":{"enabled":false}}' | Out-File "$testDir\run_metadata.json" -Encoding utf8
        
    - name: Validate schema
      shell: powershell
      run: |
        python path_issues\validate_artifacts.py --dir "D:\botg\test_artifacts"
        if ($LASTEXITCODE -ne 0) {
          Write-Error "Schema validation failed"
          exit 1
        }
```

---

## CHECKLIST HOÀN THÀNH

- [ ] TODO 1: TelemetryConfig.cs updated với env overrides
- [ ] TODO 2: gate24h.yml workflow updated với env variables
- [ ] TODO 3: ExecutionModule logging đầy đủ cột
- [ ] TODO 4: RiskManager có timer 60s
- [ ] TODO 5: reconstruct_fifo.ps1 & .py created
- [ ] TODO 6: validate_artifacts.py & postrun_collect.ps1 created
- [ ] TODO 7: SchemaGuardTests.cs & CI job added

---

## LỆNH KIỂM TRA SAU KHI HOÀN THÀNH

```powershell
# Build & test
dotnet build BotG.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SchemaGuard"

# Test validation script
python path_issues\validate_artifacts.py --dir gate24h-artifacts-18184164248-1\artifacts_20251003_124834

# Trigger gate24h
gh workflow run gate24h.yml -f hours=0.1 -f mode=paper -f source=test
```
