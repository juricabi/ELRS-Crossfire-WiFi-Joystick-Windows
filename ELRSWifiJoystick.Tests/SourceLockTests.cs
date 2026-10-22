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

    // ---- what the joystick does when no module drives it (the owner's rule, and the Linux app's) ----

    [Fact]
    public void LockLost_CentersTheAxes_AndReleasesTheButtons()
    {
        // Radio off or out of range with the sticks deflected and CH9-16 high: the sim must not keep them.
        var h = new Harness();
        var dev = new FakeOutput();
        h.Engine.DeviceForTest = dev;
        h.Same("192.168.1.10", 16, 32767);
        Assert.Equal(0xFF, h.Engine.ButtonMask);
        dev.Clear();
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, JoystickMap.Center)), dev.Axes);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, false)), dev.Btns);
        Assert.Equal(0, h.Engine.ButtonMask);
        // once
        dev.Clear();
        h.Clock.Advance(60);
        h.Engine.Tick();
        Assert.Empty(dev.Axes);
        Assert.Empty(dev.Btns);
        // the next lock writes its frame (only what differs from the center)
        h.Same("192.168.1.10", 4, 100);
        Assert.Equal(new[] { (0, 100), (1, 100), (2, 100), (3, 100) }, dev.Axes);
    }

    [Fact]
    public void LockLost_ByAPlaceholderFlood_CentersToo()
    {
        var h = new Harness();
        var dev = new FakeOutput();
        h.Engine.DeviceForTest = dev;
        h.Same("192.168.1.10", 16, 32767);
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.1);
        dev.Clear();
        h.Same("192.168.1.10", 16, JoystickEngine.XF_PLACEHOLDER);   // radio off: the module sends placeholders
        Assert.Null(h.Engine.Source);
        Assert.Equal(8, dev.Axes.Count(a => a.Value == JoystickMap.Center));
        Assert.Equal(0, h.Engine.ButtonMask);
    }

    [Fact]
    public void SetTarget_RacingTheFirstLockOfTheModuleLeft_NeverLeavesItStreaming()
    {
        // Connect (UI thread) at the moment the engine thread takes the first lock of the module being left:
        // SetTarget must not drop that lock between the lock and its Streaming, or the banner stays green
        // ("streaming 10.0.0.1") while nothing drives the joystick - for good if the new module never answers.
        var h = new Harness { ActivateResult = false };
        Thread? ui = null;
        bool uiDoneMeanwhile = false;
        h.Engine.Log += m =>
        {
            if (m != "Joystick source locked to 10.0.0.1" || ui != null) return;
            ui = new Thread(() => h.Engine.SetTarget("10.0.0.2"));
            ui.Start();
            uiDoneMeanwhile = ui.Join(300);    // it may run now, while the engine thread is between the two
        };
        h.Same("10.0.0.1", 16, 32767);
        Assert.True(ui!.Join(5000));
        Assert.False(uiDoneMeanwhile, "SetTarget waits until the lock is said");
        Assert.Null(h.Engine.Source);
        Assert.Equal((EngineState.Searching, "connecting to 10.0.0.2"), h.States[^1]);
        for (int i = 0; i < 50; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.1", 16, 32767); h.Engine.Tick(); }
        Assert.Null(h.Engine.Source);
        Assert.Equal(EngineState.Searching, h.States[^1].State);
        Assert.Equal(1, h.StreamingCount);
    }

    [Fact]
    public void SetTarget_WhileLocked_SaysSearching_PostsAtOnce_AndCentersOnTheEngineThread()
    {
        var h = new Harness { ActivateResult = false };     // the new module does not answer
        var dev = new FakeOutput();
        h.Engine.DeviceForTest = dev;
        h.Same("10.0.0.1", 16, 32767);
        dev.Clear();
        h.Engine.SetTarget("10.0.0.2");
        Assert.Equal((EngineState.Searching, "connecting to 10.0.0.2"), h.States[^1]);
        Assert.Equal(new[] { "10.0.0.2" }, h.Activated);    // the activation went out at once
        Assert.Empty(dev.Axes);                             // vJoy is written by the engine thread only...
        h.Engine.Tick();                                    // ...on its next tick (100 ms at most)
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, JoystickMap.Center)), dev.Axes);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, false)), dev.Btns);
        // the old module keeps streaming: filtered, so the joystick stays centered and the state Searching
        for (int i = 0; i < 100; i++)
        {
            h.Clock.Advance(0.1);
            h.Same("10.0.0.1", 16, 32767);
            h.Engine.Tick();
        }
        Assert.Equal(EngineState.Searching, h.States[^1].State);
        Assert.Equal(0, h.Engine.ButtonMask);
        Assert.Equal(8, dev.Axes.Count);
    }

    [Theory]
    [InlineData("192.168.001.050")]     // HttpClient reads leading zeros as octal: another host
    [InlineData("192.168.4.01")]        // reaches the module, but its frames come from 192.168.4.1
    [InlineData("192.168.1.50:80")]
    [InlineData("elrs_tx.local")]       // a name: its frames never match it
    [InlineData("10.0.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.1.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("1.2.3.-4")]
    [InlineData("1.2.3.4 x")]
    public void SetTarget_NotAnIPv4Address_IsRefused_AndTheTargetKept(string text)
    {
        var h = new Harness();
        h.Engine.SetTarget("10.0.0.9");
        h.Posts.Clear();
        h.Engine.SetTarget(text);
        Assert.Equal("10.0.0.9", h.Engine.ActivationIP);
        Assert.Equal(1, h.CountLog($"Invalid module IP \"{text}\" - use a.b.c.d"));
        Assert.Empty(h.Posts);
    }

    [Fact]
    public void SetTarget_TheAddressFramesAreComparedWith()
    {
        var h = new Harness();
        h.Engine.SetTarget(" \t192.168.4.1\r\n");
        Assert.Equal("192.168.4.1", h.Engine.ActivationIP);
        h.Same("192.168.4.1", 16, 100);
        Assert.Equal("192.168.4.1", h.Engine.Source);
    }
}
