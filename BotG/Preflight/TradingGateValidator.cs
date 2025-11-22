using System;
using System.Collections.Generic;
using System.Globalization;
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
            if (ShouldBypassTradingGate(cfg))
            {
                PrintToLog(cfg, "TRADING GATE VALIDATION BYPASSED - backtest/development environment detected");
                return;
            }

            var violations = new List<string>();
            
            // Rule 1: Trading enabled but not in paper mode
            if (cfg.Ops.EnableTrading && cfg.Mode != "paper")
            {
                violations.Add($"TRADING_VIOLATION: Trading enabled (ops.enable_trading=true) but mode is '{cfg.Mode}' instead of 'paper'");
            }
            
            // Rule 2: Trading enabled but no recent preflight result
            var freshnessWindow = GetPreflightWindow(cfg);
            if (cfg.Ops.EnableTrading && !HasRecentPreflightResult(cfg, freshnessWindow))
            {
                violations.Add($"PREFLIGHT_VIOLATION: Trading enabled but no recent preflight result found (must be <{freshnessWindow.TotalMinutes:F0} minutes old)");
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
        private static bool HasRecentPreflightResult(TelemetryConfig cfg, TimeSpan freshnessWindow)
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
                
                // Check file age - must be within configured freshness window
                var lastWrite = existingFiles.Max(f => File.GetLastWriteTimeUtc(f));
                var age = DateTime.UtcNow - lastWrite;

                return age < freshnessWindow;
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

        private static bool ShouldBypassTradingGate(TelemetryConfig cfg)
        {
            if (IsBacktestLikeMode(cfg?.Mode))
            {
                return true;
            }

            var modeCandidates = new[]
            {
                Environment.GetEnvironmentVariable("BOTG_MODE"),
                Environment.GetEnvironmentVariable("Mode"),
                Environment.GetEnvironmentVariable("BOTG_RUN_MODE")
            };

            if (modeCandidates.Any(IsBacktestLikeMode))
            {
                return true;
            }

            var environmentCandidates = new[]
            {
                Environment.GetEnvironmentVariable("BOTG_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("ENVIRONMENT"),
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            };

            if (environmentCandidates.Any(IsDevOrBacktestEnvironment))
            {
                return true;
            }

            var ctraderBacktest = Environment.GetEnvironmentVariable("CTRADER_BACKTEST");
            if (!string.IsNullOrWhiteSpace(ctraderBacktest) &&
                !string.Equals(ctraderBacktest, "0", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ctraderBacktest, "false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsDevOrBacktestEnvironment(string? env)
        {
            if (string.IsNullOrWhiteSpace(env))
            {
                return false;
            }

            env = env.Trim();
            return env.Equals("backtest", StringComparison.OrdinalIgnoreCase) ||
                   env.Equals("development", StringComparison.OrdinalIgnoreCase) ||
                   env.Equals("dev", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBacktestLikeMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return false;
            }

            mode = mode.Trim();
            return mode.Equals("backtest", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("simulation", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("sim", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("test", StringComparison.OrdinalIgnoreCase);
        }

        private static TimeSpan GetPreflightWindow(TelemetryConfig cfg)
        {
            double ttlMinutes = cfg?.Preflight?.TtlMinutes > 0
                ? cfg.Preflight.TtlMinutes
                : 10;

            var envOverride = Environment.GetEnvironmentVariable("BOTG_PREFLIGHT_TTL_MINUTES");
            if (!string.IsNullOrWhiteSpace(envOverride) &&
                double.TryParse(envOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                ttlMinutes = parsed;
            }

            return TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
        }
    }
}
