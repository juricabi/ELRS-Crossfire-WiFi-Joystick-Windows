using Xunit;

namespace ELRSWifiJoystick.Tests;

// R8: the module IP and the request changed together (SetTarget(text, intervalUs, channels), or StoreRequest
// and then SetTarget on one thread): the new module's joystick_begin carries the request, the module being left
// gets nothing. (core: Request_Store_NoRestart, Asked_SwitchToAModuleStreamingAnOldRequest,
// Restart_Pending_OnlyForItsModule)
public class StoreRequestTests
{
    private const string DefIp = "192.168.1.50";
    private const string XfIp = "192.168.2.138";

    [Fact]
    public void Request_Store_NoRestart()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        Assert.Equal(DefIp, h.Engine.Source);
        h.Engine.StoreRequest(0, 99);                         // clamped, nothing sent
        Assert.Equal(1000, h.Engine.IntervalUs);
        Assert.Equal(16, h.Engine.Channels);
        Assert.Empty(h.Ends);
        h.Engine.StoreRequest(2000, 8);
        Assert.Empty(h.Ends);
        // the host changes the module IP next: the new module's joystick_begin asks for the stored request
        Assert.True(h.Engine.SetTarget("10.0.0.7"));
        Assert.Equal(new ActivationPost("10.0.0.7", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());
        for (int i = 0; i < 30; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Empty(h.Ends);                                 // the module being left gets nothing
        Assert.DoesNotContain(h.Posts.Skip(1), p => p.Ip == DefIp);
    }

    [Fact]
    public void Asked_SwitchToAModuleStreamingAnOldRequest()
    {
        // The new module turns out to stream already what an earlier activation asked for: restarted at its lock.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.1");
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");          // both activated with 250 Hz, 16 channels
        Assert.Equal(2, h.Activated.Count);
        h.Same("10.0.0.1", 16, 100);
        h.Same("10.0.0.2", 16, 100);                          // the second one streams too: ignored
        Assert.Equal("10.0.0.1", h.Engine.Source);
        h.Engine.StoreRequest(2000, 16);
        Assert.True(h.Engine.SetTarget("10.0.0.2"));
        Assert.Equal(new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 2000, 16, false), h.Posts.Last());
        h.Same("10.0.0.2", 16, 100);                          // it streams the old 250 Hz: restarted (ELRS: at once)
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal("10.0.0.2", Assert.Single(h.Ends).Ip);
        Assert.Equal(2000, h.RestartBegins[0].IntervalUs);
        for (int i = 0; i < 10; i++) h.Same("10.0.0.1", 16, 100);
        Assert.Single(h.Ends);
    }

    [Fact]
    public void Restart_Failed_ForTheModuleLeft_IsNotSentAgain()
    {
        // A failed restart of the locked module waits to be sent again; the host switches to another module
        // together with a new request: the module being left is not restarted.
        var h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.1");
        h.Same("10.0.0.1", 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(1, h.CountLog("The restart of 10.0.0.1 failed - sending it again in 5 s"));
        h.RestartResult = true;
        h.Engine.StoreRequest(3000, 8);
        Assert.True(h.Engine.SetTarget("10.0.0.2"));
        Assert.Equal(new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 3000, 8, false), h.Posts.Last());
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.Equal("10.0.0.1", Assert.Single(h.Ends).Ip);
    }

    [Fact]
    public void SetTargetWithRequest_TheModuleLeftGetsNothing()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        Assert.True(h.Engine.SetTarget("10.0.0.7", 2000, 8));
        Assert.Equal((2000, 8), (h.Engine.IntervalUs, h.Engine.Channels));
        Assert.Null(h.Engine.Source);
        Assert.Equal(new ActivationPost("10.0.0.7", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());
        for (int i = 0; i < 30; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Empty(h.Ends);
        Assert.DoesNotContain(h.Posts.Skip(1), p => p.Ip == DefIp);
        Assert.Equal(1, h.CountLog("Target module set to 10.0.0.7 - connecting..."));

        // clamped like SetRequest; an invalid module IP changes nothing
        h = new Harness();
        Assert.True(h.Engine.SetTarget("10.0.0.7", 0, 99));
        Assert.Equal((1000, 16), (h.Engine.IntervalUs, h.Engine.Channels));
        Assert.False(h.Engine.SetTarget("10.0.0.300", 2000, 8));
        Assert.Equal((1000, 16), (h.Engine.IntervalUs, h.Engine.Channels));
        Assert.Equal("10.0.0.7", h.Engine.ActivationIP);

        // nothing locked: stored, the new module's joystick_begin carries it
        h = new Harness();
        Assert.True(h.Engine.SetTarget("10.0.0.7", 3000, 12));
        Assert.Equal(new ActivationPost("10.0.0.7", JoystickEngine.ActionBegin, 3000, 12, false), Assert.Single(h.Posts));

        // stopped: stored and set, nothing sent
        h = new Harness();
        h.Engine.Stop();
        Assert.True(h.Engine.SetTarget("10.0.0.7", 2000, 8));
        Assert.Equal((2000, 8), (h.Engine.IntervalUs, h.Engine.Channels));
        Assert.Equal("10.0.0.7", h.Engine.ActivationIP);
        Assert.Empty(h.Posts);
    }

    [Fact]
    public void SetTargetWithRequest_AFrameOfTheModuleLeft_InBetween_RestartsNothing()
    {
        // The UI thread changes both while the engine thread handles a frame of the module streaming now: the
        // frame was applied (past the module IP check) when the change comes, and is checked (CheckRequest) after
        // it. In one step the module being left is unlocked with the new request: not restarted.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        bool once = true;
        h.Engine.ChannelsUpdated += _ =>
        {
            if (!once) return;
            once = false;
            h.Engine.SetTarget("10.0.0.7", 2000, 8);
        };
        h.Same(DefIp, 16, 16000);
        Assert.False(once);
        Assert.Empty(h.Ends);
        Assert.Equal(new ActivationPost("10.0.0.7", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());

        // the two calls with that frame between them: it finds the new request while still locked - restarted
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        once = true;
        h.Engine.ChannelsUpdated += _ =>
        {
            if (!once) return;
            once = false;
            h.Engine.StoreRequest(2000, 8);
        };
        h.Same(DefIp, 16, 16000);
        h.Engine.SetTarget("10.0.0.7");
        Assert.Equal(DefIp, Assert.Single(h.Ends).Ip);
    }

    [Fact]
    public void SetTargetWithRequest_TheModuleKept_OrABlankModuleIp_AsSetRequest()
    {
        // the module streaming now named: the lock stays, and the change restarts it (R2, R7)
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        Assert.True(h.Engine.SetTarget(DefIp, 2000, 16));
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, true), Assert.Single(h.RestartBegins));
        Assert.Equal(1, h.CountLog("Restarting the stream of 192.168.1.50: 500 Hz, 16 channels"));
        Assert.Equal(1, h.CountLog("Target module set to 192.168.1.50 - streaming from it already"));
        Assert.True(h.Engine.SetTarget(DefIp, 2000, 16));    // the same request: nothing
        Assert.Single(h.Ends);

        // ... a Crossfire: noted once, nothing sent
        h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        h.Same(XfIp, 16, 16000);
        Assert.True(h.Engine.SetTarget(XfIp, 2000, 8));
        Assert.True(h.Engine.SetTarget(XfIp, 3000, 8));
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("Crossfire module 192.168.2.138 ignores the rate and channel count"));
        Assert.Equal(XfIp, h.Engine.Source);

        // a blank module IP (auto-discover): no module is being left - the locked one is restarted
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        h.Engine.SetTarget(DefIp);
        Assert.True(h.Engine.SetTarget("", 2000, 16));
        Assert.Null(h.Engine.ActivationIP);
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(DefIp, Assert.Single(h.Ends).Ip);
    }
}
