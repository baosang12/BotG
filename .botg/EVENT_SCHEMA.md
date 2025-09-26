# BotG Events Schema Documentation

## Event Structure

Each line in `events.jsonl` must be valid JSON with required fields:

```json
{
  "ts": "2025-09-26T05:30:00Z",        // ISO 8601 timestamp
  "run_id": 123,                       // GitHub run ID
  "pr": 130,                           // PR number  
  "stage": "shadow",                   // Stage: setup|shadow|decide|auto
  "level": "info",                     // Level: info|warn|error
  "msg": "start",                      // Message type
  "kv": {                              // Key-value context
    "sha": "<commit>",
    "actor": "<user>",
    "status": "PASS|NEEDS_ACTION|AUTO_RERUN",
    "reason": "missing_heartbeat|tests_failed|transient_error",
    "latency_ms": 1250,
    "price_requested": 0.01,
    "price_filled": 0.0095
  }
}
```

## Required Fields
- `ts`: ISO 8601 timestamp
- `run_id`: GitHub Actions run ID
- `pr`: Pull request number
- `stage`: Current processing stage
- `level`: Log level (info/warn/error)
- `msg`: Event message type
- `kv`: Context object with stage-specific data

## Status Classifications

### PASS
- All shadow tests pass
- No policy violations  
- Recent heartbeat (5min)
- Required artifacts present

### NEEDS_ACTION  
- Missing artifacts or stale heartbeat (>5min)
- Policy/allowlist violations
- Manual review required

### AUTO_RERUN
- Infrastructure/transient errors (exit 137/143, OOM, 5xx, rate-limit)
- Flaky tests (fail then pass on retry)
- Limited to 2 attempts with 5m15m backoff

## Event Examples

### Start Event
```json
{"ts":"2025-09-26T05:30:00Z","run_id":123,"pr":130,"stage":"shadow","level":"info","msg":"start","kv":{"sha":"abc123","actor":"user1"}}
```

### Warning Event  
```json
{"ts":"2025-09-26T05:31:12Z","run_id":123,"pr":130,"stage":"shadow","level":"warn","msg":"missing_heartbeat","kv":{"last_hb_age_s":420}}
```

### Decision Event
```json
{"ts":"2025-09-26T05:33:50Z","run_id":123,"pr":130,"stage":"decide","level":"info","msg":"conclusion","kv":{"status":"NEEDS_ACTION","reason":"missing_heartbeat"}}
```
