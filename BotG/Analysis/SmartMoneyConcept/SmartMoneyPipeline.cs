using System;
using System.Collections.Generic;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    /// <summary>
    /// Executes a series of SmartMoney detectors on a bar series.
    /// </summary>
    public class SmartMoneyPipeline
    {
        private readonly IList<ISmartMoneyDetector> _detectors;
        private readonly bool _enableLogging;

        public SmartMoneyPipeline(IEnumerable<ISmartMoneyDetector> detectors, bool enableLogging = false)
        {
            _detectors = new List<ISmartMoneyDetector>(detectors);
            _enableLogging = enableLogging;
        }

        /// <summary>
        /// Runs all enabled detectors on the provided bars and returns their signals.
        /// </summary>
        public IList<SmartMoneySignal> Execute(IList<Bar> bars)
        {
            var signals = new List<SmartMoneySignal>();
            if (bars == null || bars.Count == 0)
                return signals;

            foreach (var detector in _detectors)
            {
                if (!detector.IsEnabled)
                    continue;

                if (detector.Detect(bars, out var signal) && signal != null)
                {
                    signals.Add(signal);
                    if (_enableLogging)
                        Console.WriteLine($"[SmartMoneyPipeline] {detector.Type} signal at {signal.Time:yyyy-MM-dd HH:mm}, bullish={signal.IsBullish}");
                }
            }

            return signals;
        }
    }
}
