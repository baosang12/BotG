using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AnalysisModule.Telemetry;

namespace AnalysisModule.Preprocessor.TrendAnalysis.Layers
{
    /// <summary>
    /// Xuất debug output chi tiết cho PatternLayer và các detector trong môi trường cTrader (.NET 6).
    /// </summary>
    public static class PatternLayerDebugger
    {
        private const string IndicatorGood = "✅";
        private const string IndicatorWarn = "⚠️";
        private const string IndicatorBad = "❌";
        private const string IndicatorInfo = "ℹ️";
        private const string IndicatorFlag = "🚩";

        private static readonly object _lock = new();

        private static bool _isEnabled;
        private static int _sampleRate = 1;
        private static double _minScoreThreshold;
        private static bool _includeDetectorDetails = true;
        private static bool _includeRawMetrics;
        private static long _analysisCounter;
        private static bool _logCurrentAnalysis;

        /// <summary>
        /// Khởi tạo debugger với cấu hình mới.
        /// </summary>
        public static void Initialize(
            bool enable,
            int sampleRate = 1,
            double minScoreThreshold = 0.0,
            bool includeDetectorDetails = true,
            bool includeRawMetrics = false)
        {
            lock (_lock)
            {
                _isEnabled = enable;
                _sampleRate = Math.Max(1, sampleRate);
                _minScoreThreshold = ClampScore(minScoreThreshold);
                _includeDetectorDetails = includeDetectorDetails;
                _includeRawMetrics = includeRawMetrics;
                _analysisCounter = 0;
                _logCurrentAnalysis = false;

                if (_isEnabled)
                {
                    Print($"{IndicatorInfo} PatternLayer Debugger ENABLED");
                    Print(FormattableString.Invariant($"{IndicatorInfo} Sample rate: 1/{_sampleRate}, Min score: {_minScoreThreshold:F1}"));
                }
            }
        }

        /// <summary>
        /// Ghi log phần mở đầu của một lần phân tích.
        /// </summary>
        public static void LogAnalysisStart(string symbol, string timeframe, DateTime timestampUtc)
        {
            if (!BeginAnalysisScope())
            {
                return;
            }

            Print(new string('=', 60));
            Print($"🔍 PATTERN LAYER ANALYSIS - {symbol} {timeframe}");
            Print($"📅 {timestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} UTC");
            Print(new string('=', 60));
        }

        /// <summary>
        /// Ghi log chi tiết từng detector.
        /// </summary>
        public static void LogDetectorAnalysis(
            string detectorName,
            Dictionary<string, double>? metrics,
            Dictionary<string, double>? scores,
            IList<string>? flags,
            double finalScore)
        {
            if (!ShouldLogCurrentAnalysis(finalScore) || !_includeDetectorDetails)
            {
                return;
            }

            var upperName = detectorName?.ToUpperInvariant() ?? "UNKNOWN";
            Print(string.Empty);
            Print($"📊 {upperName}:");
            Print(FormattableString.Invariant($"  {IndicatorInfo} Final Score: {finalScore:F2}"));

            if (scores != null && scores.Count > 0)
            {
                Print($"  {IndicatorInfo} Component Scores:");
                foreach (var score in scores.OrderByDescending(s => s.Value))
                {
                    var indicator = score.Value >= 0.7 ? IndicatorGood : score.Value >= 0.4 ? IndicatorWarn : IndicatorBad;
                    Print(FormattableString.Invariant($"    {indicator} {score.Key}: {score.Value:F2}"));
                }
            }

            if (_includeRawMetrics && metrics != null && metrics.Count > 0)
            {
                var formatted = DebugOutputFormatter.FormatTable(metrics, $"  {IndicatorInfo} Raw Metrics:");
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    Print(formatted.TrimEnd());
                }
            }

            if (flags != null && flags.Count > 0)
            {
                Print($"  {IndicatorFlag} Flags: {string.Join(", ", flags)}");
            }
        }

        /// <summary>
        /// Ghi log chi tiết Liquidity.
        /// </summary>
        public static void LogLiquidityAnalysis(
            double wickRejectionRatio,
            double falseBreakoutConfidence,
            bool hasWickRejection,
            bool hasFalseBreakout,
            double cleanPriceActionScore)
        {
            if (!ShouldLogCurrentAnalysis() || !_includeDetectorDetails)
            {
                return;
            }

            Print($"  {IndicatorInfo} Liquidity Details:");
            Print(FormattableString.Invariant($"    • Wick Rejection: {wickRejectionRatio:F2} {(hasWickRejection ? IndicatorFlag : string.Empty)}"));
            Print(FormattableString.Invariant($"    • False Breakout Confidence: {falseBreakoutConfidence:F2} {(hasFalseBreakout ? IndicatorFlag : string.Empty)}"));
            Print(FormattableString.Invariant($"    • Clean Price Action: {cleanPriceActionScore:F2}"));
        }

        /// <summary>
        /// Ghi log chi tiết Breakout.
        /// </summary>
        public static void LogBreakoutAnalysis(
            double breakoutStrength,
            double retestQuality,
            double followThrough,
            bool isStrongBreakout,
            bool hasCleanRetest,
            bool hasGoodFollowThrough)
        {
            if (!ShouldLogCurrentAnalysis() || !_includeDetectorDetails)
            {
                return;
            }

            Print($"  {IndicatorInfo} Breakout Details:");
            Print(FormattableString.Invariant($"    • Strength: {breakoutStrength:F2} {(isStrongBreakout ? IndicatorGood : IndicatorWarn)}"));
            Print(FormattableString.Invariant($"    • Retest Quality: {retestQuality:F2} {(hasCleanRetest ? IndicatorGood : IndicatorWarn)}"));
            Print(FormattableString.Invariant($"    • Follow Through: {followThrough:F2} {(hasGoodFollowThrough ? IndicatorGood : IndicatorWarn)}"));
        }

        /// <summary>
        /// Ghi log tổng kết PatternLayer.
        /// </summary>
        public static void LogPatternLayerResult(
            double patternScore,
            double liquidityScore,
            double breakoutScore,
            IList<string>? patternFlags,
            long processingTicks,
            double overallConfidence)
        {
            if (!ShouldLogCurrentAnalysis(patternScore))
            {
                return;
            }

            var processingMs = processingTicks / (double)TimeSpan.TicksPerMillisecond;

            Print(string.Empty);
            Print("🎯 PATTERN LAYER SUMMARY:");
            Print(FormattableString.Invariant($"  📈 Pattern Score: {patternScore:F2}/100"));
            Print(FormattableString.Invariant($"  💧 Liquidity Score: {liquidityScore:F2}/100"));
            Print(FormattableString.Invariant($"  🚀 Breakout Score: {breakoutScore:F2}/100"));
            Print(FormattableString.Invariant($"  🎯 Overall Confidence: {overallConfidence:F2}"));

            if (patternFlags != null && patternFlags.Count > 0)
            {
                Print(string.Empty);
                Print($"  {IndicatorFlag} PATTERN FLAGS:");
                foreach (var flag in patternFlags)
                {
                    Print($"    {GetFlagSymbol(flag)} {flag}");
                }
            }
            else
            {
                Print(string.Empty);
                Print($"  {IndicatorInfo} No significant pattern flags detected");
            }

            Print(string.Empty);
            Print(FormattableString.Invariant($"  ⚡ Performance: {processingMs:F3} ms"));
            Print(new string('=', 60));
            Print(string.Empty);
        }

        /// <summary>
        /// Ghi log cảnh báo.
        /// </summary>
        public static void LogWarning(string detector, string message)
        {
            if (!_isEnabled)
            {
                return;
            }

            Print($"{IndicatorWarn} [{detector}] {message}");
        }

        /// <summary>
        /// Ghi log lỗi.
        /// </summary>
        public static void LogError(string detector, string message, Exception? exception = null)
        {
            if (!_isEnabled)
            {
                return;
            }

            Print($"{IndicatorBad} [{detector}] ERROR: {message}");
            if (exception != null)
            {
                Print($"      Exception: {exception.GetType().Name} - {exception.Message}");
            }
        }

        /// <summary>
        /// Ghi log hiệu năng khi vượt quá 1ms.
        /// </summary>
        public static void LogPerformance(string operation, long elapsedTicks)
        {
            if (!_isEnabled)
            {
                return;
            }

            var elapsedMs = elapsedTicks / (double)TimeSpan.TicksPerMillisecond;
            if (elapsedMs > 1.0)
            {
                Print(FormattableString.Invariant($"{IndicatorInfo} Performance: {operation} took {elapsedMs:F3} ms"));
            }
        }

        /// <summary>
        /// Trả về trạng thái hiện tại.
        /// </summary>
        public static (bool Enabled, long Counter, int SampleRate) GetStatus()
        {
            lock (_lock)
            {
                return (_isEnabled, _analysisCounter, _sampleRate);
            }
        }

        private static bool BeginAnalysisScope()
        {
            if (!_isEnabled)
            {
                return false;
            }

            lock (_lock)
            {
                _analysisCounter++;
                _logCurrentAnalysis = _analysisCounter % _sampleRate == 0;
                return _logCurrentAnalysis;
            }
        }

        private static bool ShouldLogCurrentAnalysis(double? candidateScore = null)
        {
            if (!_isEnabled)
            {
                return false;
            }

            lock (_lock)
            {
                if (!_logCurrentAnalysis)
                {
                    return false;
                }

                if (candidateScore.HasValue && candidateScore.Value < _minScoreThreshold)
                {
                    return false;
                }

                return true;
            }
        }

        private static double ClampScore(double value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static string GetFlagSymbol(string? flag)
        {
            var normalized = flag?.ToUpperInvariant();
            return normalized switch
            {
                "LIQUIDITYGRAB" => "📌",
                "CLEANBREAKOUT" => "🚀",
                "FAILEDBREAKOUT" => "💥",
                "STRONGBREAKOUT" => "🔼",
                "WEAKBREAKOUT" => "🔽",
                "WICKREJECTION" => "↕️",
                "FALSEBREAKOUT" => "🔄",
                _ => "🏷️"
            };
        }

        private static void Print(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
        }
    }
}
