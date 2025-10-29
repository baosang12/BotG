using System;
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

        private readonly ManualResetEventSlim _fillEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _rejectEvent = new ManualResetEventSlim(false);
        private OrderFill? _lastFill;
        private OrderReject? _lastReject;

        public CanaryTrade(
            IOrderExecutor executor,
            Robot robot,
            Action<string> logger,
            string symbol = "EURUSD",
            double? volumeOverride = null,
            int timeoutMs = 10000)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _symbol = symbol;
            _volume = volumeOverride ?? Math.Max(1000, robot.Symbol?.VolumeInUnitsMin ?? 1000);
            _timeoutMs = timeoutMs;
        }

        /// <summary>
        /// Execute canary trade: REQUEST → FILL → CLOSE.
        /// Returns true if successful, false if timeout or error.
        /// </summary>
        public async Task<bool> ExecuteAsync(CancellationToken ct)
        {
            var orderId = Guid.NewGuid().ToString("N");
            _logger($"[CANARY] Enabled (paper), volume={_volume} units, timeout={_timeoutMs}ms");

            try
            {
                // Subscribe to fill/reject events
                _executor.OnFill += HandleFill;
                _executor.OnReject += HandleReject;

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
                await _executor.SendAsync(order).ConfigureAwait(false);

                // 2. Wait for FILL or REJECT
                var fillTask = Task.Run(() => _fillEvent.Wait(_timeoutMs, ct), ct);
                var rejectTask = Task.Run(() => _rejectEvent.Wait(_timeoutMs, ct), ct);
                var completedTask = await Task.WhenAny(fillTask, rejectTask).ConfigureAwait(false);

                if (completedTask == rejectTask && _rejectEvent.IsSet)
                {
                    var reason = _lastReject?.Reason ?? "unknown";
                    _logger($"[CANARY] REJECT: {reason}");
                    return false;
                }

                if (!_fillEvent.IsSet)
                {
                    _logger($"[CANARY] TIMEOUT: no FILL within {_timeoutMs}ms");
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
                    return true;
                }
                else
                {
                    _logger($"[CANARY] CLOSE failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger($"[CANARY] Exception: {ex.Message}");
                return false;
            }
            finally
            {
                _executor.OnFill -= HandleFill;
                _executor.OnReject -= HandleReject;
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
                // Find and close the canary position
                return await Task.Run(() =>
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
            }
            catch
            {
                return false;
            }
        }
    }
}
