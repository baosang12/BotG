using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connectivity;

namespace Telemetry
{
    public static class TelemetryContext
    {
        private static readonly object _lock = new object();
        private static bool _initialized;
        public static TelemetryConfig Config { get; private set; } = new TelemetryConfig();
        public static OrderLifecycleLogger? OrderLogger { get; private set; }
        public static RiskSnapshotPersister? RiskPersister { get; set; } // A8 FIX: Changed to public set for re-initialization with callback
        public static PositionSnapshotPersister? PositionPersister { get; private set; } // A9: Position-level snapshots
        public static MemoryProfiler? MemoryProfiler { get; private set; } // A10: Memory profiling
        public static TelemetryCollector? Collector { get; private set; }
        public static ClosedTradesWriter? ClosedTrades { get; private set; }
        public static Level1SnapshotLogger? Level1Snapshots { get; private set; }
        public static OrderQuoteTelemetry? QuoteTelemetry { get; private set; }
        public static string RunFolder { get; private set; } = string.Empty;
        public static Action<JsonObject>? MetadataHook
        {
            get => _metadataHook;
            set
            {
                _metadataHook = value;
                if (value != null)
                {
                    TryApplyMetadataHookToFile(value);
                }
            }
        }

        private static Action<JsonObject>? _metadataHook;

        public static void InitOnce(TelemetryConfig? cfg = null)
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                Config = cfg ?? TelemetryConfig.Load();
                var runDir = RunInitializer.EnsureRunFolderAndMetadata(Config);
                RunFolder = runDir;
                // Write all runtime files (orders, telemetry, risk_snapshots) inside runDir for historical preservation.
                // For monitoring continuity, use hard link: D:\botg\logs\risk_snapshots.csv -> artifacts\telemetry_run_TIMESTAMP\risk_snapshots.csv
                // Auto-update link with: D:\botg\logs\update_risk_link.ps1
                OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv", null, null, null, Config);
                ClosedTrades = new ClosedTradesWriter(runDir);

                // Detect paper mode from config and setup RiskPersister with appropriate model
                bool isPaperMode = Config.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
                Func<double>? getOpenPnlCallback = null; // TODO: Implement via RiskManager callback if needed
                RiskPersister = new RiskSnapshotPersister(runDir, Config.RiskSnapshotFile, isPaperMode, getOpenPnlCallback);

                // A9: Initialize position-level snapshot persister - DISABLED (causing restart loop)
                // PositionPersister = new PositionSnapshotPersister(runDir, "position_snapshots.csv");

                // A10: Initialize memory profiler - DISABLED (causing restart loop)
                // MemoryProfiler = new MemoryProfiler(runDir, "memory_snapshots.csv", 512, 1024, 10);

                Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
                Level1Snapshots = new Level1SnapshotLogger(runDir);
                _initialized = true;
            }
        }

        public static void ResetForTesting()
        {
            lock (_lock)
            {
                DisposeIfNeeded(OrderLogger);
                DisposeIfNeeded(RiskPersister);
                DisposeIfNeeded(PositionPersister);
                DisposeIfNeeded(MemoryProfiler);
                DisposeIfNeeded(Collector);
                DisposeIfNeeded(ClosedTrades);
                DisposeIfNeeded(Level1Snapshots);
                DisposeIfNeeded(QuoteTelemetry);
                OrderLogger = null;
                RiskPersister = null;
                PositionPersister = null;
                MemoryProfiler = null;
                Collector = null;
                ClosedTrades = null;
                Level1Snapshots = null;
                QuoteTelemetry = null;
                RunFolder = string.Empty;
                _initialized = false;
            }
        }

        public static void AttachConnectivity(string mode, IMarketDataProvider marketData, IOrderExecutor executor, int level1SnapshotHz = 5)
        {
            if (!_initialized) return;
            lock (_lock)
            {
                AttachLevel1SnapshotsInternal(marketData, level1SnapshotHz);
                AttachOrderLoggerInternal(new OrderQuoteTelemetry(marketData), executor);
                UpdateDataSourceMetadata(mode, executor.BrokerName, executor.Server, executor.AccountId);
            }
        }

        public static void AttachLevel1Snapshots(IMarketDataProvider marketData, int level1SnapshotHz)
        {
            if (!_initialized) return;
            lock (_lock)
            {
                AttachLevel1SnapshotsInternal(marketData, level1SnapshotHz);
            }
        }

        public static void AttachOrderLogger(OrderQuoteTelemetry telemetry, IOrderExecutor executor)
        {
            if (!_initialized) return;
            if (telemetry == null) throw new ArgumentNullException(nameof(telemetry));
            if (executor == null) throw new ArgumentNullException(nameof(executor));
            lock (_lock)
            {
                AttachOrderLoggerInternal(telemetry, executor);
            }
        }

        public static void UpdateDataSourceMetadata(string mode, string brokerName, string server, string accountId)
        {
            if (!_initialized) return;
            try
            {
                var folder = string.IsNullOrEmpty(RunFolder) ? Config.LogPath : RunFolder;
                var writer = new RunMetadataWriter(folder);
                writer.UpsertSourceMetadata(mode, brokerName, server, accountId);
            }
            catch
            {
                // swallow to avoid impacting runtime
            }
        }

        internal static void InvokeMetadataHook(JsonObject meta)
        {
            try
            {
                _metadataHook?.Invoke(meta);
            }
            catch
            {
                // swallow customization failures
            }
        }

        private static void AttachLevel1SnapshotsInternal(IMarketDataProvider marketData, int level1SnapshotHz)
        {
            Level1Snapshots ??= new Level1SnapshotLogger(string.IsNullOrEmpty(RunFolder) ? Config.LogPath : RunFolder);
            var hz = level1SnapshotHz <= 0 ? 5 : level1SnapshotHz;
            Level1Snapshots.Attach(marketData, hz);
        }

        private static void AttachOrderLoggerInternal(OrderQuoteTelemetry telemetry, IOrderExecutor executor)
        {
            QuoteTelemetry?.Dispose();
            QuoteTelemetry = telemetry;
            var folder = string.IsNullOrEmpty(RunFolder) ? Config.LogPath : RunFolder;
            OrderLogger ??= new OrderLifecycleLogger(folder, "orders.csv", null, null, null, Config);
            OrderLogger.AttachConnectivity(QuoteTelemetry, executor);
        }

        private static void TryApplyMetadataHookToFile(Action<JsonObject> hook)
        {
            try
            {
                var folder = string.IsNullOrEmpty(RunFolder) ? Config.LogPath : RunFolder;
                if (string.IsNullOrEmpty(folder)) return;
                var path = Path.Combine(folder, "run_metadata.json");
                if (!File.Exists(path)) return;
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                hook(root);
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // swallow to avoid interfering with runtime
            }
        }

        private static void DisposeIfNeeded(object? target)
        {
            if (target is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // swallow cleanup issues in tests
                }
            }
        }
    }
}
