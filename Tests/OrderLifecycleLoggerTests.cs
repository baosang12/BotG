using System;
using System.IO;
using System.Globalization;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class OrderLifecycleLoggerTests
    {
        [Fact]
        public void WritesHeaderAndRow_WithRequiredColumns()
        {
            var temp = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var log = new OrderLifecycleLogger(temp, "orders_test.csv");
            log.LogV2("REQUEST", "ORD-1", "ORD-1", "Buy", "BUY", "Market", 100.0, 99.95, null, null, null, 1, null, "REQUEST", "test", "UNIT");
            var path = Path.Combine(temp, "orders_test.csv");
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 2);
            Assert.StartsWith("phase,timestamp_iso,epoch_ms,orderId,intendedPrice,stopLoss,execPrice,", lines[0]);
            Assert.Contains(",client_order_id,side,action,type,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled,session,host", lines[0]);
            Assert.EndsWith(",symbol,bid_at_request,ask_at_request,spread_pips_at_request,bid_at_fill,ask_at_fill,spread_pips_at_fill,request_server_time,fill_server_time,timestamp,requested_lots,commission_usd,spread_cost_usd,slippage_pips", lines[0]);
            Assert.Contains("ORD-1", lines[1]);
        }

        [Fact]
        public void SlippagePipsIgnoresPipetteScaledSpread()
        {
            var temp = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var log = new OrderLifecycleLogger(temp, "orders_slippage.csv");
            var quotes = new OrderQuoteEnvelope
            {
                Symbol = "EURUSD",
                Request = new OrderQuoteEnvelope.QuoteSnapshot
                {
                    Bid = 1.0000,
                    Ask = 1.0002,
                    SpreadPips = 20.0
                }
            };

            log.LogV2(
                phase: "FILL",
                orderId: "ORD-SLIP",
                clientOrderId: "ORD-SLIP",
                side: "Buy",
                action: "BUY",
                type: "Market",
                intendedPrice: 1.0002,
                stopLoss: null,
                execPrice: 1.0003,
                theoreticalLots: 1.0,
                theoreticalUnits: 100000,
                requestedVolume: 100000,
                filledSize: 100000,
                status: "FILL",
                reason: null,
                session: "EURUSD",
                quotes: quotes);

            var path = Path.Combine(temp, "orders_slippage.csv");
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 2);
            var lastLine = lines[^1];
            var lastCell = ExtractLastColumn(lastLine);
            Assert.True(double.TryParse(lastCell, NumberStyles.Float, CultureInfo.InvariantCulture, out var slippagePips));
            Assert.Equal(1.0, slippagePips, 3);
        }

        private static string ExtractLastColumn(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            bool inQuotes = false;
            for (int i = line.Length - 1; i >= 0; i--)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    return line[(i + 1)..];
                }
            }

            return line;
        }
    }
}
