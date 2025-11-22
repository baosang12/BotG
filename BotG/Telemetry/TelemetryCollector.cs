using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Telemetry
{
    public class TelemetryCollector : IDisposable
    {
    private readonly string _filePath;

        // rolling counters within window
        private long _ticksReceived;
        private long _signals;
        private long _ordersRequested;
        private long _ordersFilled;
        private long _errors;

        public TimeSpan FlushInterval { get; }

        public TelemetryCollector(string folder, string fileName, int flushIntervalSeconds)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            EnsureHeader();
            int interval = flushIntervalSeconds > 0 ? flushIntervalSeconds : 60;
            FlushInterval = TimeSpan.FromSeconds(interval);
        }

        private void EnsureHeader()
        {
            if (!File.Exists(_filePath))
            {
                File.AppendAllText(_filePath, "timestamp_iso,ticksPerSec,signalsLastMinute,ordersRequestedLastMinute,ordersFilledLastMinute,errorsLastMinute,memoryMB,gen0,gen1,gen2" + Environment.NewLine);
            }
        }

        public void IncTick() => Interlocked.Increment(ref _ticksReceived);
        public void IncSignal() => Interlocked.Increment(ref _signals);
        public void IncOrderRequested() => Interlocked.Increment(ref _ordersRequested);
        public void IncOrderFilled() => Interlocked.Increment(ref _ordersFilled);
        public void IncError() => Interlocked.Increment(ref _errors);

        internal void FlushOnMainThread()
        {
            FlushCore();
        }

        private void FlushCore()
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
                
                // EMERGENCY: Capture memory metrics for profiling
                long memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                
                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    ticksPerSec.ToString(CultureInfo.InvariantCulture),
                    signals.ToString(CultureInfo.InvariantCulture),
                    req.ToString(CultureInfo.InvariantCulture),
                    filled.ToString(CultureInfo.InvariantCulture),
                    errs.ToString(CultureInfo.InvariantCulture),
                    memoryMB.ToString(CultureInfo.InvariantCulture),
                    gen0.ToString(CultureInfo.InvariantCulture),
                    gen1.ToString(CultureInfo.InvariantCulture),
                    gen2.ToString(CultureInfo.InvariantCulture)
                );
                // EMERGENCY: Use async IO to avoid blocking telemetry pipeline
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await File.AppendAllTextAsync(_filePath, line + Environment.NewLine).ConfigureAwait(false);
                    }
                    catch { /* swallow */ }
                });
            }
            catch { /* swallow */ }
        }

        public void Dispose()
        {
            // no-op: timer được quản lý bởi MainThreadTimer ở cấp Robot
        }
    }
}
