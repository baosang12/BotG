using System;
using System.Collections.Concurrent;
using System.Globalization;
using Connectivity;

namespace Telemetry
{
    public sealed class OrderQuoteEnvelope
    {
        public string? Symbol { get; set; }
        public QuoteSnapshot? Request { get; set; }
        public QuoteSnapshot? Fill { get; set; }
        public DateTime? RequestServerTime { get; set; }
        public DateTime? FillServerTime { get; set; }
        public string? SourceServer { get; set; }

        public sealed class QuoteSnapshot
        {
            public double? Bid { get; set; }
            public double? Ask { get; set; }
            public double? SpreadPips { get; set; }
            public DateTime? TimestampUtc { get; set; }
            public string? SourceServer { get; set; }
        }
    }

    public sealed class OrderQuoteTelemetry : IDisposable
    {
        private readonly IMarketDataProvider _provider;
        private readonly ConcurrentDictionary<string, QuoteCacheEntry> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private sealed class QuoteCacheEntry
        {
            public double Bid;
            public double Ask;
            public DateTime TimestampUtc;
            public double PipSize;
            public string SourceServer = string.Empty;
        }

        public OrderQuoteTelemetry(IMarketDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _provider.OnQuote += OnQuote;
        }

        public void TrackSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            _provider.Subscribe(symbol);
        }

        public bool TryGetQuote(string symbol, out OrderQuoteEnvelope.QuoteSnapshot snapshot)
        {
            snapshot = default!;
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            if (_quotes.TryGetValue(symbol, out var entry))
            {
                snapshot = new OrderQuoteEnvelope.QuoteSnapshot
                {
                    Bid = entry.Bid,
                    Ask = entry.Ask,
                    SpreadPips = ComputeSpreadPips(entry.Bid, entry.Ask, entry.PipSize),
                    TimestampUtc = entry.TimestampUtc,
                    SourceServer = entry.SourceServer
                };
                return true;
            }
            return false;
        }

        public OrderQuoteEnvelope.QuoteSnapshot? Capture(string symbol)
        {
            return TryGetQuote(symbol, out var snap) ? snap : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _provider.OnQuote -= OnQuote;
            _disposed = true;
        }

        private void OnQuote(Quote quote)
        {
            if (string.IsNullOrWhiteSpace(quote.Symbol)) return;
            var entry = new QuoteCacheEntry
            {
                Bid = quote.Bid,
                Ask = quote.Ask,
                TimestampUtc = quote.TimestampUtc,
                PipSize = GuessPipSize(quote.Symbol, quote.Bid, quote.Ask),
                SourceServer = _provider.Server
            };
            _quotes[quote.Symbol] = entry;
        }

        internal static double ComputeSpreadPips(double bid, double ask, double pipSize)
        {
            if (!double.IsFinite(bid) || !double.IsFinite(ask)) return double.NaN;
            if (pipSize <= 0) pipSize = GuessPipSize(null, bid, ask);
            if (pipSize <= 0) return double.NaN;
            return (ask - bid) / pipSize;
        }

        internal static double GuessPipSize(string? symbol, double bid, double ask)
        {
            if (!string.IsNullOrWhiteSpace(symbol) && symbol.EndsWith("JPY", StringComparison.OrdinalIgnoreCase))
            {
                return 0.01;
            }

            var diff = Math.Abs(ask - bid);
            if (diff > 0)
            {
                var magnitude = Math.Pow(10, Math.Floor(Math.Log10(diff)));
                if (magnitude > 0)
                {
                    return magnitude;
                }
            }

            var decimals = Math.Max(CountDecimals(bid), CountDecimals(ask));
            return decimals switch
            {
                >= 5 => 0.00001,
                4 => 0.0001,
                3 => 0.001,
                2 => 0.01,
                1 => 0.1,
                _ => 0.0001,
            };
        }

        private static int CountDecimals(double value)
        {
            if (!double.IsFinite(value)) return 0;
            var text = value.ToString("G17", CultureInfo.InvariantCulture);
            var idx = text.IndexOf('.');
            if (idx < 0) idx = text.IndexOf(',');
            if (idx < 0) return 0;
            return text.Length - idx - 1;
        }
    }
}
