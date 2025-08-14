using System;
using System.Collections.Generic;
using System.Linq;
using Analysis.PriceAction;
using Analysis.SmartMoneyConcept;
using Indicators.Volatility;
using Indicators.Structure;
using Bot3.Core;
using cAlgo.API;

namespace Analysis
{
    /// <summary>
    /// Module tổng hợp và phân tích kết quả từ các indicator.
    /// </summary>
    public class AnalysisModule : IModule
    {
        private readonly AtrIndicator _atrIndicator;
        private readonly MarketStructureIndicator _marketStructureIndicator;
        // private analyzers list removed; using direct calls
        /// <summary>
        /// Fired when a candle pattern is detected.
        /// </summary>
        public event EventHandler<CandlePatternSignal> PatternDetected;
        /// <summary>
        /// Fired when a Smart Money event (BOS/CHoCH) is detected.
        /// </summary>
        public event EventHandler<SmartMoneySignal> SmartMoneyDetected;

        public AnalysisModule(AtrIndicator atrIndicator, MarketStructureIndicator msIndicator)
        {
            _atrIndicator = atrIndicator;
            _marketStructureIndicator = msIndicator;
        }

        private readonly PriceAction.CandlePatternAnalyzer _candleAnalyzer = new PriceAction.CandlePatternAnalyzer();
        private readonly List<DataFetcher.Models.Bar> _barBuffer = new List<DataFetcher.Models.Bar>();
        // Danh sách Smart Money detectors
        private readonly List<ISmartMoneyDetector> _smDetectors = new List<ISmartMoneyDetector>();

        public void Initialize()
        {
            // No analyzers list; using direct analyzer invocations
            // Khởi tạo Smart Money detectors
            _smDetectors.Add(new BreakOfStructureDetector());
            _smDetectors.Add(new ChangeOfCharacterDetector());
        }

        public void Start()
        {
            // Subscribe event từ các indicator
            _atrIndicator.Updated += (_, atr) => RunAnalysis();
            _marketStructureIndicator.Updated += (_, ms) => RunAnalysis();
        }

        public void Stop()
        {
            // Unsubscribe indicator events
            _atrIndicator.Updated -= (_, atr) => RunAnalysis();
            _marketStructureIndicator.Updated -= (_, ms) => RunAnalysis();
        }

        /// <summary>
        /// Receive a new bar, buffer and analyze candle patterns.
        /// </summary>
        public void ProcessBar(DataFetcher.Models.Bar bar)
        {
            _barBuffer.Add(bar);
            // keep last 5 bars
            if (_barBuffer.Count > 5)
                _barBuffer.RemoveAt(0);
            // detect candle patterns
            var atrValue = _atrIndicator.GetCurrentAtr();
            var signals = _candleAnalyzer.Analyze(_barBuffer, atrValue);
            foreach (var sig in signals)
            {
                // notify subscribers about detected pattern
                PatternDetected?.Invoke(this, sig);
                // log for debugging
                Console.WriteLine($"Pattern {sig.Pattern} at {sig.Time} bullish={sig.IsBullish}");
            }
            // detect Smart Money events
            foreach (var det in _smDetectors)
            {
                if (det.IsEnabled && det.Detect(_barBuffer, out var smSig))
                {
                    SmartMoneyDetected?.Invoke(this, smSig);
                    Console.WriteLine($"[SmartMoney] {smSig.Type} at {smSig.Time} bullish={smSig.IsBullish}");
                }
            }
        }

        private void RunAnalysis()
        {
            // Gọi từng analyzer với input phù hợp và xử lý kết quả
            // Ví dụ combine ATR và MarketStructure thành xu hướng tổng hợp
        }

        public void Initialize(BotContext ctx)
        {
            throw new NotImplementedException();
        }

        public void OnBar(IReadOnlyList<Bar> bars)
        {
            throw new NotImplementedException();
        }

        public void OnTick(Tick tick)
        {
            throw new NotImplementedException();
        }
    }
}
