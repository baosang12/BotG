using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BotG.Preflight
{
    public class PreflightLiveFreshness
    {
        private readonly IL1TickSource _tickSource;
        private readonly Func<DateTime> _serverTime;
        private readonly double _thresholdSec;
        private readonly string _fallbackCsvPath;

        public PreflightLiveFreshness(
            IL1TickSource tickSource,
            Func<DateTime> serverTime,
            double thresholdSec = 5.0,
            string fallbackCsvPath = "D:\\botg\\logs\\preflight\\l1_sample.csv")
        {
            _tickSource = tickSource;
            _serverTime = serverTime;
            _thresholdSec = thresholdSec;
            _fallbackCsvPath = fallbackCsvPath;
        }

        public async Task<PreflightResult> CheckAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            bool gotLiveTick = await _tickSource.WaitForNextTickAsync(TimeSpan.FromSeconds(3), ct);

            string source;
            double ageSec;

            if (gotLiveTick && _tickSource.LastTickUtc.HasValue)
            {
                source = "live";
                ageSec = (_serverTime() - _tickSource.LastTickUtc.Value).TotalSeconds;
            }
            else
            {
                // Fallback to l1_sample.csv only
                source = "l1_sample.csv";
                ageSec = TryReadAgeFromSampleCsv(_fallbackCsvPath);
            }

            bool ok = ageSec <= _thresholdSec;

            return new PreflightResult
            {
                Ok = ok,
                Source = source,
                LastAgeSec = ageSec,
                Timestamp = _serverTime(),
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        private double TryReadAgeFromSampleCsv(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return 999.0; // No fallback file

                var lines = File.ReadAllLines(path);
                if (lines.Length < 2)
                    return 999.0; // No data

                // Expected CSV format: timestamp,bid,ask,volume
                // Last line is most recent tick
                var lastLine = lines[^1];
                var parts = lastLine.Split(',');
                if (parts.Length < 1)
                    return 999.0;

                // Parse timestamp (ISO 8601 or similar)
                if (DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var tickTime))
                {
                    return (_serverTime() - tickTime).TotalSeconds;
                }

                return 999.0;
            }
            catch
            {
                return 999.0;
            }
        }

        public void WriteResultJson(PreflightResult result, string outputPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(new
                {
                    ok = result.Ok,
                    source = result.Source,
                    last_age_sec = Math.Round(result.LastAgeSec, 1),
                    ts = result.Timestamp.ToString("o"),
                    elapsed_ms = result.ElapsedMs
                }, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(outputPath, json);
            }
            catch
            {
                // Silent failure - preflight is non-critical for logging
            }
        }
    }

    public class PreflightResult
    {
        public bool Ok { get; set; }
        public string Source { get; set; } = string.Empty;
        public double LastAgeSec { get; set; }
        public DateTime Timestamp { get; set; }
        public long ElapsedMs { get; set; }
    }
}
