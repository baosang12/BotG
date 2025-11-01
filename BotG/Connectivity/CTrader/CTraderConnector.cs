using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using cAlgo.API;

namespace Connectivity.CTrader
{
    internal interface ICTraderTickPump
    {
        void Pump();
    }

    /// <summary>
    /// Market data provider that samples quotes from the hosting cTrader robot.
    /// </summary>
    public sealed class CTraderMarketDataProvider : IMarketDataProvider, ICTraderTickPump
    {
        private readonly Robot _robot;
        private readonly ConcurrentDictionary<string, Quote> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastEmit = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _emitThrottle = TimeSpan.FromMilliseconds(150); // ~6-7 Hz
        private readonly object _startLock = new();
        private bool _started;

        public event Action<Quote>? OnQuote;

        public CTraderMarketDataProvider(Robot robot)
        {
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
        }

        public string BrokerName => SafeAccountField("BrokerName") ?? "cTrader";
        public string Server => SafeAccountField("Server") ?? "demo";
        public string AccountId => SafeAccountField("Number") ?? SafeAccountField("Id") ?? "demo";

        public void Subscribe(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            _cache.TryAdd(symbol, new Quote(symbol, double.NaN, double.NaN, DateTime.UtcNow));
        }

        public void Start()
        {
            lock (_startLock)
            {
                if (_started) return;
                _started = true;
            }
        }

        public void Stop()
        {
            lock (_startLock)
            {
                _started = false;
            }
        }

        public void Pump()
        {
            if (!_started) return;
            foreach (var symbol in _cache.Keys)
            {
                try
                {
                    var quote = CaptureQuote(symbol);
                    if (quote == null) continue;
                    var now = DateTime.UtcNow;
                    var last = _lastEmit.GetOrAdd(symbol, DateTime.MinValue);
                    if (now - last < _emitThrottle) continue;
                    _lastEmit[symbol] = now;
                    _cache[symbol] = quote;
                    OnQuote?.Invoke(quote);
                }
                catch
                {
                    // swallow
                }
            }
        }

        private Quote? CaptureQuote(string symbol)
        {
            var symbolsProp = _robot.GetType().GetProperty("Symbols");
            object? symbols = symbolsProp?.GetValue(_robot);
            if (symbols == null)
            {
                if (!string.Equals(symbol, _robot.SymbolName, StringComparison.OrdinalIgnoreCase)) return null;
                var sym = _robot.Symbol;
                return new Quote(symbol, sym.Bid, sym.Ask, DateTime.UtcNow);
            }

            var getSymbol = symbols.GetType().GetMethod("GetSymbol", new[] { typeof(string) });
            object? symObj = getSymbol?.Invoke(symbols, new object[] { symbol });
            if (symObj == null)
            {
                if (string.Equals(symbol, _robot.SymbolName, StringComparison.OrdinalIgnoreCase))
                {
                    symObj = _robot.Symbol;
                }
                else
                {
                    return null;
                }
            }

            var bidProp = symObj.GetType().GetProperty("Bid");
            var askProp = symObj.GetType().GetProperty("Ask");
            if (bidProp == null || askProp == null) return null;
            double bid = Convert.ToDouble(bidProp.GetValue(symObj), CultureInfo.InvariantCulture);
            double ask = Convert.ToDouble(askProp.GetValue(symObj), CultureInfo.InvariantCulture);
            return new Quote(symbol, bid, ask, DateTime.UtcNow);
        }

        private string? SafeAccountField(string name)
        {
            try
            {
                var accountProp = _robot.GetType().GetProperty("Account");
                var account = accountProp?.GetValue(_robot);
                if (account == null) return null;
                var field = account.GetType().GetProperty(name);
                if (field == null) return null;
                var value = field.GetValue(account);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Order executor bridging Execution module to cTrader robot.
    /// </summary>
    public sealed class CTraderOrderExecutor : IOrderExecutor
    {
        private readonly Robot _robot;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public CTraderOrderExecutor(Robot robot)
        {
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
        }

        public event Action<OrderFill>? OnFill;
        public event Action<OrderReject>? OnReject;

        public string BrokerName => SafeAccountField("BrokerName") ?? "cTrader";
        public string Server => SafeAccountField("Server") ?? "demo";
        public string AccountId => SafeAccountField("Number") ?? SafeAccountField("Id") ?? "demo";

        public async Task SendAsync(NewOrder order)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    // Marshal all cTrader API calls to the UI thread
                    _robot.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            var tradeType = order.Side == OrderSide.Buy ? TradeType.Buy : TradeType.Sell;
                            long volumeUnits = Convert.ToInt64(Math.Round(order.Volume));
                            // ORDER pipeline log: REQUEST
                            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "REQUEST", "market_order", new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["symbol"] = order.Symbol,
                                ["side"] = order.Side.ToString(),
                                ["units"] = volumeUnits
                            });
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var result = _robot.ExecuteMarketOrder(tradeType, order.Symbol, volumeUnits, order.ClientTag ?? "BotG", order.StopLoss, null);
                            var serverTs = result?.Position?.EntryTime ?? DateTime.UtcNow;
                            if (result?.IsSuccessful == true)
                            {
                                double price = result.Position?.EntryPrice ?? 0.0;
                                double filledVol = result.Position?.VolumeInUnits ?? order.Volume;
                                try { OnFill?.Invoke(new OrderFill(order.OrderId, order.Symbol, order.Side, price, filledVol, serverTs, DateTime.UtcNow, "filled")); } catch { }
                                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "ACK", "ok", new System.Collections.Generic.Dictionary<string, object>
                                {
                                    ["id"] = result.Position?.Id ?? 0,
                                    ["latency_ms"] = sw.ElapsedMilliseconds
                                });
                                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "FILL", "ok", new System.Collections.Generic.Dictionary<string, object>
                                {
                                    ["id"] = result.Position?.Id ?? 0,
                                    ["price"] = price
                                });
                            }
                            else
                            {
                                var reason = result?.Error?.ToString() ?? "unknown";
                                try { OnReject?.Invoke(new OrderReject(order.OrderId, order.Symbol, reason, serverTs, DateTime.UtcNow, reason)); } catch { }
                                BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "REJECT", reason, new System.Collections.Generic.Dictionary<string, object>
                                {
                                    ["units"] = volumeUnits,
                                    ["latency_ms"] = sw.ElapsedMilliseconds
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            var now = DateTime.UtcNow;
                            try { OnReject?.Invoke(new OrderReject(order.OrderId, order.Symbol, ex.Message, now, now, ex.Message)); } catch { }
                            BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "REJECT", ex.Message, new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["exception"] = ex.GetType().Name
                            });
                        }
                        finally
                        {
                            tcs.TrySetResult(true);
                        }
                    });
                }
                catch (Exception exOuter)
                {
                    // Never let exceptions escape to Robot caller
                    var now = DateTime.UtcNow;
                    try { OnReject?.Invoke(new OrderReject(order.OrderId, order.Symbol, exOuter.Message, now, now, exOuter.Message)); } catch { }
                    BotG.Runtime.Logging.PipelineLogger.Log("ORDER", "REJECT", exOuter.Message, new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["exception_outer"] = exOuter.GetType().Name
                    });
                    tcs.TrySetResult(true);
                }

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private string? SafeAccountField(string name)
        {
            try
            {
                var accountProp = _robot.GetType().GetProperty("Account");
                var account = accountProp?.GetValue(_robot);
                if (account == null) return null;
                var field = account.GetType().GetProperty(name);
                if (field == null) return null;
                var value = field.GetValue(account);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
