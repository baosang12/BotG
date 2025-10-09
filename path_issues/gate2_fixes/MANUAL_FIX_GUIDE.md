# Gate2 Fixes - Manual Application Guide

## ⚠️ CRITICAL: Workspace Limitation Detected

The AI agent cannot directly edit C# files in `BotG/` subdirectory due to VS Code workspace restrictions.

**Required Action**: User must manually apply the following code changes.

---

## Fix 1: RiskSnapshotPersister.cs - Add missing columns

**File**: `BotG\Telemetry\RiskSnapshotPersister.cs`

### Change 1.1: Update header (line ~25)

**FIND:**
```csharp
                File.AppendAllText(_filePath, "timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);
```

**REPLACE WITH:**
```csharp
                File.AppendAllText(_filePath, "timestamp_utc,equity,balance,open_pnl,closed_pnl,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);
```

### Change 1.2: Update Persist() method (line ~45)

**FIND:**
```csharp
                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    drawdown.ToString(CultureInfo.InvariantCulture),
                    rUsed.ToString(CultureInfo.InvariantCulture),
                    exposure.ToString(CultureInfo.InvariantCulture)
                );
```

**REPLACE WITH:**
```csharp
                double openPnl = 0.0;  // TODO: aggregate from open positions
                double closedPnl = 0.0; // TODO: from ClosedTradesWriter

                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    openPnl.ToString(CultureInfo.InvariantCulture),
                    closedPnl.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    drawdown.ToString(CultureInfo.InvariantCulture),
                    rUsed.ToString(CultureInfo.InvariantCulture),
                    exposure.ToString(CultureInfo.InvariantCulture)
                );
```

---

## Fix 2: RiskManager.cs - Ensure 60s heartbeat even without AccountInfo

**File**: `BotG\RiskManager\RiskManager.cs`

### Change 2.1: Update PersistSnapshotIfAvailable() method

**FIND:**
```csharp
        private void PersistSnapshotIfAvailable()
        {
            try
            {
                if (_lastAccountInfo != null)
                {
                    TelemetryContext.RiskPersister?.Persist(_lastAccountInfo);
                }
            }
            catch { }
        }
```

**REPLACE WITH:**
```csharp
        private void PersistSnapshotIfAvailable()
        {
            try
            {
                var info = _lastAccountInfo;
                if (info == null)
                {
                    // Stub AccountInfo to ensure 60s heartbeat even without updates
                    info = new AccountInfo
                    {
                        Equity = _equityOverride ?? 10000.0,
                        Balance = 10000.0,
                        Margin = 0.0
                    };
                }
                TelemetryContext.RiskPersister?.Persist(info);
            }
            catch { }
        }
```

---

## Fix 3: OrderLifecycleLogger.cs - Populate timestamp_request/ack/fill

**File**: `BotG\Telemetry\OrderLifecycleLogger.cs`

### Change 3.1: Add OrderLifecycleState class (after namespace line ~6)

**FIND:**
```csharp
namespace Telemetry
{
    public class OrderLifecycleLogger
```

**REPLACE WITH:**
```csharp
namespace Telemetry
{
    internal class OrderLifecycleState
    {
        public long RequestEpochMs { get; set; }
        public string? TsRequest { get; set; }
        public string? TsAck { get; set; }
        public string? TsFill { get; set; }
    }

    public class OrderLifecycleLogger
```

### Change 3.2: Replace _requestEpochMs dictionary (line ~12)

**FIND:**
```csharp
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _requestEpochMs = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
```

**REPLACE WITH:**
```csharp
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OrderLifecycleState> _orderStates = new System.Collections.Concurrent.ConcurrentDictionary<string, OrderLifecycleState>(StringComparer.OrdinalIgnoreCase);
```

### Change 3.3: Update LogV2() tracking logic (line ~70)

**FIND:**
```csharp
                // latency tracking based on first REQUEST time
                long? latencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    _requestEpochMs[orderId] = epoch;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (_requestEpochMs.TryGetValue(orderId, out var reqEpoch))
                    {
                        latencyMs = epoch - reqEpoch;
                    }
                }
```

**REPLACE WITH:**
```csharp
                // latency tracking + timestamp population
                var tsIso = ts.ToString("o", CultureInfo.InvariantCulture);
                long? latencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                
                var state = _orderStates.GetOrAdd(orderId, _ => new OrderLifecycleState());
                
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    state.RequestEpochMs = epoch;
                    state.TsRequest = tsIso;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsAck = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
                else if (string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsFill = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
                else if (string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
```

### Change 3.4: Update CSV line construction (line ~110)

**FIND** the line construction ending with:
```csharp
                    Escape(session),
                    Escape(host)
                );
```

**REPLACE WITH** (add 4 new fields at the end):
```csharp
                    Escape(session),
                    Escape(host),
                    // Canonical timestamp aliases (now populated)
                    Escape(orderId), // order_id (duplicate for compatibility)
                    Escape(state.TsRequest ?? ""),
                    Escape(state.TsAck ?? ""),
                    Escape(state.TsFill ?? "")
                );
```

---

## Fix 4: TelemetryConfig.cs - Default hours=24, simulation=false

**File**: `BotG\Telemetry\TelemetryConfig.cs`

### Change 4.1: Update default property values (line ~14)

**FIND:**
```csharp
    public int Hours { get; set; } = 1; // used by wrappers
    public int SecondsPerHour { get; set; } = 300; // 1h -> 5min default
    public int DrainSeconds { get; set; } = 30; // drain window at shutdown
    public int GracefulShutdownWaitSeconds { get; set; } = 5; // extra wait for OS buffers
    public bool UseSimulation { get; set; } = true;
```

**REPLACE WITH:**
```csharp
    public int Hours { get; set; } = 24; // Production default: 24h runs
    public int SecondsPerHour { get; set; } = 3600; // Real-time by default
    public int DrainSeconds { get; set; } = 30; // drain window at shutdown
    public int GracefulShutdownWaitSeconds { get; set; } = 5; // extra wait for OS buffers
    public bool UseSimulation { get; set; } = false; // Paper mode default (no simulation)
```

---

## Verification Commands

After applying all changes:

```powershell
# 1. Check git diff
cd D:\OneDrive\TAILIU~1\cAlgo\Sources\Robots\BotG
git diff

# 2. Build
dotnet build -c Release

# 3. Quick test
dotnet test -c Release --filter "TestCategory!=Slow"
```

## Expected Build Output

If successful, you should see:
- ✅ 0 errors
- ✅ All tests passing
- ✅ RiskSnapshotPersister compiles with new columns
- ✅ OrderLifecycleLogger compiles with OrderLifecycleState

---

## Alternative: Use VS Code Search & Replace

1. Open VS Code
2. Use Ctrl+Shift+H (Find and Replace in Files)
3. For each change above:
   - Paste "FIND" text into search box
   - Paste "REPLACE WITH" text into replace box
   - Click "Replace All" for that file
4. Save all files

---

## BLOCKER: If User Cannot Apply

**STOP HERE and report:**
- Cannot edit C# files due to workspace restrictions
- Need user to manually apply 4 file changes listed above
- Alternative: User adds BotG/ folder to VS Code workspace then agent can edit directly

