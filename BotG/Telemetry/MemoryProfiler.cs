using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Telemetry
{
    /// <summary>
    /// Dedicated memory profiling persister for detailed GC and process memory tracking
    /// Separate from general telemetry for focused analysis
    /// </summary>
    public class MemoryProfiler
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly Process _currentProcess;

        // Thresholds for alerts
        private readonly long _heapSizeLimitMB;
        private readonly long _processSizeLimitMB;
        private readonly int _gen2CollectionThreshold;

        // Tracking
        private int _lastGen2Count = 0;
        private long _peakHeapSizeMB = 0;
        private long _peakProcessSizeMB = 0;
        private DateTime _lastAlert = DateTime.MinValue;
        private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(5);

        public MemoryProfiler(string folder, string fileName, long heapLimitMB = 512, long processLimitMB = 1024, int gen2Threshold = 10)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            _currentProcess = Process.GetCurrentProcess();
            _heapSizeLimitMB = heapLimitMB;
            _processSizeLimitMB = processLimitMB;
            _gen2CollectionThreshold = gen2Threshold;

            EnsureHeader();
        }

        private void EnsureHeader()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    var header = "timestamp_utc,heap_mb,process_mb,gen0,gen1,gen2,gen2_delta," +
                                 "total_memory_mb,working_set_mb,private_bytes_mb,virtual_memory_mb," +
                                 "heap_alert,process_alert,gen2_alert" + Environment.NewLine;
                    File.WriteAllText(_filePath, header);
                }
            }
        }

        /// <summary>
        /// Capture detailed memory snapshot
        /// </summary>
        public MemorySnapshot CaptureSnapshot()
        {
            try
            {
                _currentProcess.Refresh();

                var snapshot = new MemorySnapshot
                {
                    Timestamp = DateTime.UtcNow,

                    // GC Heap metrics
                    HeapSizeMB = GC.GetTotalMemory(false) / (1024 * 1024),
                    Gen0Collections = GC.CollectionCount(0),
                    Gen1Collections = GC.CollectionCount(1),
                    Gen2Collections = GC.CollectionCount(2),
                    Gen2Delta = GC.CollectionCount(2) - _lastGen2Count,

                    // Process metrics
                    WorkingSetMB = _currentProcess.WorkingSet64 / (1024 * 1024),
                    PrivateBytesMB = _currentProcess.PrivateMemorySize64 / (1024 * 1024),
                    VirtualMemoryMB = _currentProcess.VirtualMemorySize64 / (1024 * 1024),
                    TotalMemoryMB = (_currentProcess.WorkingSet64 + _currentProcess.PrivateMemorySize64) / (1024 * 1024)
                };

                // Update tracking
                _lastGen2Count = snapshot.Gen2Collections;
                if (snapshot.HeapSizeMB > _peakHeapSizeMB)
                    _peakHeapSizeMB = snapshot.HeapSizeMB;
                if (snapshot.PrivateBytesMB > _peakProcessSizeMB)
                    _peakProcessSizeMB = snapshot.PrivateBytesMB;

                // Check thresholds
                snapshot.HeapAlert = snapshot.HeapSizeMB > _heapSizeLimitMB;
                snapshot.ProcessAlert = snapshot.PrivateBytesMB > _processSizeLimitMB;
                snapshot.Gen2Alert = snapshot.Gen2Delta > _gen2CollectionThreshold;

                return snapshot;
            }
            catch
            {
                return new MemorySnapshot { Timestamp = DateTime.UtcNow };
            }
        }

        /// <summary>
        /// Persist memory snapshot to CSV
        /// </summary>
        public void Persist(MemorySnapshot snapshot)
        {
            if (snapshot == null) return;

            var line = string.Join(",",
                snapshot.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                snapshot.HeapSizeMB.ToString(CultureInfo.InvariantCulture),
                snapshot.PrivateBytesMB.ToString(CultureInfo.InvariantCulture),
                snapshot.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                snapshot.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                snapshot.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                snapshot.Gen2Delta.ToString(CultureInfo.InvariantCulture),
                snapshot.TotalMemoryMB.ToString(CultureInfo.InvariantCulture),
                snapshot.WorkingSetMB.ToString(CultureInfo.InvariantCulture),
                snapshot.PrivateBytesMB.ToString(CultureInfo.InvariantCulture),
                snapshot.VirtualMemoryMB.ToString(CultureInfo.InvariantCulture),
                snapshot.HeapAlert.ToString(),
                snapshot.ProcessAlert.ToString(),
                snapshot.Gen2Alert.ToString()
            );

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);

                    // Log alerts
                    if ((snapshot.HeapAlert || snapshot.ProcessAlert || snapshot.Gen2Alert) &&
                        (DateTime.UtcNow - _lastAlert) > _alertCooldown)
                    {
                        LogAlert(snapshot);
                        _lastAlert = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            }
        }

        /// <summary>
        /// Log memory alert to separate file
        /// </summary>
        private void LogAlert(MemorySnapshot snapshot)
        {
            try
            {
                var alertLog = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "memory_alerts.log");
                var message = $"[{snapshot.Timestamp:o}] MEMORY ALERT:{Environment.NewLine}" +
                              $"  Heap: {snapshot.HeapSizeMB}MB (limit: {_heapSizeLimitMB}MB) - {(snapshot.HeapAlert ? "EXCEEDED" : "OK")}{Environment.NewLine}" +
                              $"  Process: {snapshot.PrivateBytesMB}MB (limit: {_processSizeLimitMB}MB) - {(snapshot.ProcessAlert ? "EXCEEDED" : "OK")}{Environment.NewLine}" +
                              $"  Gen2: {snapshot.Gen2Delta} collections (threshold: {_gen2CollectionThreshold}) - {(snapshot.Gen2Alert ? "EXCEEDED" : "OK")}{Environment.NewLine}" +
                              $"  Peak Heap: {_peakHeapSizeMB}MB, Peak Process: {_peakProcessSizeMB}MB{Environment.NewLine}{Environment.NewLine}";

                File.AppendAllText(alertLog, message);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        /// <summary>
        /// Log errors
        /// </summary>
        private void LogError(Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "memory_profiler_errors.log");
                var message = $"[{DateTime.UtcNow:o}] ERROR: {ex}{Environment.NewLine}";
                File.AppendAllText(logPath, message);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        /// <summary>
        /// Get current memory status
        /// </summary>
        public (long heapMB, long processMB, int gen2, bool anyAlert) GetStatus()
        {
            var snapshot = CaptureSnapshot();
            return (snapshot.HeapSizeMB, snapshot.PrivateBytesMB, snapshot.Gen2Collections,
                    snapshot.HeapAlert || snapshot.ProcessAlert || snapshot.Gen2Alert);
        }

        /// <summary>
        /// Force garbage collection (use sparingly!)
        /// </summary>
        public void ForceGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>
    /// Memory snapshot data structure
    /// </summary>
    public class MemorySnapshot
    {
        public DateTime Timestamp { get; set; }

        // GC metrics
        public long HeapSizeMB { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public int Gen2Delta { get; set; }

        // Process metrics
        public long TotalMemoryMB { get; set; }
        public long WorkingSetMB { get; set; }
        public long PrivateBytesMB { get; set; }
        public long VirtualMemoryMB { get; set; }

        // Alerts
        public bool HeapAlert { get; set; }
        public bool ProcessAlert { get; set; }
        public bool Gen2Alert { get; set; }
    }
}
