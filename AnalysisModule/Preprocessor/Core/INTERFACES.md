# Core Interfaces

Tài liệu này liệt kê các interface/bản hợp đồng trung tâm của Preprocessor, dùng làm chuẩn khi hiện thực và review code.

## Danh sách

- `IPreprocessorPipeline`: quản lý vòng đời pipeline, nhận tick từ nguồn và phát snapshot.
- `IPreprocessorSource`: adapter cho các nguồn tick (DataFetcher, feed khác).
- `PreprocessorContext`: dữ liệu kết hợp (ticks/bars/metadata) truyền vào indicators.
- `PreprocessorSnapshot`: payload phát ra cho Analysis hub.
- `PreprocessorOptions`: tham số cấu hình pipeline (timeframe, buffer, indicator set).
- `IIndicatorCalculator`: định nghĩa mô-đun tính toán indicator đơn lẻ.
- `IIndicatorOrchestrator`: điều phối và gom kết quả từ nhiều calculators.

Theo chính sách ops, bất kỳ thay đổi nào ở đây phải cập nhật `API_REFERENCE.md` và các tài liệu liên quan trước khi merge.
