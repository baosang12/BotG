# Preprocessor API Reference

## IPreprocessorPipeline

```csharp
public interface IPreprocessorPipeline
{
    event EventHandler<PreprocessorSnapshot> SnapshotGenerated;
    void Start(IPreprocessorSource source, PreprocessorOptions options);
    void Stop();
}
```

- `SnapshotGenerated`: phát snapshot đã làm giàu chỉ số.
- `Start`: nhận nguồn dữ liệu (adapter DataFetcher) + cấu hình (timeframe, indicator set).
- `Stop`: giải phóng tài nguyên, dừng thread/cancellation token.

## AnalysisPreprocessorEngine

```csharp
public sealed class AnalysisPreprocessorEngine : IPreprocessorPipeline
{
    PreprocessorState State { get; }
    ValueTask PublishTickAsync(Tick tick, CancellationToken ct = default);
}
```

- `State`: theo dõi trạng thái pipeline (Running/Stopped/Degraded).
- `PublishTickAsync`: API optional cho tình huống nhận tick thủ công (không qua source).

## IndicatorOrchestrator

```csharp
public interface IIndicatorOrchestrator
{
    void RegisterCalculator(IIndicatorCalculator calculator);
    ValueTask<IReadOnlyDictionary<string, IndicatorResult>> CalculateAsync(PreprocessorContext context, CancellationToken ct);
}
```

- `RegisterCalculator`: thêm calculator mới runtime.
- `CalculateAsync`: chạy indicator theo context hiện tại, cho phép chạy song song nội bộ.

## PreprocessorContext

Truyền vào calculators:

```csharp
public sealed class PreprocessorContext
{
    public IReadOnlyList<Tick> RecentTicks { get; }
    public IReadOnlyList<Bar> Bars(TimeFrame tf) { get; }
    public DateTime TimestampUtc { get; }
    public IReadOnlyDictionary<string, object> Metadata { get; }
}
```

## Snapshot model

```csharp
public sealed record PreprocessorSnapshot(
    DateTime TimestampUtc,
    IReadOnlyDictionary<string, double> Indicators,
    IReadOnlyDictionary<TimeFrame, Bar> LatestBars,
    AccountInfo? Account,
    bool IsDegraded = false,
    string? DegradedReason = null);
```

> **Ghi chú**: từ Task 4B trở đi, mọi snapshot được gắn multi-timeframe history thông qua `SnapshotHistoryRegistry.Attach(snapshot, history)`. Các layer có thể lấy lại history qua accessor mới.

## Event flow

- `IPreprocessorSource.TickReceived` → TickPreProcessor.
- Tick sạch → BarAggregator.
- Bar đóng → IndicatorOrchestrator.
- Indicator results + metadata → Snapshot bus.

## PatternLayer API

```csharp
public sealed class PatternLayer : BaseLayerCalculator
{
    public IReadOnlyList<IPatternDetector> Detectors { get; }
    public void UpdateDetectorConfig(string name, bool enabled, double? weight = null);
}

public interface IPatternDetector
{
    string Name { get; }
    double Weight { get; set; }
    bool IsEnabled { get; set; }
    PatternDetectionResult Detect(SnapshotDataAccessor accessor);
}

public sealed class PatternDetectionResult
{
    public double Score { get; set; } = 50;
    public double Confidence { get; set; } = 0.5;
    public Dictionary<string, object> Diagnostics { get; };
    public List<string> Flags { get; };
    public static PatternDetectionResult Neutral();
}
```

- `LiquidityAnalyzer` và `BreakoutQualityEvaluator` là hai detector mặc định; có thể mở rộng bằng cách implement `IPatternDetector` mới.
- `PatternLayerConfig` (trong `TrendAnalyzerConfig`) điều khiển trọng số và tham số từng detector.
- Các flag từ `PatternDetectionResult` được MarketTrendAnalyzer dùng để nhân/giảm `TrendSignal.Confidence`.

## SnapshotHistoryRegistry

```csharp
public static class SnapshotHistoryRegistry
{
    public static void Attach(PreprocessorSnapshot snapshot, IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> history);
    public static IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>>? TryGet(PreprocessorSnapshot snapshot);
}
```

- Được gọi trong `AnalysisPreprocessorEngine` ngay sau khi snapshot được dựng.
- `MarketTrendAnalyzer` tự động lấy history (nếu có) và truyền cho `SnapshotDataAccessor`, giúp PatternLayer đọc đủ dữ liệu đa timeframe mà không cần thay đổi model snapshot gốc.
