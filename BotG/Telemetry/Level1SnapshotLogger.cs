using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Connectivity;

namespace Telemetry
{
    public sealed class Level1SnapshotLogger
    {
        private readonly string _filePath;
        private TimeSpan _minInterval;
        private readonly ConcurrentDictionary<string, DateTime> _lastWrite = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private IMarketDataProvider? _provider;

        public Level1SnapshotLogger(string folder, string fileName = "l1_snapshots.csv", TimeSpan? minInterval = null)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            _minInterval = minInterval ?? TimeSpan.FromMilliseconds(150);
            EnsureHeader();
        }

        public void Attach(IMarketDataProvider provider, int hz)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (hz <= 0) hz = 1;
            if (hz > 50) hz = 50;
            _minInterval = TimeSpan.FromSeconds(1.0 / hz);

            if (_provider != null)
            {
                try { _provider.OnQuote -= OnQuote; } catch { }
            }

            _provider = provider;
            _provider.OnQuote += OnQuote;
        }

        private void EnsureHeader()
        {
            if (!File.Exists(_filePath))
            {
                File.AppendAllText(_filePath, "timestamp_utc,symbol,bid,ask,spread_pips,source_server" + Environment.NewLine);
            }
        }

        private void OnQuote(Quote quote)
        {
            try
            {
                TryWrite(quote, _provider?.Server ?? string.Empty);
            }
            catch
            {
                // swallow to keep feed resilient
            }
        }

        private void TryWrite(Quote quote, string sourceServer)
        {
            double pipSize = OrderQuoteTelemetry.GuessPipSize(quote.Symbol, quote.Bid, quote.Ask);
            TryWrite(quote, pipSize, sourceServer);
        }

        public void TryWrite(Connectivity.Quote quote, double pipSize, string sourceServer)
        {
            var now = DateTime.UtcNow;
            if (_lastWrite.TryGetValue(quote.Symbol, out var last) && now - last < _minInterval)
            {
                return;
            }

            _lastWrite[quote.Symbol] = now;
            double spreadPips = 0.0;
            if (pipSize > 0)
            {
                spreadPips = (quote.Ask - quote.Bid) / pipSize;
            }

            var line = string.Join(",",
                now.ToString("o", CultureInfo.InvariantCulture),
                Csv(quote.Symbol),
                quote.Bid.ToString(CultureInfo.InvariantCulture),
                quote.Ask.ToString(CultureInfo.InvariantCulture),
                spreadPips.ToString(CultureInfo.InvariantCulture),
                Csv(sourceServer)
            );

            lock (_lock)
            {
                CsvUtils.SafeAppendCsv(_filePath, string.Empty, line);
            }
        }

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"'))
            {
                return '"' + value.Replace("\"", "\"\"") + '"';
            }
            return value;
        }
    }
}
