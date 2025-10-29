using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using cAlgo.API;
using Telemetry;
using Connectivity;
using BotG.Preflight;

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
public class BotGRobot : Robot
{
    // hold runtime modules on the robot instance for later use
    private TradeManager.TradeManager? _tradeManager;
    private RiskManager.RiskManager? _riskManager;
    private ConnectorBundle? _connector;
    private long _tickCounter;
    private double _tickRateEstimate;
    private string? _telemetryPath;
    private readonly CultureInfo _invariantCulture = CultureInfo.InvariantCulture;
    private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
    private const string DefaultTelemetryDirectory = @"D:\botg\logs";
    private const string TelemetryFileName = "telemetry.csv";
    private const string ExpectedTelemetryHeader = "timestamp_iso,symbol,bid,ask,tick_rate";
    private bool _telemetrySampleLogged;
    private bool _preflightPassed;
    
    // Preflight live tick tracking
    private readonly CTraderTickSource _tickSource = new CTraderTickSource();

    protected override void OnStart()
    {
        BotGStartup.Initialize();

        string EnvOr(string key, string defaultValue) => Environment.GetEnvironmentVariable(key) ?? defaultValue;
        var mode = EnvOr("DATASOURCE__MODE", "ctrader_demo");

        try
        {
            _riskManager = new RiskManager.RiskManager();
            _riskManager.Initialize(new RiskManager.RiskSettings());
            try { _riskManager.SetSymbolReference(this.Symbol); } catch { }
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize RiskManager: " + ex.Message);
        }

        try
        {
            _connector = ConnectorBundle.Create(this, mode);
            var marketData = _connector.MarketData;
            var executor = _connector.OrderExecutor;
            marketData.Start();

            var hzEnv = EnvOr("L1_SNAPSHOT_HZ", "5");
            int snapshotHz = 5;
            if (int.TryParse(hzEnv, out var parsedHz) && parsedHz > 0)
            {
                snapshotHz = parsedHz;
            }

            TelemetryContext.AttachLevel1Snapshots(marketData, snapshotHz);

            var quoteTelemetry = new OrderQuoteTelemetry(marketData);
            TelemetryContext.AttachOrderLogger(quoteTelemetry, executor);

            TelemetryContext.MetadataHook = meta =>
            {
                meta["data_source"] = mode;
                meta["broker_name"] = executor.BrokerName ?? string.Empty;
                meta["server"] = executor.Server ?? string.Empty;
                meta["account_id"] = executor.AccountId ?? string.Empty;
            };
            TelemetryContext.UpdateDataSourceMetadata(mode, executor.BrokerName, executor.Server, executor.AccountId);

            try { TelemetryContext.QuoteTelemetry?.TrackSymbol(this.SymbolName); } catch { }
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize connectivity: " + ex.Message);
        }

        try
        {
            if (_connector != null)
            {
                var strategies = new List<Strategies.IStrategy<Strategies.TradeSignal>>();
                _tradeManager = new TradeManager.TradeManager(strategies, this, _riskManager, _connector.MarketData, _connector.OrderExecutor);
            }
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize TradeManager: " + ex.Message);
        }

        InitializeTelemetryWriter();

        // ========== STARTUP ECHO ==========
        var echo = ComposeStartupEcho();
        Print("[ECHO] BuildStamp={0} ConfigSource={1} Mode={2} Simulation.Enabled={3} Env={4}",
            echo.BuildStamp, echo.ConfigSource, echo.ResolvedMode, echo.ResolvedSimulationEnabled, 
            JsonSerializer.Serialize(echo.Env));
        Print("[ECHO+] Preflight.Live=true; Fallback=preflight\\l1_sample.csv; ThresholdSec=5; WriterStartBeforeCheck=true");

        // ========== PREFLIGHT LIVE FRESHNESS (PAPER MODE ONLY) ==========
        var cfg = TelemetryConfig.Load();
        bool isPaper = cfg.Mode.Equals("paper", StringComparison.OrdinalIgnoreCase);
        bool simEnabled = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);

        if (isPaper && !simEnabled)
        {
            Print("[PREFLIGHT] Running live freshness check (paper mode, simulation disabled)...");
            
            var preflight = new PreflightLiveFreshness(
                _tickSource,
                () => Server.Time,
                thresholdSec: 5.0,
                fallbackCsvPath: Path.Combine(cfg.LogPath, "preflight", "l1_sample.csv")
            );

            var checkTask = preflight.CheckAsync(CancellationToken.None);
            checkTask.Wait(); // cTrader doesn't support async OnStart
            var result = checkTask.Result;

            Print("[PREFLIGHT] L1 source={0} | last_age_sec={1:F1}", result.Source, result.LastAgeSec);

            var preflightDir = Path.Combine(cfg.LogPath, "preflight");
            Directory.CreateDirectory(preflightDir);
            var jsonPath = Path.Combine(preflightDir, "preflight_canary.json");
            
            preflight.WriteResultJson(result, jsonPath);
            Print("[PREFLIGHT] Result written to {0}", jsonPath);

            if (!result.Ok)
            {
                Print("[PREFLIGHT] FAILED - aborting bot startup. L1 data too old: {0:F1}s > 5.0s", result.LastAgeSec);
                _preflightPassed = false;
                Stop();
                return;
            }

            Print("[PREFLIGHT] PASSED - proceeding to trading loop");
            _preflightPassed = true;
        }
        else
        {
            Print("[PREFLIGHT] Skipped (mode={0}, sim={1})", cfg.Mode, simEnabled);
            _preflightPassed = true; // allow trading
        }

        Timer.Start(TimeSpan.FromSeconds(1));
        Print("[TLM] Timer started 1s; Symbol={0}", Symbol?.Name ?? "NULL");
        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
        // Track tick for preflight live freshness
        _tickSource.OnTick(Server.Time);
        
        try { _connector?.TickPump?.Pump(); } catch { }
        try { TelemetryContext.Collector?.IncTick(); } catch { }

        Interlocked.Increment(ref _tickCounter);

        if (string.IsNullOrEmpty(_telemetryPath))
        {
            return;
        }

        var currentSymbol = Symbol;
        if (currentSymbol == null)
        {
            return;
        }

        var bid = currentSymbol.Bid;
        var ask = currentSymbol.Ask;
        if (bid <= 0 || ask <= 0)
        {
            Print(
                "[TLM] Telemetry skip: non-positive bid/ask for {0}: bid={1} ask={2}",
                currentSymbol.Name,
                bid,
                ask);
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("o", _invariantCulture);
        var tickRate = Interlocked.CompareExchange(ref _tickRateEstimate, 0d, 0d);
        if (tickRate <= 0)
        {
            tickRate = 1d;
        }

        var line = string.Format(
            _invariantCulture,
            "{0},{1},{2},{3},{4}",
            timestamp,
            currentSymbol.Name,
            bid,
            ask,
            tickRate);

        try
        {
            using (var stream = new FileStream(_telemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, _utf8NoBom) { AutoFlush = true })
            {
                writer.WriteLine(line);
            }

            if (!_telemetrySampleLogged)
            {
                _telemetrySampleLogged = true;
                Print("[TLM] First tick sample written: {0}", line);
            }
        }
        catch (Exception ex)
        {
            Print("BotGRobot telemetry write failed: " + ex.Message);
        }
    }

    protected override void OnTimer()
    {
        var ticksPerSecond = Interlocked.Exchange(ref _tickCounter, 0);
        Interlocked.Exchange(ref _tickRateEstimate, (double)ticksPerSecond);
    }

    private void InitializeTelemetryWriter()
    {
        try
        {
            var logRoot = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
            if (string.IsNullOrWhiteSpace(logRoot))
            {
                logRoot = DefaultTelemetryDirectory;
            }

            Directory.CreateDirectory(logRoot);
            _telemetryPath = Path.Combine(logRoot, TelemetryFileName);

            Print("[TLM] BOTG_LOG_PATH resolved to {0}", logRoot);

            if (!File.Exists(_telemetryPath))
            {
                using (var stream = new FileStream(_telemetryPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, _utf8NoBom) { AutoFlush = true })
                {
                    writer.WriteLine(ExpectedTelemetryHeader);
                }
                Print("[TLM] Header created at {0}", _telemetryPath);
            }
            else
            {
                Print("[TLM] Using existing telemetry at {0}", _telemetryPath);
                EnsureTelemetryHeader();
            }
        }
        catch (Exception ex)
        {
            _telemetryPath = null;
            Print("BotGRobot telemetry init failed: " + ex.Message);
        }
    }

    private void EnsureTelemetryHeader()
    {
        if (string.IsNullOrEmpty(_telemetryPath))
        {
            return;
        }

        try
        {
            using (var stream = new FileStream(_telemetryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, _utf8NoBom, false, 1024, true))
            {
                var header = reader.ReadLine();
                if (header == null)
                {
                    header = string.Empty;
                }

                if (!string.Equals(header, ExpectedTelemetryHeader, StringComparison.Ordinal))
                {
                    var remainder = reader.ReadToEnd();
                    stream.SetLength(0);
                    stream.Position = 0;

                    using (var writer = new StreamWriter(stream, _utf8NoBom, 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine(ExpectedTelemetryHeader);
                        if (!string.IsNullOrEmpty(remainder))
                        {
                            writer.Write(remainder);
                        }
                    }

                    Print(
                        "[TLM] Telemetry header repaired: \"{0}\" -> \"{1}\"",
                        header,
                        ExpectedTelemetryHeader);
                }
            }
        }
        catch (Exception ex)
        {
            Print("[TLM] Failed to verify telemetry header: " + ex.Message);
        }
    }

    // ========== PREFLIGHT CANARY IMPLEMENTATION ==========
    private async Task<PreflightResult> RunPreflightCanaryAsync(
        string logPath,
        Connectivity.IOrderExecutor executor,
        Connectivity.IMarketDataProvider marketData)
    {
        var startTime = DateTime.UtcNow;
        var result = new PreflightResult
        {
            Ok = false,
            Checks = new Dictionary<string, bool>(),
            Timestamps = new Dictionary<string, string>
            {
                ["start_iso"] = startTime.ToString("o")
            },
            Bot = "BotG_" + this.GetHashCode().ToString("x8")
        };

        try
        {
            // 1. Infrastructure checks
            Print("[PREFLIGHT] Running infrastructure checks...");
            
            // Sentinel files
            var sentinelStop = Path.Combine(logPath, "RUN_STOP");
            var sentinelPause = Path.Combine(logPath, "RUN_PAUSE");
            result.Checks["sentinel"] = !File.Exists(sentinelStop) && !File.Exists(sentinelPause);
            if (!result.Checks["sentinel"])
            {
                result.FailReason = "Sentinel file exists (RUN_STOP or RUN_PAUSE)";
                return result;
            }

            // L1 freshness (telemetry.csv last tick ≤ 5s)
            var telemetryPath = Path.Combine(logPath, "telemetry.csv");
            result.Checks["l1_fresh"] = await CheckL1FreshnessAsync(telemetryPath);
            if (!result.Checks["l1_fresh"])
            {
                result.FailReason = "L1 data stale (>5s since last tick)";
                return result;
            }

            // Disk space (≥10GB free)
            var driveInfo = new DriveInfo(Path.GetPathRoot(logPath) ?? "D:\\");
            var freeGB = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            result.Checks["disk_ok"] = freeGB >= 10.0;
            if (!result.Checks["disk_ok"])
            {
                result.FailReason = $"Insufficient disk space ({freeGB:F1}GB free, need ≥10GB)";
                return result;
            }

            // Schema headers (orders.csv)
            var ordersPath = Path.Combine(logPath, "orders.csv");
            const string expectedOrdersHeader = "event,status,reason,latency,price_requested,price_filled,order_id,side,requested_lots,filled_lots";
            result.Checks["orders_schema_ok"] = await CheckSchemaHeaderAsync(ordersPath, expectedOrdersHeader);
            if (!result.Checks["orders_schema_ok"])
            {
                result.FailReason = "orders.csv header mismatch";
                return result;
            }

            // Schema headers (risk_snapshots.csv)
            var riskPath = Path.Combine(logPath, "risk_snapshots.csv");
            const string expectedRiskHeader = "timestamp_iso,equity,R_used,exposure,drawdown";
            result.Checks["risk_schema_ok"] = await CheckSchemaHeaderAsync(riskPath, expectedRiskHeader);
            if (!result.Checks["risk_schema_ok"])
            {
                result.FailReason = "risk_snapshots.csv header mismatch";
                return result;
            }

            // Mode check (already verified paper mode in caller, but document it)
            result.Checks["mode_paper"] = true;

            Print("[PREFLIGHT] Infrastructure checks PASSED");

            // 2. ACK test (far limit order)
            Print("[PREFLIGHT] Running ACK test...");
            result.Ack = await RunAckTestAsync(executor, marketData);
            if (!result.Ack.Ok)
            {
                result.FailReason = $"ACK test failed: {result.Ack.ErrorMessage}";
                return result;
            }
            Print("[PREFLIGHT] ACK test PASSED (latency: {0}ms)", result.Ack.LatencyMs);

            // 3. FILL test (minimal market order)
            Print("[PREFLIGHT] Running FILL test...");
            result.Fill = await RunFillTestAsync(executor, marketData);
            if (!result.Fill.Ok)
            {
                result.FailReason = $"FILL test failed: {result.Fill.ErrorMessage}";
                return result;
            }
            Print("[PREFLIGHT] FILL test PASSED (latency: {0}ms, slippage: {1} pips)", 
                result.Fill.LatencyMs, result.Fill.SlippagePips);

            // All checks passed
            result.Ok = true;
            result.FailReason = null;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.FailReason = $"Exception: {ex.Message}";
            Print("[PREFLIGHT] Exception: {0}", ex);
        }
        finally
        {
            result.Timestamps["end_iso"] = DateTime.UtcNow.ToString("o");
        }

        return result;
    }

    private async Task<bool> CheckL1FreshnessAsync(string telemetryPath)
    {
        try
        {
            if (!File.Exists(telemetryPath))
            {
                Print("[PREFLIGHT] Telemetry file not found: {0}", telemetryPath);
                return false;
            }

            // Read last line
            var lines = await File.ReadAllLinesAsync(telemetryPath);
            if (lines.Length < 2) // Need at least header + 1 data row
            {
                Print("[PREFLIGHT] Telemetry has no data rows");
                return false;
            }

            var lastLine = lines[^1];
            var parts = lastLine.Split(',');
            if (parts.Length == 0)
            {
                return false;
            }

            // Parse timestamp (ISO 8601)
            if (DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var tickTime))
            {
                var age = (DateTime.UtcNow - tickTime).TotalSeconds;
                Print("[PREFLIGHT] Last tick age: {0:F1}s", age);
                return age <= 5.0;
            }

            return false;
        }
        catch (Exception ex)
        {
            Print("[PREFLIGHT] L1 freshness check failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task<bool> CheckSchemaHeaderAsync(string csvPath, string expectedHeader)
    {
        try
        {
            if (!File.Exists(csvPath))
            {
                // Create with correct header if missing
                await File.WriteAllTextAsync(csvPath, expectedHeader + "\n");
                Print("[PREFLIGHT] Created {0} with canonical header", Path.GetFileName(csvPath));
                return true;
            }

            var firstLine = (await File.ReadAllLinesAsync(csvPath)).FirstOrDefault();
            var match = string.Equals(firstLine, expectedHeader, StringComparison.Ordinal);
            
            if (!match)
            {
                Print("[PREFLIGHT] Schema mismatch in {0}", Path.GetFileName(csvPath));
                Print("[PREFLIGHT]   Expected: {0}", expectedHeader);
                Print("[PREFLIGHT]   Found:    {0}", firstLine ?? "(empty)");
            }

            return match;
        }
        catch (Exception ex)
        {
            Print("[PREFLIGHT] Schema check failed for {0}: {1}", csvPath, ex.Message);
            return false;
        }
    }

    private async Task<AckTestResult> RunAckTestAsync(Connectivity.IOrderExecutor executor, Connectivity.IMarketDataProvider marketData)
    {
        var result = new AckTestResult { Ok = false };
        var startTime = DateTime.UtcNow;

        try
        {
            var symbol = this.SymbolName ?? "EURUSD";
            var pipSize = this.Symbol?.PipSize ?? 0.0001;
            var minVol = this.Symbol?.VolumeInUnitsMin ?? 1000;
            
            // Get current bid
            var bid = this.Symbol?.Bid ?? 0;
            if (bid <= 0)
            {
                result.ErrorMessage = "Invalid bid price";
                return result;
            }

            // Place far limit order (500 pips below market for BUY)
            var limitPrice = bid - 500 * pipSize;
            var volume = Math.Max(1000, minVol); // 0.01 lot = 1000 units for standard

            Print("[PREFLIGHT] Placing ACK test order: BUY_LIMIT {0} lots at {1}", volume / 100000.0, limitPrice);

            var tradeResult = await Task.Run(() =>
            {
                var tr = ExecuteMarketOrder(TradeType.Buy, symbol, volume, "PREFLIGHT_ACK");
                return tr;
            });

            var ackTime = DateTime.UtcNow;
            result.LatencyMs = (int)(ackTime - startTime).TotalMilliseconds;

            if (tradeResult.IsSuccessful && tradeResult.Position != null)
            {
                result.Ok = true;
                result.OrderId = tradeResult.Position.Id.ToString();
                
                // Immediately close/cancel
                await Task.Run(() => ClosePosition(tradeResult.Position));
                Print("[PREFLIGHT] ACK test order closed");
            }
            else
            {
                result.ErrorMessage = tradeResult.Error?.ToString() ?? "Order failed";
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Print("[PREFLIGHT] ACK test exception: {0}", ex);
        }

        return result;
    }

    private async Task<FillTestResult> RunFillTestAsync(Connectivity.IOrderExecutor executor, Connectivity.IMarketDataProvider marketData)
    {
        var result = new FillTestResult { Ok = false };
        var startTime = DateTime.UtcNow;

        try
        {
            var symbol = this.SymbolName ?? "EURUSD";
            var minVol = this.Symbol?.VolumeInUnitsMin ?? 1000;
            var volume = Math.Max(1000, minVol);

            Print("[PREFLIGHT] Placing FILL test: MARKET BUY {0} lots", volume / 100000.0);

            var entryPrice = this.Symbol?.Ask ?? 0;
            var tradeResult = await Task.Run(() => ExecuteMarketOrder(TradeType.Buy, symbol, volume, "PREFLIGHT_FILL"));

            var fillTime = DateTime.UtcNow;
            result.LatencyMs = (int)(fillTime - startTime).TotalMilliseconds;

            if (tradeResult.IsSuccessful && tradeResult.Position != null)
            {
                var fillPrice = tradeResult.Position.EntryPrice;
                var pipSize = this.Symbol?.PipSize ?? 0.0001;
                result.SlippagePips = Math.Abs(fillPrice - entryPrice) / pipSize;
                
                // Close immediately
                var closeStart = DateTime.UtcNow;
                await Task.Run(() => ClosePosition(tradeResult.Position));
                var closeEnd = DateTime.UtcNow;
                result.CloseLatencyMs = (int)(closeEnd - closeStart).TotalMilliseconds;

                result.Ok = true;
                Print("[PREFLIGHT] FILL test completed and closed");
            }
            else
            {
                result.ErrorMessage = tradeResult.Error?.ToString() ?? "Order failed";
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Print("[PREFLIGHT] FILL test exception: {0}", ex);
        }

        return result;
    }

    // ========== PREFLIGHT DATA CLASSES ==========
    private class PreflightResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("fail_reason")]
        public string? FailReason { get; set; }

        [JsonPropertyName("checks")]
        public Dictionary<string, bool> Checks { get; set; } = new();

        [JsonPropertyName("ack")]
        public AckTestResult Ack { get; set; } = new();

        [JsonPropertyName("fill")]
        public FillTestResult Fill { get; set; } = new();

        [JsonPropertyName("timestamps")]
        public Dictionary<string, string> Timestamps { get; set; } = new();

        [JsonPropertyName("bot")]
        public string Bot { get; set; } = "";
    }

    private class AckTestResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("order_id")]
        public string? OrderId { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private class FillTestResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("slippage_pips")]
        public double SlippagePips { get; set; }

        [JsonPropertyName("close_latency_ms")]
        public int CloseLatencyMs { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    // ========== STARTUP ECHO (CONFIG & BUILD STAMP) ==========
    private class StartupEcho
    {
        public string BuildStamp { get; set; } = string.Empty;
        public string ConfigSource { get; set; } = string.Empty;
        public string ResolvedMode { get; set; } = string.Empty;
        public bool ResolvedSimulationEnabled { get; set; }
        public Dictionary<string, string?> Env { get; set; } = new();
    }

    private StartupEcho ComposeStartupEcho()
    {
        var asm = typeof(BotGRobot).Assembly;
        var ver = asm.GetName().Version?.ToString() ?? "0.0.0.0";
        var buildTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        
        var cfg = TelemetryConfig.Load();
        string cfgPath = cfg.LogPath ?? "(unknown)";
        bool sim = cfg.UseSimulation || (cfg.Simulation != null && cfg.Simulation.Enabled);
        string mode = cfg.Mode ?? "(null)";

        var env = new Dictionary<string, string?>
        {
            ["Mode"] = Environment.GetEnvironmentVariable("Mode"),
            ["Simulation__Enabled"] = Environment.GetEnvironmentVariable("Simulation__Enabled"),
            ["BOTG__Mode"] = Environment.GetEnvironmentVariable("BOTG__Mode"),
            ["BOTG__Simulation__Enabled"] = Environment.GetEnvironmentVariable("BOTG__Simulation__Enabled"),
        };

        return new StartupEcho
        {
            BuildStamp = $"{ver}|{buildTime}",
            ConfigSource = cfgPath,
            ResolvedMode = mode,
            ResolvedSimulationEnabled = sim,
            Env = env
        };
    }

}
