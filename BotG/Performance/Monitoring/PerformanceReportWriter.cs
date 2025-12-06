using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BotG.Performance.Monitoring
{
    public sealed class PerformanceReportWriter
    {
        private readonly string _symbol;
        private readonly string _outputDirectory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
        private readonly object _fileLock = new object();

        private DateTime _currentFileDate = DateTime.MinValue;
        private string _currentFilePath = string.Empty;

        public PerformanceReportWriter(string symbol, string outputDirectory)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine("D:\\botg\\logs", "monitoring")
                : outputDirectory;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public void Write(PerformanceMetricsSnapshot snapshot, IReadOnlyList<PerformanceAlert> alerts)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            alerts ??= Array.Empty<PerformanceAlert>();

            EnsureFile(snapshot.TimestampUtc);

            var payload = new
            {
                timestamp = snapshot.TimestampUtc,
                symbol = _symbol,
                metrics = new
                {
                    snapshot.TickRateHz,
                    snapshot.TickBaselineHz,
                    snapshot.LastSpreadPips,
                    snapshot.AverageSpreadPips,
                    snapshot.LastLatencyMs,
                    snapshot.AverageLatencyMs,
                    snapshot.MaxLatencyMs,
                    snapshot.LatencyTargetMs,
                    snapshot.RejectRate,
                    snapshot.FillCount,
                    snapshot.RejectCount,
                    snapshot.IsAligned,
                    snapshot.AntiRepaintSafe,
                    warmup = snapshot.Warmup.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new
                        {
                            kvp.Value.CompletedBars,
                            kvp.Value.RequiredBars,
                            ratio = kvp.Value.CompletionRatio
                        })
                },
                alerts = alerts.Select(a => new
                {
                    a.Metric,
                    severity = a.Severity.ToString(),
                    a.Message,
                    a.Recommendation
                })
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            lock (_fileLock)
            {
                File.AppendAllText(_currentFilePath, json + Environment.NewLine, _utf8NoBom);
            }
        }

        private void EnsureFile(DateTime timestampUtc)
        {
            var date = timestampUtc.Date;
            if (date == _currentFileDate && File.Exists(_currentFilePath))
            {
                return;
            }

            Directory.CreateDirectory(_outputDirectory);
            var fileName = $"monitoring_{_symbol}_{date:yyyyMMdd}.jsonl";
            _currentFilePath = Path.Combine(_outputDirectory, fileName);
            _currentFileDate = date;
            if (!File.Exists(_currentFilePath))
            {
                File.WriteAllText(_currentFilePath, string.Empty, _utf8NoBom);
            }
        }
    }
}
