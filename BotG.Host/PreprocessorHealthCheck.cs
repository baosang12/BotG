using AnalysisModule.Preprocessor.Core;
using BotG.Config;
using BotG.Runtime.Preprocessor;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;

namespace BotG.Host;

public class PreprocessorHealthCheck : IHealthCheck
{
    private readonly PreprocessorRuntimeManager _manager;
    private readonly IOptionsMonitor<PreprocessorRuntimeConfig> _config;
    private readonly ILogger<PreprocessorHealthCheck> _logger;

    public PreprocessorHealthCheck(
        PreprocessorRuntimeManager manager,
        IOptionsMonitor<PreprocessorRuntimeConfig> config,
        ILogger<PreprocessorHealthCheck> logger)
    {
        _manager = manager;
        _config = config;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = _manager.GetStatus();
            var snapshot = _manager.LatestSnapshot;
            var config = _config.CurrentValue;

            var data = new Dictionary<string, object?>
            {
                ["State"] = status?.State.ToString() ?? "Stopped",
                ["ProcessedTicks"] = status?.ProcessedTicks ?? 0,
                ["LastTickUtc"] = status?.LastTickTimestampUtc,
                ["IsDegraded"] = status?.IsDegraded ?? false,
                ["DegradedReason"] = status?.DegradedReason ?? "None",
                ["ConfigEnabled"] = config?.Enabled,
                ["ConfiguredTimeframes"] = config?.Timeframes,
                ["ConfiguredIndicators"] = config?.Indicators?.Select(i => $"{i.Type}:{i.Timeframe}({i.Period})").ToArray(),
                ["SnapshotTimestamp"] = snapshot?.TimestampUtc,
                ["SnapshotIndicators"] = snapshot?.Indicators?.Count,
                ["SnapshotBars"] = snapshot?.LatestBars?.Count
            };

            var payload = data.ToDictionary(kvp => kvp.Key, kvp => (object)(kvp.Value ?? DBNull.Value));

            if (status is null)
            {
                _logger.LogWarning("Health check: preprocessor chưa khởi động");
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Preprocessor not started",
                    data: payload));
            }

            if (status.State != PreprocessorState.Running || status.IsDegraded)
            {
                _logger.LogWarning("Health check: trạng thái {State}, degraded={Degraded}", status.State, status.IsDegraded);
                return Task.FromResult(HealthCheckResult.Degraded(
                    status.IsDegraded ? "Preprocessor degraded" : "Preprocessor not running",
                    data: payload));
            }

            _logger.LogDebug("Preprocessor health check: HEALTHY");
            return Task.FromResult(HealthCheckResult.Healthy(
                "Preprocessor operational",
                payload));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preprocessor health check: EXCEPTION");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Exception during health check",
                exception: ex));
        }
    }
}
