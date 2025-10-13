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
    public static ClosedTradesWriter? ClosedTrades { get; private set; }

        public static void InitOnce(TelemetryConfig? cfg = null)
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                Config = cfg ?? TelemetryConfig.Load();
        // ensure run folder and write metadata
        var runDir = RunInitializer.EnsureRunFolderAndMetadata(Config);
        // write runtime files inside runDir, but keep RiskSnapshot in base folder for continuity
        OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
        ClosedTrades = new ClosedTradesWriter(runDir);
        
        // Detect paper mode from config and setup RiskPersister with appropriate model
        bool isPaperMode = Config.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
        Func<double> getOpenPnlCallback = null; // TODO: Implement via RiskManager callback if needed
        RiskPersister = new RiskSnapshotPersister(runDir, Config.RiskSnapshotFile, isPaperMode, getOpenPnlCallback);
        
        Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
                _initialized = true;
            }
        }
    }
}
