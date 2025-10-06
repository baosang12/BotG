Tiêu đề: ci: kiểm tra lại các giao dịch đã tái tạo từ artifacts smoke (tăng cường postrun)

Tóm tắt
- Thêm job CI “Reconstruct & Validate (from smoke)” để tải về artifacts của Quick Smoke 60s, thực hiện tái tạo (reconstruction) từ `orders.csv`, và áp hai hàng rào:
  - mọi `close_time` phải >= `open_time`
  - các giá trị PnL phải ở dạng Decimal với 1–8 chữ số thập phân (không có hiện tượng float artifact)
- Upload các artifact kiểm tra (CSV + JSON tóm tắt) để debug.
- Giữ trạng thái yêu cầu hợp nhất “CI - build & test” chỉ báo thành công khi build-test, quick-smoke và reconstruct-validate đều PASS.

Các thay đổi
- `.github/workflows/ci.yml`: thêm job `reconstruct-validate` và đưa nó vào trạng thái tổng hợp bắt buộc.
- `scripts/ci_reconstruct_validate.ps1`: script giúp CI chạy tái tạo + kiểm tra trên artifacts đã tải xuống.

Lý do
- Phản hồi từ reviewer: xuất hiện các artifact PnL do float và một vài giao dịch có `close_time < open_time`. Trình tái tạo đã được sửa để dùng Decimal và đảm bảo thứ tự thời gian; PR này tự động bảo vệ các bất biến đó trong CI.

Checklist chấp nhận
- [ ] Build and Test (windows-latest) pass.
- [ ] Quick Smoke (60s) pass với `orphan_after == 0`.
- [ ] Reconstruct & Validate (from smoke) pass với:
    - `badTimeCount == 0`
    - `badPnlCount == 0`
- [ ] `recon-validation-artifacts` được upload và chứa `reconstruct_validation_*.json`

Ghi chú
- Job này chạy trên Windows, chạy sau Quick Smoke và thường rất nhanh (~10–20s).
- Nếu thất bại, mở artifact `recon-validation-artifacts` trong run để xem file `*_details.csv` chứa các hàng vi phạm.
