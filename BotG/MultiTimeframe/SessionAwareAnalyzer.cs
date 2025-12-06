using System;
using System.Collections.Generic;

namespace BotG.MultiTimeframe
{
    public enum TradingSession
    {
        Asian,
        London,
        Overlap,
        NewYork,
        Night
    }

    public sealed class SessionAwareAnalyzer
    {
        private readonly IReadOnlyDictionary<TradingSession, double> _sizeMultipliers = new Dictionary<TradingSession, double>
        {
            [TradingSession.Asian] = 0.5,
            [TradingSession.London] = 1.0,
            [TradingSession.Overlap] = 1.5,
            [TradingSession.NewYork] = 1.2,
            [TradingSession.Night] = 0.3
        };

        public TradingSession GetCurrentSession(DateTime timeUtc)
        {
            var utc = timeUtc.Kind == DateTimeKind.Utc ? timeUtc : timeUtc.ToUniversalTime();
            var hour = utc.Hour;

            if (hour < 8)
            {
                return TradingSession.Asian;
            }

            if (hour < 13)
            {
                return TradingSession.London;
            }

            if (hour < 17)
            {
                return TradingSession.Overlap;
            }

            if (hour < 22)
            {
                return TradingSession.NewYork;
            }

            return TradingSession.Night;
        }

        public double GetPositionSizeMultiplier(TradingSession session)
        {
            return _sizeMultipliers.TryGetValue(session, out var multiplier) ? multiplier : 1.0;
        }

        public double GetPositionSizeMultiplier(DateTime timeUtc)
        {
            var session = GetCurrentSession(timeUtc);
            return GetPositionSizeMultiplier(session);
        }
    }
}
