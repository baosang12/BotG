# PRShadowAuto MVP Test

This PR tests the new smoke-fast-on-pr workflow with:

## Features Tested
- [x] Shadow testing with classification
- [x] Event logging to events.jsonl  
- [x] PASS/NEEDS_ACTION/AUTO_RERUN states
- [x] Auto-rerun with backoff
- [x] Artifact upload
- [x] Heartbeat validation
- [x] Concurrency control

## Expected Behavior
1. Workflow triggers on PR creation
2. Shadow tests run (20min timeout)
3. Events logged with proper schema
4. Classification based on test results
5. Auto-rerun if transient errors (max 2x)
6. Artifacts uploaded for analysis

## Test Results
Status: Will be updated after workflow execution
Events: Check artifacts for events.jsonl
