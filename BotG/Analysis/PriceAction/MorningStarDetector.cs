using System;
using System.Collections.Generic;
using DataFetcher.Models;
using Bot3.Core;
namespace Analysis.PriceAction
{
public class MorningStarDetector : ICandlePatternDetector, IModule
    {
        public CandlePattern Pattern => CandlePattern.MorningStar;
        public bool IsEnabled { get; set; } = true;
        public bool Detect(IList<Bar> bars, double atr, out CandlePatternSignal signal)
        {
            signal = null;
            if (!IsEnabled || bars == null || bars.Count < 3) return false;
            var b1 = bars[bars.Count - 3];
            var b2 = bars[bars.Count - 2];
            var b3 = bars[bars.Count - 1];
            if (b1.Close < b1.Open && Math.Abs(b2.Close - b2.Open) < atr * 0.2 && b3.Close > b3.Open && b3.Close > b1.Open)
            {
                signal = new CandlePatternSignal { Pattern = Pattern, Time = b3.OpenTime, IsBullish = true };
                return true;
            }
            return false;
        }

        public void Initialize() { }

        public void Initialize(BotContext ctx)
        {
            throw new NotImplementedException();
        }

        public void OnBar(IReadOnlyList<cAlgo.API.Bar> bars)
        {
            throw new NotImplementedException();
        }

        public void OnTick(cAlgo.API.Tick tick)
        {
            throw new NotImplementedException();
        }

        public void Start() { }
        public void Stop() { }
    }
}
