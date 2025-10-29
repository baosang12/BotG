using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using BotG.Preflight;

namespace BotG.Tests
{
    public class PreflightTests
    {
        private class MockTickSource : IL1TickSource
        {
            public DateTime? LastTickUtc { get; set; }
            private readonly bool _willTimeout;
            private readonly int _delayMs;

            public MockTickSource(bool willTimeout = false, int delayMs = 0)
            {
                _willTimeout = willTimeout;
                _delayMs = delayMs;
            }

            public async Task<bool> WaitForNextTickAsync(TimeSpan timeout, CancellationToken ct)
            {
                if (_willTimeout)
                {
                    await Task.Delay(timeout, ct);
                    return false;
                }

                if (_delayMs > 0)
                    await Task.Delay(_delayMs, ct);

                return true;
            }
        }

        [Fact]
        public async Task Pass_when_live_tick_recent()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var mockTick = new MockTickSource { LastTickUtc = now.AddSeconds(-1) }; // 1 second old
            var preflight = new PreflightLiveFreshness(
                mockTick,
                () => now,
                thresholdSec: 5.0
            );

            // Act
            var result = await preflight.CheckAsync();

            // Assert
            Assert.True(result.Ok);
            Assert.Equal("live", result.Source);
            Assert.True(result.LastAgeSec <= 5.0);
        }

        [Fact]
        public async Task Fail_when_live_tick_missing_after_3s()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var mockTick = new MockTickSource(willTimeout: true); // Will timeout after 3s
            var preflight = new PreflightLiveFreshness(
                mockTick,
                () => now,
                thresholdSec: 5.0,
                fallbackCsvPath: "nonexistent.csv" // No fallback
            );

            // Act
            var result = await preflight.CheckAsync();

            // Assert
            Assert.False(result.Ok); // Should fail due to age > threshold
            Assert.Equal("l1_sample.csv", result.Source); // Fell back to CSV
            Assert.True(result.LastAgeSec > 5.0); // Should be 999.0 (no file)
        }

        [Fact]
        public async Task Use_fallback_when_file_recent()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var mockTick = new MockTickSource(willTimeout: true); // No live tick
            
            // Create temp CSV with recent tick
            var tempCsv = System.IO.Path.GetTempFileName();
            var recentTick = now.AddSeconds(-2).ToString("o");
            System.IO.File.WriteAllText(tempCsv, $"timestamp,bid,ask,volume\n{recentTick},1.1000,1.1001,100");

            var preflight = new PreflightLiveFreshness(
                mockTick,
                () => now,
                thresholdSec: 5.0,
                fallbackCsvPath: tempCsv
            );

            try
            {
                // Act
                var result = await preflight.CheckAsync();

                // Assert
                Assert.True(result.Ok); // Should pass with fallback
                Assert.Equal("l1_sample.csv", result.Source);
                Assert.True(result.LastAgeSec <= 5.0); // ~2 seconds old
            }
            finally
            {
                // Cleanup
                if (System.IO.File.Exists(tempCsv))
                    System.IO.File.Delete(tempCsv);
            }
        }
    }
}
