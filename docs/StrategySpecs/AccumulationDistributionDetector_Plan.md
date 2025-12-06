# Kế hoạch triển khai Accumulation Distribution Detector (Phase 2.1)

## 1. Bối cảnh & mục tiêu

- Hoàn thiện detector Accumulation/Distribution theo spec tuần 1 (09/12-13/12) để bổ sung góc nhìn Smart Money cho PatternLayer.
- Đảm bảo detector mới hòa nhập cấu trúc hiện tại: `PatternLayer`, hệ config `TrendAnalyzerConfig`, telemetry CSV (`cTraderTelemetryLogger`, `SimplecTraderTelemetryLogger`), và bộ test `AnalysisModule.Preprocessor.Tests`.
- Duy trì backwards compatibility (PatternLayer baseline, CSV cũ, config params optional) trong suốt quá trình.

## 2. Phạm vi file/code bị tác động

1. **Core detector mới**: `AnalysisModule/Preprocessor/TrendAnalysis/Layers/Detectors/AccumulationDistributionDetector.cs` (tạo mới).
2. **Model phụ trợ** (nếu cần tách riêng metrics/result): `AnalysisModule/Preprocessor/TrendAnalysis/Layers/Detectors/` hoặc `.../Models/PatternDetection/` theo spec.
3. **PatternLayer wiring**: `AnalysisModule/Preprocessor/TrendAnalysis/Layers/PatternLayer.cs` (khởi tạo detector, áp config, mở rộng telemetry snapshot class).
4. **Config C#**: `AnalysisModule/Preprocessor/Config/TrendAnalyzerConfig.cs` (bổ sung `AccumulationDistributionConfig` + params + default weight).
5. **Config JSON template**: `BotG/Config/TrendAnalyzerConfig.json` (mục `PatternLayer`).
6. **Telemetry plumbing**:
   - `AnalysisModule/Preprocessor/TrendAnalysis/TrendAnalysisTelemetry.cs`
   - `AnalysisModule/Telemetry/IPatternLayerTelemetryLogger.cs`
   - `AnalysisModule/Telemetry/cTraderTelemetryLogger.cs`
   - `AnalysisModule/Telemetry/SimplecTraderTelemetryLogger.cs`
7. **Unit & integration tests**:
   - `AnalysisModule/Preprocessor.Tests/TrendAnalysis/Layers/Detectors/AccumulationDistributionDetectorTests.cs` (mới).
   - Cập nhật `PatternLayerTests.cs`, `PatternLayerIntegrationTests.cs` nếu giả định weight/baseline thay đổi.
8. **Tài liệu/plan này** + sau đó update `docs/PATTERNLAYER_DEPLOYMENT_GUIDE.md` hoặc báo cáo Phase2 nếu cần (ngoài scope trước khi coding).

## 3. Lộ trình 5 ngày (ăn khớp kế hoạch Ops)

- **Day 1 (Core + unit tests skeleton)**
  - Hiện thực thuật toán: slope (LR 20 bars), ATR(14), SMA volume (5/20), swing detection (20-bar lookback), range + SR proximity logic, phase classification, scoring & confidence clamp.
  - Trả về `PatternDetectionResult` với diagnostics theo spec.
  - Viết bộ test synthetic data cho 6 scenario chính (accumulation/distribution/neutral/insufficient/config override/edge metrics). Chuẩn bị helper tạo bars.
- **Day 2 (Integration + config)**
  - Thêm detector vào `PatternLayer` ctor + `ApplyPatternConfig`.
  - Bổ sung `PatternLayerConfig` + JSON template + default weight/flags.
  - Đảm bảo `TrendAnalysisService` (nếu có) nhận config mới không lỗi deserialize.
- **Day 3 (Telemetry + perf)**
  - Mở rộng `PatternLayerTelemetrySnapshot` với trường Score/Flags/Confidence/Phase.
  - Append cột CSV + ghi log flag/phase qua `IPatternLayerTelemetryLogger` implementations.
  - Bổ sung `TelemetryVersion = 2`, logic fallback nếu logger chưa dùng cột mới.
  - Benchmark nhanh (<5ms) bằng `PatternLayerPerformanceTests` hoặc micro stopwatch.
- **Day 4 (Validation)**
  - Chạy toàn bộ unit tests + integration (PatternLayer tests, telemetry integration, performance smoke).
  - Sinh log sample từ `TestPatternLayerData.CreateTelemetrySnapshot` (update helper nếu cần) để verify CSV format.
  - Chuẩn bị demo script + sample diagnostics.
- **Day 5 (Review + demo)**
  - Tổng hợp kết quả, cập nhật tài liệu, demo cho Ops.

## 4. Hạng mục triển khai chi tiết

### 4.1 Detector logic

- Input: `SnapshotDataAccessor` trên `TimeFrame.H1`, fallback >=30 bars; degrade confidence nếu thiếu 60 bars.
- Tiền xử lý:
  - `GetBars(tf, lookback)` tối ưu hoá reuse (sử dụng local span, tránh LINQ nặng).
  - Linear regression slope (Close, 20 bars) => helper `ComputeSlope(IEnumerable<double>)` (double result per bar).
  - ATR(14) => re-use mini util (copy từ LiquidityAnalyzer `EstimateAtr` nhưng tuỳ biến 14).
  - SMA volume ratio = SMA5/SMA20.
  - Swing detection (20-bar lookback) -> port hoá logic `LiquidityAnalyzer.DetectSwingPoints` nhưng parameter hoá.
  - Support/resistance lists = top/bottom swing closes (có thể giữ 3 gần nhất).
- Signal evaluation & scoring per spec (±8, ±20 cap) + confidence increments.
- Diagnostics dictionary (PriceSlope, VolumeRatio, RangeATR, ATR, NearSupport/Resistance, signal counts, BarsAnalyzed, Phase) để Telemetry + PatternLayerDebugger dùng.

### 4.2 Config & wiring

- `PatternLayerConfig`:
  - Thêm `AccumulationDistribution` (`AccumulationDistributionDetectorConfig` thừa kế `PatternDetectorConfig`).
  - `AccumulationDistributionParameters` chứa toàn bộ threshold, timeframe string.
  - `EnsureDefaults()` cập nhật weight + instantiate config mới khi null.
- `PatternLayer`:
  - Khởi tạo detector list gồm `Liquidity`, `BreakoutQuality`, `AccumulationDistribution` (giữ thứ tự cũ + detector mới cuối cùng).
  - `ApplyPatternConfig` gọi `UpdateDetectorConfig<AccumulationDistributionDetector>` + forward parameter object.
  - Cân nhắc expose helper `ApplyDetectorParameters<T>` nếu pattern lặp lại.

### 4.3 Telemetry

- `PatternLayerTelemetrySnapshot`: thêm property score/flags/confidence/phase + `TelemetryVersion = 2`.
- `PatternLayer.CalculateCompositeScore`: khi set snapshot, chèn data từ Acc/Dist (detectorScores map) và phase string.
- `TrendAnalysisTelemetry.LogPatternLayerResults`:
  - Truyền phase string + detector-specific metrics vào `_patternLogger.LogPatternAnalysis` (mở rộng interface signature + default values).
- `IPatternLayerTelemetryLogger` + implementations:
  - Append tham số `double accumulationScore`, `double accumulationConfidence`, `string accumulationFlags`, `string phase`? Hoặc generic `extra` columns.
  - Update CSV header/formatter (cả async & simple logger). Đảm bảo telem readers chấp nhận cột mới (append only).
  - Bảo toàn optional params (callers cũ => compile error => update tất cả call sites).

### 4.4 Testing chiến lược

- **Unit** (`AccumulationDistributionDetectorTests`): 10 test cases theo spec + helper synthetic bars re-use pattern tests.
- **PatternLayerTests**: thêm test verifying config wiring + weights (score contributions). Update snapshots to include new detector weight khi verifying sum to <=1.
- **Telemetry integration**: cập nhật `TestPatternLayerData.CreateTelemetrySnapshot` + `PatternLayerTelemetryIntegrationTests` to assert cột mới.
- **Performance**: update/perf test file (nếu `PatternLayerPerformanceTests` fail do weight). Target <5ms (validate via stopwatch).

## 5. Kiểm chứng dữ liệu & dependency

- `SnapshotDataAccessor` cung cấp H1 bars từ lịch sử; cần confirm sample data >=80 bars trong tests (dùng `TestDataFixtures.CreateRangeBars`).
- Trend config JSON sample (`BotG/Config/TrendAnalyzerConfig.json`) phải include params để Ops copy sang runtime.
- Yêu cầu update `TestPatternLayerData` cho telemetry snapshot version 2.

## 6. Rủi ro & giảm thiểu

| Rủi ro | Tác động | Giảm thiểu |
| --- | --- | --- |
| Thiếu đủ 60 bars H1 | Confidence sai, noise | fallback sang Neutral, ghi cảnh báo, degrade confidence theo bar count |
| Regression PatternLayer weights | Baseline thay đổi -> Trend score lệch | Giữ tổng weight ≤0.8, test `PatternLayerTests.CalculateScore_WithWeightedDetectors` sau update |
| Telemetry CSV thay đổi format | Ops parsing break | Append-only columns, version bump, thông báo qua docs |
| Thuật toán LR + ATR tốn CPU | Latency >5ms | Cache arrays, tránh allocations, reuse `Span<double>`/`List<double>`, micro benchmark |

## 7. Kiểm thử & xác nhận cuối cùng

1. `dotnet test AnalysisModule.Preprocessor.Tests -c Release` (bao gồm tests mới).
2. `PatternLayerTelemetryIntegrationTests` để xem CSV header mới.
3. `PatternLayerPerformanceTests` đảm bảo thời gian.
4. Manual dry-run: simulate snapshot -> verify diagnostics + CSV appended (via sample logger).
5. Ghi log sample, gửi Ops cho review trước Day 4 demo.

## 8. Deliverables cuối tuần 1

- Code detector + unit tests pass.
- Config & JSON template update.
- Telemetry CSV version 2 + sample file.
- Updated docs (spec này + deployment notes).
- Demo script + summary chốt Phase 2.1.
