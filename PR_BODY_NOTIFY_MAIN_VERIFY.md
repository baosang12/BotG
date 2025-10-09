## Goal
Mainline verification of notify alerting with deterministic E2E driver and durable fallback. No trading strategy changes.

### Changes
- ci(notify): harden fallback and emit proof artifact
  - Keep TELEGRAM_HTTP and telegram_status outputs.
  - Fallback Issue if status != ok; no fail on missing label; upload proof json when present.
- scripts(e2e): lock to dispatch run id + acceptance + proof json
  - The driver finds the exact workflow_dispatch run on the requested ref, downloads full logs, validates acceptance, emits `notify_e2e_proof.json`, zips artifacts.
  - Exit non-zero on failure.
- docs(ops): add notify E2E runbook

### Acceptance criteria
A scenario PASSES if:
- (i) Notify logs contain `TELEGRAM_HTTP=` and `telegram_status=`; OR
- (ii) A GitHub Issue is created as fallback within 5 minutes; AND
- Logs contain at least 100 lines.

### How to reproduce (post-merge)
```powershell
$env:GH_REPO="baosang12/BotG"; gh repo set-default baosang12/BotG
# Cancel scenario on main
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ops_notify_e2e.ps1 -Source notify_test -NotifyRef main -TimeoutNotifySec 900
# Fail-fast scenario on main
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ops_notify_e2e.ps1 -Source notify_test_fail -NotifyRef main -TimeoutNotifySec 900
```

### Evidence
- After merge, attach a screenshot with 2 green notify runs on main and link the fallback Issue if triggered.

### Ops notes
- Do not expand notify logic further; the goal is reliable proof when Telegram fails → Issue fallback.
- After this PR, freeze notify and shift focus to Gate2 one-shot 24h paper.
- Every run should leave `AFTER_RUN artifacts=...` so we can grade PASS/FAIL automatically.
