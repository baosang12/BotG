using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strategies
{
    /// <summary>
    /// Relative Strength Index strategy generating reversal signals on threshold crossings.
    /// </summary>
    public sealed class RsiStrategy : IStrategy
    {
        private readonly int _period;
        private readonly double _oversold;
        private readonly double _overbought;
        private double _avgGain;
        private double _avgLoss;
        private int _samples;
        private double? _previousRsi;
        private double? _previousPrice;
        private readonly object _lock = new();

        public string Name { get; }

        public RsiStrategy(string name = "RSI_Reversal", int period = 5, double oversold = 15, double overbought = 85)
        {
            if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period));
            if (oversold <= 0 || oversold >= overbought) throw new ArgumentException("Invalid RSI thresholds");

            Name = name;
            _period = period;
            _oversold = oversold;
            _overbought = overbought;
        }

        public Task<Signal?> EvaluateAsync(MarketData data, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_previousPrice is null)
                {
                    _previousPrice = data.Mid;
                    return Task.FromResult<Signal?>(null);
                }

                double change = data.Mid - _previousPrice.Value;
                double gain = Math.Max(0, change);
                double loss = Math.Max(0, -change);

                if (_samples < _period)
                {
                    _avgGain += gain;
                    _avgLoss += loss;
                    _samples++;

                    if (_samples == _period)
                    {
                        _avgGain /= _period;
                        _avgLoss /= _period;
                    }

                    _previousPrice = data.Mid;
                    return Task.FromResult<Signal?>(null);
                }

                _avgGain = ((_avgGain * (_period - 1)) + gain) / _period;
                _avgLoss = ((_avgLoss * (_period - 1)) + loss) / _period;
                _previousPrice = data.Mid;

                double rs = _avgLoss == 0 ? 100 : _avgGain / (_avgLoss == 0 ? 1e-6 : _avgLoss);
                double rsi = 100 - (100 / (1 + rs));
                Signal? signal = null;
                var indicators = new Dictionary<string, double>
                {
                    ["rsi"] = rsi,
                    ["avg_gain"] = _avgGain,
                    ["avg_loss"] = _avgLoss
                };

                if (_previousRsi.HasValue)
                {
                    if (_previousRsi <= _oversold && rsi > _oversold)
                    {
                        signal = BuildSignal(data, TradeAction.Buy, rsi, "RSI exit oversold", indicators);
                    }
                    else if (_previousRsi >= _overbought && rsi < _overbought)
                    {
                        signal = BuildSignal(data, TradeAction.Sell, rsi, "RSI exit overbought", indicators);
                    }
                }

                _previousRsi = rsi;
                return Task.FromResult(signal);
            }
        }

        public RiskScore CalculateRisk(MarketContext context)
        {
            var rsi = _previousRsi ?? 50;
            var spread = context.LatestTick.Spread;
            var equity = Math.Max(1.0, Math.Abs(context.AccountEquity));
            var drawdownRatio = equity > 0 ? context.DailyDrawdown / equity : 0.0;

            double proximity = Math.Min(Math.Abs(rsi - 50) / 50.0, 1.0);
            double score = 5.5 + proximity; // higher score when RSI is far from neutral
            var factors = new Dictionary<string, double>
            {
                ["rsi"] = rsi,
                ["spread"] = spread,
                ["drawdown_ratio"] = drawdownRatio
            };

            if (spread > 0.002)
            {
                score -= 1.0;
            }
            if (drawdownRatio < -0.05)
            {
                score -= 1.5;
            }

            var level = RiskLevel.Normal;
            if (score < 4.0)
            {
                level = RiskLevel.Elevated;
            }

            bool acceptable = score >= 5.0 && level != RiskLevel.Blocked;
            var reason = acceptable ? null : "RsiRisk";

            return new RiskScore(score, level, acceptable, reason, factors);
        }

        private Signal BuildSignal(MarketData data, TradeAction action, double rsi, string reason, IReadOnlyDictionary<string, double> indicators)
        {
            double price = action == TradeAction.Buy ? data.Ask : data.Bid;
            var notes = new Dictionary<string, string>
            {
                ["reason"] = reason
            };

            return new Signal
            {
                StrategyName = Name,
                Action = action,
                Price = price,
                Confidence = Math.Min(1.0, Math.Abs(50 - rsi) / 50.0),
                TimestampUtc = data.TimestampUtc,
                StopLoss = action == TradeAction.Buy ? price - data.Spread * 4 : price + data.Spread * 4,
                TakeProfit = action == TradeAction.Buy ? price + data.Spread * 8 : price - data.Spread * 8,
                Indicators = indicators,
                Notes = notes
            };
        }
    }
}
