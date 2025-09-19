using System;
using System.Net.Http;
using System.Threading.Tasks;
using BotG.Harness.Models;

namespace BotG.Harness.Data
{
    /// <summary>
    /// Handshake-only cTrader live provider for health checks and environment validation.
    /// Does not provide actual market data streaming - only verifies API connectivity.
    /// </summary>
    public class CTraderLiveProvider : IMarketDataProvider
    {
        private readonly string _baseUri;
        private readonly string _apiKey;
        private readonly string _symbol;
        private readonly string _timeframe;
        private readonly HttpClient _httpClient;

        public CTraderLiveProvider(string baseUri, string apiKey, string symbol, string timeframe)
        {
            _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _timeframe = timeframe ?? throw new ArgumentNullException(nameof(timeframe));
            
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            }
        }

        public bool SupportsLive => true;

        public bool HasMore => false;

        public DateTime CurrentTime => DateTime.UtcNow;

        public Task<bool> InitializeAsync()
        {
            return Task.FromResult(true);
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUri);
                return (int)response.StatusCode >= 200 && (int)response.StatusCode <= 299;
            }
            catch
            {
                return false;
            }
        }

        public Bar? GetNextBar(string timeframe)
        {
            throw new NotSupportedException("CTraderLiveProvider is handshake-only and does not provide market data streaming");
        }

        public void Reset()
        {
            // No-op for handshake-only provider
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}