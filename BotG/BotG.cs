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

        // Diagnostic: print resolved sources for the log path
        try
        {
            var env = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
            try { Console.WriteLine("[BotGStartup] BOTG_LOG_PATH env = " + (env ?? "<null>")); } catch { }
        }
        catch { }

        try
        {
            var baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
            var cfgPath = System.IO.Path.Combine(baseDir, "config.runtime.json");
            try { Console.WriteLine("[BotGStartup] Looking for config at: " + cfgPath + " (exists=" + System.IO.File.Exists(cfgPath) + ")"); } catch { }
        }
        catch { }

        // Attempt to create the configured log directory; fallback to LocalAppData on UnauthorizedAccessException
        try
        {
            Directory.CreateDirectory(cfg.LogPath);
        }
        catch (UnauthorizedAccessException ua)
        {
            try
            {
                var local = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotG", "logs");
                try { Console.WriteLine($"[BotGStartup] Unauthorized creating '{cfg.LogPath}', falling back to '{local}': {ua.Message}"); } catch { }
                Directory.CreateDirectory(local);
                cfg.LogPath = local;
            }
            catch (Exception ex2)
            {
                try { Console.WriteLine("[BotGStartup] Fallback creation failed: " + ex2.Message); } catch { }
                // Do not rethrow; continue without crashing the bot
            }
        }
        catch (Exception ex)
        {
            try { Console.WriteLine("[BotGStartup] Could not create log path '" + cfg.LogPath + "': " + ex.Message); } catch { }
            // continue; TelemetryContext.InitOnce should handle missing directories gracefully
        }

        TelemetryContext.InitOnce(cfg);
    }
}
