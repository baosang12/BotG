# Gate2 Delta Readiness -- 2025-10-03 16:36

| Check | Status | Evidence |
| --- | --- | --- |
| (2) Validator strict | PASS | `path_issues/validate_artifacts.py:30-39` includes REQUEST/ACK/FILL plus commission/spread/slippage; `python -m py_compile path_issues/validate_artifacts.py` exited 0. |
| (3) Risk snapshot columns | PASS | `BotG/Telemetry/RiskSnapshotPersister.cs:25-58` writes `drawdown,R_used,exposure` and tracks `_equityPeak`. |
| (4) Postrun UTF-8 wiring | PASS | `scripts/postrun_collect.ps1:50-92` exports `PYTHONIOENCODING='utf-8'` and uses `python -X utf8`; workflow step shows `PYTHONIOENCODING: utf-8` (`.github/workflows/gate24h_main.yml:306-310`). |
| (5) Demo orchestration | FAIL | `scripts/postrun_collect.ps1 -RunDir D:\tmp\g24_demo` -> `Unexpected token '}'` (parser error at line 81); direct validator fallback succeeds (`python path_issues/validate_artifacts.py --dir D:\tmp\g24_demo` -> `VALIDATE_EXIT=0`). |

READY_FOR_PREFLIGHT=NO  
READY_FOR_GATE2_24H=NO
