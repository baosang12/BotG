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
                File.AppendAllText(_filePath, "phase,timestamp_iso,epoch_ms,orderId,intendedPrice,execPrice,requestedSize,filledSize,slippage,brokerMsg" + Environment.NewLine);
            }
        }

        public void Log(string phase, string orderId, double? intendedPrice, double? execPrice, double? requestedSize, double? filledSize, string? brokerMsg = null)
        {
            try
            {
                var ts = DateTime.UtcNow;
                var epoch = new DateTimeOffset(ts).ToUnixTimeMilliseconds();
                double? slippage = (execPrice.HasValue && intendedPrice.HasValue) ? execPrice.Value - intendedPrice.Value : (double?)null;
                var line = string.Join(",",
                    phase,
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    epoch.ToString(CultureInfo.InvariantCulture),
                    Escape(orderId),
                    F(intendedPrice),
                    F(execPrice),
                    F(requestedSize),
                    F(filledSize),
                    F(slippage),
                    Escape(brokerMsg)
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
