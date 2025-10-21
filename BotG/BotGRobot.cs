using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using cAlgo.API;
using Telemetry;
using Connectivity;

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
    private bool _telemetrySampleLogged;

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
        Timer.Start(TimeSpan.FromSeconds(1));
        Print("[TLM] Timer started 1s; Symbol={0}", Symbol?.Name ?? "NULL");
        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
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
                    writer.WriteLine("timestamp_iso,symbol,bid,ask,tick_rate");
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
                if (string.IsNullOrEmpty(header))
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                    using (var writer = new StreamWriter(stream, _utf8NoBom, 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine("timestamp_iso,symbol,bid,ask,tick_rate");
                    }
                    Print("[TLM] Header repaired at {0}", _telemetryPath);
                }
            }
        }
        catch (Exception ex)
        {
            Print("[TLM] Failed to verify telemetry header: " + ex.Message);
        }
    }

}
