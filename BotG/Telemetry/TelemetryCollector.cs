using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Telemetry
{
    public class TelemetryCollector : IDisposable
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly Timer _timer;

        // rolling counters within window
        private long _ticksReceived;
        private long _signals;
        private long _ordersRequested;
        private long _ordersFilled;
        private long _errors;

        public TelemetryCollector(string folder, string fileName, int flushIntervalSeconds)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            EnsureHeader();
            _timer = new Timer(Flush, null, TimeSpan.FromSeconds(flushIntervalSeconds), TimeSpan.FromSeconds(flushIntervalSeconds));
        }

        private void EnsureHeader()
        {
            if (!File.Exists(_filePath))
            {
                File.AppendAllText(_filePath, "timestamp_iso,ticksPerSec,signalsLastMinute,ordersRequestedLastMinute,ordersFilledLastMinute,errorsLastMinute" + Environment.NewLine);
            }
        }

        public void IncTick() => Interlocked.Increment(ref _ticksReceived);
        public void IncSignal() => Interlocked.Increment(ref _signals);
        public void IncOrderRequested() => Interlocked.Increment(ref _ordersRequested);
        public void IncOrderFilled() => Interlocked.Increment(ref _ordersFilled);
        public void IncError() => Interlocked.Increment(ref _errors);

        private void Flush(object? state)
        {
            try
            {
                long ticks = Interlocked.Exchange(ref _ticksReceived, 0);
                long signals = Interlocked.Exchange(ref _signals, 0);
                long req = Interlocked.Exchange(ref _ordersRequested, 0);
                long filled = Interlocked.Exchange(ref _ordersFilled, 0);
                long errs = Interlocked.Exchange(ref _errors, 0);
                var ts = DateTime.UtcNow;
                // ticksPerSec approximate over flush window
                // If flush interval != 60, column name still ticksPerSec for simplicity
                double ticksPerSec = ticks / 60.0;
                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    ticksPerSec.ToString(CultureInfo.InvariantCulture),
                    signals.ToString(CultureInfo.InvariantCulture),
                    req.ToString(CultureInfo.InvariantCulture),
                    filled.ToString(CultureInfo.InvariantCulture),
                    errs.ToString(CultureInfo.InvariantCulture)
                );
                lock (_lock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch { /* swallow */ }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
