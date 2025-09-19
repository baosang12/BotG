using BotG.Harness.Models;

namespace BotG.Harness.Data
{
    public interface IMarketDataProvider
    {
        bool HasMore { get; }
        DateTime CurrentTime { get; }
        bool SupportsLive { get; }
        
        Bar? GetNextBar(string timeframe);
        void Reset();
        void Dispose();
        
        Task<bool> InitializeAsync();
        Task<bool> CheckHealthAsync();
    }
}