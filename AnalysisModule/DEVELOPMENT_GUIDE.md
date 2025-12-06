# Development Guide

## Quy ước mã nguồn

- Ngôn ngữ: C# 10, target .NET 6 (sẽ nâng lên .NET 8 khi toàn bộ solution sẵn sàng).
- Đặt tên namespace theo cấu trúc `Analysis.Preprocessor.*`.
- Ưu tiên immutable record cho DTO snapshot, class cho component có trạng thái.
- Comment code bằng tiếng Việt, thuật ngữ kỹ thuật giữ nguyên tiếng Anh nếu không có dịch chuẩn.

## Pattern chính

- **Pipeline orchestration**: sử dụng dependency injection + options pattern để đăng ký calculators.
- **Eventing**: dùng `EventHandler<T>` cho compat, xem xét `Channel<T>` cho throughput cao.
- **Caching**: `CircularBuffer<T>` cho dữ liệu cố định, `MemoryPool` nếu cần giảm GC.

## Testing

- Unit test cho từng calculator và bước pipeline (Tick cleaning, Bar aggregation, Indicator orchestration).
- Integration test giả lập luồng tick → snapshot bằng dữ liệu mẫu.
- Performance benchmark (BenchmarkDotNet) cho indicator trọng yếu.

## Debugging

- Bật `DetailedErrors` qua cấu hình engine.
- Telemetry logger phải ghi nhận `pipeline_step`, `duration_ms`, `exception` (nếu có).
- Với lỗi thời gian thực, bật chế độ `ReplayTick` để phát lại từ cache.

## Code review checklist

1. Đã có test mới hoặc cập nhật test liên quan?
2. Tài liệu API/tài liệu người dùng có bị ảnh hưởng không?
3. Telemetry/log có đầy đủ context phục vụ điều tra sự cố?
4. Thay đổi có tương thích ngược với DataFetcher/chiến lược hiện tại?
