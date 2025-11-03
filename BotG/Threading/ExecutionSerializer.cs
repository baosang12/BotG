using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotG.Threading
{
    /// <summary>
    /// Thread-safe execution serializer using SemaphoreSlim.
    /// Prevents concurrent execution of trading operations to eliminate race conditions.
    /// 
    /// Usage:
    ///   var serializer = new ExecutionSerializer();
    ///   await serializer.RunAsync(async () => { /* operation */ });
    /// 
    /// Key safety properties:
    /// - Operations execute sequentially (one at a time)
    /// - Async/await prevents thread pool starvation
    /// - Exceptions propagate to caller for proper handling
    /// </summary>
    public sealed class ExecutionSerializer : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private volatile bool _disposed;

        public ExecutionSerializer()
        {
            // maxCount=1 ensures only one operation at a time
            _semaphore = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Execute async operation with exclusive access (no concurrent operations).
        /// Blocks until previous operation completes, then runs this operation.
        /// </summary>
        /// <typeparam name="T">Return type</typeparam>
        /// <param name="operation">Async operation to execute</param>
        /// <param name="cancellationToken">Cancellation token (optional)</param>
        /// <returns>Result from operation</returns>
        /// <exception cref="ObjectDisposedException">If serializer already disposed</exception>
        public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExecutionSerializer));
            
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            try
            {
                // Wait for exclusive access
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    // Run operation with exclusive access
                    return await operation();
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    // Convert TaskCanceledException → OperationCanceledException for consistent handling
                    throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
                }
                finally
                {
                    // Always release semaphore, even on exception
                    _semaphore.Release();
                }
            }
            catch (TaskCanceledException ex)
            {
                // From WaitAsync or operation - convert to OperationCanceledException
                throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
            }
            catch (OperationCanceledException)
            {
                // Already OperationCanceledException - pass through
                throw;
            }
        }

        /// <summary>
        /// Execute async operation (void return) with exclusive access.
        /// </summary>
        public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExecutionSerializer));
            
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            try
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    await operation();
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    // Convert TaskCanceledException → OperationCanceledException for consistent handling
                    throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (TaskCanceledException ex)
            {
                // From WaitAsync or operation - convert to OperationCanceledException
                throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
            }
            catch (OperationCanceledException)
            {
                // Already OperationCanceledException - pass through
                throw;
            }
        }

        /// <summary>
        /// Execute synchronous operation with exclusive access.
        /// Wraps sync code in Task.Run for compatibility with async model.
        /// </summary>
        public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExecutionSerializer));
            
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            try
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    // Run sync operation on thread pool to avoid blocking
                    return await Task.Run(operation, cancellationToken);
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    // Convert TaskCanceledException → OperationCanceledException for consistent handling
                    throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (TaskCanceledException ex)
            {
                // From WaitAsync or operation - convert to OperationCanceledException
                throw new OperationCanceledException("Execution cancelled by safety timeout", ex);
            }
            catch (OperationCanceledException)
            {
                // Already OperationCanceledException - pass through
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore?.Dispose();
        }
    }
}
