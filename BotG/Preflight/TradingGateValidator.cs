using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Telemetry;

namespace BotG.Preflight
{
    /// <summary>
    /// Validates trading gate safety rules before allowing bot to start trading.
    /// Enforces paper mode, preflight completion, and sentinel file checks.
    /// </summary>
    public static class TradingGateValidator
    {
        /// <summary>
        /// Validates trading gate rules. Throws InvalidOperationException if any violation detected.
        /// </summary>
        /// <param name="cfg">Telemetry configuration to validate</param>
        /// <exception cref="InvalidOperationException">Thrown when trading gate validation fails</exception>
        public static void ValidateOrThrow(TelemetryConfig cfg)
        {
            var violations = new List<string>();
            
            // Rule 1: Trading enabled but not in paper mode
            if (cfg.Ops.EnableTrading && cfg.Mode != "paper")
            {
                violations.Add($"TRADING_VIOLATION: Trading enabled (ops.enable_trading=true) but mode is '{cfg.Mode}' instead of 'paper'");
            }
            
            // Rule 2: Trading enabled but no recent preflight result
            if (cfg.Ops.EnableTrading && !HasRecentPreflightResult(cfg))
            {
                violations.Add($"PREFLIGHT_VIOLATION: Trading enabled but no recent preflight result found (must be <10 minutes old)");
            }
            
            // Rule 3: Trading enabled but stop sentinel exists
            if (cfg.Ops.EnableTrading && HasStopSentinel(cfg))
            {
                violations.Add($"SENTINEL_VIOLATION: Trading enabled but STOP sentinel file exists (RUN_STOP or RUN_PAUSE)");
            }
            
            if (violations.Any())
            {
                var message = $"TRADING GATE VALIDATION FAILED:\n{string.Join("\n", violations)}";
                PrintToLog(cfg, message);
                throw new InvalidOperationException(message);
            }
            
            PrintToLog(cfg, "TRADING GATE VALIDATION PASSED - Trading is safe to proceed");
        }
        
        /// <summary>
        /// Checks if recent preflight results exist (less than 10 minutes old)
        /// </summary>
        private static bool HasRecentPreflightResult(TelemetryConfig cfg)
        {
            try
            {
                var preflightDir = Path.Combine(cfg.LogPath, "preflight");
                if (!Directory.Exists(preflightDir))
                    return false;
                
                var candidateFiles = new[]
                {
                    Path.Combine(preflightDir, "preflight_canary.json"),
                    Path.Combine(preflightDir, "executor_wireproof.json"),
                    Path.Combine(preflightDir, "connection_ok.json")
                };
                
                var existingFiles = candidateFiles.Where(File.Exists).ToList();
                if (!existingFiles.Any())
                    return false;
                
                // Check file age - must be < 10 minutes
                var lastWrite = existingFiles.Max(f => File.GetLastWriteTimeUtc(f));
                var age = DateTime.UtcNow - lastWrite;
                
                return age.TotalMinutes < 10;
            }
            catch
            {
                return false; // Conservative - assume no preflight if we can't check
            }
        }
        
        /// <summary>
        /// Checks if stop/pause sentinel files exist
        /// </summary>
        private static bool HasStopSentinel(TelemetryConfig cfg)
        {
            try
            {
                var stopFile = Path.Combine(cfg.LogPath, "RUN_STOP");
                var pauseFile = Path.Combine(cfg.LogPath, "RUN_PAUSE");
                return File.Exists(stopFile) || File.Exists(pauseFile);
            }
            catch
            {
                return true; // Conservative - assume stop if we can't check
            }
        }
        
        /// <summary>
        /// Writes validation result to trading_gate.log
        /// </summary>
        private static void PrintToLog(TelemetryConfig cfg, string message)
        {
            try
            {
                var logPath = Path.Combine(cfg.LogPath, "trading_gate.log");
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var logDir = Path.GetDirectoryName(logPath);
                
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
                
                File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
            catch
            {
                // Fallback - don't throw from logging
            }
        }
    }
}
