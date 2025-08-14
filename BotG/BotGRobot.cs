using cAlgo.API;
using Telemetry;

[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
public class BotGRobot : Robot
{
    protected override void OnStart()
    {
        BotGStartup.Initialize();
        Print("BotGRobot started; telemetry initialized");
    }

    protected override void OnTick()
    {
        // Lightweight counter for runtime health
        try { TelemetryContext.Collector?.IncTick(); } catch { }
    }
}
