using System;
using System.Collections.Generic;
using Config;
using DataFetcher.Models;

namespace Analysis.SmartMoneyConcept
{
    public class VolumeImbalanceDetector : ISmartMoneyDetector
    {
        private readonly BotConfig _cfg;
        public VolumeImbalanceDetector(BotConfig config) { _cfg = config; IsEnabled = _cfg.ShowVolumeImbalance; }
        public SmartMoneyType Type => SmartMoneyType.VolumeImbalance;
        public bool IsEnabled { get; set; }

        public bool Detect(IList<Bar> bars, out SmartMoneySignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < _cfg.VolumeImbalanceLookback) return false;
            int look = Math.Min(_cfg.VolumeImbalanceLookback, bars.Count - 1);
            double totalVolume = 0;
            double upVolume = 0;
            double downVolume = 0;
            for (int i = bars.Count - look - 1; i < bars.Count; i++)
            {
                totalVolume += bars[i].Volume;
                if (bars[i].Close > bars[i].Open) upVolume += bars[i].Volume;
                else downVolume += bars[i].Volume;
            }
            if (totalVolume == 0) return false;
            double upRatio = upVolume / totalVolume;
            double downRatio = downVolume / totalVolume;
            var last = bars[bars.Count - 1];
            if (upRatio > _cfg.VolumeImbalanceThreshold)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = true };
                return true;
            }
            if (downRatio > _cfg.VolumeImbalanceThreshold)
            {
                signal = new SmartMoneySignal { Type = Type, Time = last.OpenTime, IsBullish = false };
                return true;
            }
            return false;
        }
    }
}
