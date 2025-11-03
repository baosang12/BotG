using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BotG.Threading;
using Xunit;

namespace BotG.Tests.Threading
{
    public class ExecutionSerializerTests
    {
        [Fact]
        public async Task RunAsync_ExecutesOperationSequentially()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            int executionCount = 0;
            int maxConcurrent = 0;
            int currentConcurrent = 0;
            var tasks = new Task[10];

            // Act - Launch 10 concurrent operations
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = serializer.RunAsync(async () =>
                {
                    int concurrent = Interlocked.Increment(ref currentConcurrent);
                    int max = Math.Max(maxConcurrent, concurrent);
                    Interlocked.CompareExchange(ref maxConcurrent, max, maxConcurrent);

                    await Task.Delay(10); // Simulate work
                    Interlocked.Increment(ref executionCount);

                    Interlocked.Decrement(ref currentConcurrent);
                });
            }

            await Task.WhenAll(tasks);

            // Assert - All operations executed, but never more than 1 concurrent
            Assert.Equal(10, executionCount);
            Assert.Equal(1, maxConcurrent); // Critical: Never more than 1 concurrent
        }

        [Fact]
        public async Task RunAsync_WithReturnValue_ReturnsCorrectResult()
        {
            // Arrange
            var serializer = new ExecutionSerializer();

            // Act
            var result = await serializer.RunAsync(async () =>
            {
                await Task.Delay(10);
                return 42;
            });

            // Assert
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task RunAsync_SyncOverload_ExecutesOperation()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            int executionCount = 0;

            // Act
            var result = await serializer.RunAsync(() =>
            {
                Interlocked.Increment(ref executionCount);
                return "test";
            });

            // Assert
            Assert.Equal(1, executionCount);
            Assert.Equal("test", result);
        }

        [Fact]
        public async Task RunAsync_ExceptionPropagates()
        {
            // Arrange
            var serializer = new ExecutionSerializer();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await serializer.RunAsync(async () =>
                {
                    await Task.Delay(10);
                    throw new InvalidOperationException("Test exception");
                });
            });
        }

        [Fact]
        public async Task RunAsync_AfterException_NextOperationExecutes()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            int successCount = 0;

            // Act - First operation throws
            try
            {
                await serializer.RunAsync(async () =>
                {
                    await Task.Delay(10);
                    throw new InvalidOperationException("Test exception");
                });
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Second operation should still work
            await serializer.RunAsync(async () =>
            {
                await Task.Delay(10);
                Interlocked.Increment(ref successCount);
            });

            // Assert
            Assert.Equal(1, successCount);
        }

        [Fact]
        public async Task RunAsync_AfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            serializer.Dispose();

            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await serializer.RunAsync(async () => await Task.Delay(10));
            });
        }

        [Fact]
        public async Task RunAsync_Cancellation_ThrowsOperationCanceledException()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            var cts = new CancellationTokenSource();

            // Start long-running operation
            var task1 = serializer.RunAsync(async () =>
            {
                await Task.Delay(1000);
            });

            // Wait a bit for first operation to start
            await Task.Delay(50);

            // Cancel second operation while waiting for semaphore
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await serializer.RunAsync(async () =>
                {
                    await Task.Delay(10);
                }, cts.Token);
            });
        }

        [Fact]
        public async Task RunAsync_PreservesExecutionOrder()
        {
            // Arrange
            var serializer = new ExecutionSerializer();
            var executionOrder = new System.Collections.Concurrent.ConcurrentQueue<int>();

            // Act - Queue operations in order
            var tasks = new Task[5];
            for (int i = 0; i < 5; i++)
            {
                int index = i; // Capture loop variable
                tasks[i] = serializer.RunAsync(async () =>
                {
                    await Task.Delay(10);
                    executionOrder.Enqueue(index);
                });
            }

            await Task.WhenAll(tasks);

            // Assert - Execution order matches submission order
            Assert.Equal(5, executionOrder.Count);
            var orderArray = executionOrder.ToArray();
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(i, orderArray[i]);
            }
        }
    }
}
