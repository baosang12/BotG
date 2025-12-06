using System;
using System.Collections.Generic;
using System.Linq;
using DataFetcher.Models;
using Analysis.PriceAction;
using Config;

namespace Analysis.PriceAction
{
    /// <summary>
    /// Collects and normalizes bar data across multiple timeframes.
    /// </summary>
    public class DataIngestionStep : IPriceActionStep
    {
        private readonly Indicators.EmaIndicator _ema;
        private readonly Indicators.VwapIndicator _vwap;
        private readonly bool _enabled;

        public DataIngestionStep(Indicators.EmaIndicator ema,
                                 Indicators.VwapIndicator vwap,
                                 PAConfig config)
        {
            _ema = ema;
            _vwap = vwap;
            // If no steps specified, enable all; otherwise check if this step is enabled
            _enabled = config.EnabledSteps == null || config.EnabledSteps.Contains(nameof(DataIngestionStep).Replace("Step", ""));
        }

        public void Execute(IDictionary<string, IList<Bar>> multiTfBars,
                            IList<Bar> currentBars,
                            PriceActionContext context)
        {
            try
            {
                if (!_enabled)
                {
                    return;
                }
                context.MultiTfBars.Clear();
                foreach (var kv in multiTfBars)
                    context.MultiTfBars[kv.Key] = kv.Value;
                context.CurrentBars = currentBars;
                // compute EMA and VWAP series
                void OnEma(object s, double value) => context.EmaSeries.Add(value);
                void OnVwap(object s, double value) => context.VwapSeries.Add(value);
                _ema.Updated += OnEma;
                _vwap.Updated += OnVwap;
                foreach (var bar in currentBars)
                {
                    _ema.Update(bar);
                    _vwap.Update(bar);
                }
                _ema.Updated -= OnEma;
                _vwap.Updated -= OnVwap;
            }
            catch (Exception)
            {
            }
        }
    }
}
