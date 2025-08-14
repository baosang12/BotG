using System;
using System.IO;
using System.Threading.Tasks;
using DataFetcher.Models;
using DataFetcher.Interfaces;

namespace DataFetcher.Providers
{
    /// <summary>
    /// Đọc dữ liệu tick lịch sử từ file CSV, phát event TickReceived cho từng tick.
    /// </summary>
    public class HistoricalTickFetcher : ITickProvider
    {
        public event EventHandler<Tick> TickReceived;
        private bool _running;
        private Task _readTask;
        private string _csvPath;

        public HistoricalTickFetcher(string csvPath = "ticks.csv")
        {
            _csvPath = csvPath;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _readTask = Task.Run(() => ReadCsv(_csvPath));
        }

        public void Stop()
        {
            _running = false;
        }

        private void ReadCsv(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                if (!_running) break;
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                if (!DateTime.TryParse(parts[0], out var ts)) continue;
                if (!double.TryParse(parts[1], out var bid)) continue;
                if (!double.TryParse(parts[2], out var ask)) continue;
                if (!long.TryParse(parts[3], out var vol)) continue;
                var tick = new Tick
                {
                    Timestamp = ts,
                    Bid = bid,
                    Ask = ask,
                    Volume = vol
                };
                TickReceived?.Invoke(this, tick);
                Task.Delay(10).Wait(); // Giả lập realtime
            }
        }
    }
}
