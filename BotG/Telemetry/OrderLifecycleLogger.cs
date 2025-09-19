using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BotG.Telemetry
{
    public sealed class OrderLifecycleLogger
    {
        private readonly string _file;
        private static readonly object _lock = new object();
        private static readonly string _hdr =
"ts_iso,epoch_ms,phase,order_id,side,action,type,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled,sl,tp,price_exec,theoretical_lots,theoretical_units,requestedVolume,filledSize,slippage,brokerMsg,client_order_id,host,session";

        public OrderLifecycleLogger(string filePath)
        {
            _file = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            EnsureHeader();
        }

        private void EnsureHeader()
        {
            lock (_lock)
            {
                if (!File.Exists(_file) || new FileInfo(_file).Length == 0)
                    File.WriteAllText(_file, _hdr + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string Csv(object? v)
        {
            if (v == null) return "";
            var s = v switch
            {
                double d => d.ToString("G17", CultureInfo.InvariantCulture),
                float f  => f.ToString("G9", CultureInfo.InvariantCulture),
                DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
                _ => v.ToString()
            } ?? "";
            return (s.Contains(",") || s.Contains("\"")) ? $"\"{s.Replace("\"","\"\"")}\"" : s;
        }

        public void LogV3(
            string phase, string orderId, string side,
            double priceRequested, double? priceFilled,
            double? stopLoss, double? takeProfit,
            string status, string reason,
            long? latencyMs, double? sizeRequested, double? sizeFilled,
            double? priceExec = null, double? theoreticalLots = null, double? theoreticalUnits = null,
            double? requestedVolume = null, double? filledSize = null, double? slippage = null,
            string? brokerMsg = null, string? clientOrderId = null, string? host = null, string? session = null)
        {
            var now = DateTime.UtcNow;
            var epochMs = (long)(now - DateTime.UnixEpoch).TotalMilliseconds;

            var line = string.Join(",",
                Csv(now.ToString("o")), Csv(epochMs),
                Csv(phase), Csv(orderId), Csv(side), Csv(""), Csv(""),
                Csv(status), Csv(reason), Csv(latencyMs),
                Csv(priceRequested), Csv(priceFilled),
                Csv(sizeRequested), Csv(sizeFilled),
                Csv(stopLoss), Csv(takeProfit),
                Csv(priceExec), Csv(theoreticalLots), Csv(theoreticalUnits),
                Csv(requestedVolume), Csv(filledSize), Csv(slippage),
                Csv(brokerMsg), Csv(clientOrderId), Csv(host), Csv(session)
            );

            lock (_lock) { File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8); }
        }

        // Bridge cho code cũ còn gọi V2
        public void LogV2(string phase, string orderId, string side,
                          double priceRequested, double? priceFilled,
                          double? stopLoss, string status,
                          string reason, long? latencyMs, double? sizeRequested, double? sizeFilled)
        {
            LogV3(phase, orderId, side, priceRequested, priceFilled, stopLoss, null,
                  status, reason, latencyMs, sizeRequested, sizeFilled);
        }
    }
}