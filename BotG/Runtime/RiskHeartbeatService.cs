using System;
using System.Collections.Generic;
using System.Linq;
using Telemetry;
using cAlgo.API;
using BotG.Runtime.Preprocessor;
using AccountInfo = DataFetcher.Models.AccountInfo;

namespace BotG.Runtime
{
    public sealed class RiskHeartbeatService
    {
        private DateTime _last = DateTime.MinValue;
        private readonly TimeSpan _period;
        private readonly Telemetry.RiskSnapshotPersister _persister;
        private readonly Telemetry.PositionSnapshotPersister? _positionPersister; // A9: Position-level persister
        private readonly Robot _robot;
        private readonly TelemetryConfig _config;
        private readonly string _logPath;
        private IPreprocessorStrategyDataBridge? _preprocessorBridge;

        public RiskHeartbeatService(Robot robot, Telemetry.RiskSnapshotPersister persister, Telemetry.PositionSnapshotPersister? positionPersister = null, int seconds = 15, IPreprocessorStrategyDataBridge? preprocessorBridge = null)
            : this(robot, persister, positionPersister, seconds, TelemetryConfig.Load(), preprocessorBridge) { }

        public RiskHeartbeatService(Robot robot, Telemetry.RiskSnapshotPersister persister, Telemetry.PositionSnapshotPersister? positionPersister, int seconds, TelemetryConfig config, IPreprocessorStrategyDataBridge? preprocessorBridge = null)
        {
            _robot = robot;
            _persister = persister;
            _positionPersister = positionPersister; // A9: Optional position persister
            _period = TimeSpan.FromSeconds(seconds);
            _config = config;
            _logPath = config.LogPath ?? TelemetryConfig.DefaultBasePath;
            _preprocessorBridge = preprocessorBridge;
            try { System.IO.Directory.CreateDirectory(_logPath); } catch { }
        }

        public void AttachPreprocessorBridge(IPreprocessorStrategyDataBridge? bridge)
        {
            _preprocessorBridge = bridge;
        }
        public void Tick()
        {
            var now = DateTime.UtcNow;

            // A8 DEBUG: Log EVERY tick (before threshold)
            try
            {
                var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                var elapsed = (now - _last).TotalSeconds;
                System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Tick() called, elapsed={elapsed:F1}s, threshold={_period.TotalSeconds}s\n");
            }
            catch { }

            if (now - _last < _period) return;
            _last = now;

            // A8 FIX: Debug logging to trace execution
            try
            {
                var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Threshold PASSED, _robot.Account={((_robot.Account == null ? "NULL" : "NOT NULL"))}\n");
            }
            catch { }

            var info = ResolveAccountInfo();
            if (info != null)
            {
                try
                {
                    var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                    System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Creating AccountInfo object...\n");
                }
                catch { }

                // A9: Collect position snapshots
                List<PositionSnapshot>? positionSnapshots = null;
                try
                {
                    if (_robot.Positions.Count > 0)
                    {
                        positionSnapshots = new List<PositionSnapshot>();
                        foreach (var pos in _robot.Positions)
                        {
                            try
                            {
                                // A9: Direct property access (safer than dynamic FromPosition())
                                positionSnapshots.Add(new PositionSnapshot
                                {
                                    Symbol = pos.SymbolName ?? pos.SymbolCode ?? "UNKNOWN",
                                    Direction = pos.TradeType.ToString(),
                                    Volume = (double)pos.VolumeInUnits,
                                    EntryPrice = pos.EntryPrice,
                                    CurrentPrice = pos.CurrentPrice,
                                    UnrealizedPnL = pos.NetProfit,
                                    Pips = pos.Pips,
                                    UsedMargin = pos.Margin,
                                    OpenTime = pos.EntryTime,
                                    Id = pos.Id
                                });
                            }
                            catch
                            {
                                // Skip positions that fail to serialize
                            }
                        }
                    }
                }
                catch
                {
                    // If position collection fails, continue with null
                }

                try
                {
                    var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                    System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Calling Persist() with {positionSnapshots?.Count ?? 0} positions...\n");
                }
                catch { }

                try
                {
                    // Persist account-level risk snapshot with portfolio metrics
                    _persister.Persist(info, positionSnapshots);

                    // A9: Persist position-level snapshots separately
                    if (_positionPersister != null && positionSnapshots != null && positionSnapshots.Count > 0)
                    {
                        _positionPersister.Persist(positionSnapshots);
                    }

                    try
                    {
                        var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                        System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] Persist() completed successfully!\n");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                        System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] EXCEPTION in Persist(): {ex.GetType().Name}: {ex.Message}\n");
                    }
                    catch { }
                }
            }
            else
            {
                // A8 FIX: Log when Account is null
                try
                {
                    var debugLog = System.IO.Path.Combine(_logPath, "a8_tick_debug.log");
                    System.IO.File.AppendAllText(debugLog, $"[{DateTime.UtcNow:o}] CRITICAL: _robot.Account is NULL - cannot persist!\n");
                }
                catch { }
            }
        }

        private AccountInfo? ResolveAccountInfo()
        {
            try
            {
                var bridged = _preprocessorBridge?.GetAccountInfo();
                if (bridged != null)
                {
                    return bridged;
                }
            }
            catch { }

            var acc = _robot.Account;
            if (acc == null)
            {
                return null;
            }

            try
            {
                return new AccountInfo
                {
                    Equity = acc.Equity,
                    Balance = acc.Balance,
                    Margin = acc.Margin,
                    Positions = _robot.Positions.Count
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
