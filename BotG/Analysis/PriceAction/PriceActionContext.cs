using System.Collections.Generic;
using DataFetcher.Models;
using Analysis.PriceAction;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Context object shared across Price Action steps.
    /// </summary>
    public class PriceActionContext
    {
        // Raw input bars per timeframe
        public IDictionary<string, IList<Bar>> MultiTfBars { get; } = new Dictionary<string, IList<Bar>>();
        // Current timeframe bars
        public IList<Bar> CurrentBars { get; set; }

        // Indicators
        public double Atr { get; set; }
        // EMA series computed over current bars
        public IList<double> EmaSeries { get; set; } = new List<double>();
        // VWAP series computed over current bars
        public IList<double> VwapSeries { get; set; } = new List<double>();

        // Multi-timeframe market structure
        public IDictionary<string, MarketStructure> StructureByTf { get; } = new Dictionary<string, MarketStructure>();

        // Single timeframe swing points (for legacy steps)
        public List<Bar> SwingHighs { get; } = new List<Bar>();
        public List<Bar> SwingLows { get; } = new List<Bar>();

        // Enriched market structure info
        public class MarketStructure
        {
            public List<Bar> SwingHighs { get; set; } = new();
            public List<Bar> SwingLows { get; set; } = new();
            public StructureType BreakOfStructure { get; set; }
            public StructureType ChangeOfCharacter { get; set; }
            public List<StructureType> BosHistory { get; set; } = new();
            public List<StructureType> ChochHistory { get; set; } = new();
            public double TrendStrength { get; set; }
            public double Volatility { get; set; }
            public double Range { get; set; }
            public double VolumeProfile { get; set; }
            public double ConfidenceScore { get; set; }
            public MarketState State { get; set; } // Trending, Ranging, Breakout, Reversal, etc.
        }

        public enum MarketState
        {
            Trending,
            Ranging,
            Breakout,
            Reversal,
            Unknown
        }

        // Detected patterns
        public IList<CandlePatternSignal> PatternSignals { get; } = new List<CandlePatternSignal>();

        // Confirmed signals after multi-tf check
        public IList<CandlePatternSignal> ConfirmedSignals { get; } = new List<CandlePatternSignal>();

        // Scored signals
        public IDictionary<CandlePatternSignal, int> SignalScores { get; } = new Dictionary<CandlePatternSignal, int>();

        // Final selected signals
        public IList<CandlePatternSignal> FinalSignals { get; } = new List<CandlePatternSignal>();
    }
}
