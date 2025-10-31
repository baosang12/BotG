## OPS: Require executor_wireproof.json validation in gate24h workflow

### Summary
Add mandatory validation step to `gate24h.yml` that verifies `executor_wireproof.json` exists and has `ok=true` after bot startup. This ensures executor initialization completed successfully before proceeding with postrun analysis.

### Changes
**`.github/workflows/gate24h.yml`**:
- Added new step "Verify executor wireproof" after "Verify heartbeat output"
- Checks file exists at `D:\botg\logs\preflight\executor_wireproof.json`
- Validates required fields: `generated_at`, `trading_enabled`, `connector`, `executor`, `ok`
- Throws error if `ok ≠ true`

### Validation Logic
```powershell
# File must exist
if (-not (Test-Path -LiteralPath $wireproofPath)) {
  throw "Missing executor wireproof: $wireproofPath"
}

# Schema validation
$wireproof = Get-Content -LiteralPath $wireproofPath -Raw | ConvertFrom-Json
$required = @('generated_at','trading_enabled','connector','executor','ok')
foreach ($field in $required) {
  if (-not $wireproof.PSObject.Properties.Name.Contains($field)) {
    throw "executor_wireproof.json missing required field: $field"
  }
}

# Must be ok=true
if ($wireproof.ok -ne $true) {
  throw "executor_wireproof.ok is not true (value: $($wireproof.ok))"
}
```

### Related PRs
- **PR#307**: Runtime changes to generate `executor_wireproof.json` during `OnStart()` (companion PR)

### Agent A Specification Compliance
✅ Point 4: "gate24h.yml: bỏ bước Canary, thêm kiểm tra bắt buộc file preflight/executor_wireproof.json.ok==true"
- No canary step existed in workflow (already clean)
- Added mandatory wireproof validation ✓

### Testing
After merge:
1. Workflow will fail if bot doesn't generate wireproof file
2. Workflow will fail if executor initialization fails (ok=false)
3. Success output shows: `✓ Executor wireproof validated: ok=true, executor=<type>, generated_at=<timestamp>`

### Impact
- **Breaking**: gate24h runs will FAIL if using old BotGRobot.cs without wireproof generation
- **Required merge order**: Merge PR#307 (runtime) BEFORE this PR to avoid gate failures
- **Safety**: Prevents gate from passing when executor initialization silently fails
