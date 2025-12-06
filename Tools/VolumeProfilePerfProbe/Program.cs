using System.Diagnostics;
using System.IO;
using System.Linq;
using AnalysisModule.Preprocessor.Config;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;
using AnalysisModule.Preprocessor.TrendAnalysis.Layers.Detectors;
using AnalysisModule.Telemetry;

var bars = CreateSequentialBars(
	count: 120,
	startPrice: 100,
	step: 0.25,
	volumeSelector: i => 2_000 + (i % 10) * 250);
var accessor = BuildAccessor(bars);
var detector = new VolumeProfileDetector(new VolumeProfileParameters
{
	LookbackBars = 150,
	MinBars = 80,
	NumberOfBuckets = 30,
	ValueAreaPercentage = 0.70,
	HighVolumeThreshold = 1.35,
	LowVolumeThreshold = 0.55,
	PrimaryTimeFrame = "H1"
});

// JiT warm-up để tránh penalize lần chạy đầu
detector.Detect(accessor);

const int iterations = 500;
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
var sw = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
{
	detector.Detect(accessor);
}

sw.Stop();
var afterAlloc = GC.GetAllocatedBytesForCurrentThread();
var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
var avgAllocBytes = Math.Max(0, afterAlloc - beforeAlloc) / (double)iterations;
var avgAllocKb = avgAllocBytes / 1024d;

Console.WriteLine($"Iterations: {iterations}");
Console.WriteLine($"Average execution time: {avgMs:F4} ms");
Console.WriteLine($"Average allocations: {avgAllocBytes:F2} bytes ({avgAllocKb:F4} KB)");

var telemetryDir = Path.Combine(Path.GetTempPath(), $"PatternLayerTelemetryProbe_{Guid.NewGuid():N}");
Directory.CreateDirectory(telemetryDir);
using (var logger = new SimplecTraderTelemetryLogger(telemetryDir))
{
	logger.LogPatternAnalysis(
		symbol: "EURUSD",
		timeframe: "M15",
		patternScore: 72.3,
		liquidityScore: 65.4,
		breakoutScore: 63.5,
		liquidityGrabFlag: true,
		cleanBreakoutFlag: false,
		failedBreakoutFlag: false,
		processingTimeMs: avgMs,
		marketCondition: "trend",
		rsi: 54,
		volumeRatio: 1.18,
		candleSize: 0.42,
		accumulationScore: 58,
		accumulationConfidence: 0.52,
		accumulationFlags: "Distribution",
		phaseDetected: "Distribution",
		marketStructureScore: 67.8,
		marketStructureState: "Uptrend",
		marketStructureTrendDirection: 1,
		marketStructureBreakDetected: true,
		marketStructureSwingPoints: 6,
		lastSwingHigh: 1.1085,
		lastSwingLow: 1.1022,
		volumeProfileScore: 69.1,
		volumeProfilePoc: 1.1062,
		volumeProfileVaHigh: 1.1098,
		volumeProfileVaLow: 1.1031,
		volumeProfileFlags: "ValueAreaBreakUp|NearPOC",
		hvnCount: 3,
		lvnCount: 1,
		volumeConcentration: 0.74,
		telemetryVersion: 4);
	logger.Flush();
}

var csvPath = Directory.GetFiles(telemetryDir, "*.csv").Single();
var csvLines = File.ReadAllLines(csvPath);
Console.WriteLine($"Telemetry CSV path: {csvPath}");
Console.WriteLine($"CSV header: {csvLines.FirstOrDefault()}");
Console.WriteLine($"CSV sample row: {csvLines.Skip(1).FirstOrDefault()}");

static SnapshotDataAccessor BuildAccessor(IReadOnlyList<Bar> bars, TimeFrame? timeFrameOverride = null)
{
	if (bars == null || bars.Count == 0)
	{
		throw new ArgumentException("Bars collection must contain at least one element", nameof(bars));
	}

	var timeframe = timeFrameOverride ?? bars[0].TimeFrame;
	var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
	{
		[timeframe] = bars
	};

	var latest = new Dictionary<TimeFrame, Bar>
	{
		[timeframe] = bars[^1]
	};

	var snapshot = new PreprocessorSnapshot(
		DateTime.UtcNow,
		new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
		latest,
		null,
		false,
		null);

	return new SnapshotDataAccessor(snapshot, history);
}

static List<Bar> CreateSequentialBars(
	int count,
	double startPrice,
	double step,
	Func<int, long>? volumeSelector = null,
	Func<int, double?>? closeSelector = null,
	TimeFrame timeFrame = TimeFrame.H1)
{
	if (count <= 0)
	{
		throw new ArgumentOutOfRangeException(nameof(count));
	}

	volumeSelector ??= _ => 1_500L;
	var bars = new List<Bar>(count);
	var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
	for (var i = 0; i < count; i++)
	{
		var nominalPrice = startPrice + step * i;
		var lowGuess = nominalPrice - 0.25;
		var highGuess = nominalPrice + 0.25;
		var close = closeSelector?.Invoke(i) ?? nominalPrice;
		var actualLow = Math.Min(Math.Min(lowGuess, close), highGuess);
		var actualHigh = Math.Max(Math.Max(highGuess, close), actualLow);
		var open = Math.Clamp(nominalPrice, actualLow, actualHigh);
		var volume = Math.Max(1L, volumeSelector(i));
		bars.Add(new Bar(
			baseTime.AddMinutes(i * 60),
			open,
			actualHigh,
			actualLow,
			close,
			volume,
			timeFrame));
	}

	return bars;
}
