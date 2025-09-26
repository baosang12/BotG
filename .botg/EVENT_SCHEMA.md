# Event Schema Documentation

## Event Structure
All events follow this JSON schema:
```json
{
  "ts": "2024-01-15T10:30:00Z",     // ISO 8601 UTC timestamp
  "run_id": 12345,                  // GitHub run ID (number)
  "pr": 42,                        // Pull request number
  "stage": "shadow",               // Stage: shadow|decide|auto_rerun
  "level": "info",                 // Log level: info|warn|error
  "msg": "start",                  // Event message
  "kv": {}                         // Optional key-value pairs
}
```

## Standard Events

### Shadow Stage
- `start`: Run begins
- `heartbeat`: Periodic health check
- `conclusion`: Stage completes

### Decide Stage  
- `conclusion`: Classification result

### Auto-Rerun Stage
- `backoff`: Delay before retry
- `rerun`: Triggering retry

## Classification Results
- `PASS`: Tests succeeded  merge eligible
- `NEEDS_ACTION`: Manual intervention required
- `AUTO_RERUN`: Transient error  retry automatically

## Validation
Use `.botg/validate_events.sh` to verify schema compliance.
