## ops(gate2): Bundle risk_snapshots into artifacts + flatten run_metadata schema

### Overview
Follow-up to PR #234 to fix remaining Gate2 architectural issues discovered during investigation:
1. **Artifact Bundling**: `risk_snapshots.csv` now written to per-run artifact folder instead of base `LogPath`
2. **Metadata Flattening**: `run_metadata.json` now has top-level fields matching DoD expected schema

### Changes

#### 1. TelemetryContext.cs - Artifact Bundling Fix
**File**: `BotG/Telemetry/TelemetryContext.cs`

**Before**:
```csharp
// write runtime files inside runDir, but keep RiskSnapshot in base folder for continuity
OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
ClosedTrades = new ClosedTradesWriter(runDir);
RiskPersister = new RiskSnapshotPersister(Config.LogPath, Config.RiskSnapshotFile);  // ❌ Written to D:\botg\logs\
Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
```

**After**:
```csharp
// write all runtime files inside runDir (per-run artifact folder)
OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
ClosedTrades = new ClosedTradesWriter(runDir);
RiskPersister = new RiskSnapshotPersister(runDir, Config.RiskSnapshotFile);  // ✅ Bundled with artifacts
Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
```

**Impact**:
- `risk_snapshots.csv` now appears in `D:\botg\logs\artifacts\telemetry_run_*/` alongside other telemetry files
- Fixes DoD compliance requirement for complete artifact bundling
- Eliminates need for post-run collection scripts to copy files from separate locations

---

#### 2. RunInitializer.cs - Metadata Schema Flattening
**File**: `BotG/Telemetry/RunInitializer.cs`

**Before**:
```json
{
  "run_id": "telemetry_run_20251008_103700",
  "start_time_iso": "2025-10-08T10:37:00.123Z",
  "host": "DESKTOP-ABC123",
  "git_commit": "11c93d3",
  "config_snapshot": {
    "simulation": { "enabled": false },
    "hours": 24,
    "seconds_per_hour": 3600
  }
}
```

**After**:
```json
{
  "run_id": "telemetry_run_20251008_103700",
  "start_time_iso": "2025-10-08T10:37:00.123Z",
  "host": "DESKTOP-ABC123",
  "git_commit": "11c93d3",
  "mode": "paper",
  "hours": 24,
  "seconds_per_hour": 3600,
  "simulation": { "enabled": false },
  "config_snapshot": { 
    "simulation": { "enabled": false },
    "hours": 24,
    "seconds_per_hour": 3600
  }
}
```

**Impact**:
- Top-level fields (`mode`, `hours`, `seconds_per_hour`, `simulation`) match DoD expected schema
- Backward compatibility maintained via nested `config_snapshot`
- Enables DoD validation scripts to parse metadata without deep nesting

---

### Testing

#### Build & Unit Tests
```
Build: SUCCESS (0 errors, 169 warnings)
Tests: 9/9 PASSED (169ms)
```

#### 12-Minute Smoke Test
**Command**:
```powershell
dotnet run --project Harness/Harness.csproj -c Release -- 720
```

**Expected Validation (pending completion)**:
- ✅ `risk_snapshots.csv` exists in artifact folder
- ✅ `run_metadata.json` has top-level `mode`, `hours`, `seconds_per_hour` fields
- ✅ Risk snapshot count ≥ 12 samples (720s / 60s = 12)
- ✅ No file collection errors

---

### Technical Details

**Artifact Location Changes**:
- **Before**: `D:\botg\logs\risk_snapshots.csv` (separate from artifacts)
- **After**: `D:\botg\logs\artifacts\telemetry_run_20251008_103700\risk_snapshots.csv` (bundled)

**Metadata Schema Evolution**:
- Added top-level fields for DoD compliance
- Retained nested `config_snapshot` for backward compatibility
- No breaking changes to existing parsers

**Related Issues**:
- Follows up on PR #234 (Gate2 blockers 1-3)
- Addresses artifact completeness requirement from DoD checklist
- Resolves metadata schema mismatch discovered during validation

---

### Files Changed
- `BotG/Telemetry/TelemetryContext.cs` (1 line changed)
- `BotG/Telemetry/RunInitializer.cs` (9 lines added for top-level fields)
- `scripts/patch_gate2_artifacts_metadata.ps1` (new automation script)

---

### Checklist
- [x] Branch created from latest main (`11c93d3`)
- [x] Changes committed with descriptive message
- [x] Branch pushed to remote
- [x] Build succeeds (0 errors)
- [x] Unit tests pass (9/9)
- [ ] 12-minute smoke test validates artifact bundling (**running**)
- [ ] PR created with this description
- [ ] Evidence JSON attached (after smoke test completes)

---

### References
- **Previous PR**: #234 (Gate2 blockers: risk heartbeat, timestamps, config defaults)
- **DoD Checklist**: Artifact completeness + metadata schema compliance
- **Run ID**: `telemetry_run_20251008_103700` (12-minute test)
