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
            if (!File.Exists(_filePath) || new FileInfo(_filePath).Length == 0)
            {
                var header = string.Join(",",
                    "phase","timestamp_iso","epoch_ms","order_id","intended_price","stop_loss","exec_price","theoretical_lots","theoretical_units","requested_volume","filled_size","slippage_points","broker_msg",
                    "client_order_id","side","action","type","status","reason","latency_ms","price_requested","price_filled","size_requested","size_filled","take_profit","requested_units","level","risk_R_usd","session","host",
                    "timestamp_request","timestamp_ack","timestamp_fill"
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

        public void LogV2(string phase, string orderId, string? clientOrderId, string? side, string? action, string? type,
            double? intendedPrice, double? stopLoss, double? execPrice, double? theoreticalLots, double? theoreticalUnits,
            double? requestedVolume, double? filledSize, string? status, string? reason, string? session,
            double? takeProfit = null, double? requestedUnits = null, int? level = null, double? riskRUsd = null,
            double? latencyMs = null, double? priceRequested = null, double? priceFilled = null)
        {
            try
            {
                var ts = DateTime.UtcNow;
                var epoch = new DateTimeOffset(ts).ToUnixTimeMilliseconds();
                double? slippage = (execPrice.HasValue && intendedPrice.HasValue) ? execPrice.Value - intendedPrice.Value : (double?)null;

                // latency tracking based on first REQUEST time
                long? internalLatencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    _requestEpochMs[orderId] = epoch;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (_requestEpochMs.TryGetValue(orderId, out var reqEpoch))
                    {
                        internalLatencyMs = epoch - reqEpoch;
                    }
                }

                var host = Environment.MachineName;
                var line = string.Join(",",
                    EscapeCsv(phase), EscapeCsv(ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")), epoch, EscapeCsv(orderId),
                    intendedPrice?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    stopLoss?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    execPrice?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    theoreticalLots?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    theoreticalUnits?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
                    requestedVolume?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    filledSize?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    slippage?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    "", // broker_msg placeholder
                    EscapeCsv(clientOrderId), EscapeCsv(side), EscapeCsv(action), EscapeCsv(type), EscapeCsv(status), EscapeCsv(reason),
                    (latencyMs ?? internalLatencyMs)?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
                    priceRequested?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    priceFilled?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    requestedVolume?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    filledSize?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    takeProfit?.ToString("F5", CultureInfo.InvariantCulture) ?? "",
                    requestedUnits?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
                    level?.ToString() ?? "",
                    riskRUsd?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                    EscapeCsv(session), EscapeCsv(host),
                    "", "", "" // timestamp_request/ack/fill placeholders
                );
                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch { /* swallow for safety */ }
        }

        private static string EscapeCsv(string? s)
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