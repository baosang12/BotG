using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BotG.Runtime.Logging;

namespace Strategies
{
    /// <summary>
    /// Simple moving average crossover strategy.
    /// Generates signals when the fast SMA crosses above/below the slow SMA.
    /// </summary>
    public sealed class SmaCrossoverStrategy : IStrategy
    {
        private readonly int _fastPeriod;
        private readonly int _slowPeriod;
        private readonly Queue<double> _fastWindow = new();
        private readonly Queue<double> _slowWindow = new();
        private double _fastSum;
        private double _slowSum;
        private double? _previousDiff;
        private readonly object _lock = new();
        private DateTime _lastSignalTime = DateTime.MinValue;
        private readonly Queue<double> _volumeWindow = new();
        private double _volumeSum;
        private const double MinCrossoverStrength = 0.002;
        private static readonly TimeSpan InternalCooldown = TimeSpan.FromMinutes(5);
        private const int VolumeLookback = 5;

        public string Name { get; }

        public SmaCrossoverStrategy(string name = "SMA_Crossover", int fastPeriod = 2, int slowPeriod = 5)
        {
            if (fastPeriod <= 1) throw new ArgumentOutOfRangeException(nameof(fastPeriod));
            if (slowPeriod <= fastPeriod) throw new ArgumentException("Slow period must be greater than fast period", nameof(slowPeriod));

            Name = name;
            _fastPeriod = fastPeriod;
            _slowPeriod = slowPeriod;
        }

        public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
        {
            lock (_lock)
            {
                UpdateWindow(_fastWindow, ref _fastSum, _fastPeriod, data.Mid);
                UpdateWindow(_slowWindow, ref _slowSum, _slowPeriod, data.Mid);
                if (data.Volume.HasValue)
                {
                    UpdateWindow(_volumeWindow, ref _volumeSum, VolumeLookback, data.Volume.Value);
                }

                if (_slowWindow.Count < _slowPeriod)
                {
                    return Task.FromResult<Signal?>(null);
                }

                if (data.TimestampUtc != default && data.TimestampUtc - _lastSignalTime < InternalCooldown)
                {
                    return Task.FromResult<Signal?>(null);
                }

                var fastSma = _fastSum / _fastWindow.Count;
                var slowSma = _slowSum / _slowWindow.Count;
                var diff = fastSma - slowSma;
                var strength = Math.Abs(diff) / Math.Max(1e-6, slowSma);

                if (strength < MinCrossoverStrength)
                {
                    _previousDiff = diff;
                    return Task.FromResult<Signal?>(null);
                }

                Signal? signal = null;
                var indicators = new Dictionary<string, double>
                {
                    ["fast_sma"] = fastSma,
                    ["slow_sma"] = slowSma,
                    ["delta"] = diff
                };

                if (_previousDiff.HasValue)
                {
                    // Bullish crossover (fast crosses above slow)
                    if (_previousDiff <= 0 && diff > 0)
                    {
                        var confidence = CalculateConfidence(strength, data.Volume);
                        if (confidence >= 0.12)
                        {
                            signal = BuildSignal(data, TradeAction.Buy, confidence, strength, indicators);
                        }
                    }
                    // Bearish crossover
                    else if (_previousDiff >= 0 && diff < 0)
                    {
                        var confidence = CalculateConfidence(strength, data.Volume);
                        if (confidence >= 0.12)
                        {
                            signal = BuildSignal(data, TradeAction.Sell, confidence, strength, indicators);
                        }
                    }
                }

                _previousDiff = diff;
                return Task.FromResult(signal);
            }
        }

        public RiskScore CalculateRisk(MarketContext context)
        {
            var spread = context.LatestTick.Spread;
            var equity = Math.Max(1.0, Math.Abs(context.AccountEquity));
            var exposureRatio = equity > 0 ? Math.Abs(context.OpenPositionExposure) / equity : 0.0;
            var drawdownRatio = equity > 0 ? context.DailyDrawdown / equity : 0.0;

            double score = 6.0;
            var factors = new Dictionary<string, double>
            {
                ["spread"] = spread,
                ["exposure_ratio"] = exposureRatio,
                ["drawdown_ratio"] = drawdownRatio
            };

            if (spread > 0.002)
            {
                score -= 1.0;
            }
            if (exposureRatio > 0.8)
            {
                score -= 1.0;
            }
            if (drawdownRatio < -0.05)
            {
                score -= 1.0;
            }

            var level = RiskLevel.Normal;
            if (score < 4.0)
            {
                level = RiskLevel.Elevated;
            }

            bool acceptable = score >= 5.0 && level != RiskLevel.Blocked;
            var reason = acceptable ? null : "SmaRisk";

            return new RiskScore(score, level, acceptable, reason, factors);
        }

        private static void UpdateWindow(Queue<double> window, ref double sum, int period, double value)
        {
            window.Enqueue(value);
            sum += value;

            if (window.Count > period)
            {
                sum -= window.Dequeue();
            }
        }

        private Signal BuildSignal(MarketData data, TradeAction action, double confidence, double strength, IReadOnlyDictionary<string, double> indicators)
        {
            double price = action == TradeAction.Buy ? data.Ask : data.Bid;
            _lastSignalTime = data.TimestampUtc == default ? DateTime.UtcNow : data.TimestampUtc;

            PipelineLogger.Log(
                "STRATEGY",
                "SMA/SignalGenerated",
                $"SMA generated {action} signal",
                new
                {
                    direction = action.ToString(),
                    confidence,
                    strength,
                    timestamp = _lastSignalTime
                },
                null);

            var notes = new Dictionary<string, string>
            {
                ["reason"] = action == TradeAction.Buy ? "Bullish crossover" : "Bearish crossover"
            };

            return new Signal
            {
                StrategyName = Name,
                Action = action,
                Price = price,
                Confidence = confidence,
                TimestampUtc = data.TimestampUtc,
                StopLoss = action == TradeAction.Buy ? price - data.Spread * 3 : price + data.Spread * 3,
                TakeProfit = action == TradeAction.Buy ? price + data.Spread * 6 : price - data.Spread * 6,
                Indicators = indicators,
                Notes = notes
            };
        }

        private double CalculateConfidence(double strength, double? currentVolume)
        {
            var baseConfidence = Math.Min(0.3, strength * 10);
            var volumeBoost = CalculateVolumeBoost(currentVolume);
            var confidence = baseConfidence * volumeBoost;
            return Math.Max(confidence, 0.05);
        }

        private double CalculateVolumeBoost(double? currentVolume)
        {
            if (!currentVolume.HasValue || _volumeWindow.Count < VolumeLookback)
            {
                return 1.0;
            }

            var averageVolume = _volumeSum / _volumeWindow.Count;
            return currentVolume.Value > averageVolume ? 1.2 : 0.8;
        }
    }
}
