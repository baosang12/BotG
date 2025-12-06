using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;
using Bot3.Core;
using Execution;
using BotG.Tests.Fakes;
using BotG.RiskManager;
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
            TelemetryContext.ResetForTesting();
            var cfg = TelemetryConfig.Load(null, ensureRunFolder: true, useCache: false);
            TelemetryContext.InitOnce(cfg);
            var logFolder = string.IsNullOrWhiteSpace(TelemetryContext.RunFolder)
                ? TelemetryContext.Config.LogPath
                : TelemetryContext.RunFolder;

            var strategies = new List<IStrategy>();

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
            var signal = new Signal { StrategyName = "test", Action = TradeAction.Buy, Price = price, StopLoss = price - stopDist };

            // Act
            execModule.Execute(signal, price);

            // Assert: volume approx equals expectedUnits (rounded inside executor)
            Assert.InRange(fakeExec.LastVolume, expectedUnits - 0.6, expectedUnits + 0.6);

            // Assert: orders.csv has a REQUEST row with requestedVolume
            var ordersPath = Path.Combine(logFolder, "orders.csv");
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

        [Fact]
        public void Execute_AdjustsStopAndTakeProfit_ForFeePips()
        {
            var temp = Path.Combine(Path.GetTempPath(), "botg-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("BOTG_LOG_PATH", temp);
            TelemetryContext.ResetForTesting();
            TelemetryContext.InitOnce(TelemetryConfig.Load(null, ensureRunFolder: true, useCache: false));

            var strategies = new List<IStrategy>();
            var rm = new RiskManager.RiskManager();
            rm.Initialize(new RiskSettings { MaxLotsPerTrade = 5.0, LotSizeDefault = 1000 });
            rm.SetEquityOverrideForTesting(10000);

            var fakeExec = new FakeOrderExecutor();
            var execModule = new Execution.ExecutionModule(strategies, fakeExec, null, rm);

            var feeField = typeof(Execution.ExecutionModule).GetField("_feePipsRoundtrip", BindingFlags.NonPublic | BindingFlags.Instance);
            var pipField = typeof(Execution.ExecutionModule).GetField("_symbolPipSize", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(feeField);
            Assert.NotNull(pipField);
            feeField!.SetValue(execModule, 0.6); // 0.6 pip phí roundtrip
            pipField!.SetValue(execModule, 0.0001); // pip size tiêu chuẩn Forex

            double price = 1.2345;
            double strategySlPrice = price - (15 * 0.0001);
            double strategyTpPrice = price + (30 * 0.0001);

            var signal = new Signal
            {
                StrategyName = "fee-test",
                Action = TradeAction.Buy,
                Price = price,
                StopLoss = strategySlPrice,
                TakeProfit = strategyTpPrice
            };

            execModule.Execute(signal, price);

            double expectedSl = price - (14.4 * 0.0001);
            double expectedTp = price + (30.6 * 0.0001);

            Assert.InRange(signal.StopLoss!.Value, expectedSl - 1e-6, expectedSl + 1e-6);
            Assert.InRange(signal.TakeProfit!.Value, expectedTp - 1e-6, expectedTp + 1e-6);
        }

        // no Robot shim needed; tests use injected executor and null bot
    }
}
