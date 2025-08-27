using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Telemetry
{
    public class TradeCloseLogger
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        public TradeCloseLogger(string folder, string fileName = "trade_closes.log")
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
        }

        public void LogClose(object payload)
        {
            try
            {
                var ts = DateTime.UtcNow;
                var wrapper = new { timestamp_iso = ts.ToString("o", CultureInfo.InvariantCulture), payload };
                var line = JsonSerializer.Serialize(wrapper);
                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
