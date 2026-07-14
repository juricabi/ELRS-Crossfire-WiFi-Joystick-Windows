using Xunit;

namespace ELRSWifiJoystick.Tests;

public class SourceLockTests
{
    [Fact]
    public void FirstChannelFrame_LocksSource_AndFiresStreaming()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        Assert.Equal("192.168.1.10", h.Engine.Source);
        Assert.Contains(h.States, s => s.State == EngineState.Streaming && s.Detail == "192.168.1.10");
    }

    [Fact]
    public void SecondSource_IsIgnoredWhileLockIsFresh()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Clock.Advance(1);
        h.Packet(Frames.Channels(9, 9, 9, 9), "192.168.1.99");
        Assert.Single(h.Applied);                 // only the first source got through
        Assert.Equal("192.168.1.10", h.Engine.Source);
    }

    [Fact]
    public void SecondSource_WarnsOnlyOnce()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Clock.Advance(1);
        h.Packet(Frames.Channels(9, 9, 9, 9), "192.168.1.99");
        h.Packet(Frames.Channels(9, 9, 9, 9), "192.168.1.99");
        h.Packet(Frames.Channels(9, 9, 9, 9), "192.168.1.99");
        Assert.Single(h.Log.Where(l => l.Contains("Ignoring 2nd source")));
    }

    [Fact]
    public void StaleLock_ReleasesToNewSource()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.5);
        h.Packet(Frames.Channels(5, 6, 7, 8), "192.168.1.99");
        Assert.Equal("192.168.1.99", h.Engine.Source);
        Assert.Equal(2, h.StreamingCount); // Streaming fired for each lock
        Assert.Equal(2, h.Applied.Count);
    }

    [Fact]
    public void ForcedModule_ChannelsFromOtherIp_AreIgnored()
    {
        var h = new Harness();
        h.Engine.ActivationIP = "10.0.0.9";
        h.Packet(Frames.Channels(1, 2, 3, 4), "10.0.0.7");
        Assert.Empty(h.Applied);
        h.Packet(Frames.Channels(1, 2, 3, 4), "10.0.0.9");
        Assert.Single(h.Applied);
        Assert.Equal("10.0.0.9", h.Engine.Source);
    }

    [Fact]
    public void SessionReset_AllowsRelock_RegressionForStopStartBug()
    {
        // Regression: after Stop/Start against a module that never stops broadcasting,
        // the stale lock used to suppress the "locked -> Streaming" transition forever.
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        Assert.Equal(1, h.StreamingCount);

        h.Engine.ResetSessionState();

        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        Assert.Equal(2, h.StreamingCount);
        Assert.Equal("192.168.2.138", h.Engine.Source);
    }

    [Fact]
    public void Tick_ReleasesStaleLock_AndReturnsToSearching()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        Assert.Contains(h.Log, l => l.Contains("went quiet"));
        Assert.Contains(h.States, s => s.State == EngineState.Searching);
    }

    [Fact]
    public void Tick_DoesNotReleaseFreshLock()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Clock.Advance(1);
        h.Engine.Tick();
        Assert.Equal("192.168.1.10", h.Engine.Source);
    }

    [Fact]
    public void FirewallHint_ShownOnce_WhenActivatedButNoData()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138"); // successful activation
        h.Clock.Advance(5);                                     // >4s, still no channels
        h.Engine.Tick();
        h.Engine.Tick();
        Assert.Single(h.Log.Where(l => l.Contains("Firewall")));
    }

    [Fact]
    public void FirewallHint_NotShown_WhenDataArrived()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.2.138"); // data flows
        h.Clock.Advance(5);
        h.Engine.Tick();
        Assert.DoesNotContain(h.Log, l => l.Contains("!! Activated OK"));
    }

    [Fact]
    public void SetTarget_TrimsIp_AndUnlocksCurrentSource()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10");
        h.Engine.SetTarget("  192.168.2.138  ");
        Assert.Equal("192.168.2.138", h.Engine.ActivationIP);
        Assert.Null(h.Engine.Source); // old lock released so the new target can take over

        // and the old module can no longer feed the joystick
        h.Packet(Frames.Channels(9, 9, 9, 9), "192.168.1.10");
        Assert.Single(h.Applied); // still only the original frame
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetTarget_NullOrWhitespace_ClearsTarget(string? ip)
    {
        var h = new Harness();
        h.Engine.ActivationIP = "10.0.0.9";
        h.Engine.SetTarget(ip);
        Assert.Null(h.Engine.ActivationIP);
    }
}
