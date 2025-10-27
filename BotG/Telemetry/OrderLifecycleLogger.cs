using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Connectivity;

namespace Telemetry
{
    internal class OrderLifecycleState
    {
        public long RequestEpochMs { get; set; }
        public string? TsRequest { get; set; }
        public string? TsAck { get; set; }
        public string? TsFill { get; set; }
        public OrderQuoteEnvelope Quote { get; } = new OrderQuoteEnvelope();
        public double? RequestedLots { get; set; }
        public double? RequestedUnits { get; set; }
        public double? FilledUnits { get; set; }
        public double? PointValuePerUnit { get; set; }
        public double? CommissionUsd { get; set; }
        public double? SpreadCostUsd { get; set; }
        public double? SlippagePips { get; set; }
        public double? LotSize { get; set; }
        public double? PipSize { get; set; }
        public string? Side { get; set; }
    }

    public sealed class OrderLifecycleExtras
    {
        public double? RequestedLots { get; set; }
        public double? CommissionUsd { get; set; }
        public double? SpreadCostUsd { get; set; }
        public double? SlippagePips { get; set; }
        public double? PointValuePerUnit { get; set; }
        public double? LotSize { get; set; }
    }

    public class OrderLifecycleLogger : IDisposable
    {
        private static readonly string[] HeaderColumns = new[]
        {
            "phase","timestamp_iso","epoch_ms","orderId","intendedPrice","stopLoss","execPrice","theoretical_lots","theoretical_units","requestedVolume","filledSize","slippage","brokerMsg",
            "client_order_id","side","action","type","status","reason","latency_ms","price_requested","price_filled","size_requested","size_filled","session","host",
            "order_id","timestamp_request","timestamp_ack","timestamp_fill",
            "symbol","bid_at_request","ask_at_request","spread_pips_at_request","bid_at_fill","ask_at_fill","spread_pips_at_fill","request_server_time","fill_server_time",
            "timestamp","requested_lots","commission_usd","spread_cost_usd","slippage_pips",
            // Gate2 aliases (CHANGE-001)
            "latency","request_id","ts_request","ts_ack","ts_fill"
        };

        private static readonly string ExpectedHeader = string.Join(",", HeaderColumns);

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<string, OrderLifecycleState> _orderStates = new(StringComparer.OrdinalIgnoreCase);
        private OrderQuoteTelemetry? _quoteTelemetry;
        private IOrderExecutor? _executor;
        private readonly Func<DateTime> _clock;
        private readonly TelemetryConfig? _config;
        private static readonly double EnvCommissionPerLot = TryGetEnvDouble("COMMISSION_PER_LOT");
        private static readonly double EnvSpreadPips = TryGetEnvDouble("SPREAD_PIPS");
        private static readonly double EnvPointValuePerUnit = TryGetEnvDouble("POINT_VALUE_PER_UNIT");
        private bool _disposed;

        public OrderLifecycleLogger(string folder, string fileName, OrderQuoteTelemetry? quoteTelemetry = null, IOrderExecutor? executor = null, Func<DateTime>? clock = null, TelemetryConfig? config = null)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            _clock = clock ?? (() => DateTime.UtcNow);
            _config = config;
            EnsureHeader();
            AttachConnectivity(quoteTelemetry, executor);
        }

        public void AttachConnectivity(OrderQuoteTelemetry? quoteTelemetry, IOrderExecutor? executor = null)
        {
            if (quoteTelemetry != null)
            {
                _quoteTelemetry = quoteTelemetry;
            }

            if (executor != null)
            {
                if (_executor != null)
                {
                    try { _executor.OnFill -= OnExecutorFill; } catch { }
                }
                _executor = executor;
                _executor.OnFill += OnExecutorFill;
            }
        }

        private void OnExecutorFill(OrderFill fill)
        {
            try
            {
                var state = _orderStates.GetOrAdd(fill.OrderId, _ => new OrderLifecycleState());
                CaptureFillQuote(state, fill.Symbol, fill.TimestampServer);
            }
            catch
            {
                // telemetry only; ignore failures
            }
        }

        private void EnsureHeader()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    File.AppendAllText(_filePath, ExpectedHeader + Environment.NewLine);
                    return;
                }

                var firstLine = File.ReadLines(_filePath).FirstOrDefault() ?? string.Empty;
                if (string.Equals(firstLine.TrimEnd('\r', '\n'), ExpectedHeader, StringComparison.Ordinal))
                {
                    return;
                }

                var lines = File.ReadAllLines(_filePath);
                if (lines.Length == 0)
                {
                    File.WriteAllText(_filePath, ExpectedHeader + Environment.NewLine);
                }
                else
                {
                    lines[0] = ExpectedHeader;
                    File.WriteAllLines(_filePath, lines);
                }
            }
            catch
            {
                try { File.WriteAllText(_filePath, ExpectedHeader + Environment.NewLine); } catch { }
            }
        }

        // Legacy logging API (kept). New fields will be auto-derived/left blank.
        public void Log(string phase, string orderId, double? intendedPrice, double? stopLoss, double? execPrice, double? theoreticalLots, double? theoreticalUnits, double? requestedVolume, double? filledSize, string? brokerMsg = null)
        {
            LogV2(
                phase: phase,
                orderId: orderId,
                clientOrderId: orderId,
                side: null,
                action: null,
                type: null,
                intendedPrice: intendedPrice,
                stopLoss: stopLoss,
                execPrice: execPrice,
                theoreticalLots: theoreticalLots,
                theoreticalUnits: theoreticalUnits,
                requestedVolume: requestedVolume,
                filledSize: filledSize,
                status: phase,
                reason: brokerMsg,
                session: null
            );
        }

        // New richer API
        public void LogV2(string phase, string orderId, string? clientOrderId, string? side, string? action, string? type,
            double? intendedPrice, double? stopLoss, double? execPrice, double? theoreticalLots, double? theoreticalUnits,
            double? requestedVolume, double? filledSize, string? status, string? reason, string? session,
            OrderQuoteEnvelope? quotes = null, OrderLifecycleExtras? extras = null)
        {
            try
            {
                var extrasEffective = extras ?? new OrderLifecycleExtras();
                var ts = _clock();
                var epoch = new DateTimeOffset(ts).ToUnixTimeMilliseconds();
                var tsIso = ts.ToString("o", CultureInfo.InvariantCulture);

                double? slippagePrice = (execPrice.HasValue && intendedPrice.HasValue) ? execPrice.Value - intendedPrice.Value : (double?)null;

                var state = _orderStates.GetOrAdd(orderId, _ => new OrderLifecycleState());

                if (!string.IsNullOrWhiteSpace(session))
                {
                    state.Quote.Symbol ??= session;
                }

                var normalizedSide = NormalizeSide(side);
                if (!string.IsNullOrEmpty(normalizedSide))
                {
                    state.Side = normalizedSide;
                }

                if (requestedVolume.HasValue && requestedVolume.Value > 0)
                {
                    state.RequestedUnits = requestedVolume;
                }

                if (filledSize.HasValue && filledSize.Value > 0)
                {
                    state.FilledUnits = filledSize;
                }

                if (extrasEffective.LotSize.HasValue && extrasEffective.LotSize.Value > 0)
                {
                    state.LotSize = extrasEffective.LotSize;
                }

                double? requestedLots = extrasEffective.RequestedLots ?? theoreticalLots;
                if (!requestedLots.HasValue)
                {
                    var lotSize = state.LotSize;
                    if (lotSize.HasValue && lotSize.Value > 0 && requestedVolume.HasValue)
                    {
                        requestedLots = requestedVolume.Value / lotSize.Value;
                    }
                }
                if (requestedLots.HasValue && requestedLots.Value > 0)
                {
                    state.RequestedLots = requestedLots;
                }

                // latency tracking + timestamp population
                var st = (status ?? phase ?? string.Empty).ToUpperInvariant();
                long? latencyMs = null;

                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    state.RequestEpochMs = epoch;
                    state.TsRequest = tsIso;
                    CaptureRequestQuote(state, session);
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsAck = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
                else if (string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsFill = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                    if (state.Quote.FillServerTime == null)
                    {
                        CaptureFillQuote(state, session, ts);
                    }
                }
                else if (string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }

                var host = Environment.MachineName;
                MergeQuoteTelemetry(state.Quote, quotes);
                var qt = state.Quote;
                state.PipSize ??= DeterminePipSize(state);

                // Diagnostics: warn if FILL without price_filled or size_filled
                try
                {
                    if (string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase))
                    {
                        bool missPx = !execPrice.HasValue || execPrice.Value == 0.0;
                        bool missSz = !filledSize.HasValue || filledSize.Value == 0.0;
                        if (missPx || missSz)
                        {
                            var warnDir = Path.GetDirectoryName(_filePath) ?? string.Empty;
                            var warnPath = Path.Combine(warnDir, "orders_warnings.log");
                            var msg = tsIso + " WARN FILL missing fields orderId=" + orderId + (missPx ? " price_filled" : string.Empty) + (missSz ? " size_filled" : string.Empty);
                            try { File.AppendAllText(warnPath, msg + Environment.NewLine); } catch { }
                        }
                    }
                }
                catch { }

                var pointValuePerUnit = ResolvePointValuePerUnit(state, extrasEffective);
                if (pointValuePerUnit.HasValue && pointValuePerUnit.Value > 0)
                {
                    state.PointValuePerUnit = pointValuePerUnit.Value;
                }

                state.CommissionUsd = extrasEffective.CommissionUsd ?? state.CommissionUsd ?? ComputeCommission(state);
                state.SpreadCostUsd = extrasEffective.SpreadCostUsd ?? state.SpreadCostUsd ?? ComputeSpreadCost(state);
                var slippagePips = extrasEffective.SlippagePips ?? ComputeSlippagePips(state, normalizedSide, execPrice, intendedPrice);
                if (slippagePips.HasValue)
                {
                    state.SlippagePips = slippagePips;
                }

                var values = new List<string>(HeaderColumns.Length)
                {
                    phase ?? string.Empty,
                    tsIso,
                    epoch.ToString(CultureInfo.InvariantCulture),
                    Escape(orderId),
                    F(intendedPrice),
                    F(stopLoss),
                    F(execPrice),
                    F(theoreticalLots),
                    F(theoreticalUnits),
                    F(requestedVolume),
                    F(filledSize),
                    F(slippagePrice),
                    Escape(reason), // keep brokerMsg slot for backward compatibility
                    Escape(clientOrderId),
                    Escape(side),
                    Escape(action),
                    Escape(type),
                    Escape(string.IsNullOrEmpty(status) ? phase : status),
                    Escape(reason),
                    latencyMs.HasValue ? latencyMs.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    F(intendedPrice),
                    F(execPrice),
                    F(requestedVolume),
                    F(filledSize),
                    Escape(session),
                    Escape(host),
                    Escape(orderId),
                    Escape(state.TsRequest ?? string.Empty),
                    Escape(state.TsAck ?? string.Empty),
                    Escape(state.TsFill ?? string.Empty),
                    Escape(qt.Symbol),
                    FormatQuoteValue(qt.Request?.Bid),
                    FormatQuoteValue(qt.Request?.Ask),
                    FormatQuoteValue(qt.Request?.SpreadPips),
                    FormatQuoteValue(qt.Fill?.Bid),
                    FormatQuoteValue(qt.Fill?.Ask),
                    FormatQuoteValue(qt.Fill?.SpreadPips),
                    Escape(ToIso(qt.RequestServerTime)),
                    Escape(ToIso(qt.FillServerTime)),
                    tsIso,
                    F(state.RequestedLots),
                    F(state.CommissionUsd),
                    F(state.SpreadCostUsd),
                    F(state.SlippagePips),
                    // Gate2 aliases (CHANGE-001)
                    latencyMs.HasValue ? latencyMs.Value.ToString(CultureInfo.InvariantCulture) : string.Empty, // latency = latency_ms
                    Escape(clientOrderId ?? orderId), // request_id = client_order_id ?? orderId
                    Escape(state.TsRequest ?? string.Empty), // ts_request = timestamp_request
                    Escape(state.TsAck ?? string.Empty), // ts_ack = timestamp_ack
                    Escape(state.TsFill ?? string.Empty) // ts_fill = timestamp_fill
                };

                var line = string.Join(",", values);
                lock (_lock)
                {
                    CsvUtils.SafeAppendCsv(_filePath, string.Empty, line);
                }
            }
            catch { /* swallow for safety */ }
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
        private static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"'))
            {
                return '"' + s.Replace("\"", "\"\"") + '"';
            }
            return s;
        }

        private double? ResolvePointValuePerUnit(OrderLifecycleState state, OrderLifecycleExtras extras)
        {
            if (extras.PointValuePerUnit.HasValue && extras.PointValuePerUnit.Value > 0)
            {
                state.PointValuePerUnit = extras.PointValuePerUnit.Value;
                return state.PointValuePerUnit;
            }

            if (state.PointValuePerUnit.HasValue && state.PointValuePerUnit.Value > 0)
            {
                return state.PointValuePerUnit;
            }

            if (!double.IsNaN(EnvPointValuePerUnit) && EnvPointValuePerUnit > 0)
            {
                state.PointValuePerUnit = EnvPointValuePerUnit;
                return state.PointValuePerUnit;
            }

            return null;
        }

        private double? ComputeCommission(OrderLifecycleState state)
        {
            var lots = state.RequestedLots;
            if (!lots.HasValue || lots.Value <= 0)
            {
                return null;
            }

            var perLot = ResolveCommissionPerLot();
            if (perLot <= 0)
            {
                return null;
            }

            return lots.Value * perLot;
        }

        private double ResolveCommissionPerLot()
        {
            if (_config?.Execution != null)
            {
                if (_config.Execution.CommissionRoundtripUsdPerLot > 0)
                {
                    return _config.Execution.CommissionRoundtripUsdPerLot;
                }

                if (_config.Execution.CommissionRoundturnUsdPerLot > 0)
                {
                    return _config.Execution.CommissionRoundturnUsdPerLot;
                }

                if (_config.Execution.FeePerTrade > 0)
                {
                    return _config.Execution.FeePerTrade;
                }
            }

            if (!double.IsNaN(EnvCommissionPerLot) && EnvCommissionPerLot > 0)
            {
                return EnvCommissionPerLot;
            }

            return 0.0;
        }

        private double? ComputeSpreadCost(OrderLifecycleState state)
        {
            var pvu = state.PointValuePerUnit;
            if (!pvu.HasValue || pvu.Value <= 0)
            {
                return null;
            }

            var units = state.FilledUnits ?? state.RequestedUnits;
            if (!units.HasValue || units.Value <= 0)
            {
                return null;
            }

            var spread = GetSpreadPrice(state);
            if (!spread.HasValue || spread.Value <= 0)
            {
                return null;
            }

            return spread.Value * units.Value * pvu.Value;
        }

        private double? GetSpreadPrice(OrderLifecycleState state)
        {
            static double? Diff(OrderQuoteEnvelope.QuoteSnapshot? snapshot)
            {
                if (snapshot?.Bid.HasValue == true && snapshot.Ask.HasValue)
                {
                    var diff = snapshot.Ask.Value - snapshot.Bid.Value;
                    if (diff > 0)
                    {
                        return diff;
                    }
                }

                return null;
            }

            var diff = Diff(state.Quote.Request);
            if (diff.HasValue)
            {
                return diff;
            }

            diff = Diff(state.Quote.Fill);
            if (diff.HasValue)
            {
                return diff;
            }

            if (!double.IsNaN(EnvSpreadPips) && EnvSpreadPips > 0)
            {
                var pip = state.PipSize;
                if (!pip.HasValue || pip.Value <= 0)
                {
                    var bid = state.Quote.Fill?.Bid ?? state.Quote.Request?.Bid;
                    var ask = state.Quote.Fill?.Ask ?? state.Quote.Request?.Ask;
                    if (bid.HasValue && ask.HasValue)
                    {
                        pip = OrderQuoteTelemetry.GuessPipSize(state.Quote.Symbol, bid.Value, ask.Value);
                    }
                }

                if (pip.HasValue && pip.Value > 0)
                {
                    return EnvSpreadPips * pip.Value;
                }
            }

            return null;
        }

        private double? ComputeSlippagePips(OrderLifecycleState state, string? normalizedSide, double? execPrice, double? intendedPrice)
        {
            var side = NormalizeSide(normalizedSide ?? state.Side);
            if (string.IsNullOrEmpty(side) || !execPrice.HasValue)
            {
                return null;
            }

            state.PipSize ??= DeterminePipSize(state);
            var pipSize = state.PipSize;
            if (!pipSize.HasValue || pipSize.Value <= 0)
            {
                return null;
            }

            if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase))
            {
                var reference = state.Quote.Request?.Ask ?? state.Quote.Fill?.Ask ?? intendedPrice;
                if (!reference.HasValue)
                {
                    return null;
                }

                return (execPrice.Value - reference.Value) / pipSize.Value;
            }

            if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
            {
                var reference = state.Quote.Request?.Bid ?? state.Quote.Fill?.Bid ?? intendedPrice;
                if (!reference.HasValue)
                {
                    return null;
                }

                return (reference.Value - execPrice.Value) / pipSize.Value;
            }

            return null;
        }

        private static double? DeterminePipSize(OrderLifecycleState state)
        {
            var pip = GetPipSizeFromSnapshot(state.Quote.Request, state.Quote.Symbol);
            if (pip.HasValue && pip.Value > 0)
            {
                return pip;
            }

            pip = GetPipSizeFromSnapshot(state.Quote.Fill, state.Quote.Symbol);
            if (pip.HasValue && pip.Value > 0)
            {
                return pip;
            }

            return null;
        }

        private static double? GetPipSizeFromSnapshot(OrderQuoteEnvelope.QuoteSnapshot? snapshot, string? symbol)
        {
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.SpreadPips.HasValue && snapshot.SpreadPips.Value > 0 && snapshot.Bid.HasValue && snapshot.Ask.HasValue)
            {
                var diff = snapshot.Ask.Value - snapshot.Bid.Value;
                if (diff > 0)
                {
                    return diff / snapshot.SpreadPips.Value;
                }
            }

            if (snapshot.Bid.HasValue && snapshot.Ask.HasValue)
            {
                return OrderQuoteTelemetry.GuessPipSize(symbol, snapshot.Bid.Value, snapshot.Ask.Value);
            }

            return null;
        }

        private static string? NormalizeSide(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim().ToUpperInvariant();
            return text switch
            {
                "BUY" or "B" or "LONG" => "BUY",
                "SELL" or "S" or "SHORT" => "SELL",
                _ => text
            };
        }

        private static double TryGetEnvDouble(string name)
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return double.NaN;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
            catch
            {
                // ignore
            }

            return double.NaN;
        }

        private static void MergeQuoteTelemetry(OrderQuoteEnvelope target, OrderQuoteEnvelope? source)
        {
            if (source == null) return;
            if (!string.IsNullOrWhiteSpace(source.Symbol)) target.Symbol = source.Symbol;

            if (source.Request != null)
            {
                target.Request ??= new OrderQuoteEnvelope.QuoteSnapshot();
                target.Request.Bid = source.Request.Bid ?? target.Request.Bid;
                target.Request.Ask = source.Request.Ask ?? target.Request.Ask;
                target.Request.SpreadPips = source.Request.SpreadPips ?? target.Request.SpreadPips;
                target.Request.TimestampUtc = source.Request.TimestampUtc ?? target.Request.TimestampUtc;
            }

            if (source.Fill != null)
            {
                target.Fill ??= new OrderQuoteEnvelope.QuoteSnapshot();
                target.Fill.Bid = source.Fill.Bid ?? target.Fill.Bid;
                target.Fill.Ask = source.Fill.Ask ?? target.Fill.Ask;
                target.Fill.SpreadPips = source.Fill.SpreadPips ?? target.Fill.SpreadPips;
                target.Fill.TimestampUtc = source.Fill.TimestampUtc ?? target.Fill.TimestampUtc;
            }

            target.RequestServerTime ??= source.RequestServerTime;
            target.FillServerTime ??= source.FillServerTime;
            if (!string.IsNullOrEmpty(source.SourceServer))
            {
                target.SourceServer = source.SourceServer;
            }
        }

        private static string FormatQuoteValue(double? value)
        {
            if (!value.HasValue) return "NA";
            return value.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string? ToIso(DateTime? dt)
        {
            return dt.HasValue ? dt.Value.ToString("o", CultureInfo.InvariantCulture) : null;
        }

        private void CaptureRequestQuote(OrderLifecycleState state, string? session)
        {
            var symbol = ResolveSymbol(session, state);
            state.Quote.Symbol ??= symbol ?? session;
            state.Quote.RequestServerTime ??= _clock();

            if (_quoteTelemetry == null || string.IsNullOrEmpty(state.Quote.Symbol))
            {
                return;
            }

            try
            {
                _quoteTelemetry.TrackSymbol(state.Quote.Symbol);
                var snapshot = _quoteTelemetry.Capture(state.Quote.Symbol);
                if (snapshot == null) return;

                state.Quote.Request ??= new OrderQuoteEnvelope.QuoteSnapshot();
                state.Quote.Request.Bid = snapshot.Bid ?? state.Quote.Request.Bid;
                state.Quote.Request.Ask = snapshot.Ask ?? state.Quote.Request.Ask;
                state.Quote.Request.SpreadPips = snapshot.SpreadPips ?? state.Quote.Request.SpreadPips;
                state.Quote.Request.TimestampUtc = snapshot.TimestampUtc ?? state.Quote.Request.TimestampUtc;
                state.Quote.RequestServerTime = snapshot.TimestampUtc ?? state.Quote.RequestServerTime;
                if (!string.IsNullOrEmpty(snapshot.SourceServer))
                {
                    state.Quote.SourceServer = snapshot.SourceServer;
                }
            }
            catch
            {
                // ignored
            }
        }

        private void CaptureFillQuote(OrderLifecycleState state, string? session, DateTime serverTime)
        {
            var symbol = ResolveSymbol(session, state);
            state.Quote.Symbol ??= symbol ?? session;
            state.Quote.FillServerTime ??= serverTime;

            if (_quoteTelemetry == null || string.IsNullOrEmpty(state.Quote.Symbol))
            {
                return;
            }

            try
            {
                _quoteTelemetry.TrackSymbol(state.Quote.Symbol);
                var snapshot = _quoteTelemetry.Capture(state.Quote.Symbol);
                if (snapshot == null) return;

                state.Quote.Fill ??= new OrderQuoteEnvelope.QuoteSnapshot();
                state.Quote.Fill.Bid = snapshot.Bid ?? state.Quote.Fill.Bid;
                state.Quote.Fill.Ask = snapshot.Ask ?? state.Quote.Fill.Ask;
                state.Quote.Fill.SpreadPips = snapshot.SpreadPips ?? state.Quote.Fill.SpreadPips;
                state.Quote.Fill.TimestampUtc = snapshot.TimestampUtc ?? state.Quote.Fill.TimestampUtc;
                if (!string.IsNullOrEmpty(snapshot.SourceServer))
                {
                    state.Quote.SourceServer = snapshot.SourceServer;
                }
            }
            catch
            {
                // ignored
            }
        }

        private static string? ResolveSymbol(string? session, OrderLifecycleState state)
        {
            if (!string.IsNullOrWhiteSpace(session)) return session;
            if (!string.IsNullOrWhiteSpace(state.Quote.Symbol)) return state.Quote.Symbol;
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_executor != null)
            {
                try { _executor.OnFill -= OnExecutorFill; } catch { }
            }
        }
    }
}
