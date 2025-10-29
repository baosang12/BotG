using System;
using System.Threading;
using System.Threading.Tasks;

namespace BotG.Preflight
{
    public interface IL1TickSource
    {
        DateTime? LastTickUtc { get; }
        Task<bool> WaitForNextTickAsync(TimeSpan timeout, CancellationToken ct);
    }
}
