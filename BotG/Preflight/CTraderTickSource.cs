using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotG.Preflight
{
    /// <summary>
    /// Adapter for cTrader Robot to track live ticks (non-blocking event-based)
    /// </summary>
    public class CTraderTickSource : IL1TickSource
    {
        private DateTime? _lastTickUtc;
        private readonly ManualResetEventSlim _tickEvent = new ManualResetEventSlim(false);

        public DateTime? LastTickUtc => _lastTickUtc;

        public void OnTick(DateTime serverTime)
        {
            _lastTickUtc = serverTime;
            
            // Signal waiting task
            _tickEvent.Set();
        }

        public async Task<bool> WaitForNextTickAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                // Wait on background thread to avoid blocking
                return await Task.Run(() => _tickEvent.Wait(timeout, ct), ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
