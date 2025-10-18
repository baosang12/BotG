using System;
using System.Collections.Generic;
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

        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
        try { _connector?.TickPump?.Pump(); } catch { }
        // Lightweight counter for runtime health
        try { TelemetryContext.Collector?.IncTick(); } catch { }
    }

}
