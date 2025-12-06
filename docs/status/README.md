# Trạng thái hạ tầng & dọn dẹp workspace

Tài liệu này thay thế các file placeholder kiểu `== CI status (required checks) ==`. Ghi chú trạng thái CI/gate cũng như checklist dọn dẹp sẽ nằm ở đây để tránh rác ngay thư mục gốc.

## 1. Snapshot CI / Gate

- Thay vì tạo file rỗng, cập nhật ngay bảng dưới đây (giữ 3 mục gần nhất).
- Nếu cần thêm bằng chứng chi tiết, đặt tại `docs/status/<tên-báo-cáo>.md`.

| Ngày | Nội dung | Ghi chú |
| --- | --- | --- |
| 2025-11-22 | Đã xóa các file placeholder và chuyển log status vào tài liệu này | N/A |

## 2. Dọn artifact cục bộ

- Dùng `scripts/cleanup_local_artifacts.ps1 [-DryRun]` để gom các file build tay (`BotG.zip`, `build_report.txt`, `temp_*.cs`, `test*.txt`, v.v.) và thư mục tạm (`_tmp/`, `tmp_untracked_backups/`).
- Mặc định script sẽ xóa ngay; dùng `-DryRun` để xem trước.
- Sau khi xóa, commit tree sạch hơn và dễ kiểm soát diff.

## 3. Lưu trữ dung lượng lớn

Các thư mục sau chỉ phục vụ điều tra tại chỗ. Di chuyển chúng ra `artifacts/` riêng hoặc ổ lưu trữ bên ngoài khi không còn cần truy cập thường xuyên:

- `artifacts_ascii/`
- `artifacts_run_100/`
- `gate24h-artifacts-*`
- `gate24h-selfcheck-*`

Nếu cần giữ trong repo (ví dụ khi đang viết báo cáo), nén lại và đặt vào `artifacts/archive/` để dễ xoá khi hoàn tất.

## 4. Log cục bộ

- `monitor_logs/` đã được ignore để tránh vô tình commit log sản xuất.
- Các log hệ thống thực tế vẫn nên nằm tại `D:\botg\logs`. Chỉ copy đoạn cần phân tích vào repo và xóa sau khi kết thúc ticket.
