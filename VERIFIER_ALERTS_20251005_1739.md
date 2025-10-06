# Agent B - Alerts & Watchdog Hotfix Verification

| Check | Status | Notes |
| --- | --- | --- |
| YAML parse (notify/watchdog/gate24h_main) | PASS | `python -c "..."` returned `YAML_OK_ALL`. |
| Notify workflow indentation/backtick | PASS | Rewritten `notify_on_failure.yml` with fallback issue path; `Select-String` for backticks returned no hits. |
| Watchdog workflow indentation/backtick | PASS | Rewritten `watch_gate24h.yml`; cron + github-script present. |
| Telegram secrets present | PASS | `gh secret list` shows `TELEGRAM_BOT_TOKEN` & `TELEGRAM_CHAT_ID`. |
| gate24h_main exports MODE/HOURS/SOURCE/SIM_ENABLED/RUN_DIR | PASS | `Resolve inputs` step writes all env/output variables. |
| ExecutionPolicy bypass within Resolve inputs | **FAIL** | Step still terminates with `PSSecurityException` on self-hosted runner (run 18257500130) before Set-ExecutionPolicy takes effect. |
| Workflow dispatch accepts `sim_enabled` input | PASS | `workflow_dispatch` block now includes `sim_enabled`; `gh workflow run ...` accepted inputs. |
| Preflight 0.1h run & cancel triggers notify | **FAIL** | Run 18257500130 aborted by ExecutionPolicy prior to cancellation; `notify_on_failure` `workflow_run` not fired. |

## Evidence

### YAML Parse
```text
python -c "import yaml,sys; [yaml.safe_load(open(p,'r',encoding='utf-8')) for p in sys.argv[1:]]; print('YAML_OK_ALL')" \
  .github/workflows/notify_on_failure.yml \
  .github/workflows/watch_gate24h.yml \
  .github/workflows/gate24h_main.yml
YAML_OK_ALL
```

### Secrets
```text
$secrets = gh secret list | Out-String
Secrets: BOT=True CHAT=True
AUTO_ENABLED  2025-09-26T12:58:48Z
TELEGRAM_BOT_TOKEN  2025-10-05T09:14:47Z
TELEGRAM_CHAT_ID    2025-10-05T09:14:48Z
```

### Resolve inputs step log (run 18257500130)
```text
gh run view 18257500130 --job 51980874802 --log-failed | Select-String 'Resolve inputs' -Context 0,12
... Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
...
##[error]Process completed with exit code 1. (PSSecurityException)
```

### Notify workflow runs
```text
gh run list --workflow notify_on_failure.yml --limit 5
completed  failure  ops(alerts): fix YAML indentation...  notify_on_failure.yml  ops/alerts-hotfix-parse  push  18257493477  0s  2025-10-05T10:27:18Z
completed  failure  ...  notify_on_failure.yml  main  push  18256978118  0s  2025-10-05T09:35:29Z
```

### Gate24h preflight run
```text
gh run view 18257500130 --json status,conclusion
{"conclusion":"failure","status":"completed"}
```

## Conclusion
READY_FOR_PROD_ALERTS=NO
READY_FOR_GATE24H_PREFLIGHT=NO
