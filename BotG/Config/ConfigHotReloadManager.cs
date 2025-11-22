using System;
using System.IO;
using System.Threading;
using BotG.Strategies.Coordination;
using BotG.Runtime.Logging;

namespace BotG.Config
{
    public sealed class ConfigHotReloadManager : IDisposable
    {
        private readonly string _configFilePath;
        private readonly TimeSpan _debounceDelay;
        private readonly object _sync = new object();
        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private bool _disposed;

        public event EventHandler<StrategyCoordinationConfig>? ConfigReloaded;

        public ConfigHotReloadManager(string configFilePath, TimeSpan debounceDelay)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
            {
                throw new ArgumentException("Path is required", nameof(configFilePath));
            }

            _configFilePath = Path.GetFullPath(configFilePath);
            _debounceDelay = debounceDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : debounceDelay;
        }

        public void StartWatching()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_watcher != null)
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_configFilePath);
                if (string.IsNullOrEmpty(directory))
                {
                    directory = Directory.GetCurrentDirectory();
                }

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var fileName = Path.GetFileName(_configFilePath);
                var watcher = new FileSystemWatcher(directory)
                {
                    Filter = string.IsNullOrEmpty(fileName) ? "config.runtime.json" : fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
                };

                watcher.Changed += OnConfigFileEvent;
                watcher.Created += OnConfigFileEvent;
                watcher.Renamed += OnConfigFileEvent;
                watcher.Deleted += OnConfigFileEvent;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;

                PipelineLogger.Log("CONFIG", "HotReloadStarted", "Config watcher started", new { path = _configFilePath, debounceSeconds = _debounceDelay.TotalSeconds }, null);
            }
        }

        public void StopWatching()
        {
            lock (_sync)
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnConfigFileEvent;
                    _watcher.Created -= OnConfigFileEvent;
                    _watcher.Renamed -= OnConfigFileEvent;
                    _watcher.Deleted -= OnConfigFileEvent;
                    _watcher.Dispose();
                    _watcher = null;
                }

                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }

        private void OnConfigFileEvent(object sender, FileSystemEventArgs e)
        {
            if (!string.Equals(Path.GetFullPath(e.FullPath), _configFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (_debounceTimer == null)
                {
                    _debounceTimer = new Timer(OnDebounceTimerElapsed, null, _debounceDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void OnDebounceTimerElapsed(object? state)
        {
            StrategyCoordinationConfig? config = null;
            try
            {
                config = StrategyCoordinationConfigLoader.SafeReloadConfig();
            }
            catch (Exception ex)
            {
                PipelineLogger.Log("CONFIG", "HotReloadException", "Unhandled exception during config reload", new { error = ex.Message }, null);
            }

            if (config != null)
            {
                try
                {
                    ConfigReloaded?.Invoke(this, config);
                }
                catch (Exception ex)
                {
                    PipelineLogger.Log("CONFIG", "HotReloadHandlerError", "Config reload handler threw", new { error = ex.Message }, null);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                StopWatching();
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConfigHotReloadManager));
            }
        }
    }
}
