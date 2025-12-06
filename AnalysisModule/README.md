# Analysis Preprocessor Engine

## Mục tiêu dự án

- Cung cấp lớp tiền xử lý thống nhất cho mọi chiến lược và mô-đun phân tích của BotG.
- Chuẩn hóa dữ liệu tick/bar, tính toán indicator nền tảng và phát snapshot cho Analysis Engine.
- Cho phép mở rộng theo hướng modular: chỉ cần đăng ký indicator/aggregator mới mà không chạm pipeline cốt lõi.

## Phạm vi

- Tiếp nhận dữ liệu realtime/historical từ DataFetcher hoặc nguồn bên ngoài.
- Làm sạch, gom bar đa timeframe, tính indicator cơ bản/advanced và đẩy kết quả vào Analysis.
- Cung cấp bridge để giữ tương thích với hạ tầng cũ cho đến khi migration hoàn tất.

## Thành phần chính

1. **Preprocessor Core**: orchestration pipeline, event bus, lifecycle management.
2. **Indicators Module**: bộ sưu tập calculator + orchestrator để tính SMA/EMA/RSI/ATR…
3. **DataModels & Caching**: tái sử dụng cấu trúc từ DataFetcher, chuẩn hóa namespace và document.
4. **Integration Layer**: adapters/migration bridge kết nối với BotGRobot và chiến lược cũ.

## Kiến trúc tổng quan

```text
Market Data / Historical Feeds
        │
        ▼
Preprocessor Engine ──► Tick Cleaning ──► Bar Aggregation ──► Indicator Orchestrator
        │                                                             │
        └──────────────► Snapshot Bus ◄───────────────────────────────┘
                                   │
                                   ▼
                          Analysis Engine / Strategies
```

## Quick Start (dự kiến)

1. `cd AnalysisModule && dotnet new classlib -n Analysis.Preprocessor` (khi code khởi tạo).
2. Thêm reference tới `BotG` hoặc project phụ trợ chứa DataFetcher models.
3. Đăng ký preprocessor trong host:

   ```csharp
   var engine = new AnalysisPreprocessorEngine(options);
   engine.Start(new DataFetcherAdapter());
   ```

4. Subscribe snapshot:

   ```csharp
   engine.SnapshotGenerated += HandleSnapshot;
   ```

## Workflow phát triển

1. Viết/cập nhật tài liệu (README, ARCHITECTURE, DEVELOPMENT_GUIDE, MIGRATION_PLAN).
2. Thêm interface/core code skeleton.
3. Viết unit test tối thiểu cho mỗi component mới.
4. Chạy toàn bộ `dotnet test` trước khi mở PR.

## Tiếp theo

- Hoàn thiện `ARCHITECTURE.md` với sơ đồ chi tiết.
- Viết `DEVELOPMENT_GUIDE.md` và `MIGRATION_PLAN.md`.
- Tạo cấu trúc thư mục Preprocessor như mô tả trong kế hoạch.
