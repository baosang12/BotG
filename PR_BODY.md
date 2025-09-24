## Mục tiêu
Thay thế phần stub (PLACEHOLDER) trong Gate 24h bằng supervisor thực sự:
- Vòng lặp bash theo phút, heartbeat comment định kỳ, early-stop, ghi log đầy đủ.

## Thay đổi chính
- Step “Run supervised 24h gate” → `id: supervise`, shell: bash.
- Heartbeat: gửi comment vào tracking issue mỗi HB_MINUTES phút (đọc từ repo variable).
- Early-stop: dừng sớm nếu ≥3 FAIL trong 10 vòng gần nhất (xuất `supervisor_status=FAIL_EARLY`).
- Ghi log `gate24h.log` (START/ITER/HB/EARLY_STOP/END), upload artifact.
- Bổ sung render ở bước “Close tracking issue” (trạng thái, số phút thực, totals).
- Giữ nguyên triggers: `workflow_dispatch` + file-trigger (push `path_issues/start_24h_command_ready.txt` trên `main`).

## Acceptance (DoD)
- Manual smoke: `mode=paper, hours=0.2` ⇒ có ≥1 HB comment, artifact chứa `gate24h.log` (≥10 dòng), `[END] status=OK`.
- Early-stop: đặt repo variable `TEST_FORCE_FAIL=1`, chạy `hours=0.2` ⇒ `supervisor_status=FAIL_EARLY`, log có `[EARLY_STOP]`.
- File-trigger (main): push `path_issues/start_24h_command_ready.txt` với:

mode=paper
hours=0.2
source=file-trigger

⇒ Run tự kích hoạt, có HB + log.

## Rủi ro & rollback
- Thấp, chỉ chạm workflow. Rollback = revert PR.
- Sau merge, commit rỗng để reindex nếu cần.

## Hướng dẫn test nhanh
```bash
# đặt HB 1 phút cho test nhanh
gh variable set HB_MINUTES --repo baosang12/BotG --body "1"

# 1) Manual
gh workflow run gate24h.yml --repo baosang12/BotG -f mode=paper -f hours=0.2 -f source=manual

# 2) Early-stop
gh variable set TEST_FORCE_FAIL --repo baosang12/BotG --body "1"
gh workflow run gate24h.yml --repo baosang12/BotG -f mode=paper -f hours=0.2 -f source=manual
gh variable delete TEST_FORCE_FAIL --repo baosang12/BotG

# 3) File-trigger (trên main)
printf "mode=paper\nhours=0.2\nsource=file-trigger\n" > path_issues/start_24h_command_ready.txt
git add path_issues/start_24h_command_ready.txt
git commit -m "test: trigger gate24h by file"
git push
```
