using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.PriceAction
{
    public class CandlePatternAnalyzer
    {
        private readonly List<ICandlePatternDetector> _detectors;

        public CandlePatternAnalyzer()
        {
            _detectors = new List<ICandlePatternDetector>
            {
                new EngulfingDetector(),
                new PinBarDetector(),
                new ThreeWhiteSoldiersDetector(),
                new ThreeBlackCrowsDetector(),
                new DojiDetector(),
                new HaramiDetector(),
                new PiercingLineDetector(),
                new DarkCloudCoverDetector(),
                new MorningStarDetector(),
                new EveningStarDetector(),
                new TweezerTopDetector(),
                new TweezerBottomDetector()
            };
        }

        public IList<CandlePatternSignal> Analyze(IList<Bar> bars, double atr = 0)
        {
            var signals = new List<CandlePatternSignal>();
            foreach (var detector in _detectors)
            {
                if (detector.IsEnabled && detector.Detect(bars, atr, out var signal) && signal != null)
                    signals.Add(signal);
            }
            return signals;
        }
    }

    internal class MarubozuDetector : ICandlePatternDetector
    {
        public CandlePattern Pattern => CandlePattern.Marubozu;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            return false; // default stub implementation
        }
    }
}
