# Data Models

Các model trong thư mục này được di chuyển từ `BotG/BotG/DataFetcher/Models` để bảo đảm Preprocessor tách biệt vẫn có đủ cấu trúc dữ liệu cơ bản (tick, bar, account info, timeframe).

## Kế hoạch hợp nhất

1. **Giai đoạn hiện tại (song song)**: giữ bản sao tại đây để unblock việc dựng project mới. `AnalysisModule.Preprocessor` xem đây là nguồn chuẩn, còn DataFetcher sử dụng bản gốc cho tới khi adapter sẵn sàng.
2. **Giai đoạn chuyển tiếp**: tạo project shared `BotG.SharedModels` (hoặc đổi namespace hiện tại thành shared) và di chuyển các file `Tick/Bar/AccountInfo/TimeFrame` vào đó. Cả `BotG` lẫn `AnalysisModule.Preprocessor` sẽ tham chiếu chung → loại bỏ duplication.
3. **Giai đoạn làm sạch**: cập nhật DataFetcher để reference shared models, xóa hẳn versions cũ, thêm unit test bảo vệ serialization để tránh breaking change.

Mọi thay đổi shape dữ liệu phải khai báo trong PR mô tả rõ tác động tới hai project và cập nhật tài liệu này đi kèm.
