namespace YMM4CloudSync.Core.Commons.Utilities;

public sealed class ThrottledProgress : IProgress<double>
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProgress<double> _inner;
    private readonly long _intervalMs;

    private long _lastReportedAt;
    private bool _hasReported;
    private bool _completed;

    public ThrottledProgress(IProgress<double> inner, TimeSpan? interval = null)
    {
        _inner = inner;
        _intervalMs = (long)(interval ?? DefaultInterval).TotalMilliseconds;
    }

    public void Report(double value)
    {
        if (value >= 100.0)
        {
            if (_completed) return;

            _completed = true;
            _hasReported = true;
            _lastReportedAt = Environment.TickCount64;
            _inner.Report(100.0);

            return;
        }

        _completed = false;

        var now = Environment.TickCount64;

        if (_hasReported && now - _lastReportedAt < _intervalMs) return;

        _hasReported = true;
        _lastReportedAt = now;
        _inner.Report(value);
    }
}
