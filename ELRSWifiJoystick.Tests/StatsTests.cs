using System.Diagnostics;
using Xunit;

namespace ELRSWifiJoystick.Tests;

public class StatsTests
{
    private static (double hz, double jitter, int count) Emit(JoystickEngine e)
    {
        (double, double, int) result = default;
        e.StatsUpdated += (hz, jit, cnt) => result = (hz, jit, cnt);
        e.EmitStats();
        return result;
    }

    [Fact]
    public void SteadyStream_Reports100Hz_ZeroJitter()
    {
        var h = new Harness();
        long tickPer10Ms = Stopwatch.Frequency / 100;
        for (int i = 0; i <= 100; i++)
            h.Engine.AddArrivalForTest(i * tickPer10Ms);

        var (hz, jitter, count) = Emit(h.Engine);
        Assert.InRange(hz, 99.0, 101.0);
        Assert.InRange(jitter, 0.0, 0.05);
        Assert.Equal(101, count);
    }

    [Fact]
    public void AlternatingGaps_ReportCorrectJitter()
    {
        // Gaps alternate 5ms / 15ms: mean 10ms -> rate 100Hz, stdev exactly 5ms.
        var h = new Harness();
        long t = 0;
        h.Engine.AddArrivalForTest(0);
        for (int i = 0; i < 100; i++)
        {
            t += Stopwatch.Frequency / 1000 * (i % 2 == 0 ? 5 : 15);
            h.Engine.AddArrivalForTest(t);
        }

        var (hz, jitter, _) = Emit(h.Engine);
        Assert.InRange(hz, 99.0, 101.0);
        Assert.InRange(jitter, 4.9, 5.1);
    }

    [Fact]
    public void StatsReset_AfterEachEmit()
    {
        var h = new Harness();
        long tickPer10Ms = Stopwatch.Frequency / 100;
        for (int i = 0; i < 10; i++)
            h.Engine.AddArrivalForTest(i * tickPer10Ms);

        var first = Emit(h.Engine);
        Assert.Equal(10, first.count);

        var second = Emit(h.Engine);
        Assert.Equal(0, second.count);
        Assert.Equal(0, second.hz);
    }

    [Fact]
    public void TooFewSamples_ReportZeroRate_WithoutCrashing()
    {
        var h = new Harness();
        h.Engine.AddArrivalForTest(12345);
        var (hz, jitter, count) = Emit(h.Engine);
        Assert.Equal(0, hz);
        Assert.Equal(0, jitter);
        Assert.Equal(1, count);
    }

    [Fact]
    public void RealPackets_AreCountedInStats()
    {
        var h = new Harness();
        for (int i = 0; i < 5; i++)
            h.Packet(Frames.Channels(1, 2, 3, 4));
        var (_, _, count) = Emit(h.Engine);
        Assert.Equal(5, count);
    }
}
