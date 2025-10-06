# Notify E2E Runbook

This runbook explains how to verify the notify workflow end-to-end on `main` without touching strategy logic.

## Scenarios

- Cancel: gate run is cancelled mid-flight (`source=notify_test`)
- Fail-fast: gate run fails early (`source=notify_test_fail`)

## Acceptance

A scenario PASSES if:

1) The notify run log contains both of the following lines:
   - `TELEGRAM_HTTP=<code>`
   - `telegram_status=ok|fail`

OR

2) A GitHub Issue was created as fallback within 5 minutes of the run, referencing the run id or source.

Additionally, the notify run log must contain at least 100 lines.

## How to run

Run on Windows PowerShell:

```powershell
$env:GH_REPO="baosang12/BotG"; gh repo set-default baosang12/BotG
# Cancel scenario on main
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ops_notify_e2e.ps1 -Source notify_test -NotifyRef main -TimeoutNotifySec 900
# Fail-fast scenario on main
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ops_notify_e2e.ps1 -Source notify_test_fail -NotifyRef main -TimeoutNotifySec 900
```

## Artifacts

- `path_issues/notify_e2e_log.txt`: full notify job log
- `path_issues/notify_e2e_proof.json`: structured proof:
  ```json
  {
    "scenario": "notify_test",
    "run_id": 1234567890,
    "ref": "main",
    "http_code": "200",
    "telegram_status": "ok",
    "log_line_count": 120,
    "fallback_issue_url": "https://github.com/owner/repo/issues/1",
    "started_at": "2025-10-06T06:30:00Z",
    "finished_at": "2025-10-06T06:31:10Z"
  }
  ```

Zip bundle: `path_issues/alerts/notify_e2e_main_<timestamp>.zip`.
