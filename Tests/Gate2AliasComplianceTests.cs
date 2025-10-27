using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Telemetry;
using Xunit;

namespace BotG.Tests
{
    public class Gate2AliasComplianceTests
    {
        [Fact]
        public void OrderLifecycleLogger_ContainsGate2AliasHeaders()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var log = new OrderLifecycleLogger(temp, "orders_test.csv");
            
            // Act
            var path = Path.Combine(temp, "orders_test.csv");
            var header = File.ReadLines(path).FirstOrDefault() ?? string.Empty;
            
            // Assert - Gate2 aliases (CHANGE-001)
            Assert.Contains("latency", header); // latency = latency_ms
            Assert.Contains("request_id", header); // request_id = client_order_id ?? orderId
            Assert.Contains("ts_request", header); // ts_request = timestamp_request
            Assert.Contains("ts_ack", header); // ts_ack = timestamp_ack
            Assert.Contains("ts_fill", header); // ts_fill = timestamp_fill
            
            // Cleanup
            try { Directory.Delete(temp, true); } catch { }
        }

        [Fact]
        public void OrderLifecycleLogger_REQUEST_ACK_FILL_Cycle_PopulatesAliases()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var log = new OrderLifecycleLogger(temp, "orders_test.csv");

            // Act - Simulate REQUEST → ACK → FILL cycle
            log.LogV2("REQUEST", "ORD-123", "CLIENT-123", "BUY", "OPEN", "MARKET", 
                intendedPrice: 1.1000, stopLoss: 1.0950, execPrice: null, 
                theoreticalLots: 1.0, theoreticalUnits: 100000.0, 
                requestedVolume: 100000.0, filledSize: null, 
                status: "REQUEST", reason: null, session: "EURUSD");

            System.Threading.Thread.Sleep(10); // Small delay to ensure different timestamps

            log.LogV2("ACK", "ORD-123", "CLIENT-123", "BUY", "OPEN", "MARKET", 
                intendedPrice: 1.1000, stopLoss: 1.0950, execPrice: null, 
                theoreticalLots: 1.0, theoreticalUnits: 100000.0, 
                requestedVolume: 100000.0, filledSize: null, 
                status: "ACK", reason: null, session: "EURUSD");

            System.Threading.Thread.Sleep(10); // Small delay to ensure different timestamps

            log.LogV2("FILL", "ORD-123", "CLIENT-123", "BUY", "OPEN", "MARKET", 
                intendedPrice: 1.1000, stopLoss: 1.0950, execPrice: 1.1002, 
                theoreticalLots: 1.0, theoreticalUnits: 100000.0, 
                requestedVolume: 100000.0, filledSize: 100000.0, 
                status: "FILL", reason: null, session: "EURUSD");

            // Assert
            var path = Path.Combine(temp, "orders_test.csv");
            var lines = File.ReadAllLines(path);
            var header = lines[0];
            var headerColumns = header.Split(',');

            // Find alias column indices
            var latencyIdx = Array.IndexOf(headerColumns, "latency");
            var requestIdIdx = Array.IndexOf(headerColumns, "request_id");
            var tsRequestIdx = Array.IndexOf(headerColumns, "ts_request");
            var tsAckIdx = Array.IndexOf(headerColumns, "ts_ack");
            var tsFillIdx = Array.IndexOf(headerColumns, "ts_fill");

            Assert.True(latencyIdx >= 0, "latency column should exist");
            Assert.True(requestIdIdx >= 0, "request_id column should exist");
            Assert.True(tsRequestIdx >= 0, "ts_request column should exist");
            Assert.True(tsAckIdx >= 0, "ts_ack column should exist");
            Assert.True(tsFillIdx >= 0, "ts_fill column should exist");

            // Check FILL row (last row) has non-empty values
            var fillRow = lines.Last();
            var fillColumns = SplitCsvLine(fillRow);

            Assert.NotEmpty(fillColumns[latencyIdx]); // latency should be populated
            Assert.Equal("CLIENT-123", fillColumns[requestIdIdx]); // request_id = client_order_id
            Assert.NotEmpty(fillColumns[tsRequestIdx]); // ts_request should be populated
            Assert.NotEmpty(fillColumns[tsAckIdx]); // ts_ack should be populated
            Assert.NotEmpty(fillColumns[tsFillIdx]); // ts_fill should be populated

            // Verify timestamps are ISO 8601 format
            Assert.True(DateTime.TryParse(fillColumns[tsRequestIdx], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _), "ts_request should be valid ISO timestamp");
            Assert.True(DateTime.TryParse(fillColumns[tsFillIdx], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _), "ts_fill should be valid ISO timestamp");

            // Cleanup
            try { Directory.Delete(temp, true); } catch { }
        }

        [Fact]
        public void OrderLifecycleLogger_RequestId_FallsBackToOrderId_WhenClientOrderIdNull()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var log = new OrderLifecycleLogger(temp, "orders_test.csv");

            // Act - clientOrderId is null, should fallback to orderId
            log.LogV2("REQUEST", "ORD-999", clientOrderId: null, "BUY", "OPEN", "MARKET", 
                intendedPrice: 1.1000, stopLoss: null, execPrice: null, 
                theoreticalLots: null, theoreticalUnits: null, 
                requestedVolume: 100000.0, filledSize: null, 
                status: "REQUEST", reason: null, session: "EURUSD");

            // Assert
            var path = Path.Combine(temp, "orders_test.csv");
            var lines = File.ReadAllLines(path);
            var header = lines[0];
            var headerColumns = header.Split(',');
            var requestIdIdx = Array.IndexOf(headerColumns, "request_id");

            var row = lines[1];
            var columns = SplitCsvLine(row);

            Assert.Equal("ORD-999", columns[requestIdIdx]); // request_id should fallback to orderId

            // Cleanup
            try { Directory.Delete(temp, true); } catch { }
        }

        [Fact]
        public void RiskSnapshotPersister_ContainsGate2AliasHeader()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var persister = new RiskSnapshotPersister(temp, "risk_test.csv", isPaperMode: false);

            // Act
            var path = Path.Combine(temp, "risk_test.csv");
            var header = File.ReadLines(path).FirstOrDefault() ?? string.Empty;

            // Assert - Gate2 alias (CHANGE-001)
            Assert.Contains("ts", header.Split(',')); // ts = timestamp_utc

            // Cleanup
            try { Directory.Delete(temp, true); } catch { }
        }

        [Fact]
        public void RiskSnapshotPersister_Persist_PopulatesTsAlias()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var persister = new RiskSnapshotPersister(temp, "risk_test.csv", isPaperMode: false);
            var mockInfo = new DataFetcher.Models.AccountInfo
            {
                Balance = 10000,
                Equity = 10050,
                Margin = 500
            };

            // Act
            persister.Persist(mockInfo);

            // Assert
            var path = Path.Combine(temp, "risk_test.csv");
            var lines = File.ReadAllLines(path);
            var header = lines[0];
            var headerColumns = header.Split(',');
            var tsIdx = Array.IndexOf(headerColumns, "ts");
            var timestampUtcIdx = Array.IndexOf(headerColumns, "timestamp_utc");

            Assert.True(tsIdx >= 0, "ts column should exist");

            var row = lines[1];
            var columns = row.Split(',');

            // ts should equal timestamp_utc
            Assert.Equal(columns[timestampUtcIdx], columns[tsIdx]);
            Assert.NotEmpty(columns[tsIdx]);

            // Verify it's a valid ISO timestamp
            Assert.True(DateTime.TryParse(columns[tsIdx], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _), "ts should be valid ISO timestamp");

            // Cleanup
            try { Directory.Delete(temp, true); } catch { }
        }

        [Fact]
        public void TelemetryCollector_ContainsGate2AliasHeader()
        {
            // Arrange
            var temp = Path.Combine(Path.GetTempPath(), "botg-gate2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var collector = new TelemetryCollector(temp, "telemetry_test.csv", flushIntervalSeconds: 60);

            // Act
            var path = Path.Combine(temp, "telemetry_test.csv");
            var header = File.ReadLines(path).FirstOrDefault() ?? string.Empty;

            // Assert - Gate2 alias (CHANGE-001)
            Assert.Contains("timestamp", header.Split(',')); // timestamp = timestamp_iso

            // Cleanup
            collector.Dispose();
            try { Directory.Delete(temp, true); } catch { }
        }

        // Helper method to properly split CSV lines (handles quoted values)
        private static string[] SplitCsvLine(string line)
        {
            var result = new System.Collections.Generic.List<string>();
            var inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString());

            return result.ToArray();
        }
    }
}
