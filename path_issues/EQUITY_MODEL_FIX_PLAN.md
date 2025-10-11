# AGENT A - EQUITY/BALANCE MODEL FIX PLAN

## Problem Statement

**Current Issue**: In paper mode (Harness), risk_snapshots.csv shows:
- Final equity: $10,000 (unchanged from initial)
- Final closed_pnl: $26,610.80
- **Expected** final equity: $36,610.80
- **Discrepancy**: -$26,610.80

**Root Cause**: RiskSnapshotPersister currently uses AccountInfo.Equity/Balance from cAlgo API, which in paper mode simulation (Harness) does **NOT** reflect actual P&L because there is no real broker account.

## Current Implementation

### RiskSnapshotPersister.cs (lines 44-70)
```csharp
public void Persist(AccountInfo info)
{
    // Get core account metrics FROM ACCOUNTINFO (WRONG IN PAPER MODE)
    double balance = info.Balance;  // ← Always $10,000 in paper mode
    double equity = info.Equity;    // ← Always $10,000 in paper mode
    double usedMargin = info.Margin;
    double freeMargin = equity - usedMargin;

    // Calculate open_pnl: equity - balance 
    double openPnl = equity - balance;  // ← Always 0 because both are $10,000
    
    // Get closed_pnl from tracking
    double closedPnl;
    lock (_lock)
    {
        closedPnl = _closedPnl;  // ← This is CORRECT ($26,610.80 accumulated)
    }
    
    // ... write to CSV
}
```

**Problem**: `info.Equity` and `info.Balance` come from Harness simulation which doesn't update them based on closed trades.

## Required Fix (A.2)

### Solution: Internal Balance/Equity Model

Implement self-contained balance/equity calculation that **does not depend** on AccountInfo values in paper mode.

### Implementation Steps

#### 1. Add Fields to RiskSnapshotPersister

```csharp
public class RiskSnapshotPersister
{
    private readonly string _filePath;
    private readonly object _lock = new object();
    private double _equityPeak = 0.0;
    private double _closedPnl = 0.0;
    
    // NEW FIELDS for paper mode equity model
    private double _initialBalance = 10000.0;  // Set from config or first snapshot
    private bool _isPaperMode = false;         // Set via config or constructor parameter
    private readonly Func<double> _getOpenPnlCallback;  // Callback to get open P&L from RiskManager
```

#### 2. Constructor with Paper Mode Flag

```csharp
public RiskSnapshotPersister(string folder, string fileName, bool isPaperMode = false, Func<double> getOpenPnl = null)
{
    Directory.CreateDirectory(folder);
    _filePath = Path.Combine(folder, fileName);
    _isPaperMode = isPaperMode;
    _getOpenPnlCallback = getOpenPnl;
    EnsureHeader();
}
```

#### 3. Capture Initial Balance on First Snapshot

```csharp
public void Persist(AccountInfo info)
{
    try
    {
        if (info == null) return;
        var ts = DateTime.UtcNow;
        
        double balance;
        double equity;
        double openPnl;
        
        if (_isPaperMode)
        {
            // PAPER MODE: Model balance/equity internally
            lock (_lock)
            {
                // Balance model: initial balance + cumulative closed P&L
                balance = _initialBalance + _closedPnl;
                
                // Open P&L: aggregate from open positions via callback
                openPnl = _getOpenPnlCallback?.Invoke() ?? 0.0;
                
                // Equity model: balance + open P&L
                equity = balance + openPnl;
            }
        }
        else
        {
            // LIVE MODE: Use AccountInfo values
            balance = info.Balance;
            equity = info.Equity;
            openPnl = equity - balance;
        }
        
        // Rest of method unchanged...
    }
    catch { /* swallow for safety */ }
}
```

#### 4. Add GetOpenPnL Method to RiskManager

RiskManager needs to provide total unrealized P&L from open positions:

```csharp
// In RiskManager.cs
private List<Position> _openPositions = new List<Position>();

public double GetTotalOpenPnL()
{
    lock (_openPositions)
    {
        double totalPnl = 0.0;
        foreach (var pos in _openPositions)
        {
            // In paper mode, Position may be a custom class
            // with UnrealizedNetProfit or similar property
            totalPnl += pos.UnrealizedNetProfit;  // Or pos.Pips * pos.PipValue * pos.Volume
        }
        return totalPnl;
    }
}
```

#### 5. Wire Callback in BotG Initialization

```csharp
// In BotG.cs or wherever RiskSnapshotPersister is created
bool isPaperMode = _config.Simulation.Enabled;  // Or from config.runtime.json
var riskPersister = new RiskSnapshotPersister(
    logPath, 
    "risk_snapshots.csv",
    isPaperMode: isPaperMode,
    getOpenPnl: () => _riskManager.GetTotalOpenPnL()
);
TelemetryContext.RiskPersister = riskPersister;
```

## Validation Criteria

### After Fix, risk_snapshots.csv Should Show:

1. **equity_model = balance_model + open_pnl**
   - balance_model = 10000 + closed_pnl
   - equity_model = balance_model + open_pnl
   
2. **closed_pnl matches FIFO sum**
   - `closed_pnl[-1] ≈ sum(closed_trades_fifo.csv['net_realized_usd'])`
   - Tolerance: `|diff| < 1e-6`

3. **open_pnl reflects open positions**
   - When no positions open: open_pnl ≈ 0
   - When positions open: open_pnl = sum(position.UnrealizedNetProfit)

4. **Final equity reflects total P&L**
   - final_equity = initial_balance + closed_pnl + open_pnl
   - Example: 10000 + 26610.80 + 0 = 36610.80

## Unit Test Requirements

```csharp
[Test]
public void Test_PaperMode_EquityModel()
{
    // Arrange
    var initialBalance = 10000.0;
    var persister = new RiskSnapshotPersister("./test", "risk.csv", isPaperMode: true, getOpenPnl: () => 50.0);
    
    // Act: Simulate 3 closed trades
    persister.AddClosedPnl(100.0);  // closed_pnl = 100
    persister.AddClosedPnl(200.0);  // closed_pnl = 300
    persister.AddClosedPnl(-50.0);  // closed_pnl = 250
    
    var mockAccountInfo = new AccountInfo { Equity = 10000, Balance = 10000 };  // Harness values (ignored)
    persister.Persist(mockAccountInfo);
    
    // Assert
    var lines = File.ReadAllLines("./test/risk.csv");
    var lastLine = lines[^1];
    var fields = lastLine.Split(',');
    
    double equity = double.Parse(fields[1]);
    double balance = double.Parse(fields[2]);
    double openPnl = double.Parse(fields[3]);
    double closedPnl = double.Parse(fields[4]);
    
    Assert.AreEqual(10250.0, balance, 0.01);    // 10000 + 250 (closed_pnl)
    Assert.AreEqual(50.0, openPnl, 0.01);       // From callback
    Assert.AreEqual(10300.0, equity, 0.01);     // 10250 + 50
    Assert.AreEqual(250.0, closedPnl, 0.01);    // Accumulated
}

[Test]
public void Test_ClosedPnL_Matches_FIFO_Sum()
{
    // Arrange
    var fifoSum = 26610.804499909427;  // From closed_trades_fifo.csv
    var persister = new RiskSnapshotPersister("./test", "risk.csv", isPaperMode: true);
    
    // Act: Accumulate all closed trades
    // (In real scenario, this happens via ClosedTradesWriter.Append)
    foreach (var trade in closedTrades)
    {
        persister.AddClosedPnl(trade.NetRealizedUsd);
    }
    
    // Assert
    double finalClosedPnl = persister.GetFinalClosedPnl();  // Add getter if needed
    Assert.AreEqual(fifoSum, finalClosedPnl, 1e-6);
}
```

## Alternative Approaches (If Callback Not Feasible)

### Approach B: Track Positions in RiskSnapshotPersister

If passing callback is complex, track positions directly:

```csharp
public class RiskSnapshotPersister
{
    private Dictionary<string, double> _openPositionPnl = new Dictionary<string, double>();
    
    public void UpdatePositionPnL(string positionId, double unrealizedPnl)
    {
        lock (_lock)
        {
            _openPositionPnl[positionId] = unrealizedPnl;
        }
    }
    
    public void RemovePosition(string positionId)
    {
        lock (_lock)
        {
            _openPositionPnl.Remove(positionId);
        }
    }
    
    private double GetTotalOpenPnL()
    {
        lock (_lock)
        {
            return _openPositionPnl.Values.Sum();
        }
    }
}
```

Then wire from ExecutionModule:
```csharp
// On position open/update
TelemetryContext.RiskPersister?.UpdatePositionPnL(positionId, position.UnrealizedNetProfit);

// On position close
TelemetryContext.RiskPersister?.RemovePosition(positionId);
```

## Implementation Priority

1. **HIGH**: Fix balance model = initial + closed_pnl ✅
2. **HIGH**: Ensure closed_pnl matches FIFO sum ✅ (already working)
3. **MEDIUM**: Implement open_pnl from open positions
4. **MEDIUM**: Add paper mode flag/config
5. **LOW**: Add unit tests

## DoD (Definition of Done)

- [ ] balance_model computed internally when isPaperMode = true
- [ ] equity_model = balance_model + open_pnl (not from AccountInfo)
- [ ] Unit test: last_equity == init_balance + closed_pnl + open_pnl
- [ ] Unit test: abs(sum_fifo - last_closed_pnl) < 1e-6
- [ ] Smoke test passes with correct equity values
- [ ] Re-run 24h Gate2: final equity = 10000 + 26610.80 + open_pnl (not $10,000)

## Notes

- **Do NOT modify trading logic** - only telemetry/risk snapshots
- **Do NOT change ExecutionModule sizing** - RiskManager.CalculateOrderSize untouched
- **Paper mode detection**: Can use `config.runtime.json` field `simulation.enabled` or Harness-specific flag
- **Thread safety**: All updates to _closedPnl, _openPositionPnl must be locked

## References

- Issue: path_issues/GATE2_24H_POSTRUN_REPORT.txt (equity mismatch)
- Current code: BotG/Telemetry/RiskSnapshotPersister.cs
- FIFO ledger: closed_trades_fifo.csv (sum = $26,610.80)
- Risk snapshots: risk_snapshots.csv (equity stuck at $10,000)
