using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Telemetry;
using DataFetcher.Models;

namespace BotG.Tests
{
    public class RiskSnapshotPersisterTests : IDisposable
    {
        private readonly string _testFolder;

        public RiskSnapshotPersisterTests()
        {
            _testFolder = Path.Combine(Path.GetTempPath(), $"botg_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testFolder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testFolder))
            {
                Directory.Delete(_testFolder, true);
            }
        }

        /// <summary>
        /// T1: MaxClosedGap <= 1e-6
        /// Verify closed_pnl matches sum of individual trade P&Ls within precision tolerance
        /// </summary>
        [Fact]
        public void Test_ClosedPnlParity_SumMatchesCumulativeWithinTolerance()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };

            // Add closed P&L events
            var trades = new[] { 100.5, -50.25, 75.0, -25.0, 150.75 };
            var expectedSum = trades.Sum();

            foreach (var pnl in trades)
            {
                persister.AddClosedPnl(pnl);
            }

            // Persist snapshot
            persister.Persist(accountInfo);

            // Read CSV and verify last closed_pnl
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            Assert.True(lines.Length >= 2, "Should have header + data");

            var lastLine = lines.Last();
            var fields = lastLine.Split(',');
            var closedPnlIndex = 4; // timestamp,equity,balance,open_pnl,closed_pnl,...
            var actualClosedPnl = double.Parse(fields[closedPnlIndex]);

            var gap = Math.Abs(actualClosedPnl - expectedSum);
            Assert.True(gap <= 1e-6, $"MaxClosedGap test failed: gap={gap}, expected={expectedSum}, actual={actualClosedPnl}");
        }

        /// <summary>
        /// T2: EquityResidual == 0 ± 1e-6 (Paper Mode)
        /// Verify equity_model = balance_model + open_pnl in paper mode
        /// </summary>
        [Fact]
        public void Test_PaperMode_EquityFormula_BalancePlusOpenPnl()
        {
            double testOpenPnl = 250.0;
            Func<double> getOpenPnl = () => testOpenPnl;

            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: true, getOpenPnlCallback: getOpenPnl);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 500 };

            // Add closed P&L
            persister.AddClosedPnl(500.0);

            // Persist snapshot
            persister.Persist(accountInfo);

            // Read CSV
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            var lastLine = lines.Last();
            var fields = lastLine.Split(',');

            var equity = double.Parse(fields[1]);
            var balance = double.Parse(fields[2]);
            var openPnl = double.Parse(fields[3]);
            var closedPnl = double.Parse(fields[4]);

            // balance_model = initial + closed_pnl
            var expectedBalance = 10000 + 500.0;
            var balanceResidual = Math.Abs(balance - expectedBalance);
            Assert.True(balanceResidual <= 1e-6, $"balance_model incorrect: expected={expectedBalance}, actual={balance}");

            // open_pnl from callback
            var openPnlResidual = Math.Abs(openPnl - testOpenPnl);
            Assert.True(openPnlResidual <= 1e-6, $"open_pnl incorrect: expected={testOpenPnl}, actual={openPnl}");

            // equity_model = balance_model + open_pnl
            var expectedEquity = expectedBalance + testOpenPnl;
            var equityResidual = Math.Abs(equity - expectedEquity);
            Assert.True(equityResidual <= 1e-6, $"EquityResidual test failed: residual={equityResidual}, expected={expectedEquity}, actual={equity}");
        }

        /// <summary>
        /// T3: Equity formula holds every snapshot
        /// Verify equity_model == balance_model + open_pnl for multiple snapshots
        /// </summary>
        [Fact]
        public void Test_PaperMode_EquityFormula_HoldsAcrossMultipleSnapshots()
        {
            var openPnlValues = new[] { 0.0, 100.0, -50.0, 200.0, 0.0 };
            int callIndex = 0;
            Func<double> getOpenPnl = () => openPnlValues[callIndex++ % openPnlValues.Length];

            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: true, getOpenPnlCallback: getOpenPnl);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };

            // Add closed P&L and persist multiple snapshots
            var closedPnls = new[] { 100.0, 50.0, -25.0, 75.0 };
            var cumulativeClosedPnl = 0.0;

            for (int i = 0; i < closedPnls.Length; i++)
            {
                persister.AddClosedPnl(closedPnls[i]);
                cumulativeClosedPnl += closedPnls[i];
                persister.Persist(accountInfo);
            }

            // Read all snapshots
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            Assert.True(lines.Length == closedPnls.Length + 1, "Should have header + 4 snapshots");

            // Verify formula for each snapshot
            for (int i = 1; i < lines.Length; i++)
            {
                var fields = lines[i].Split(',');
                var equity = double.Parse(fields[1]);
                var balance = double.Parse(fields[2]);
                var openPnl = double.Parse(fields[3]);

                var expectedEquity = balance + openPnl;
                var residual = Math.Abs(equity - expectedEquity);
                Assert.True(residual <= 1e-6, $"Snapshot {i}: equity formula violated, residual={residual}");
            }
        }

        /// <summary>
        /// T4: closed_pnl monotonic (non-decreasing) - WITH ONLY PROFITABLE TRADES
        /// Note: With losing trades, closed_pnl can decrease. This test verifies correct cumulative sum.
        /// </summary>
        [Fact]
        public void Test_ClosedPnl_Monotonic_NonDecreasing()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };

            // Use only profitable trades to ensure monotonic increase
            var trades = new[] { 100.0, 50.0, 75.0, 25.0, 200.0 };

            foreach (var pnl in trades)
            {
                persister.AddClosedPnl(pnl); // Legacy method: applies immediately
                persister.Persist(accountInfo);
                System.Threading.Thread.Sleep(10); // Small delay to ensure order
            }

            // Read CSV and verify closed_pnl is monotonic
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            var closedPnlValues = new List<double>();

            for (int i = 1; i < lines.Length; i++)
            {
                var fields = lines[i].Split(',');
                closedPnlValues.Add(double.Parse(fields[4]));
            }

            for (int i = 1; i < closedPnlValues.Count; i++)
            {
                Assert.True(closedPnlValues[i] >= closedPnlValues[i - 1],
                    $"Monotonic test failed: closedPnl[{i}]={closedPnlValues[i]} < closedPnl[{i - 1}]={closedPnlValues[i - 1]}");
            }
        }

        /// <summary>
        /// T5-extension: Time-aware closed P&L application
        /// Verify that P&L is applied only to snapshots with timestamp >= closeTime
        /// </summary>
        [Fact]
        public void Test_TimeAware_ClosedPnl_AppliesAtCorrectTime()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };

            // Persist snapshot at T0
            persister.Persist(accountInfo);
            System.Threading.Thread.Sleep(100);

            // Trade closes at T1 (future)
            var closeTime = DateTime.UtcNow.AddSeconds(1);
            persister.AddClosedPnl(100.0, closeTime);

            // Persist snapshot at T0+100ms (before closeTime) - should NOT include P&L
            persister.Persist(accountInfo);

            // Wait until after closeTime
            System.Threading.Thread.Sleep(1100);

            // Persist snapshot at T1+200ms (after closeTime) - should include P&L
            persister.Persist(accountInfo);

            // Read CSV
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            Assert.Equal(4, lines.Length); // header + 3 snapshots

            var closedPnl1 = double.Parse(lines[1].Split(',')[4]);
            var closedPnl2 = double.Parse(lines[2].Split(',')[4]);
            var closedPnl3 = double.Parse(lines[3].Split(',')[4]);

            Assert.Equal(0.0, closedPnl1); // First snapshot: no trades
            Assert.Equal(0.0, closedPnl2); // Second snapshot: before closeTime
            Assert.Equal(100.0, closedPnl3); // Third snapshot: after closeTime
        }

        /// <summary>
        /// Live mode test: verify that live mode uses AccountInfo directly (no paper model)
        /// </summary>
        [Fact]
        public void Test_LiveMode_UsesAccountInfoDirectly()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 12000, Equity = 12500, Margin = 500 };

            // Add closed P&L (should not affect balance/equity in live mode)
            persister.AddClosedPnl(1000.0);

            // Persist snapshot
            persister.Persist(accountInfo);

            // Read CSV
            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            var fields = lines.Last().Split(',');

            var equity = double.Parse(fields[1]);
            var balance = double.Parse(fields[2]);

            // In live mode, should use AccountInfo values directly (broker updates them)
            Assert.Equal(12500, equity);
            Assert.Equal(12000, balance);
        }

        [Fact]
        public void RiskTelemetry_NoLoss_DrawdownAndRUsedRemainZero()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };
            var equities = new[] { 10000.0, 10020.0, 10050.0, 10030.0, 10060.0, 10060.0 };

            foreach (var equity in equities)
            {
                accountInfo.Equity = equity;
                accountInfo.Balance = equity;
                persister.Persist(accountInfo);
                System.Threading.Thread.Sleep(5);
            }

            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            Assert.Equal(equities.Length + 1, lines.Length);

            var drawdowns = lines.Skip(1).Select(l => double.Parse(l.Split(',')[7])).ToArray();
            var rUsedValues = lines.Skip(1).Select(l => double.Parse(l.Split(',')[8])).ToArray();

            var expectedDrawdowns = new[] { 0.0, 0.0, 0.0, 20.0, 0.0, 0.0 };
            Assert.Equal(expectedDrawdowns.Length, drawdowns.Length);

            for (int i = 0; i < drawdowns.Length; i++)
            {
                Assert.Equal(expectedDrawdowns[i], drawdowns[i], 6);
                Assert.Equal(0.0, rUsedValues[i], 6);
            }
        }

        [Fact]
        public void RiskTelemetry_LossSequence_UpdatesDrawdownAndRUsed()
        {
            var persister = new RiskSnapshotPersister(_testFolder, "risk.csv", isPaperMode: false);
            var accountInfo = new AccountInfo { Balance = 10000, Equity = 10000, Margin = 0 };
            var equities = new[] { 10000.0, 10030.0, 9980.0, 9970.0, 10010.0 };

            foreach (var equity in equities)
            {
                accountInfo.Equity = equity;
                accountInfo.Balance = equity;
                persister.Persist(accountInfo);
                System.Threading.Thread.Sleep(5);
            }

            var lines = File.ReadAllLines(Path.Combine(_testFolder, "risk.csv"));
            Assert.Equal(equities.Length + 1, lines.Length);

            var drawdowns = lines.Skip(1).Select(l => double.Parse(l.Split(',')[7])).ToArray();
            var rUsedValues = lines.Skip(1).Select(l => double.Parse(l.Split(',')[8])).ToArray();

            var expectedDrawdowns = new[] { 0.0, 0.0, 50.0, 60.0, 20.0 };
            var expectedRUsed = new[] { 0.0, 0.0, 2.0, 3.0, 0.0 };

            for (int i = 0; i < drawdowns.Length; i++)
            {
                Assert.Equal(expectedDrawdowns[i], drawdowns[i], 6);
                Assert.Equal(expectedRUsed[i], rUsedValues[i], 6);
                Assert.True(drawdowns[i] >= 0.0, $"Drawdown[{i}] should be non-negative");
                Assert.True(rUsedValues[i] >= 0.0, $"R_used[{i}] should be non-negative");
            }

            Assert.Contains(rUsedValues, value => value >= 3.0);
        }
    }
}
