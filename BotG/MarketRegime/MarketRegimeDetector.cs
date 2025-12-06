using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using cAlgo.API;
using BotG.Runtime.Logging;

namespace BotG.MarketRegime
{
    /// <summary>
    /// Bộ phân tích chế độ thị trường: gom dữ liệu OHLC từ robot và trả về `RegimeType`
    /// để bộ định tuyến chiến lược lựa chọn chiến thuật phù hợp.
    /// </summary>
    public class MarketRegimeDetector
    {
        private readonly Robot _bot;
        private readonly RegimeConfiguration _config;
        private readonly RegimeIndicators _indicators;
        private readonly Dictionary<string, AtrCacheEntry> _averageAtrCache;
        private readonly object _cacheLock = new object();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2);
        private readonly RegimePerformanceMetrics _performanceMetrics;
        private int _cacheCleanupCounter;
        private readonly object _lock = new object();

        private DateTime _lastAnalysisTime;
        private RegimeType _lastRegime;
        private string _lastSymbol;
        private TimeFrame _lastTimeframe;
        private RegimeAnalysisResult? _lastResult;

        public MarketRegimeDetector(Robot bot, RegimeConfiguration config = null)
        {
            _bot = bot ?? throw new ArgumentNullException(nameof(bot));
            _config = config ?? new RegimeConfiguration();
            _indicators = new RegimeIndicators();
            _averageAtrCache = new Dictionary<string, AtrCacheEntry>(StringComparer.Ordinal);
            _performanceMetrics = new RegimePerformanceMetrics();
            _lastRegime = RegimeType.Uncertain;
            _lastSymbol = string.Empty;
            _lastTimeframe = _bot.TimeFrame;
        }

        public RegimePerformanceMetrics PerformanceMetrics => _performanceMetrics;

        /// <summary>
        /// Phân loại chế độ thị trường cho symbol/timeframe hiện tại hoặc đầu vào.
        /// Hoàn trả kết quả cache nếu vẫn nằm trong cùng khung thời gian nhằm tiết kiệm tính toán.
        /// </summary>
        public RegimeType AnalyzeCurrentRegime(string symbol = null, TimeFrame timeframe = null)
        {
            var result = AnalyzeCurrentRegimeDetailed(symbol, timeframe);
            return result.Regime;
        }

        /// <summary>
        /// Provides a detailed regime analysis including confidence score and indicator snapshot.
        /// </summary>
        public RegimeAnalysisResult AnalyzeCurrentRegimeDetailed(string symbol = null, TimeFrame timeframe = null)
        {
            lock (_lock)
            {
                try
                {
                    symbol ??= _bot.Symbol.Name;
                    timeframe ??= _bot.TimeFrame;

                    var currentTime = _bot.Server.Time;
                    if (symbol == _lastSymbol &&
                        timeframe == _lastTimeframe &&
                        currentTime.Subtract(_lastAnalysisTime).TotalSeconds < GetTimeframeSeconds(timeframe))
                    {
                        return _lastResult ?? RegimeAnalysisResult.CreateFallback(_lastRegime);
                    }

                    var highs = new List<double>();
                    var lows = new List<double>();
                    var closes = new List<double>();

                    if (!TryPopulateSeries(symbol, timeframe, highs, lows, closes))
                    {
                        var fallback = RegimeAnalysisResult.CreateFallback(RegimeType.Uncertain);
                        _lastResult = fallback;
                        _lastRegime = fallback.Regime;
                        _lastSymbol = symbol;
                        _lastTimeframe = timeframe;
                        _lastAnalysisTime = currentTime;
                        return fallback;
                    }

                    if (closes.Count < _config.BollingerPeriod)
                    {
                        LogWarning($"Insufficient closes ({closes.Count}) for Bollinger period {_config.BollingerPeriod}. Returning Uncertain.");
                        var fallback = RegimeAnalysisResult.CreateFallback(RegimeType.Uncertain);
                        _lastResult = fallback;
                        _lastRegime = fallback.Regime;
                        _lastSymbol = symbol;
                        _lastTimeframe = timeframe;
                        _lastAnalysisTime = currentTime;
                        return fallback;
                    }

                    double adx = _indicators.CalculateADX(highs, lows, closes, _config.AdxPeriod);
                    double currentAtr = _indicators.CalculateATR(highs, lows, closes, _config.AtrPeriod);
                    double bollingerWidth = _indicators.CalculateBollingerBandWidth(closes, _config.BollingerPeriod, _config.BollingerDeviations);

                    double averageAtr = currentAtr;
                    if (closes.Count >= _config.AtrPeriod * 2)
                    {
                        int samplesToAverage = Math.Min(10, closes.Count - _config.AtrPeriod);
                        averageAtr = GetCachedAverageAtr(
                            symbol,
                            timeframe,
                            highs,
                            lows,
                            closes,
                            _config.AtrPeriod,
                            samplesToAverage);
                    }

                    var regime = ClassifyRegime(adx, currentAtr, averageAtr, bollingerWidth);

                    double confidence = CalculateClassificationConfidence(regime, adx, currentAtr, averageAtr, bollingerWidth);
                    var indicators = new Dictionary<string, double>
                    {
                        ["adx"] = adx,
                        ["atr_current"] = currentAtr,
                        ["atr_average"] = averageAtr,
                        ["bollinger_width"] = bollingerWidth
                    };

                    var result = new RegimeAnalysisResult
                    {
                        Regime = regime,
                        Confidence = Math.Clamp(confidence, 0.0, 1.0),
                        Adx = adx,
                        Atr = currentAtr,
                        AverageAtr = averageAtr,
                        BollingerWidth = bollingerWidth,
                        AnalysisTimeUtc = currentTime.Kind == DateTimeKind.Utc ? currentTime : currentTime.ToUniversalTime(),
                        Symbol = symbol,
                        Timeframe = timeframe?.Name ?? string.Empty,
                        Notes = "Regime classification successful",
                        Indicators = indicators
                    };

                    _lastRegime = regime;
                    _lastSymbol = symbol;
                    _lastTimeframe = timeframe;
                    _lastAnalysisTime = currentTime;
                    _lastResult = result;

                    LogInfo($"Regime={regime}, Confidence={result.Confidence:P1}, ADX={adx:F2}, ATR={currentAtr:F5}, AvgATR={averageAtr:F5}, BBWidth={bollingerWidth:F2}%, CacheHitRatio={_performanceMetrics.CacheHitRatio:P1}, CacheSize={_performanceMetrics.CacheSize}");
                    return result;
                }
                catch (Exception ex)
                {
                    LogError($"Regime analysis failed: {ex.Message}", ex);
                    var fallback = RegimeAnalysisResult.CreateFallback(RegimeType.Uncertain);
                    _lastResult = fallback;
                    _lastRegime = fallback.Regime;
                    return fallback;
                }
            }
        }

        private double CalculateClassificationConfidence(RegimeType regime, double adx, double currentAtr, double averageAtr, double bollingerWidth)
        {
            double atrRatio = averageAtr <= double.Epsilon ? 0.0 : currentAtr / Math.Max(averageAtr, double.Epsilon);

            switch (regime)
            {
                case RegimeType.Trending:
                    return NormalizeAscending(adx, _config.AdxTrendThreshold, _config.AdxTrendThreshold * 1.8);
                case RegimeType.Ranging:
                    return 1.0 - NormalizeAscending(adx, _config.AdxRangeThreshold, _config.AdxTrendThreshold);
                case RegimeType.Volatile:
                    var atrVol = NormalizeAscending(atrRatio, _config.VolatilityThreshold, _config.VolatilityThreshold * 1.5);
                    var bbVol = !_config.UseBollingerInClassification
                        ? 0.0
                        : NormalizeAscending(bollingerWidth, _config.BollingerVolatilityThreshold, _config.BollingerVolatilityThreshold * 1.5);
                    return Math.Max(atrVol, bbVol);
                case RegimeType.Calm:
                    var atrCalm = 1.0 - NormalizeAscending(atrRatio, _config.CalmThreshold, _config.VolatilityThreshold);
                    var bbCalm = !_config.UseBollingerInClassification
                        ? atrCalm
                        : 1.0 - NormalizeAscending(bollingerWidth, _config.BollingerCalmThreshold, _config.BollingerVolatilityThreshold);
                    return Math.Clamp(Math.Max(atrCalm, bbCalm), 0.0, 1.0);
                default:
                    return 0.2;
            }
        }

        private static double NormalizeAscending(double value, double threshold, double target)
        {
            if (target <= threshold)
            {
                return value >= threshold ? 1.0 : 0.0;
            }

            return Math.Clamp((value - threshold) / (target - threshold), 0.0, 1.0);
        }

        private double GetCachedAverageAtr(
            string symbol,
            TimeFrame timeframe,
            IList<double> highs,
            IList<double> lows,
            IList<double> closes,
            int period,
            int samplesToAverage)
        {
            string cacheKey = BuildCacheKey(symbol, timeframe, period, samplesToAverage, highs.Count);
            string fingerprint = GenerateSeriesFingerprint(highs, lows, closes);
            DateTime utcNow = DateTime.UtcNow;

            lock (_cacheLock)
            {
                if (_averageAtrCache.TryGetValue(cacheKey, out var entry) &&
                    utcNow - entry.Timestamp <= _cacheDuration &&
                    string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _performanceMetrics.CacheHits++;
                    _performanceMetrics.CacheSize = _averageAtrCache.Count;
                    return entry.Value;
                }

                _performanceMetrics.CacheMisses++;
            }

            var stopwatch = Stopwatch.StartNew();
            double averageAtr = _indicators.CalculateRollingAverageAtr(highs, lows, closes, period, samplesToAverage);
            stopwatch.Stop();

            lock (_cacheLock)
            {
                _averageAtrCache[cacheKey] = new AtrCacheEntry
                {
                    Value = averageAtr,
                    Timestamp = utcNow,
                    Fingerprint = fingerprint
                };

                _performanceMetrics.LastAtrCalculationTime = stopwatch.Elapsed;
                _performanceMetrics.TotalCalculations++;
                _performanceMetrics.CacheSize = _averageAtrCache.Count;

                if (++_cacheCleanupCounter >= 128)
                {
                    CleanExpiredCache_NoLock(utcNow);
                    _cacheCleanupCounter = 0;
                }
            }

            return averageAtr;
        }

        private string BuildCacheKey(string symbol, TimeFrame timeframe, int period, int samplesToAverage, int barCount)
        {
            var builder = new StringBuilder();
            builder.Append(symbol ?? string.Empty);
            builder.Append('|');
            builder.Append(timeframe?.Name ?? string.Empty);
            builder.Append('|');
            builder.Append(period);
            builder.Append('|');
            builder.Append(samplesToAverage);
            builder.Append('|');
            builder.Append(barCount);
            return builder.ToString();
        }

        private string GenerateSeriesFingerprint(IList<double> highs, IList<double> lows, IList<double> closes)
        {
            var builder = new StringBuilder();
            int sampleSize = Math.Min(5, closes.Count);
            int startIndex = closes.Count - sampleSize;

            for (int i = startIndex; i < closes.Count; i++)
            {
                builder.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F4}:{1:F4}:{2:F4};",
                    highs[i],
                    lows[i],
                    closes[i]);
            }

            return builder.ToString();
        }

        private void CleanExpiredCache_NoLock(DateTime referenceTime)
        {
            var expiredKeys = new List<string>();

            foreach (var pair in _averageAtrCache)
            {
                if (referenceTime - pair.Value.Timestamp > _cacheDuration)
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _averageAtrCache.Remove(key);
            }

            _performanceMetrics.CacheSize = _averageAtrCache.Count;
        }

        private struct AtrCacheEntry
        {
            public double Value;
            public DateTime Timestamp;
            public string Fingerprint;
        }

        private bool TryPopulateSeries(
            string symbol,
            TimeFrame timeframe,
            List<double> highs,
            List<double> lows,
            List<double> closes)
        {
            if (string.Equals(symbol, _bot.Symbol.Name, StringComparison.OrdinalIgnoreCase) &&
                timeframe == _bot.TimeFrame)
            {
                return TryPopulateDefaultBars(highs, lows, closes);
            }

            try
            {
                var targetSymbol = string.Equals(symbol, _bot.Symbol.Name, StringComparison.OrdinalIgnoreCase)
                    ? _bot.Symbol
                    : _bot.MarketData.GetSymbol(symbol);

                var series = _bot.MarketData.GetSeries(targetSymbol, timeframe);
                int count = series.Close.Count;
                if (count < _config.LookbackPeriod)
                {
                    LogWarning($"Insufficient bars for {symbol} {timeframe} ({count} < {_config.LookbackPeriod}). Returning Uncertain.");
                    return false;
                }

                int startIndex = count - _config.LookbackPeriod;
                for (int i = startIndex; i < count; i++)
                {
                    highs.Add(series.High[i]);
                    lows.Add(series.Low[i]);
                    closes.Add(series.Close[i]);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogWarning($"Multi-symbol/timeframe retrieval failed for {symbol} {timeframe}: {ex.Message}. Falling back to default Bars.");
                return TryPopulateDefaultBars(highs, lows, closes);
            }
        }

        private bool TryPopulateDefaultBars(List<double> highs, List<double> lows, List<double> closes)
        {
            var bars = _bot.Bars;
            if (bars.Count < _config.LookbackPeriod)
            {
                LogWarning($"Insufficient bars ({bars.Count}), need {_config.LookbackPeriod}. Returning Uncertain.");
                return false;
            }

            int startIndex = bars.Count - _config.LookbackPeriod;
            for (int i = startIndex; i < bars.Count; i++)
            {
                highs.Add(bars.HighPrices[i]);
                lows.Add(bars.LowPrices[i]);
                closes.Add(bars.ClosePrices[i]);
            }

            return true;
        }

        private RegimeType ClassifyRegime(double adx, double currentAtr, double averageAtr, double bollingerWidth)
        {
            bool isTrending = adx > _config.AdxTrendThreshold;

            bool isVolatile = currentAtr > (averageAtr * _config.VolatilityThreshold) ||
                              (_config.UseBollingerInClassification && bollingerWidth > _config.BollingerVolatilityThreshold);

            bool isCalm = currentAtr < (averageAtr * _config.CalmThreshold) ||
                          (_config.UseBollingerInClassification && bollingerWidth < _config.BollingerCalmThreshold);

            bool isRanging = adx < _config.AdxRangeThreshold;

            if (isTrending)
            {
                return RegimeType.Trending;
            }

            if (isVolatile)
            {
                return RegimeType.Volatile;
            }

            if (isCalm)
            {
                return RegimeType.Calm;
            }

            if (isRanging)
            {
                return RegimeType.Ranging;
            }

            return RegimeType.Uncertain;
        }

        private int GetTimeframeSeconds(TimeFrame timeframe)
        {
            if (timeframe == TimeFrame.Minute) return 60;
            if (timeframe == TimeFrame.Minute2) return 120;
            if (timeframe == TimeFrame.Minute3) return 180;
            if (timeframe == TimeFrame.Minute4) return 240;
            if (timeframe == TimeFrame.Minute5) return 300;
            if (timeframe == TimeFrame.Minute10) return 600;
            if (timeframe == TimeFrame.Minute15) return 900;
            if (timeframe == TimeFrame.Minute30) return 1800;
            if (timeframe == TimeFrame.Hour) return 3600;
            if (timeframe == TimeFrame.Hour4) return 14400;
            if (timeframe == TimeFrame.Daily) return 86400;
            return 900;
        }

        private void LogInfo(string message)
        {
            try
            {
                PipelineLogger.Log("REGIME", "Info", message, null, _bot.Print);
            }
            catch
            {
                _bot.Print($"[REGIME] {message}");
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                PipelineLogger.Log("REGIME", "Warning", message, null, _bot.Print);
            }
            catch
            {
                _bot.Print($"[REGIME:WARN] {message}");
            }
        }

        private void LogError(string message, Exception ex)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["stack"] = ex.StackTrace
                };
                PipelineLogger.Log("REGIME", "Error", message, data, _bot.Print);
            }
            catch
            {
                _bot.Print($"[REGIME:ERROR] {message}: {ex.Message}");
            }
        }
    }
}
