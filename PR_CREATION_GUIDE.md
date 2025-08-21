# Complete PR Creation Guide

## 1) Mở Compare URL (một click)

Mở (bấm hoặc copy vào trình duyệt):

```
https://github.com/baosang12/BotG/compare/main...botg/automation/reconcile-streaming-20250821_084334
```

> Nếu trang vẫn show "There isn't anything to compare", chọn **base = main** từ dropdown bên trái rồi chọn **compare = botg/automation/reconcile-streaming-20250821\_084334**.

---

## 2) Tạo Draft PR trên web (nhanh)

1. Click **Create pull request** (hoặc **Create draft pull request**).
2. **Title** (ô Title) — dán:

```
feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)
```

3. **Description** — dán toàn bộ nội dung PR body (từ file `scripts/pr_body_final.txt`):

```
feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)

Tóm tắt
Thêm các script tự động hoá để:
- Tạo file "closes" từ reconstructed closed trades,
- Chạy reconcile giữa closed trades và closes,
- Tính toán fill-breakdown trên orders.csv lớn bằng chế độ streaming/chunked để tránh OOM.

What changed
- scripts/make_closes_from_reconstructed.py — stream + dedupe, xuất cleaned closes CSV/JSONL.
- scripts/compute_fill_breakdown_stream.py — streaming/chunked compute; outputs: fill_rate_by_side.csv, fill_breakdown_by_hour.csv, analysis_summary_stats.json.
- scripts/run_reconcile_and_compute.ps1 — parameterized wrapper with backups, logging, CSV/JSONL fallback.
- scripts/reconcile.py — compatibility tweaks for closes keys.

Why
Giải quyết nguy cơ OOM và cải thiện khả năng xử lý orders.csv lớn bằng streaming và wrapper an toàn.

How I tested
- Reconcile: closed_sum == closes_sum == 150016.07399960753 (diff 0)
- Sample run: rows: 3,010,362; chunks: 302; elapsed ~277.38s
- Build: dotnet build (PASS); Tests: dotnet test (7 passed)

How to validate locally
1. Fetch & checkout:
   git fetch origin
   git checkout botg/automation/reconcile-streaming-20250821_084334
2. Build & tests:
   dotnet build
   dotnet test
3. Run wrapper (sample):
   .\scripts\run_reconcile_and_compute.ps1 -ArtifactPath .\artifacts\telemetry_run_20250819_154459 -ChunkSize 10000
   Expect: exit code 0 and auto_reconcile_compute_summary.json with closed_sum == closes_sum.

Risk & rollback
- Backup tag: pre_push_backup_20250821_105716
- Revert: git checkout main; git reset --hard pre_push_backup_20250821_105716

Files changed
- scripts/compute_fill_breakdown_stream.py
- scripts/make_closes_from_reconstructed.py
- scripts/reconcile.py
- scripts/run_reconcile_and_compute.ps1
```

4. Ở phải: thêm Reviewers / Labels (tuỳ bạn).
5. Click **Create draft pull request**.

---

## 3) Tạo PR bằng gh CLI (nếu đã login)

Chạy trên máy bạn nếu đã `gh auth login`:

```powershell
gh pr create --draft `
  --title "feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)" `
  --body-file scripts/pr_body_final.txt `
  --base main --head botg/automation/reconcile-streaming-20250821_084334
```

`gh` sẽ in ra URL PR khi thành công.

---

## 4) PR reviewer checklist (paste vào PR hoặc attach `scripts/pr_review_checklist.md`)

Copy-paste this into the PR description or first comment:

```
# PR Review Checklist

1. Fetch & checkout:
   git fetch origin
   git checkout botg/automation/reconcile-streaming-20250821_084334

2. Build & tests:
   dotnet build
   dotnet test
   Expect: success and tests pass.

3. Run sample wrapper:
   .\scripts\run_reconcile_and_compute.ps1 -ArtifactPath .\artifacts\telemetry_run_20250819_154459 -ChunkSize 10000
   Expect: exit 0; auto_reconcile_compute_summary.json created; closed_sum == closes_sum.

4. Inspect outputs:
   - fill_rate_by_side.csv
   - fill_breakdown_by_hour.csv
   - analysis_summary_stats.json

5. Performance checks: change -ChunkSize to tune memory/time if needed.

6. Repo hygiene: ensure no artifacts/ large files were committed.

7. Merge readiness:
   [ ] Build & tests pass
   [ ] Sample wrapper run successful
   [ ] Reviewer approvals (1 backend + 1 QA)
```

---

## 5) Dọn file NOOP tạm sau PR (recommended)

Sau khi PR được tạo (vẫn trên branch), xóa file tạm để không để rác trong repo:

```powershell
git checkout botg/automation/reconcile-streaming-20250821_084334
git rm scripts/.pr_trigger_for_pr.txt
git commit -m "chore: remove temporary PR trigger file"
git push origin botg/automation/reconcile-streaming-20250821_084334
```

> Hoặc bạn có thể wait until PR merged then remove in a follow-up commit.

---

## 6) Nếu gặp lỗi trên web (Compare vẫn không show)

* Reload trang; chắc chắn base = main selected.
* Nếu vẫn lỗi, paste vào đây output các lệnh:

```bash
git fetch origin --prune
git branch -r
git log --oneline origin/main..origin/botg/automation/reconcile-streaming-20250821_084334
```

---

## Files Created/Updated in this PR:

- **scripts/pr_body_final.txt** - Ready-to-paste PR description
- **scripts/pr_review_checklist.md** - Reviewer checklist
- **.gitignore** - Added to prevent build artifacts
- **scripts/compute_fill_breakdown_stream.py** (existing)
- **scripts/make_closes_from_reconstructed.py** (existing)  
- **scripts/reconcile.py** (existing)
- **scripts/run_reconcile_and_compute.ps1** (existing)