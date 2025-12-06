using System;
using AnalysisModule.Preprocessor.DataModels;

namespace AnalysisModule.Preprocessor.Aggregators;

/// <summary>
/// Gom tick sạch thành bar đóng, phát event khi hoàn tất.
/// </summary>
public sealed class BarAggregator
{
    private readonly TimeFrame _timeFrame;
    private Bar? _currentBar;
    private DateTime _windowStartUtc;

    public BarAggregator(TimeFrame timeFrame)
    {
        _timeFrame = timeFrame;
    }

    public event EventHandler<Bar>? BarClosed;

    public void Process(Tick tick)
    {
        var aligned = _timeFrame.AlignTimestampUtc(tick.TimestampUtc);
        if (_currentBar is null || aligned > _windowStartUtc)
        {
            CloseCurrentBar();
            _windowStartUtc = aligned;
            _currentBar = new Bar(aligned, tick.Bid, tick.Bid, tick.Bid, tick.Bid, tick.Volume, _timeFrame);
            return;
        }

        UpdateBar(tick);
    }

    public void Flush()
    {
        CloseCurrentBar();
    }

    private void UpdateBar(Tick tick)
    {
        if (_currentBar is null)
        {
            return;
        }

        var high = Math.Max(_currentBar.High, tick.Bid);
        var low = Math.Min(_currentBar.Low, tick.Bid);
        var close = tick.Bid;
        var volume = _currentBar.Volume + tick.Volume;
        _currentBar = new Bar(_currentBar.OpenTimeUtc, _currentBar.Open, high, low, close, volume, _timeFrame);
    }

    private void CloseCurrentBar()
    {
        if (_currentBar is not null)
        {
            BarClosed?.Invoke(this, _currentBar);
            _currentBar = null;
        }
    }
}
