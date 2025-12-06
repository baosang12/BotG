using System;
using System.Threading;
using System.Threading.Tasks;
using cAlgo.API;

namespace BotG.Threading
{
    /// <summary>
    /// Bộ hẹn giờ chạy callback trên main thread của cTrader bằng BeginInvokeOnMainThread.
    /// Đảm bảo không có callback chạy trùng và tự hủy khi được Stop/Dispose.
    /// </summary>
    public sealed class MainThreadTimer : IDisposable
    {
        private readonly Robot _robot;
        private readonly Action _callback;
        private readonly TimeSpan _interval;
        private readonly bool _runImmediately;
        private readonly object _gate = new object();
        private CancellationTokenSource? _cts;
        private bool _running;

        public MainThreadTimer(Robot robot, Action callback, TimeSpan interval, bool runImmediately = false)
        {
            _robot = robot ?? throw new ArgumentNullException(nameof(robot));
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
            }

            _interval = interval;
            _runImmediately = runImmediately;
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _running;
                }
            }
        }

        public void Start()
        {
            CancellationToken token;
            lock (_gate)
            {
                if (_running)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                _running = true;
                token = _cts.Token;
            }

            ScheduleNext(_runImmediately ? TimeSpan.Zero : _interval, token);
        }

        public void Stop()
        {
            CancellationTokenSource? ctsToDispose = null;
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                ctsToDispose = _cts;
                _cts = null;
            }

            try
            {
                ctsToDispose?.Cancel();
            }
            finally
            {
                ctsToDispose?.Dispose();
            }
        }

        private void ScheduleNext(TimeSpan delay, CancellationToken token)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                _robot.BeginInvokeOnMainThread(() =>
                {
                    if (token.IsCancellationRequested || !IsRunning)
                    {
                        return;
                    }

                    try
                    {
                        _callback();
                    }
                    catch (Exception ex)
                    {
                        SafeLog(ex);
                    }

                    CancellationToken nextToken;
                    lock (_gate)
                    {
                        if (!_running || _cts == null)
                        {
                            return;
                        }

                        nextToken = _cts.Token;
                    }

                    ScheduleNext(_interval, nextToken);
                });
            }, token);
        }

        private void SafeLog(Exception ex)
        {
            try
            {
                _robot.Print("[MainThreadTimer] Callback exception: {0}", ex.Message);
            }
            catch
            {
                // bỏ qua lỗi ghi log để tránh vòng lặp lỗi
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
