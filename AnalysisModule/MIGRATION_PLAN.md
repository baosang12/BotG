# Migration Plan

## Mục tiêu

- Tách lớp tiền xử lý khỏi chiến lược hiện tại, chuyển sang Analysis Preprocessor Engine.
- Duy trì hoạt động ổn định của BotG trong suốt quá trình chuyển đổi.

## Giai đoạn

1. **Chuẩn bị**
   - Hoàn thiện tài liệu và interface mới.
   - Viết adapter lấy dữ liệu từ DataFetcher vào engine mới.
2. **Triển khai song song**
   - Chạy Preprocessor mới ở chế độ shadow, so sánh output với pipeline cũ.
   - Thu thập telemetry (latency, khác biệt indicator).
3. **Cắt chuyển từng chiến lược**
   - Ưu tiên TrendPullback RSI, sau đó các chiến lược multi-timeframe khác.
   - Mỗi chiến lược cần test regression + so sánh hiệu suất.
4. **Loại bỏ phần cũ**
   - Khi toàn bộ consumer dùng snapshot mới, loại bỏ TickPreProcessor/BarAggregator cũ hoặc biến thành shim.

## Rủi ro & giảm thiểu

- **Sai lệch dữ liệu**: dùng shadow mode + diff tool, set ngưỡng alert.
- **Độ trễ tăng**: benchmark định kỳ, tối ưu cache/batching nếu vượt 30 ms.
- **Không tương thích chiến lược cũ**: giữ backward-compat bridge cho đến khi kiểm chứng từng chiến lược.

## Tiêu chí hoàn thành

- 100% chiến lược đọc dữ liệu từ Preprocessor snapshot.
- Tài liệu integration được cập nhật và thông qua review.
- Telemetry cho thấy latency nằm trong mục tiêu.
- Không còn phụ thuộc trực tiếp vào DataFetcher trong code chiến lược.
