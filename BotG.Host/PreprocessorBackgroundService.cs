using BotG.Config;
using BotG.Runtime.Preprocessor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotG.Host;

public class PreprocessorBackgroundService : BackgroundService
{
    private readonly PreprocessorRuntimeManager _preprocessor;
    private readonly PreprocessorRolloutManager _rolloutManager;
    private readonly ILogger<PreprocessorBackgroundService> _logger;
    private readonly IOptionsMonitor<PreprocessorRuntimeConfig> _configMonitor;
    private IDisposable? _configSubscription;
    private readonly object _startSync = new();

    public PreprocessorBackgroundService(
        PreprocessorRuntimeManager preprocessor,
        PreprocessorRolloutManager rolloutManager,
        ILogger<PreprocessorBackgroundService> logger,
        IOptionsMonitor<PreprocessorRuntimeConfig> configMonitor)
    {
        _preprocessor = preprocessor;
        _rolloutManager = rolloutManager;
        _logger = logger;
        _configMonitor = configMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Preprocessor Background Service Starting...");

        try
        {
            StartOrRestartPreprocessor(_configMonitor.CurrentValue);
            _configSubscription = _configMonitor.OnChange(config =>
            {
                _logger.LogInformation("🔄 Config thay đổi – restart preprocessor");
                StartOrRestartPreprocessor(config);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to initialize preprocessor");
            return;
        }

        _logger.LogInformation("🎯 Preprocessor ready for production deployment");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _rolloutManager.AdvancePhaseIfReady();
                var status = _preprocessor.GetStatus();
                if (status?.ProcessedTicks is long ticks && ticks > 0 && ticks % 100 == 0)
                {
                    _logger.LogInformation("📊 Preprocessor Status: {Ticks} ticks processed", ticks);
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in Preprocessor Background Service");
            }
        }

        _logger.LogInformation("🛑 Preprocessor Background Service Stopping...");
        _configSubscription?.Dispose();
        _preprocessor.Stop();
    }

    private void StartOrRestartPreprocessor(PreprocessorRuntimeConfig? config)
    {
        if (config == null)
        {
            _logger.LogWarning("⚠️ Không có cấu hình preprocessor. Bỏ qua khởi động.");
            return;
        }

        lock (_startSync)
        {
            _preprocessor.Stop();
            if (_preprocessor.TryStart(config))
            {
                _logger.LogInformation("✅ Preprocessor started với {IndicatorCount} indicator(s)", config.Indicators?.Count ?? 0);
            }
            else
            {
                _logger.LogError("❌ Preprocessor không thể khởi động. Kiểm tra config runtime.");
            }
        }
    }
}
