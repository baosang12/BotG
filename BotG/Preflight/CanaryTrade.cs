using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Connectivity;
using cAlgo.API;

namespace BotG.Preflight
{
    /// <summary>
    /// Paper-only canary trade to verify order pipeline and logging.
    /// Sends a minimal market order, waits for FILL, then closes immediately.
    /// </summary>
    public class CanaryTrade
    {
        private readonly IOrderExecutor _executor;
        private readonly Robot _robot;
        private readonly Action<string> _logger;
        private readonly string _symbol;
        private readonly double _volume;
        private readonly int _timeoutMs;
        private readonly string _logPath;
        private readonly int _waitExecutorSec;

        private readonly ManualResetEventSlim _fillEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _rejectEvent = new ManualResetEventSlim(false);
        private OrderFill? _lastFill;
        private OrderReject? _lastReject;
        private string? _proofPath;

        public CanaryTrade(
            IOrderExecutor executor,
            Robot robot,
            Action<string> logger,
            string symbol = "EURUSD",
            double? volumeOverride = null,
            int timeoutMs = 10000,
            string? logPath = null,
            int waitExecutorSec = 60)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _symbol = symbol;
            _volume = volumeOverride ?? Math.Max(1000, robot.Symbol?.VolumeInUnitsMin ?? 1000);
            _timeoutMs = timeoutMs;
            _logPath = ResolveLogPath(logPath);
            _waitExecutorSec = waitExecutorSec <= 0 ? 60 : waitExecutorSec;
        }

        /// <summary>
        /// Execute canary trade: REQUEST → FILL → CLOSE.
        /// Returns true if successful, false if timeout or error.
        /// </summary>
        public async Task<bool> ExecuteAsync(CancellationToken ct)
        {
            _fillEvent.Reset();
            _rejectEvent.Reset();
            _lastFill = null;
            _lastReject = null;

            var proofPath = EnsureProofPath();
            var orderId = Guid.NewGuid().ToString("N");
            _logger($"[CANARY] Enabled (paper), volume={_volume} units, timeout={_timeoutMs}ms");

            var (ready, waitedSec) = await WaitForReadinessAsync(ct).ConfigureAwait(false);
            bool tradingOk = IsTradingEnabled();
            bool executorOk = _executor != null;
            if (!ready)
            {
                _logger($"[CANARY] SKIP TradingEnabled={tradingOk} ExecutorReady={executorOk} waited={waitedSec:F1}s");
                WriteProof(proofPath, new
                {
                    ok = false,
                    stage = "pre-exec-wait",
                    reason = $"TradingEnabled={tradingOk}, ExecutorReady={executorOk}",
                    waited_sec = Math.Round(waitedSec, 2),
                    symbol = _symbol,
                    generated_at = DateTime.UtcNow.ToString("o")
                });
                return false;
            }

            var executor = _executor;
            if (executor == null)
            {
                WriteProof(proofPath, new
                {
                    ok = false,
                    stage = "pre-exec-wait",
                    reason = "executor_null",
                    waited_sec = Math.Round(waitedSec, 2),
                    symbol = _symbol,
                    generated_at = DateTime.UtcNow.ToString("o")
                });
                return false;
            }

            try
            {
                // Subscribe to fill/reject events
                executor.OnFill += HandleFill;
                executor.OnReject += HandleReject;

                var startTime = DateTime.UtcNow;

                // 1. Send market BUY order
                var order = new NewOrder(
                    OrderId: orderId,
                    Symbol: _symbol,
                    Side: OrderSide.Buy,
                    Volume: _volume,
                    Type: Connectivity.OrderType.Market,
                    ClientTag: "BotG_CANARY"
                );

                _logger($"[CANARY] REQUEST sent: orderId={orderId}, symbol={_symbol}, volume={_volume}");
                await executor.SendAsync(order).ConfigureAwait(false);

                // 2. Wait for FILL or REJECT using polling loop for responsiveness
                var waitStopwatch = Stopwatch.StartNew();
                while (waitStopwatch.ElapsedMilliseconds < _timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_rejectEvent.IsSet)
                    {
                        var rejectReason = _lastReject?.Reason ?? "unknown";
                        _logger($"[CANARY] REJECT: {rejectReason}");
                        WriteProof(proofPath, new
                        {
                            ok = false,
                            stage = "reject",
                            reason = rejectReason,
                            order_id = orderId,
                            symbol = _symbol,
                            generated_at = DateTime.UtcNow.ToString("o")
                        });
                        return false;
                    }

                    if (_fillEvent.IsSet)
                    {
                        break;
                    }

                    await Task.Delay(50, ct).ConfigureAwait(false);
                }

                if (!_fillEvent.IsSet)
                {
                    _logger($"[CANARY] TIMEOUT: no FILL within {_timeoutMs}ms");
                    WriteProof(proofPath, new
                    {
                        ok = false,
                        stage = "fill-timeout",
                        reason = $"no fill within {_timeoutMs}ms",
                        order_id = orderId,
                        symbol = _symbol,
                        generated_at = DateTime.UtcNow.ToString("o")
                    });
                    return false;
                }

                var fillTime = DateTime.UtcNow;
                var latencyMs = (int)(fillTime - startTime).TotalMilliseconds;
                var fillPrice = _lastFill?.Price ?? 0.0;

                _logger($"[CANARY] FILL price={fillPrice:F5}, latency_ms={latencyMs}");

                // 3. Close position immediately
                var closeStartTime = DateTime.UtcNow;
                bool closed = await ClosePositionAsync(ct).ConfigureAwait(false);

                if (closed)
                {
                    var closeLatency = (int)(DateTime.UtcNow - closeStartTime).TotalMilliseconds;
                    _logger($"[CANARY] CLOSE ok (latency_ms={closeLatency})");
                    WriteProof(proofPath, new
                    {
                        ok = true,
                        stage = "executed",
                        order_id = orderId,
                        fill_price = fillPrice,
                        latency_ms = latencyMs,
                        lots = Math.Round(_volume / 100000.0, 4),
                        volume_units = _volume,
                        symbol = _symbol,
                        generated_at = DateTime.UtcNow.ToString("o")
                    });
                    return true;
                }
                else
                {
                    _logger($"[CANARY] CLOSE failed");
                    WriteProof(proofPath, new
                    {
                        ok = false,
                        stage = "close-failed",
                        reason = "close_failed",
                        order_id = orderId,
                        symbol = _symbol,
                        generated_at = DateTime.UtcNow.ToString("o")
                    });
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger($"[CANARY] Exception: {ex.Message}");
                WriteProof(proofPath, new
                {
                    ok = false,
                    stage = "exception",
                    reason = ex.Message,
                    symbol = _symbol,
                    generated_at = DateTime.UtcNow.ToString("o")
                });
                return false;
            }
            finally
            {
                executor.OnFill -= HandleFill;
                executor.OnReject -= HandleReject;
            }
        }

        private void HandleFill(OrderFill fill)
        {
            _lastFill = fill;
            _fillEvent.Set();
        }

        private void HandleReject(OrderReject reject)
        {
            _lastReject = reject;
            _rejectEvent.Set();
        }

        private async Task<bool> ClosePositionAsync(CancellationToken ct)
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    bool closed = await Task.Run(() =>
                    {
                        foreach (var pos in _robot.Positions)
                        {
                            if (pos.SymbolName == _symbol && pos.Label == "BotG_CANARY")
                            {
                                var result = _robot.ClosePosition(pos);
                                return result?.IsSuccessful == true;
                            }
                        }
                        return false;
                    }, ct).ConfigureAwait(false);

                    if (closed)
                    {
                        return true;
                    }

                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveLogPath(string? overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath!;
            }

            var env = Environment.GetEnvironmentVariable("BOTG_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env!;
            }

            return "D:/botg/logs";
        }

        private string EnsureProofPath()
        {
            if (!string.IsNullOrEmpty(_proofPath))
            {
                return _proofPath!;
            }

            var dir = Path.Combine(_logPath, "preflight");
            Directory.CreateDirectory(dir);
            _proofPath = Path.Combine(dir, "canary_proof.json");
            return _proofPath!;
        }

        private async Task<(bool Ready, double WaitedSec)> WaitForReadinessAsync(CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            var deadline = start.AddSeconds(_waitExecutorSec);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (IsTradingEnabled() && _executor != null)
                {
                    return (true, (DateTime.UtcNow - start).TotalSeconds);
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            return (IsTradingEnabled() && _executor != null, (DateTime.UtcNow - start).TotalSeconds);
        }

        private bool IsTradingEnabled()
        {
            try
            {
                var tradingProp = _robot.GetType().GetProperty("Trading");
                var trading = tradingProp?.GetValue(_robot);
                if (trading == null) return false;
                var enabledProp = trading.GetType().GetProperty("IsEnabled");
                var value = enabledProp?.GetValue(trading);
                if (value is bool flag)
                {
                    return flag;
                }
            }
            catch
            {
                // Ignore reflection failures - assume not ready yet
            }
            return false;
        }

        private static void WriteProof(string proofPath, object payload)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(proofPath, json, new UTF8Encoding(false));
            }
            catch
            {
                // Swallow IO errors to avoid crashing the bot during instrumentation
            }
        }
    }
}
