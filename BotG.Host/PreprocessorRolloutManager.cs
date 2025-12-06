using Microsoft.Extensions.Logging;

namespace BotG.Host;

public sealed class PreprocessorRolloutManager
{
    private readonly ILogger<PreprocessorRolloutManager> _logger;
    private readonly TimeSpan _minimumPhaseDuration = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private DateTime _lastAdvanceUtc = DateTime.UtcNow;
    private int _currentPhase = 1;

    public PreprocessorRolloutManager(ILogger<PreprocessorRolloutManager> logger)
    {
        _logger = logger;
    }

    public int CurrentPhase
    {
        get { lock (_sync) { return _currentPhase; } }
    }

    public DateTime LastAdvancedUtc
    {
        get { lock (_sync) { return _lastAdvanceUtc; } }
    }

    public void AdvancePhaseIfReady()
    {
        lock (_sync)
        {
            if (_currentPhase >= 3)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastAdvanceUtc < _minimumPhaseDuration)
            {
                return;
            }

            _currentPhase++;
            _lastAdvanceUtc = now;
            _logger.LogInformation("Rollout advanced to phase {Phase} at {Timestamp}", _currentPhase, now);
        }
    }

    public object GetState()
    {
        lock (_sync)
        {
            return new
            {
                Phase = _currentPhase,
                LastAdvancedUtc = _lastAdvanceUtc,
                MinimumPhaseDurationMinutes = _minimumPhaseDuration.TotalMinutes
            };
        }
    }
}
