using System;
using DataFetcher.Models;
using System.Linq;
using System.Collections.Generic;

namespace Indicators.Structure
{
    /// <summary>
    /// Xác định cấu trúc thị trường đơn giản dựa trên bar: Bullish (higher highs & higher lows), Bearish (lower highs & lower lows), hoặc Neutral.
    /// </summary>
    public enum MarketStructure
    {
        Neutral,
        Bullish,
        Bearish
    }

    public class MarketStructureIndicator : IIndicator<Bar, MarketStructure>
    {
        private double _lastHigh = double.NaN;
        private double _lastLow = double.NaN;
        private MarketStructure _currentState = MarketStructure.Neutral;
        private double _trendStrength = 0;
        private double _volatility = 0;
        private double _range = 0;
        private readonly List<Bar> _swingHighs = new();
        private readonly List<Bar> _swingLows = new();
        // History buffer for swing detection with symmetrical window
        private readonly List<Bar> _history = new();
        private readonly int _swingLookback = 3;  // number of bars on each side for swing detection
        private int WindowSize => _swingLookback * 2 + 1;
        // Events for detection
        public event EventHandler<Bar> SwingHighDetected;
        public event EventHandler<Bar> SwingLowDetected;
        public event EventHandler<string> BreakOfStructureDetected;
        public event EventHandler<string> ChangeOfCharacterDetected;
        private MarketStructure _lastBreak = MarketStructure.Neutral;

        public Analysis.PriceAction.PriceActionContext.MarketStructure GetCurrentStructure()
        {
            return new Analysis.PriceAction.PriceActionContext.MarketStructure
            {
                State = _currentState == MarketStructure.Bullish ? Analysis.PriceAction.PriceActionContext.MarketState.Trending : Analysis.PriceAction.PriceActionContext.MarketState.Ranging,
                BreakOfStructure = Analysis.PriceAction.StructureType.None,
                ChangeOfCharacter = Analysis.PriceAction.StructureType.None,
                SwingHighs = _swingHighs,
                SwingLows = _swingLows,
                TrendStrength = _trendStrength,
                Volatility = _volatility,
                Range = _range
            };
        }
        public event EventHandler<MarketStructure> Updated;

        public void Update(Bar bar)
        {
            // Add new bar to history buffer
            _history.Add(bar);
            if (_history.Count > WindowSize)
                _history.RemoveAt(0);
            // Swing detection when buffer full (use symmetrical window)
            if (_history.Count == WindowSize)
            {
                int midIndex = _swingLookback;
                var segment = _history;
                var maxHigh = segment.Max(b => b.High);
                var minLow = segment.Min(b => b.Low);
                var midBar = segment[midIndex];
                if (midBar.High == maxHigh)
                {
                    _swingHighs.Add(midBar);
                    SwingHighDetected?.Invoke(this, midBar);
                    // Change of Character nếu BOS gần nhất là Bearish
                    if (_lastBreak == MarketStructure.Bearish)
                        ChangeOfCharacterDetected?.Invoke(this, "CHoCH Bullish");
                }
                if (midBar.Low == minLow)
                {
                    _swingLows.Add(midBar);
                    SwingLowDetected?.Invoke(this, midBar);
                    // Change of Character nếu BOS gần nhất là Bullish
                    if (_lastBreak == MarketStructure.Bullish)
                        ChangeOfCharacterDetected?.Invoke(this, "CHoCH Bearish");
                }
            }
            // On first bar, seed baseline
            if (double.IsNaN(_lastHigh) || double.IsNaN(_lastLow))
            {
                _lastHigh = bar.High;
                _lastLow = bar.Low;
                _currentState = MarketStructure.Neutral;
                _trendStrength = 0;
                _volatility = 0;
                _range = 0;
                _swingHighs.Clear();
                _swingLows.Clear();
                Updated?.Invoke(this, _currentState);
                return;
            }

            if (bar.High > _lastHigh && bar.Low > _lastLow)
            {
                _currentState = MarketStructure.Bullish;
                _swingHighs.Add(bar);
            }
            else if (bar.High < _lastHigh && bar.Low < _lastLow)
            {
                _currentState = MarketStructure.Bearish;
                _swingLows.Add(bar);
            }
            else
            {
                _currentState = MarketStructure.Neutral;
            }

            // Simple calculations for demo
            _trendStrength = Math.Abs(bar.Close - bar.Open);
            _volatility = Math.Abs(bar.High - bar.Low);
            _range = bar.High - bar.Low;

            _lastHigh = bar.High;
            _lastLow = bar.Low;
            // Notify updated state
            Updated?.Invoke(this, _currentState);
            // Break of Structure detection
            if (_swingHighs.Count >= 2)
            {
                var prev = _swingHighs[_swingHighs.Count - 2];
                if (bar.Close > prev.High && _lastBreak != MarketStructure.Bullish)
                {
                    _lastBreak = MarketStructure.Bullish;
                    BreakOfStructureDetected?.Invoke(this, $"BOS Bullish: Close {bar.Close} > {prev.High}");
                }
            }
            if (_swingLows.Count >= 2)
            {
                var prev = _swingLows[_swingLows.Count - 2];
                if (bar.Close < prev.Low && _lastBreak != MarketStructure.Bearish)
                {
                    _lastBreak = MarketStructure.Bearish;
                    BreakOfStructureDetected?.Invoke(this, $"BOS Bearish: Close {bar.Close} < {prev.Low}");
                }
            }
        }
    }
}
