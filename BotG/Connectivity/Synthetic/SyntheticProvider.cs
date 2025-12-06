using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Connectivity.Synthetic
{
    /// <summary>
    /// Lightweight synthetic market data feed for tests and legacy smoke environments.
    /// Quotes are published on demand via <see cref="PublishQuote"/> and replayed to subscribers.
    /// </summary>
    public sealed class SyntheticMarketDataProvider : IMarketDataProvider, IDisposable
    {
        private readonly ConcurrentDictionary<string, Quote> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new();
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(1);
        private Task? _heartbeat;

        public event Action<Quote>? OnQuote;

        public string BrokerName { get; init; } = "Synthetic";
        public string Server { get; init; } = "synthetic-feed";
        public string AccountId { get; init; } = "SIM";

        public void Subscribe(string symbol)
        {
            // ensure entry exists so callers can inspect TryGet later
            _quotes.TryAdd(symbol, new Quote(symbol, double.NaN, double.NaN, DateTime.UtcNow));
        }

        public void PublishQuote(string symbol, double bid, double ask, DateTime? timestampUtc = null)
        {
            var ts = timestampUtc ?? DateTime.UtcNow;
            var quote = new Quote(symbol, bid, ask, ts);
            _quotes[symbol] = quote;
            OnQuote?.Invoke(quote);
        }

        public Quote? GetLatest(string symbol)
        {
            return _quotes.TryGetValue(symbol, out var q) ? q : null;
        }

        public void Start()
        {
            if (_heartbeat != null) return;

            // JUSTIFICATION: Fire-and-forget Task.Run acceptable here because:
            // 1. SIMULATION MODE ONLY: SyntheticProvider used for testing, not production
            // 2. BACKGROUND HEARTBEAT: Quote generation loop, no trading operations
            // 3. CANCELLATION SAFE: Respects CancellationToken, stops cleanly
            // 4. NO RACE CONDITION: Only invokes OnQuote event, stateless
            _heartbeat = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var entry in _quotes)
                        {
                            OnQuote?.Invoke(new Quote(entry.Key, entry.Value.Bid, entry.Value.Ask, DateTime.UtcNow));
                        }
                        await Task.Delay(_heartbeatInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // ignore to keep synthetic feed running
                    }
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _heartbeat?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Simplistic synthetic executor that fills orders immediately using latest quote context.
    /// </summary>
    public sealed class SyntheticOrderExecutor : IOrderExecutor
    {
        private readonly SyntheticMarketDataProvider _marketData;
        private readonly Random _random;

        public SyntheticOrderExecutor(SyntheticMarketDataProvider marketData)
        {
            _marketData = marketData;
            _random = new Random();
        }

        public event Action<OrderFill>? OnFill;
        public event Action<OrderReject>? OnReject;

        public string BrokerName { get; init; } = "Synthetic";
        public string Server { get; init; } = "synthetic-executor";
        public string AccountId { get; init; } = "SIM";

        public Task SendAsync(NewOrder order)
        {
            // JUSTIFICATION: Task.Run acceptable here because:
            // 1. SIMULATION MODE ONLY: SyntheticExecutor used for testing, not production
            // 2. SYNCHRONOUS WRAPPER: SendAsync must return Task, but logic is sync
            // 3. NO ACTUAL BROKER: Synthetic fill generation, no real trading risk
            // 4. TEST HARNESS: Used in Harness project for local development
            return Task.Run(() =>
            {
                var now = DateTime.UtcNow;
                var quote = _marketData.GetLatest(order.Symbol);
                if (quote == null)
                {
                    // Without quotes we still emit a fill but mark broker message accordingly
                    var fallbackPrice = order.Price ?? 0.0;
                    OnFill?.Invoke(new OrderFill(order.OrderId, order.Symbol, order.Side, fallbackPrice, order.Volume, now, now, "synthetic-fill-noquote"));
                    return;
                }

                var slip = (_random.NextDouble() - 0.5) * 0.0002; // ±0.0001 synthetic slippage (~1 pip @5 digits)
                double fillPrice = order.Side == OrderSide.Buy ? quote.Ask + slip : quote.Bid - slip;
                var brokerMsg = "synthetic-fill";
                OnFill?.Invoke(new OrderFill(order.OrderId, order.Symbol, order.Side, fillPrice, order.Volume, now, now, brokerMsg));
            });
        }
    }
}
