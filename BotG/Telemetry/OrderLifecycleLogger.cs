using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Telemetry
{
    public class OrderLifecycleLogger
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _requestEpochMs = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public OrderLifecycleLogger(string folder, string fileName)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            EnsureHeader();
        }

        private void EnsureHeader()
        {
            if (!File.Exists(_filePath))
            {
                // Backward-compatible base columns + appended v2 fields to satisfy analytics
                var header = string.Join(",",
                    "phase","timestamp_iso","epoch_ms","orderId","intendedPrice","stopLoss","execPrice","theoretical_lots","theoretical_units","requestedVolume","filledSize","slippage","brokerMsg",
                    // v2 appended fields (presence required by analyzer)
                    "client_order_id","side","action","type","status","reason","latency_ms","price_requested","price_filled","size_requested","size_filled","session","host"
                );
                File.AppendAllText(_filePath, header + Environment.NewLine);
            }
        }

        // Legacy logging API (kept). New fields will be auto-derived/left blank.
    public void Log(string phase, string orderId, double? intendedPrice, double? stopLoss, double? execPrice, double? theoreticalLots, double? theoreticalUnits, double? requestedVolume, double? filledSize, string? brokerMsg = null)
        {
            LogV2(
                phase: phase,
                orderId: orderId,
                clientOrderId: orderId,
                side: null,
                action: null,
                type: null,
                intendedPrice: intendedPrice,
                stopLoss: stopLoss,
                execPrice: execPrice,
                theoreticalLots: theoreticalLots,
                theoreticalUnits: theoreticalUnits,
                requestedVolume: requestedVolume,
                filledSize: filledSize,
                status: phase,
                reason: brokerMsg,
                session: null
            );
        }

        // New richer API
        public void LogV2(string phase, string orderId, string? clientOrderId, string? side, string? action, string? type,
            double? intendedPrice, double? stopLoss, double? execPrice, double? theoreticalLots, double? theoreticalUnits,
            double? requestedVolume, double? filledSize, string? status, string? reason, string? session)
        {
            try
            {
                var ts = DateTime.UtcNow;
                var epoch = new DateTimeOffset(ts).ToUnixTimeMilliseconds();
                double? slippage = (execPrice.HasValue && intendedPrice.HasValue) ? execPrice.Value - intendedPrice.Value : (double?)null;

                // latency tracking based on first REQUEST time
                long? latencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    _requestEpochMs[orderId] = epoch;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (_requestEpochMs.TryGetValue(orderId, out var reqEpoch))
                    {
                        latencyMs = epoch - reqEpoch;
                    }
                }

                var host = Environment.MachineName;
                var line = string.Join(",",
                    phase,
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    epoch.ToString(CultureInfo.InvariantCulture),
                    Escape(orderId),
                    F(intendedPrice),
                    F(stopLoss),
                    F(execPrice),
                    F(theoreticalLots),
                    F(theoreticalUnits),
                    F(requestedVolume),
                    F(filledSize),
                    F(slippage),
                    Escape(reason), // keep brokerMsg slot for backward compatibility
                    Escape(clientOrderId),
                    Escape(side),
                    Escape(action),
                    Escape(type),
                    Escape(string.IsNullOrEmpty(status) ? phase : status),
                    Escape(reason),
                    latencyMs.HasValue ? latencyMs.Value.ToString(CultureInfo.InvariantCulture) : "",
                    F(intendedPrice),
                    F(execPrice),
                    F(requestedVolume),
                    F(filledSize),
                    Escape(session),
                    Escape(host)
                );
                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch { /* swallow for safety */ }
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
    private static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"'))
            {
                return '"' + s.Replace("\"", "\"\"") + '"';
            }
            return s;
        }
    }
}
