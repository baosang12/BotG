# Preprocessor Module Overview

## Vai trò

- Orchestrate luồng dữ liệu từ DataFetcher/nguồn ngoài tới Analysis Engine.
- Chuẩn hóa tick, gom bar đa timeframe, tính indicator cơ bản/advanced.
- Cung cấp snapshot giàu thông tin cho chiến lược, risk monitor và telemetry.

## Thành phần chính

1. **Core**: engine, pipeline interface, context, option bindings.
2. **Indicators**: orchestrator + calculators (common và advanced).
3. **DataModels & Caching**: tái sử dụng từ DataFetcher, chuẩn hóa namespace.
4. **Aggregators**: TickPreProcessor, BarAggregator.
5. **Integration**: adapter kết nối DataFetcher và bridge tương thích ngược.

## Tính năng ưu tiên

- Xử lý tick < 10 ms, hỗ trợ 1k tps.
- Snapshot bus đa subscriber (Analysis, risk, logging).
- Cấu hình indicator linh hoạt (bật/tắt, tham số, lịch chạy).
- Telemetry tích hợp (latency, error rate, throughput).

## Tài liệu liên quan

- `API_REFERENCE.md`: mô tả interface/pub-sub API.
- `DATA_FLOW.md`: luồng dữ liệu chi tiết và sequence diagram.
- `Indicators/` docs: hướng dẫn thêm indicator mới.

## Phase 3 - Implementation roadmap

1. **Project wiring (Day 1)**: hoàn tất project `AnalysisModule.Preprocessor`, add vào `BotG.sln`, đảm bảo build sạch và cấu hình reference tới `BotG`.
2. **Data model hợp nhất (Day 1-2)**: chuyển các model chung sang project mới hoặc shared library, cập nhật DataFetcher để dùng chung qua adapter tạm thời.
3. **Core engines (Day 2-4)**: implement `AnalysisPreprocessorEngine` + `IndicatorOrchestrator`, thêm unit test cơ bản và telemetry hooks.
4. **Common indicators (Day 4-5)**: port SMA/EMA/RSI/ATR calculators, viết hướng dẫn bổ sung trong `Indicators/Common/`.
5. **Integration (Week 2)**: kết nối vào `BotGRobot.OnTick`, dựng `Integration/` adapters, chạy smoke + gate2 test để xác nhận latency.
6. **Cleanup & docs**: cập nhật `MIGRATION_PLAN.md`, tạo checklist vận hành, xóa duplication còn lại.
