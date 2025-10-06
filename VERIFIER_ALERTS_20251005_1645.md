# Agent B - Alerts & Watchdog verification (PR #195)

| Check | Status | Notes |
| --- | --- | --- |
| notify_on_failure.yml parses with PyYAML | FAIL | Parser error at .github/workflows/notify_on_failure.yml lines 17-21 (inconsistent indentation). |
| watch_gate24h.yml parses with PyYAML | FAIL | Parser error at .github/workflows/watch_gate24h.yml lines 30-35 (inconsistent indentation). |
| gate24h_main.yml parses with PyYAML | PASS | PyYAML safe_load succeeded. |
| No backticks before ${{ }} expressions | PASS | `Select-String` found no occurrences. |
| Telegram secrets present | PASS | TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID found via `gh secret list`. |
| gate24h_main concurrency blocks | PASS | Top-level concurrency and job-level concurrency present (see evidence). |
| "Prep selfcheck dir" step present | PASS | Step name located in gate24h_main.yml. |
| Resolve inputs fallback covers HOURS/MODE/SIM_ENABLED/RUN_DIR | FAIL | Only MODE/HOURS/SOURCE are handled; SIM_ENABLED and RUN_DIR missing in resolve block. |
| Preflight Resolve inputs step passes | FAIL | Step fails on runner due to PowerShell execution policy (see run 18257034877). |
| Ensure log directory step executes | FAIL | Step not reached because earlier failure stopped job. |
| Notify on Gate24h failure triggered by workflow_run | FAIL | No runs found for notify_on_failure.yml with event=workflow_run after preflight failure. |
| Watchdog cron registered | PASS | Cron "*/10 * * * *" present and visible via `gh workflow view`. |
| Watchdog telegram step guarded | PASS | actions/github-script + appleboy steps present in watch_gate24h.yml. |
| Workflow dispatch accepts sim_enabled input | FAIL | gh workflow run rejected sim_enabled extra input (not defined). |

## Evidence

### YAML parser output
```text
python -c "import yaml; import pathlib; yaml.safe_load(pathlib.Path('.github/workflows/notify_on_failure.yml').read_text(encoding='utf-8'))"
Traceback (most recent call last):
  File "<string>", line 1, in <module>
    import yaml; import pathlib; yaml.safe_load(pathlib.Path('.github/workflows/notify_on_failure.yml').read_text(encoding='utf-8'))
  File "C:\Users\TechCare\AppData\Roaming\Python\Python313\site-packages\yaml\__init__.py", line 125, in safe_load
    return load(stream, SafeLoader)
  File "C:\Users\TechCare\AppData\Roaming\Python\Python313\site-packages\yaml\parser.py", line 438, in parse_block_mapping_key
    raise ParserError("while parsing a block mapping", self.marks[-1],
yaml.parser.ParserError: while parsing a block mapping
  in "<unicode string>", line 14, column 11:
              to:    ${{ secrets.TELEGRAM_CHAT ...
              ^
expected <block end>, but found '<block mapping start>'
  in "<unicode string>", line 18, column 13:
                Repo: ${{ github.repository }}
                ^
```

```text
python -c "import yaml; import pathlib; yaml.safe_load(pathlib.Path('.github/workflows/watch_gate24h.yml').read_text(encoding='utf-8'))"
Traceback (most recent call last):
  File "<string>", line 1, in <module>
    import yaml; import pathlib; yaml.safe_load(pathlib.Path('.github/workflows/watch_gate24h.yml').read_text(encoding='utf-8'))
  File "C:\Users\TechCare\AppData\Roaming\Python\Python313\site-packages\yaml\parser.py", line 438, in parse_block_mapping_key
    raise ParserError("while parsing a block mapping", self.marks[-1],
yaml.parser.ParserError: while parsing a block mapping
  in "<unicode string>", line 27, column 11:
              to:    ${{ secrets.TELEGRAM_CHAT ...
              ^
expected <block end>, but found '<block mapping start>'
  in "<unicode string>", line 31, column 13:
                Status: ${{ steps.s.outputs.stat ...
                ^
```

### Backtick scan
```text
Select-String -Path .github/workflows/*.yml -Pattern '`\$\{\{' -SimpleMatch
(no matches)
```

### Secrets
```text
$secrets = gh secret list | Out-String
Secrets: BOT=True CHAT=True
AUTO_ENABLED  2025-09-26T12:58:48Z
TELEGRAM_BOT_TOKEN  2025-10-05T09:14:47Z
TELEGRAM_CHAT_ID    2025-10-05T09:14:48Z
```

### gate24h_main structure
```text
Select-String -Path .github/workflows/gate24h_main.yml -Pattern '^concurrency:'
.github\workflows\gate24h_main.yml:3:concurrency:

Select-String -Path .github/workflows/gate24h_main.yml -Pattern 'Prep selfcheck dir'
.github\workflows\gate24h_main.yml:400:    - name: Prep selfcheck dir

Select-String -Path .github/workflows/gate24h_main.yml -Pattern 'Resolve inputs'
.github\workflows\gate24h_main.yml:76:    - name: Resolve inputs
```

### Missing SIM_ENABLED and RUN_DIR handling
```text
Select-String -Path .github/workflows/gate24h_main.yml -Pattern 'SIM_ENABLED'
(no matches)

Select-String -Path .github/workflows/gate24h_main.yml -Pattern 'RUN_DIR'
(no matches)
```

### Workflow dispatch inputs
```text
gh workflow run gate24h_main.yml -f hours=0.1 -f source=manual -f mode=paper -f sim_enabled=false
could not create workflow dispatch event: HTTP 422: Unexpected inputs provided: ["sim_enabled"]
```

### Preflight run 18257034877 (Resolve inputs failure on runner)
```text
gh run view --job 51979795328 --log-failed
(PSSecurityException) File ...\_temp\05f255df-40f1-4e19-8b93-196e20ae22ee.ps1 cannot be loaded because running scripts is disabled on this system.
```

### Notify workflow after failure
```text
gh api "repos/baosang12/BotG/actions/workflows/notify_on_failure.yml/runs?event=workflow_run&per_page=5"
{"total_count":0,"workflow_runs":[]}
```

### Watchdog cron confirmation
```text
Select-String -Path .github/workflows/watch_gate24h.yml -Pattern 'cron: "\*/10 \* \* \* \*"'
.github\workflows\watch_gate24h.yml:4:    - cron: "*/10 * * * *"
```

## Conclusion
READY_FOR_PROD_ALERTS=NO
READY_FOR_GATE24H_PREFLIGHT=NO
