using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Bot3.Core;
using Execution;
using BotG.Tests.Fakes;
using RiskManager;
using Strategies;
using Telemetry;
using cAlgo.API; // use Symbol shim

namespace BotG.Tests
{
    public class ExecutionModuleSizingTests
    {
        [Fact]
        public void Execute_UsesRiskManagerUnits()
        {
            // Arrange: telemetry to a temp folder to inspect orders.csv
            var temp = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("BOTG_LOG_PATH", temp);
            TelemetryContext.InitOnce(TelemetryConfig.Load());

            var strategies = new List<IStrategy<TradeSignal>>();

            // RiskManager with lot-based sizing
            var rm = new RiskManager.RiskManager();
            rm.Initialize(new RiskSettings { MaxLotsPerTrade = 10.0, LotSizeDefault = 100 });
            rm.SetEquityOverrideForTesting(10000);
            var sym = new Symbol { TickSize = 0.01, TickValue = 0.01, LotSize = 100.0 };
            rm.GetSettings().PointValuePerUnit = 0.0; // force auto-compute path
            rm.SetSymbolReference(sym);

            // Expected units for stop distance = 0.05
            double stopDist = 0.05;
            double expectedUnits = rm.CalculateOrderSize(stopDist, 0.0);
            Assert.True(expectedUnits > 0);

            // Fake executor
            var fakeExec = new FakeOrderExecutor();

            // ExecutionModule with injected executor and rm
            // Pass null bot; ExecutionModule handles this via GetSymbolName() and injected executor
            var execModule = new Execution.ExecutionModule(strategies, fakeExec, null, rm);

            var price = 100.0;
            var signal = new TradeSignal { Action = TradeAction.Buy, Price = price, StopLoss = price - stopDist };

            // Act
            execModule.Execute(signal, price);

            // Assert: volume approx equals expectedUnits (rounded inside executor)
            Assert.InRange(fakeExec.LastVolume, expectedUnits - 0.6, expectedUnits + 0.6);

            // Assert: orders.csv has a REQUEST row with requestedVolume
            var ordersPath = Path.Combine(temp, "orders.csv");
            Assert.True(File.Exists(ordersPath));
            var lines = File.ReadAllLines(ordersPath);
            Assert.True(lines.Length >= 2);
            bool hasRequest = false;
            foreach (var line in lines)
            {
                var cols = line.Split(',');
                if (cols.Length > 0 && cols[0] == "REQUEST") { hasRequest = true; break; }
            }
            Assert.True(hasRequest, "orders.csv should contain a REQUEST row");
        }

    // no Robot shim needed; tests use injected executor and null bot
    }
}
