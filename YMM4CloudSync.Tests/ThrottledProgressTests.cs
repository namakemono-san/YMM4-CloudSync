using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Tests;

public class ThrottledProgressTests
{
    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void CollapsesRapidReports()
    {
        var recorder = new Recorder();
        var progress = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 10_000; i++) progress.Report(i / 1000.0);

        Assert.Single(recorder.Values);
    }

    [Fact]
    public void AlwaysForwardsCompletion()
    {
        var recorder = new Recorder();
        var progress = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        progress.Report(1.0);
        progress.Report(2.0);
        progress.Report(100.0);

        Assert.Equal(100.0, recorder.Values[^1]);
    }

    [Fact]
    public void ForwardsCompletionOnlyOnce()
    {
        var recorder = new Recorder();
        var progress = new ThrottledProgress(recorder, TimeSpan.FromSeconds(30));

        progress.Report(100.0);
        progress.Report(100.0);
        progress.Report(100.0);

        Assert.Single(recorder.Values);
    }

    [Fact]
    public void ForwardsEveryReport_WhenIntervalIsZero()
    {
        var recorder = new Recorder();
        var progress = new ThrottledProgress(recorder, TimeSpan.Zero);

        progress.Report(10.0);
        progress.Report(20.0);
        progress.Report(30.0);

        Assert.Equal([10.0, 20.0, 30.0], recorder.Values);
    }

    [Fact]
    public void AllowsANewRunAfterCompletion()
    {
        var recorder = new Recorder();
        var progress = new ThrottledProgress(recorder, TimeSpan.Zero);

        progress.Report(100.0);
        progress.Report(5.0);
        progress.Report(100.0);

        Assert.Equal([100.0, 5.0, 100.0], recorder.Values);
    }
}
