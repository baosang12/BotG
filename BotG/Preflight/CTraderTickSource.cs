using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotG.Preflight
{
    /// <summary>
    /// Adapter for cTrader Robot to track live ticks
    /// </summary>
    public class CTraderTickSource : IL1TickSource
    {
        private DateTime? _lastTickUtc;
        private readonly SemaphoreSlim _tickSignal = new SemaphoreSlim(0);

        public DateTime? LastTickUtc => _lastTickUtc;

        public void OnTick(DateTime serverTime)
        {
            _lastTickUtc = serverTime;
            
            // Signal waiting task (release if waiting)
            if (_tickSignal.CurrentCount == 0)
                _tickSignal.Release();
        }

        public async Task<bool> WaitForNextTickAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                return await _tickSignal.WaitAsync(timeout, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
