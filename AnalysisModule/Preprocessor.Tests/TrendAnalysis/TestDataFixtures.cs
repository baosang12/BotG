using System;
using System.Collections.Generic;
using System.Linq;
using AnalysisModule.Preprocessor.Core;
using AnalysisModule.Preprocessor.DataModels;
using AnalysisModule.Preprocessor.TrendAnalysis;

namespace AnalysisModule.Preprocessor.Tests.TrendAnalysis
{
    /// <summary>
    /// Tập hợp fixture dữ liệu deterministic cho các kịch bản market structure.
    /// </summary>
    public static class TestDataFixtures
    {
        private static readonly DateTime BaseTimeUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const int DefaultBarCount = 50;
        private const TimeFrame DefaultTimeFrame = TimeFrame.H1;

        public static SnapshotFixture CreateBullishTrendScenario(int barCount = DefaultBarCount)
        {
            var bars = CreateBullishBars(barCount);

            return BuildFixture(bars);
        }

        public static SnapshotFixture CreateBearishTrendScenario(int barCount = DefaultBarCount)
        {
            var bars = CreateBearishBars(barCount);

            return BuildFixture(bars);
        }

        public static SnapshotFixture CreateRangeMarketScenario(int barCount = DefaultBarCount)
        {
            var bars = CreateRangeBars(barCount);
            return BuildFixture(bars);
        }

        public static SnapshotFixture CreateBreakOfStructureScenario()
        {
            var bars = CreateBreakoutBars();
            return BuildFixture(bars);
        }

        public static IReadOnlyList<Bar> CreateBullishBars(int barCount = DefaultBarCount)
        {
            return GenerateTrendBars(
                barCount,
                BaseTimeUtc,
                startPrice: 100,
                step: 0.6,
                swingAmplitude: 1.2);
        }

        public static IReadOnlyList<Bar> CreateBearishBars(int barCount = DefaultBarCount)
        {
            return GenerateTrendBars(
                barCount,
                BaseTimeUtc.AddDays(-5),
                startPrice: 140,
                step: -0.6,
                swingAmplitude: 1.2);
        }

        public static IReadOnlyList<Bar> CreateRangeBars(int barCount = DefaultBarCount)
        {
            return GenerateRangeBars(barCount, BaseTimeUtc.AddDays(-10), 110, rangeWidth: 0.2);
        }

        public static IReadOnlyList<Bar> CreateBreakoutBars()
        {
            var consolidation = GenerateRangeBars(30, BaseTimeUtc.AddDays(-3), 120, rangeWidth: 1.0);
            var breakout = GenerateTrendBars(20, consolidation[^1].OpenTimeUtc.AddHours(1), 122, 1.2, 1.5);

            var bars = new List<Bar>(consolidation.Count + breakout.Count);
            bars.AddRange(consolidation);
            bars.AddRange(breakout);
            return bars;
        }

        public static SnapshotFixture CreateSnapshotFixture(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe = DefaultTimeFrame,
            IReadOnlyDictionary<string, double> indicators = null,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> barHistory = null)
        {
            return BuildFixture(bars, timeframe, indicators, barHistory);
        }

        public static PreprocessorSnapshot CreateMockSnapshot(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe = DefaultTimeFrame,
            IReadOnlyDictionary<string, double> indicators = null)
        {
            var fixture = BuildFixture(bars, timeframe, indicators);
            return fixture.Snapshot;
        }

        private static SnapshotFixture BuildFixture(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe = DefaultTimeFrame,
            IReadOnlyDictionary<string, double> indicators = null,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> barHistory = null)
        {
            var latestBars = bars.Count > 0
                ? new Dictionary<TimeFrame, Bar> { [timeframe] = bars[^1] }
                : new Dictionary<TimeFrame, Bar>();

            var snapshot = new PreprocessorSnapshot(
                TimestampUtc: bars.Count > 0 ? bars[^1].OpenTimeUtc.AddMinutes(5) : BaseTimeUtc,
                Indicators: indicators ?? new Dictionary<string, double>(),
                LatestBars: latestBars,
                Account: null);

            var history = barHistory ?? (bars.Count > 0
                ? new Dictionary<TimeFrame, IReadOnlyList<Bar>> { [timeframe] = bars }
                : new Dictionary<TimeFrame, IReadOnlyList<Bar>>());

            SnapshotHistoryRegistry.Attach(snapshot, history);
            var accessor = new SnapshotDataAccessor(snapshot, history);
            return new SnapshotFixture(snapshot, accessor, bars, timeframe);
        }

        private static IReadOnlyList<Bar> GenerateTrendBars(
            int barCount,
            DateTime startTime,
            double startPrice,
            double step,
            double swingAmplitude)
        {
            var bars = new List<Bar>(barCount);
            var time = startTime;
            var direction = Math.Sign(step == 0 ? 1 : step);

            for (var i = 0; i < barCount; i++)
            {
                var drift = i * step * 0.4;
                var oscillation = Math.Sin(i / 2.0) * swingAmplitude;
                var center = startPrice + drift + oscillation;
                var body = Math.Max(0.25, Math.Abs(step) * 0.4);
                var wick = swingAmplitude * 0.6 + 0.2;

                var open = center - direction * body;
                var close = center + direction * body;
                var high = Math.Max(open, close) + wick;
                var low = Math.Min(open, close) - wick;

                bars.Add(CreateBar(time, open, high, low, close, DefaultTimeFrame, volume: 1_000 + i * 5));

                time = time.AddHours(1);
            }

            return bars;
        }

        private static IReadOnlyList<Bar> GenerateRangeBars(int barCount, DateTime startTime, double midPrice, double rangeWidth)
        {
            var bars = new List<Bar>(barCount);
            var random = new Random(42);
            var time = startTime;

            for (var i = 0; i < barCount; i++)
            {
                var offset = Math.Sin(i * 0.5) * rangeWidth;
                var open = midPrice + offset;
                var close = open + (random.NextDouble() - 0.5) * 0.4;
                var high = Math.Max(open, close) + random.NextDouble() * 0.3;
                var low = Math.Min(open, close) - random.NextDouble() * 0.3;

                bars.Add(CreateBar(time, open, high, low, close, DefaultTimeFrame, volume: 900 + i * 3));

                time = time.AddHours(1);
            }

            return bars;
        }

        private static Bar CreateBar(
            DateTime openTimeUtc,
            double open,
            double high,
            double low,
            double close,
            TimeFrame timeframe,
            long volume)
        {
            return new Bar(openTimeUtc, open, high, low, close, volume, timeframe);
        }
    }

    public sealed record SnapshotFixture(
        PreprocessorSnapshot Snapshot,
        SnapshotDataAccessor Accessor,
        IReadOnlyList<Bar> Bars,
        TimeFrame TimeFrame);

    public enum MarketScenario
    {
        Bullish,
        Bearish,
        Range,
        Breakout
    }

    public static class TestDataFixturesExtensions
    {
        public static SnapshotFixture CreateMovingAverageTestFixture(
            MarketScenario scenario,
            TimeFrame primaryTimeframe = TimeFrame.H1,
            int barCount = 50)
        {
            var bars = CreateScenarioBars(scenario, barCount);
            var indicators = scenario switch
            {
                MarketScenario.Bullish => CreateBullishIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Bearish => CreateBearishIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Range => CreateRangeIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Breakout => CreateBullishIndicatorValues(bars, primaryTimeframe),
                _ => CreateBullishIndicatorValues(bars, primaryTimeframe)
            };

            AddMultiTimeframeIndicators(indicators, bars[^1].Close, scenario);
            var history = BuildMultiTimeframeHistory(bars, primaryTimeframe);
            return TestDataFixtures.CreateSnapshotFixture(bars, primaryTimeframe, indicators, history);
        }

        public static Dictionary<string, double> CreateBullishIndicatorValues(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var latestClose = bars[^1].Close;
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"EMA_9_{timeframe}"] = latestClose - 1.0,
                [$"EMA_20_{timeframe}"] = latestClose - 2.0,
                [$"EMA_50_{timeframe}"] = latestClose - 4.0,
                [$"EMA_200_{timeframe}"] = latestClose - 8.0,
                [$"ATR_14_{timeframe}"] = 1.5,
                [$"RSI_14_{timeframe}"] = 65.0
            };
        }

        public static Dictionary<string, double> CreateBearishIndicatorValues(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var latestClose = bars[^1].Close;
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"EMA_9_{timeframe}"] = latestClose + 1.0,
                [$"EMA_20_{timeframe}"] = latestClose + 2.0,
                [$"EMA_50_{timeframe}"] = latestClose + 4.0,
                [$"EMA_200_{timeframe}"] = latestClose + 8.0,
                [$"ATR_14_{timeframe}"] = 1.5,
                [$"RSI_14_{timeframe}"] = 35.0
            };
        }

        public static Dictionary<string, double> CreateRangeIndicatorValues(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var latestClose = bars[^1].Close;
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"EMA_9_{timeframe}"] = latestClose + 0.05,
                [$"EMA_20_{timeframe}"] = latestClose - 0.05,
                [$"EMA_50_{timeframe}"] = latestClose + 0.05,
                [$"EMA_200_{timeframe}"] = latestClose - 0.05,
                [$"ATR_14_{timeframe}"] = 0.8,
                [$"RSI_14_{timeframe}"] = 50.0
            };
        }

        private static IReadOnlyList<Bar> CreateScenarioBars(MarketScenario scenario, int barCount)
        {
            return scenario switch
            {
                MarketScenario.Bullish => TestDataFixtures.CreateBullishBars(barCount),
                MarketScenario.Bearish => TestDataFixtures.CreateBearishBars(barCount),
                MarketScenario.Range => TestDataFixtures.CreateRangeBars(barCount),
                MarketScenario.Breakout => TestDataFixtures.CreateBreakoutBars(),
                _ => TestDataFixtures.CreateBullishBars(barCount)
            };
        }

        private static void AddMultiTimeframeIndicators(
            IDictionary<string, double> indicators,
            double anchorPrice,
            MarketScenario scenario)
        {
            var higherTimeframes = new[] { TimeFrame.H4, TimeFrame.D1 };
            var bias = scenario switch
            {
                MarketScenario.Bearish => 1.0,
                MarketScenario.Range => 0.0,
                _ => -1.0
            };

            foreach (var tf in higherTimeframes)
            {
                var shift = bias * 2.0;
                indicators[$"EMA_20_{tf}"] = anchorPrice + shift;
                indicators[$"EMA_50_{tf}"] = anchorPrice + shift * 2;
            }
        }

        private static IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> BuildMultiTimeframeHistory(
            IReadOnlyList<Bar> primaryBars,
            TimeFrame primaryTimeframe)
        {
            var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [primaryTimeframe] = primaryBars
            };

            history[TimeFrame.H4] = AggregateBars(primaryBars, TimeFrame.H4, groupSize: 4);
            history[TimeFrame.D1] = AggregateBars(primaryBars, TimeFrame.D1, groupSize: 24);

            return history;
        }

        private static IReadOnlyList<Bar> AggregateBars(
            IReadOnlyList<Bar> source,
            TimeFrame targetTimeframe,
            int groupSize)
        {
            if (source.Count == 0)
            {
                return Array.Empty<Bar>();
            }

            if (groupSize <= 1)
            {
                var clone = new List<Bar>(source.Count);
                foreach (var bar in source)
                {
                    clone.Add(new Bar(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, targetTimeframe));
                }

                return clone;
            }

            var result = new List<Bar>();
            for (var i = 0; i < source.Count; i += groupSize)
            {
                var slice = source.Skip(i).Take(groupSize).ToList();
                if (slice.Count == 0)
                {
                    break;
                }

                var openTime = slice[0].OpenTimeUtc;
                var open = slice[0].Open;
                var close = slice[^1].Close;
                var high = slice.Max(b => b.High);
                var low = slice.Min(b => b.Low);
                var volume = slice.Sum(b => b.Volume);

                result.Add(new Bar(openTime, open, high, low, close, volume, targetTimeframe));
            }

            return result;
        }

        /// <summary>
        /// Fixture chuyên dụng cho MomentumLayer với RSI/ATR/ROC/Volume đa timeframe.
        /// </summary>
        public static SnapshotFixture CreateMomentumTestFixture(
            MarketScenario scenario,
            TimeFrame primaryTimeframe = TimeFrame.H1,
            int barCount = 50)
        {
            var bars = CreateScenarioBars(scenario, barCount);
            var emaIndicators = scenario switch
            {
                MarketScenario.Bullish => CreateBullishIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Bearish => CreateBearishIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Range => CreateRangeIndicatorValues(bars, primaryTimeframe),
                MarketScenario.Breakout => CreateBullishIndicatorValues(bars, primaryTimeframe),
                _ => CreateBullishIndicatorValues(bars, primaryTimeframe)
            };

            AddMultiTimeframeIndicators(emaIndicators, bars[^1].Close, scenario);

            var history = BuildMultiTimeframeHistory(bars, primaryTimeframe);
            var momentumIndicators = CreateMomentumIndicatorsForScenario(scenario, bars, primaryTimeframe, history);

            foreach (var indicator in momentumIndicators)
            {
                emaIndicators[indicator.Key] = indicator.Value;
            }

            return TestDataFixtures.CreateSnapshotFixture(bars, primaryTimeframe, emaIndicators, history);
        }

        private static Dictionary<string, double> CreateMomentumIndicatorsForScenario(
            MarketScenario scenario,
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> history)
        {
            var indicators = CreateMomentumIndicatorCore(scenario, bars, timeframe);
            AddMultiTimeframeMomentumIndicators(indicators, scenario, history);
            return indicators;
        }

        private static Dictionary<string, double> CreateMomentumIndicatorCore(
            MarketScenario scenario,
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            return scenario switch
            {
                MarketScenario.Bullish => CreateBullishMomentumIndicators(bars, timeframe),
                MarketScenario.Bearish => CreateBearishMomentumIndicators(bars, timeframe),
                MarketScenario.Range => CreateRangeMomentumIndicators(bars, timeframe),
                MarketScenario.Breakout => CreateBreakoutMomentumIndicators(bars, timeframe),
                _ => CreateBullishMomentumIndicators(bars, timeframe)
            };
        }

        public static Dictionary<string, double> CreateBullishMomentumIndicators(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var atr = CalculateAtr(bars, 14);
            var roc = CalculateRocPercent(bars, 14);
            var volume = CalculateVolumeSma(bars, 20);

            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"RSI_14_{timeframe}"] = 65.0,
                [$"ATR_14_{timeframe}"] = Math.Max(atr, 1.0),
                [$"ROC_14_{timeframe}"] = Math.Max(roc, 5.0),
                [$"Volume_SMA_20_{timeframe}"] = volume
            };
        }

        public static Dictionary<string, double> CreateBearishMomentumIndicators(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var atr = CalculateAtr(bars, 14);
            var roc = CalculateRocPercent(bars, 14);
            var volume = CalculateVolumeSma(bars, 20);

            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"RSI_14_{timeframe}"] = 35.0,
                [$"ATR_14_{timeframe}"] = Math.Max(atr, 1.0),
                [$"ROC_14_{timeframe}"] = Math.Min(roc, -4.0),
                [$"Volume_SMA_20_{timeframe}"] = volume
            };
        }

        public static Dictionary<string, double> CreateRangeMomentumIndicators(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var atr = CalculateAtr(bars, 14);
            var roc = CalculateRocPercent(bars, 14);
            var volume = CalculateVolumeSma(bars, 20);

            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"RSI_14_{timeframe}"] = 50.0,
                [$"ATR_14_{timeframe}"] = Math.Clamp(atr, 0.4, 1.2),
                [$"ROC_14_{timeframe}"] = Math.Clamp(roc, -1.5, 1.5),
                [$"Volume_SMA_20_{timeframe}"] = volume
            };
        }

        public static Dictionary<string, double> CreateBreakoutMomentumIndicators(
            IReadOnlyList<Bar> bars,
            TimeFrame timeframe)
        {
            var atr = CalculateAtr(bars, 14) * 1.3;
            var roc = CalculateRocPercent(bars, 10);
            var volume = CalculateVolumeSma(bars, 20) * 1.35;

            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [$"RSI_14_{timeframe}"] = 70.0,
                [$"ATR_14_{timeframe}"] = Math.Max(atr, 2.0),
                [$"ROC_14_{timeframe}"] = Math.Max(roc, 7.5),
                [$"Volume_SMA_20_{timeframe}"] = volume
            };
        }

        private static void AddMultiTimeframeMomentumIndicators(
            IDictionary<string, double> indicators,
            MarketScenario scenario,
            IReadOnlyDictionary<TimeFrame, IReadOnlyList<Bar>> history)
        {
            if (history == null || history.Count == 0)
            {
                return;
            }

            var higherTimeframes = new[] { TimeFrame.H4, TimeFrame.D1 };
            foreach (var timeframe in higherTimeframes)
            {
                if (!history.TryGetValue(timeframe, out var tfBars) || tfBars.Count == 0)
                {
                    continue;
                }

                var tfIndicators = CreateMomentumIndicatorCore(scenario, tfBars, timeframe);
                foreach (var pair in tfIndicators)
                {
                    indicators[pair.Key] = pair.Value;
                }
            }
        }

        private static double CalculateAtr(IReadOnlyList<Bar> bars, int period)
        {
            if (bars.Count < 2)
            {
                return 1.0;
            }

            var lookback = Math.Min(period, bars.Count - 1);
            double sum = 0;

            for (var i = 0; i < lookback; i++)
            {
                var current = bars[^(i + 1)];
                var previous = bars[^(i + 2)];
                var tr = Math.Max(
                    current.High - current.Low,
                    Math.Max(
                        Math.Abs(current.High - previous.Close),
                        Math.Abs(current.Low - previous.Close)));
                sum += tr;
            }

            return Math.Max(0.1, sum / lookback);
        }

        private static double CalculateRocPercent(IReadOnlyList<Bar> bars, int period)
        {
            if (bars.Count <= period)
            {
                period = bars.Count - 1;
            }

            if (period <= 0)
            {
                return 0;
            }

            var latestClose = bars[^1].Close;
            var pastClose = bars[^(period + 1)].Close;

            if (Math.Abs(pastClose) < 1e-6)
            {
                return 0;
            }

            return (latestClose - pastClose) / pastClose * 100.0;
        }

        private static double CalculateVolumeSma(IReadOnlyList<Bar> bars, int period)
        {
            if (bars.Count == 0)
            {
                return 0;
            }

            var lookback = Math.Min(period, bars.Count);
            double sum = 0;
            for (var i = 0; i < lookback; i++)
            {
                sum += bars[^(i + 1)].Volume;
            }

            return Math.Max(1, sum / lookback);
        }
    }

    public enum PatternScenario
    {
        StrongBreakout,
        FailedBreakout,
        LiquidityGrab,
        CleanBreakout
    }

    public static class PatternLayerTestScenarios
    {
        private static readonly DateTime PatternBaseTime = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static SnapshotFixture CreatePatternLayerFixture(PatternScenario scenario)
        {
            var bars = scenario switch
            {
                PatternScenario.StrongBreakout => CreateBreakoutScenarioBars(),
                PatternScenario.FailedBreakout => CreateBreakoutScenarioBars(failure: true, followThroughExtension: 1.2, limitedFollowThrough: true),
                PatternScenario.LiquidityGrab => CreateLiquidityGrabScenarioBars(),
                PatternScenario.CleanBreakout => CreateCleanTrendScenarioBars(),
                _ => CreateBreakoutScenarioBars()
            };

            var history = new Dictionary<TimeFrame, IReadOnlyList<Bar>>
            {
                [TimeFrame.H1] = bars,
                [TimeFrame.H4] = AggregatePatternBars(bars, TimeFrame.H4, 4),
                [TimeFrame.D1] = AggregatePatternBars(bars, TimeFrame.D1, 24)
            };

            return TestDataFixtures.CreateSnapshotFixture(bars, TimeFrame.H1, null, history);
        }

        public static List<Bar> CreateBreakoutScenarioBars(
            double breakoutRange = 3.2,
            double retestDepth = 0.4,
            double followThroughExtension = 3.2,
            bool weakBreakout = false,
            bool shallowRetest = false,
            bool deepRetest = false,
            bool failure = false,
            bool limitedFollowThrough = false)
        {
            var bars = new List<Bar>();
            var time = PatternBaseTime;

            for (var i = 0; i < 80; i++)
            {
                var wave = Math.Sin(i * 0.25) * 0.3;
                var open = 100 + wave - 0.12;
                var close = 100 + wave + 0.12;
                var high = Math.Max(open, close) + 0.18;
                var low = Math.Min(open, close) - 0.18;
                AddPatternBar(bars, ref time, open, high, low, close, 900 + i * 4);
            }

            var level = bars.Skip(Math.Max(0, bars.Count - 30)).Max(b => b.High);
            var breakoutStrength = weakBreakout ? 1.1 : breakoutRange;
            var bodyFraction = weakBreakout ? 0.4 : 0.9;
            var breakoutClose = level + breakoutStrength;
            var bodySize = breakoutStrength * bodyFraction;
            var breakoutOpen = breakoutClose - bodySize;
            var breakoutHigh = breakoutClose + 0.35;
            var breakoutLow = breakoutOpen - 0.35;
            var breakoutRangeActual = breakoutHigh - breakoutLow;
            var breakoutVolume = weakBreakout ? 1150 : 2200;
            AddPatternBar(bars, ref time, breakoutOpen, breakoutHigh, breakoutLow, breakoutClose, breakoutVolume);

            var desiredDepth = shallowRetest ? 0.08 : deepRetest ? 0.9 : retestDepth;
            desiredDepth = Math.Clamp(desiredDepth, 0.05, 0.95);
            var retestLow = breakoutClose - breakoutRangeActual * desiredDepth;
            var retestOpen = breakoutClose - breakoutRangeActual * Math.Clamp(desiredDepth * 0.6, 0.04, 0.45);
            retestOpen = Math.Max(retestOpen, retestLow + breakoutRangeActual * 0.02);
            var retestClose = retestLow + breakoutRangeActual * Math.Clamp(desiredDepth * 0.4, 0.12, 0.35);
            var retestHigh = Math.Max(retestOpen, retestClose) + breakoutRangeActual * 0.12;
            AddPatternBar(bars, ref time, retestOpen, retestHigh, retestLow, retestClose, 1400);

            var extension = limitedFollowThrough ? Math.Max(0.4, followThroughExtension * 0.35) : followThroughExtension;
            var steps = 5;
            var perStep = extension / steps;
            var lastClose = retestClose;
            for (var i = 0; i < steps; i++)
            {
                var open = lastClose + perStep * 0.25;
                var close = lastClose + perStep;
                var high = Math.Max(open, close) + 0.28;
                var low = Math.Min(open, close) - 0.28;
                AddPatternBar(bars, ref time, open, high, low, close, 1550 + i * 70);
                lastClose = close;
            }

            if (failure)
            {
                var failureClose = level - breakoutRangeActual * 0.45;
                var failureOpen = lastClose - perStep;
                var failureHigh = Math.Max(failureOpen, failureClose) + 0.2;
                var failureLow = Math.Min(failureOpen, failureClose) - 0.4;
                AddPatternBar(bars, ref time, failureOpen, failureHigh, failureLow, failureClose, 1500);
                var capitulationClose = failureClose - 0.8;
                AddPatternBar(bars, ref time, failureClose - 0.3, failureClose + 0.1, capitulationClose - 0.2, capitulationClose, 1450);
            }

            return bars;
        }

        private static List<Bar> CreateLiquidityGrabScenarioBars()
        {
            var bars = new List<Bar>();
            var time = PatternBaseTime.AddDays(-2);
            var basePrice = 120.0;

            for (var i = 0; i < 90; i++)
            {
                var oscillation = Math.Sin(i * 0.18) * 0.8;
                var open = basePrice + oscillation;
                var close = open + (i % 2 == 0 ? -0.1 : 0.1);
                var wickMultiplier = (i % 7 == 0 || i % 11 == 0) ? 1.6 : 0.3;
                var high = Math.Max(open, close) + wickMultiplier;
                var low = Math.Min(open, close) - wickMultiplier * 0.8;
                var volume = 950 + (i % 10 == 0 ? 320 : 40);
                AddPatternBar(bars, ref time, open, high, low, close, volume);
            }

            return bars;
        }

        private static List<Bar> CreateCleanTrendScenarioBars()
        {
            var bars = new List<Bar>();
            var time = PatternBaseTime.AddDays(-1);
            var price = 90.0;

            for (var i = 0; i < 30; i++)
            {
                var open = price + i * 0.35;
                var close = open + 0.85;
                var high = close + 0.25;
                var low = open - 0.15;
                var volume = 1000 + i * 80;
                AddPatternBar(bars, ref time, open, high, low, close, volume);
            }

            for (var i = 0; i < 20; i++)
            {
                var open = bars[^1].Close + 0.25;
                var close = open + 0.9;
                var high = close + 0.3;
                var low = open - 0.1;
                var volume = 1800 + i * 60;
                AddPatternBar(bars, ref time, open, high, low, close, volume);
            }

            return bars;
        }

        private static void AddPatternBar(List<Bar> bars, ref DateTime time, double open, double high, double low, double close, long volume)
        {
            var fixedHigh = Math.Max(Math.Max(open, close), high);
            var fixedLow = Math.Min(Math.Min(open, close), low);
            var adjustedHigh = Math.Max(fixedHigh, fixedLow + 0.0001);
            bars.Add(new Bar(time, open, adjustedHigh, fixedLow, close, volume, TimeFrame.H1));
            time = time.AddHours(1);
        }

        private static IReadOnlyList<Bar> AggregatePatternBars(IReadOnlyList<Bar> source, TimeFrame targetTimeframe, int groupSize)
        {
            if (source.Count == 0)
            {
                return Array.Empty<Bar>();
            }

            if (groupSize <= 1)
            {
                var copy = new List<Bar>(source.Count);
                foreach (var bar in source)
                {
                    copy.Add(new Bar(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, targetTimeframe));
                }

                return copy;
            }

            var result = new List<Bar>();
            for (var i = 0; i < source.Count; i += groupSize)
            {
                var slice = source.Skip(i).Take(groupSize).ToList();
                if (slice.Count == 0)
                {
                    break;
                }

                var openTime = slice[0].OpenTimeUtc;
                var open = slice[0].Open;
                var close = slice[^1].Close;
                var high = slice.Max(b => b.High);
                var low = slice.Min(b => b.Low);
                var volume = slice.Sum(b => b.Volume);
                result.Add(new Bar(openTime, open, high, low, close, volume, targetTimeframe));
            }

            return result;
        }
    }
}
