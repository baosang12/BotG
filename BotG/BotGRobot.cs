using System;
using cAlgo.API;
using Telemetry;

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
public class BotGRobot : Robot
{
    // hold runtime modules on the robot instance for later use
    private TradeManager.TradeManager _tradeManager;
    private RiskManager.RiskManager _riskManager;

    protected override void OnStart()
    {
        BotGStartup.Initialize();

        // Initialize RiskManager and wire into ExecutionModule via TradeManager convenience ctor
        try
        {
            _riskManager = new RiskManager.RiskManager();
            _riskManager.Initialize(new RiskManager.RiskSettings());
            try { _riskManager.SetSymbolReference(this.Symbol); } catch {}

            // Build an empty strategy list for now; real strategies can be registered later
            var strategies = new System.Collections.Generic.List<Strategies.IStrategy<Strategies.TradeSignal>>();

            // Construct TradeManager which will construct ExecutionModule(strategies, this, riskManager)
            _tradeManager = new TradeManager.TradeManager(strategies, this, _riskManager);
        }
        catch (Exception ex)
        {
            Print("BotGRobot startup: failed to initialize RiskManager/TradeManager: " + ex.Message);
        }

        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
        // Lightweight counter for runtime health
        try { TelemetryContext.Collector?.IncTick(); } catch { }
    }
}
