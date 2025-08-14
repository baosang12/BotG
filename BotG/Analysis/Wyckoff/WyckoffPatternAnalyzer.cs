using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;
using Logging;

namespace Analysis.Wyckoff
{
    public enum WyckoffPatternPhase
    {
        None,
        PhaseA, // Climax - AR
        PhaseB, // ST
        PhaseC, // Test or Spring
        PhaseD, // SOS / LPS
        PhaseE  // Markup / Markdown
    }

    public class AnnotatedEvent
    {
        public string Label { get; set; }
        public int Index { get; set; }
        public double Price { get; set; }
        public AnnotatedEvent(string label, int index, double price)
        {
            Label = label;
            Index = index;
            Price = price;
        }
    }

    public class WyckoffPatternResult
    {
        public ClimaxEvent Climax { get; set; }
        public AREvent AR { get; set; }
        public SecondaryTestEvent ST { get; set; }
        public StrengthSignalEvent SOSorUT { get; set; }
        public LPSEvent LPSorLPSY { get; set; }
        public RangeTrapDetector RangeTrap { get; set; }
        public RangeState RangeState { get; set; } // new pivot-based range state
        public List<WyckoffPatternPhase> CompletedPhases { get; set; } = new();
        public WyckoffPatternPhase CurrentPhase { get; set; } = WyckoffPatternPhase.None;

        public bool IsAccumulation => SOSorUT != null && !SOSorUT.IsUT;
        public bool IsDistribution => SOSorUT != null && SOSorUT.IsUT;

        public double ConfidenceScore()
        {
            double score = 0;
            if (AR != null) score += 0.25;
            if (ST != null) score += 0.25;
            if (SOSorUT != null) score += 0.25;
            if (LPSorLPSY != null) score += 0.25;
            return score;
        }

        public List<AnnotatedEvent> ToAnnotatedEvents()
        {
            var result = new List<AnnotatedEvent>();
            if (Climax != null) result.Add(new AnnotatedEvent("Climax", Climax.Index, Climax.Price));
            if (AR != null) result.Add(new AnnotatedEvent("AR", AR.Index, AR.Price));
            if (ST != null) result.Add(new AnnotatedEvent("ST", ST.Index, ST.Price));
            if (SOSorUT != null) result.Add(new AnnotatedEvent(SOSorUT.IsUT ? "UT" : "SOS", SOSorUT.Index, SOSorUT.Price));
            if (LPSorLPSY != null) result.Add(new AnnotatedEvent(LPSorLPSY.IsLPSY ? "LPSY" : "LPS", LPSorLPSY.Index, LPSorLPSY.Price));
            return result;
        }

        public Strategies.WyckoffTradeSignal ToWyckoffTradeSignal()
        {
            var signal = new Strategies.WyckoffTradeSignal();
            signal.HasStrongOrderBlock = (this.Climax != null);
            signal.HasFairValueGap = false;
            signal.IsInDiscountZone = (this.SOSorUT != null && !this.SOSorUT.IsUT && this.AR != null && this.SOSorUT.Price < this.AR.Price);
            signal.HasVolumeSpike = false;
            signal.ConfirmationCount = new[]{this.Climax!=null,this.AR!=null,this.ST!=null,this.SOSorUT!=null,this.LPSorLPSY!=null}.Count(x=>x);
            signal.Action = (this.SOSorUT != null && !this.SOSorUT.IsUT) ? Strategies.TradeAction.Buy : (this.SOSorUT != null && this.SOSorUT.IsUT) ? Strategies.TradeAction.Sell : Strategies.TradeAction.None;
            signal.Price = this.SOSorUT?.Price ?? 0;
            signal.StopLoss = this.LPSorLPSY?.Price;
            signal.TakeProfit = null;
            return signal;
        }
    }

    public class WyckoffPatternAnalyzer
    {
        private readonly ClimaxDetector _climaxDetector;
        private readonly RangeTrapDetector _rangeTrapDetector;
        private readonly ARDetector _arDetector;
        private readonly SecondaryTestDetector _stDetector;
        private readonly StrengthSignalDetector _strengthDetector;
        private readonly LPSDetector _lpsDetector;
        private readonly RangeAnalyzer _rangeAnalyzer; // new analyzer
    private RangeJsonLogger _rangeJsonLogger;

        private readonly Action<string> _logger;

        public WyckoffPatternAnalyzer(Action<string> logger = null)
        {
            _logger = logger;
            _climaxDetector = new ClimaxDetector(logger: _logger);
            _rangeTrapDetector = new RangeTrapDetector(logger: _logger);
            _arDetector = new ARDetector(logger: _logger);
            _stDetector = new SecondaryTestDetector(logger: _logger);
            _strengthDetector = new StrengthSignalDetector(logger: _logger);
            _lpsDetector = new LPSDetector(logger: _logger);
            _rangeAnalyzer = new RangeAnalyzer(logger: _logger);
        }

        public WyckoffPatternAnalyzer(Action<string> logger = null, Action<RangeAnalyzer> configureRange = null, bool verboseRange = true, RangeJsonLogger jsonLogger = null)
        {
            _logger = logger;
            _climaxDetector = new ClimaxDetector(logger: _logger);
            _rangeTrapDetector = new RangeTrapDetector(logger: _logger);
            _arDetector = new ARDetector(logger: _logger);
            _stDetector = new SecondaryTestDetector(logger: _logger);
            _strengthDetector = new StrengthSignalDetector(logger: _logger);
            _lpsDetector = new LPSDetector(logger: _logger);
            _rangeAnalyzer = new RangeAnalyzer(logger: _logger) { Verbose = verboseRange };
            configureRange?.Invoke(_rangeAnalyzer);
            _rangeJsonLogger = jsonLogger;
            if (_rangeJsonLogger != null)
            {
                _rangeAnalyzer.StructuredLogger = (line, state) => _rangeJsonLogger.LogEvent(line, state);
            }
        }

        // Allow late attachment of JSON logger (runtime environments where config built after analyzer construction)
        public void AttachJsonLogger(RangeJsonLogger logger)
        {
            _rangeJsonLogger = logger;
            if (_rangeJsonLogger != null && _rangeAnalyzer != null)
            {
                _rangeAnalyzer.StructuredLogger = (line, state) => _rangeJsonLogger.LogEvent(line, state);
                _rangeJsonLogger.LogEvent("AttachJsonLogger", null);
            }
        }

        public WyckoffPatternResult Analyze(IList<Bar> bars, IList<int> swings = null, IList<double> atr = null)
        {
            var result = new WyckoffPatternResult();
            // 1. Climax
            var climaxList = _climaxDetector.DetectClimax(bars, swings, atr ?? new double[bars.Count]);
            var climax = climaxList?.OrderByDescending(c => c.Score).FirstOrDefault();
            result.Climax = climax;
            // 2. RangeTrap + RangeState
            if (climax != null)
            {
                _rangeTrapDetector.Reset();
                result.RangeTrap = _rangeTrapDetector;
                result.RangeState = _rangeAnalyzer.AnalyzeInitialRange(bars, climax);
                _rangeJsonLogger?.LogEvent("AnalyzeInitialRange", result.RangeState);
            }
            // 3. AR (legacy single AREvent still produced until fully migrated)
            var ar = _arDetector.DetectAR(bars, climax, swings, atr);
            result.AR = ar;
            // 4. ST
            var st = _stDetector.DetectST(bars, climax, ar);
            result.ST = st;
            // 5. SOS/UT
            var strength = _strengthDetector.DetectStrengthSignal(bars, ar);
            result.SOSorUT = strength;
            // 6. LPS/LPSY
            var lps = _lpsDetector.DetectLPS(bars, strength, st);
            result.LPSorLPSY = lps;
            // 7. Phase labelling (unchanged for now)
            if (ar != null) result.CompletedPhases.Add(WyckoffPatternPhase.PhaseA);
            if (st != null) result.CompletedPhases.Add(WyckoffPatternPhase.PhaseB);
            if (strength != null) result.CompletedPhases.Add(WyckoffPatternPhase.PhaseD);
            if (lps != null) result.CompletedPhases.Add(WyckoffPatternPhase.PhaseD);
            if (lps != null && strength != null && !strength.IsUT) result.CurrentPhase = WyckoffPatternPhase.PhaseE;
            else if (strength != null) result.CurrentPhase = WyckoffPatternPhase.PhaseD;
            else if (st != null) result.CurrentPhase = WyckoffPatternPhase.PhaseC;
            else if (ar != null) result.CurrentPhase = WyckoffPatternPhase.PhaseA;
            return result;
        }

    // Expose range analyzer instance (read-only) for external adaptive resets
    public RangeAnalyzer RangeAnalyzerInstance => _rangeAnalyzer;

        // Incremental update: if prior RangeState exists and climax known, evolve only
        public void EvolveRange(IList<Bar> bars, WyckoffPatternResult result)
        {
            if (result?.RangeState == null || result.Climax == null) return;
            _rangeAnalyzer.EvolveRangeState(bars, result.RangeState);
        }

    // Expose current active RangeState (for overlay drawing). Returns null if none initialized yet.
    public RangeState CurrentRangeState => _rangeAnalyzer?.ActiveRangeState;

        // Expose direct range init for a given climax (used by backfill to iterate multiple climaxes)
        public RangeState AnalyzeRangeForClimax(IList<Bar> bars, ClimaxEvent climax)
        {
            if (climax == null) return null;
            return _rangeAnalyzer.AnalyzeInitialRange(bars, climax);
        }
    }
}
