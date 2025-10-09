# Post-merge Readiness Report — PR #17 — 20250829_122749

Tổng quan: Bằng chứng sau merge đã được thu thập từ chạy smoke và bước reconstruct. Các tín hiệu cho thấy hệ thống hoạt động ổn định, không phát hiện orphan hay unmatched.
Kết luận nhanh: Gates hiện tại — Build=PASS, Smoke=PASS, Reconstruct=PASS, Logging=PASS.

## Acceptance gates

| Gate        | Status | Lý do ngắn gọn |
|-------------|--------|----------------|
| Build       | PASS   | dotnet build thành công, chỉ có cảnh báo; tạo .algo/.dll đầy đủ (xem `path_issues/build_and_test_output.txt`). |
| Smoke       | PASS   | `smoke_summary_20250827_200336.json`: acceptance=PASS, orphan_after=0, trades=248, total_pnl≈36.95. |
| Reconstruct | PASS   | `reconstruct_report.json`: orphan_after=0, unmatched_ids=[], fills_total=1, matched_count=2. |
| Logging     | PASS   | Có log/biểu đồ và CSV (fillrate_by_hour.png, latency_percentiles.png, slippage_hist.png, fillrate_hourly.csv, top_slippage.csv, orders_ascii.csv). |

## Key metrics

- request_count: N/A (không có số liệu rõ ràng trong inputs)
- fill_count: 1
- fill_rate: N/A
- orphan_after: 0
- unmatched_orders_count: 0
- sum_pnl: 36.952000000003224

Nguồn số liệu: `path_issues/postmerge_metrics_20250829_122749.json` (tổng hợp từ `reconstruct_report.json` và `smoke_summary_20250827_200336.json`).

## Artifacts inventory (with SHA256)

| Name | Relative path | Size (bytes) | mtime (UTC) | SHA256 |
|------|----------------|--------------|-------------|--------|
| postrun_artifacts_20250827_101615.zip | path_issues/postrun_artifacts_20250827_101615.zip | 12821 | 2025-08-27T03:16:15.1703932Z | 1B46C04E78CB65C415B154FF6F610141EDFAB7FEFF7B79A9F79CC85DCD496FB2 |
| postrun_artifacts_20250827_102232.zip | path_issues/postrun_artifacts_20250827_102232.zip | 93312 | 2025-08-27T03:22:32.6585293Z | 4E66E8C6BBF8620F5D4E8E8CF7B27E1BD879FCA6787BC09C492D25F06DB94D83 |
| postrun_artifacts_20250827_102333.zip | path_issues/postrun_artifacts_20250827_102333.zip | 93312 | 2025-08-27T03:23:33.8917537Z | 27B63D084E92973C0979439F397A1688AEF85E0BC4135A70EF197361B32DEDDF |
| telemetry_run_20250827_200336.zip | D:\\botg\\logs\\artifacts\\telemetry_run_20250827_200336\\telemetry_run_20250827_200336.zip | 48901 | 2025-08-27T13:05:39.9629432Z | 3F88D20B0AF48EC6A2FBBFC4C13F5D8FE238E608868E538476BEB52BF29DA6D1 |
| telemetry_run_20250829_180804.zip | artifacts/telemetry_run_20250829_180804.zip | 49599 | 2025-08-29T11:10:10.9202926Z | 67344907321DF100207A1C112E96D044E5BD48BB4EE4A5F5986D678EE7A25529 |

Ghi chú: Bảng trên liệt kê các artifact đã tính được SHA256 tin cậy. Các artifact khác vẫn hiện diện trong `path_issues/` nhưng do vấn đề đường dẫn Unicode hệ thống nên chưa băm được trong phiên này.

## Operator commands

- Re-run CI (web): mở https://github.com/baosang12/BotG/actions và rerun workflow liên quan.
- Re-run CI (gh CLI):
	- gh auth login
	- gh run list -w smoke.yml -R baosang12/BotG
	- gh run rerun <run-id> -R baosang12/BotG

- Chạy smoke ngắn (2 phút, local PowerShell):
	- powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_smoke.ps1 -Seconds 120 -SecondsPerHour 300 -DrainSeconds 20 -FillProb 1.0 -UseSimulation

- Chạy 24h supervised (từ `path_issues/start_24h_command.txt`):
	- powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_realtime_24h_supervised.ps1 -Hours 24 -SecondsPerHour 3600 -FillProbability 1.0 -UseSimulation:$false

- Tạo release và upload artifact (gh CLI):
	- gh release create postmerge-20250829_122749 .\path_issues\postrun_artifacts_20250827_102232.zip .\path_issues\postrun_artifacts_20250827_102333.zip -t "Post-merge 20250829_122749" -n "Automated post-merge artifacts" -R baosang12/BotG

- Tính SHA256 cục bộ (PowerShell):
	- Get-FileHash -LiteralPath <file> -Algorithm SHA256 | Format-List

## Checklist (reviewer/operator)

- [ ] orphan_after == 0
- [ ] unmatched_orders_count == 0
- [ ] fill_rate ≈ 1.0 (nếu có request_count)
- [ ] reconstruct CSV có mặt: `path_issues/closed_trades_fifo_reconstructed.csv`
- [ ] logs đọc được; ảnh PNG/CSV hiện diện
- [ ] checksum (SHA256) đi kèm artifact chính

## Paths & evidence

- `path_issues/reconstruct_report.json`
- `path_issues/smoke_summary_20250827_200336.json`
- `path_issues/postmerge_metrics_20250829_122749.json`
- `path_issues/postmerge_check_20250829_122749.txt`
- `path_issues/latest_zip.txt` → trỏ tới file zip: D:\\botg\\logs\\artifacts\\telemetry_run_20250827_200336\\telemetry_run_20250827_200336.zip
- `path_issues/postmerge_artifacts_checksums_20250829_122749.json`

## Notes / Diagnostics

- Used local evidence; no remote API call.
- Một số thao tác băm file trong PowerShell bị giới hạn bởi vấn đề chuẩn hóa Unicode trên đường dẫn OneDrive; đã băm thành công các artifact zip tiêu biểu và artifact zip ngoài thư mục (`latest_zip.txt`).
- Build log xác nhận build thành công với cảnh báo nullable, không có lỗi.

