using System;
using System.IO;
using Telemetry;

public static class BotGStartup
{
    // This file was empty; add a minimal startup initializer for telemetry.
    public static void Initialize()
    {
        // Load config and ensure path exists
        var cfg = TelemetryConfig.Load();
        Directory.CreateDirectory(cfg.LogPath);
        TelemetryContext.InitOnce(cfg);
    }
}
