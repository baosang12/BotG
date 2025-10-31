## Objective

Không đổi chiến lược; chỉ instrumentation & wiring.

Implement comprehensive executor/canary wireproof instrumentation per Agent B verification requirements.

## Changes

### BotGRobot.cs
- Added `WriteWireproofSnapshot` helper (UTF-8 no BOM)
- **Snapshot 0 (init)**: Emitted immediately after TelemetryConfig load
- **Snapshot 1 (executor_ready)**: Emitted after executor instantiation with `events_attached=true`
- **Snapshot 2 (armed)**: Emitted after canary enabled check with `armed=true/false`
- Attached executor `OnFill` and `OnReject` events with `[ECHO+]` logging

### CanaryTrade.cs
- Enhanced `Snapshot` helper to write BOTH `canary_proof.json` AND `canary_wireproof.json`
- Full lifecycle tracking: `armed`, `requested`, `ack`, `fill`, `close`, `reason`, latencies

## Evidence

### Executor Event Wiring (BotGRobot.cs)
```csharp
executor.OnFill += (fill) => Print("[ECHO+] Executor.OnFill: OrderId={0} Price={1} Volume={2} Time={3}", fill.OrderId, fill.Price, fill.Volume, fill.ExecutionTime);
executor.OnReject += (reject) => Print("[ECHO+] Executor.OnReject: OrderId={0} Reason={1}", reject.OrderId, reject.Reason);
```

### Snapshot Lifecycle
```csharp
// Snapshot 0: init
WriteWireproofSnapshot(preflightDir, new Dictionary<string, object?> {
  ["executor_ready"] = false, ["events_attached"] = false, ["armed"] = false, ["reason"] = "init"
});

// Snapshot 1: executor_ready
WriteWireproofSnapshot(preflightDir, new Dictionary<string, object?> {
  ["executor_ready"] = true, ["events_attached"] = true, ["armed"] = false, ["reason"] = "executor_ready"
});

// Snapshot 2: armed (conditional)
if (canaryEnabled && executorReady) {
  WriteWireproofSnapshot(preflightDir, new Dictionary<string, object?> {
    ["armed"] = true, ["reason"] = "armed_onstart"
  });
}
```

## Verification Checklist
- ✅ No strategy changes
- ✅ UTF-8 no BOM enforced
- ✅ Executor events attached early
- ✅ 3 snapshots cover init → executor_ready → armed
- ✅ [ECHO+] logging for OnFill/OnReject
- ✅ CanaryTrade dual-write to wireproof file
- ✅ Build successful (Release)
