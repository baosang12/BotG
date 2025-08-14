using System;

namespace Telemetry
{
    public static class TelemetryContext
    {
        private static readonly object _lock = new object();
        private static bool _initialized;
        public static TelemetryConfig Config { get; private set; } = new TelemetryConfig();
        public static OrderLifecycleLogger? OrderLogger { get; private set; }
        public static RiskSnapshotPersister? RiskPersister { get; private set; }
        public static TelemetryCollector? Collector { get; private set; }

        public static void InitOnce(TelemetryConfig? cfg = null)
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                Config = cfg ?? TelemetryConfig.Load();
                OrderLogger = new OrderLifecycleLogger(Config.LogPath, Config.OrderLogFile);
                RiskPersister = new RiskSnapshotPersister(Config.LogPath, Config.RiskSnapshotFile);
                Collector = new TelemetryCollector(Config.LogPath, Config.TelemetryFile, Config.FlushIntervalSeconds);
                _initialized = true;
            }
        }
    }
}
