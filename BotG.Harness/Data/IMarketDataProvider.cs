using BotG.Harness.Models;

namespace BotG.Harness.Data
{
    public interface IMarketDataProvider
    {
        bool HasMore { get; }
        DateTime CurrentTime { get; }
        Bar? GetNextBar(string timeframe);
        void Reset();
        void Dispose();
    }
}