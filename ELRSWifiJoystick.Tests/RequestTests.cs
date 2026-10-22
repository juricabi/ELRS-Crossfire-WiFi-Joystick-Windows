using System.Net.Http;
using Xunit;

namespace ELRSWifiJoystick.Tests;

// The request (interval + channels) and restarts. The engine side mirrors the Linux core's tests of
// the same names (core/tests/test_wifi_proto.c: Request_*, Restart_*, Mismatch_*).
public class RequestTests
{
    private const string DefIp = "192.168.1.50";
    private const string XfIp = "192.168.2.138";

    // ---- the supported values ---------------------------------------------------------------

    [Fact]
    public void SupportedValues()
    {
        Assert.Equal(1000, StreamRequest.ClampInterval(0));
        Assert.Equal(1000, StreamRequest.ClampInterval(-5));
        Assert.Equal(1000, StreamRequest.ClampInterval(999));
        Assert.Equal(1000, StreamRequest.ClampInterval(1000));
        Assert.Equal(4000, StreamRequest.ClampInterval(4000));
        Assert.Equal(100000, StreamRequest.ClampInterval(100000));
        Assert.Equal(100000, StreamRequest.ClampInterval(100001));
        Assert.Equal(4, StreamRequest.ClampChannels(-1));
        Assert.Equal(4, StreamRequest.ClampChannels(3));
        Assert.Equal(4, StreamRequest.ClampChannels(4));
        Assert.Equal(16, StreamRequest.ClampChannels(16));
        Assert.Equal(16, StreamRequest.ClampChannels(17));
        Assert.Equal(250, StreamRequest.IntervalHz(4000));
        Assert.Equal(333, StreamRequest.IntervalHz(3000));
        Assert.Equal(150, StreamRequest.IntervalHz(6667));
        Assert.Equal(1000, StreamRequest.IntervalHz(1000));
        Assert.Equal(50, StreamRequest.IntervalHz(20000));
        Assert.Equal(10, StreamRequest.IntervalHz(100000));
        Assert.Equal(0, StreamRequest.IntervalHz(0));
        Assert.Equal(0, StreamRequest.IntervalHz(-1));
        Assert.Equal(4000, StreamRequest.IntervalDefault);
        Assert.Equal(16, StreamRequest.ChannelsDefault);
        Assert.True(StreamRequest.IsValidInterval(1000) && StreamRequest.IsValidInterval(100000));
        Assert.False(StreamRequest.IsValidInterval(999) || StreamRequest.IsValidInterval(100001));
        Assert.True(StreamRequest.IsValidChannels(4) && StreamRequest.IsValidChannels(16));
        Assert.False(StreamRequest.IsValidChannels(3) || StreamRequest.IsValidChannels(17));
    }

    [Fact]
    public void Presets_AreTheEightRates_InOrder()
    {
        Assert.Equal(new[] { 1000, 2000, 3000, 4000, 5000, 6667, 10000, 20000 }, StreamRequest.IntervalPresets);
        Assert.Equal(new[] { 1000, 500, 333, 250, 200, 150, 100, 50 },
                     StreamRequest.IntervalPresets.Select(StreamRequest.IntervalHz));
        Assert.All(StreamRequest.IntervalPresets, p => Assert.True(StreamRequest.IsValidInterval(p)));
        Assert.Contains(StreamRequest.IntervalDefault, StreamRequest.IntervalPresets);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(500, 2000)]
    [InlineData(333, 3000)]     // a preset's own interval, not 1000000/333 = 3003
    [InlineData(250, 4000)]
    [InlineData(200, 5000)]
    [InlineData(150, 6667)]
    [InlineData(100, 10000)]
    [InlineData(50, 20000)]
    [InlineData(10, 100000)]
    [InlineData(300, 3333)]     // not a preset: rounded
    [InlineData(400, 2500)]
    [InlineData(9, 0)]          // out of range
    [InlineData(1001, 0)]
    [InlineData(0, 0)]
    public void RateToInterval(int hz, int us) => Assert.Equal(us, StreamRequest.RateToInterval(hz));

    [Fact]
    public void Describe_ShowsTheIntervalOnlyForNonPresets()
    {
        Assert.Equal("250 Hz", StreamRequest.Describe(4000));
        Assert.Equal("333 Hz", StreamRequest.Describe(3000));
        Assert.Equal("286 Hz (3500 us)", StreamRequest.Describe(3500));
    }

    [Theory]
    [InlineData("250", true, 4000, "")]
    [InlineData(" 500 ", true, 2000, "")]
    [InlineData("+150", true, 6667, "")]
    [InlineData("10", true, 100000, "")]
    [InlineData("1000", true, 1000, "")]
    [InlineData("9", false, 0, "9 is not supported - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)")]
    [InlineData("1001", false, 0, "1001 is not supported - use ")]
    [InlineData("-250", false, 0, "-250 is not supported")]
    [InlineData("abc", false, 0, "\"abc\" is not a number - use 1000, 500")]
    [InlineData("250hz", false, 0, "\"250hz\" is not a number")]
    [InlineData("2.5", false, 0, "\"2.5\" is not a number")]
    [InlineData("", false, 0, "\"\" is not a number")]
    [InlineData(null, false, 0, "\"\" is not a number")]
    [InlineData("99999999999999999999", false, 0, "is not a number")]
    [InlineData("9999999999", false, 0, "9999999999 is not supported")]
    public void TryParseRate(string? text, bool ok, int us, string error)
    {
        Assert.Equal(ok, StreamRequest.TryParseRate(text, out int iv, out string err));
        Assert.Equal(us, iv);
        if (ok) Assert.Equal("", err);
        else Assert.Contains(error, err);
    }

    [Theory]
    [InlineData("4000", true, 4000, "")]
    [InlineData("3500", true, 3500, "")]
    [InlineData("1000", true, 1000, "")]
    [InlineData("100000", true, 100000, "")]
    [InlineData("999", false, 0, "999 is not supported - use 1000 to 100000")]
    [InlineData("100001", false, 0, "100001 is not supported - use 1000 to 100000")]
    [InlineData("4 ms", false, 0, "\"4 ms\" is not a number - use 1000 to 100000")]
    public void TryParseInterval(string text, bool ok, int us, string error)
    {
        Assert.Equal(ok, StreamRequest.TryParseInterval(text, out int iv, out string err));
        Assert.Equal(us, iv);
        Assert.Equal(error, err);
    }

    [Theory]
    [InlineData("16", true, 16, "")]
    [InlineData("4", true, 4, "")]
    [InlineData("8", true, 8, "")]
    [InlineData("3", false, 0, "3 is not supported - use 4 to 16")]
    [InlineData("17", false, 0, "17 is not supported - use 4 to 16")]
    [InlineData("x", false, 0, "\"x\" is not a number - use 4 to 16")]
    public void TryParseChannels(string text, bool ok, int n, string error)
    {
        Assert.Equal(ok, StreamRequest.TryParseChannels(text, out int ch, out string err));
        Assert.Equal(n, ch);
        Assert.Equal(error, err);
    }

    [Fact]
    public void ParseError_StaysOneCleanLine()
    {
        Assert.False(StreamRequest.TryParseChannels("a\nb\tc" + new string('x', 100), out _, out string err));
        Assert.DoesNotContain('\n', err);
        Assert.DoesNotContain('\t', err);
        Assert.StartsWith("\"a?b?c", err);
        Assert.True(err.Length < 100);
    }

    // ---- the POST bodies --------------------------------------------------------------------

    [Fact]
    public async Task FormBodies_AreWhatTheModuleExpects()
    {
        var begin = new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 4000, 16, false);
        var end = new ActivationPost("10.0.0.1", JoystickEngine.ActionEnd, 0, 0, true);
        Assert.Equal("action=joystick_begin&interval=4000&channels=16",
                     await new FormUrlEncodedContent(JoystickEngine.FormFields(begin)).ReadAsStringAsync());
        Assert.Equal("action=joystick_end", await new FormUrlEncodedContent(JoystickEngine.FormFields(end)).ReadAsStringAsync());
        // a restart's begin carries the request like a plain one
        Assert.Equal("action=joystick_begin&interval=2000&channels=8",
                     await new FormUrlEncodedContent(JoystickEngine.FormFields(begin with { IntervalUs = 2000, Channels = 8, Restart = true }))
                         .ReadAsStringAsync());
    }

    // ---- the engine -------------------------------------------------------------------------

    [Fact]
    public void Request_DefaultsGoIntoActivation()
    {
        var h = new Harness();
        Assert.Equal(4000, h.Engine.IntervalUs);
        Assert.Equal(16, h.Engine.Channels);
        h.Packet(Frames.Ascii("ELRS"), "192.168.1.10");
        var p = Assert.Single(h.Posts);
        Assert.Equal(new ActivationPost("192.168.1.10", JoystickEngine.ActionBegin, 4000, 16, false), p);
        // reset keeps the request
        h.Engine.SetRequest(2000, 8);
        h.Engine.ResetSessionState();
        Assert.Equal(2000, h.Engine.IntervalUs);
        Assert.Equal(8, h.Engine.Channels);
    }

    [Fact]
    public void Request_SetWhileIdle_NextActivationCarriesIt()
    {
        var h = new Harness();
        Assert.True(h.Engine.SetRequest(5000, 8));
        Assert.Empty(h.Posts);
        Assert.False(h.Engine.SetRequest(5000, 8));           // unchanged
        Assert.True(h.Engine.SetRequest(0, 99));              // clamped
        Assert.Equal(1000, h.Engine.IntervalUs);
        Assert.Equal(16, h.Engine.Channels);
        Assert.False(h.Engine.SetRequest(999, 17));           // clamps to the same
        h.Engine.SetRequest(5000, 8);
        Assert.Empty(h.Log);                                  // nothing to say while not locked
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        var p = Assert.Single(h.Posts);
        Assert.Equal((JoystickEngine.ActionBegin, 5000, 8, false), (p.Action, p.IntervalUs, p.Channels, p.Restart));
        Assert.Equal(0, h.Engine.RestartCount);
    }

    [Fact]
    public void Request_ChangeWhileStreamingElrs_RestartsIt()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 16000);
        Assert.Equal(DefIp, h.Engine.Source);
        h.Posts.Clear();

        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Equal(new[]
        {
            new ActivationPost(DefIp, JoystickEngine.ActionEnd, 0, 0, true),
            new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, true),
        }, h.Posts);
        Assert.Equal(1, h.Engine.RestartCount);
        Assert.Equal(1, h.CountLog("Restarting the stream of 192.168.1.50: 500 Hz, 16 channels"));
        Assert.Equal(DefIp, h.Engine.Source);                // the lock holds through the restart

        // and the channels too
        Assert.True(h.Engine.SetRequest(2000, 8));
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(8, h.RestartBegins[1].Channels);
        // frames of the old request (16) until the module has it: applied, no extra restart
        int applied = h.Applied.Count;
        for (int i = 0; i < 10; i++) h.Same(DefIp, 16, 16000);
        for (int i = 0; i < 10; i++) h.Same(DefIp, 8, 16000);
        Assert.Equal(applied + 20, h.Applied.Count);
        Assert.Equal(2, h.Ends.Count);
        Assert.Single(h.Activated);                           // only the first joystick_begin was plain
    }

    [Fact]
    public void Request_ChangeWhileStreamingCrossfire_NoRestart_LoggedOnce()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        h.Same(XfIp, 16, 16000);
        Assert.Equal(XfIp, h.Engine.Source);
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("ignores the rate and channel count"));
        Assert.True(h.Engine.SetRequest(3000, 8));
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("ignores the rate and channel count"));
        Assert.Equal(3000, h.Engine.IntervalUs);              // kept for the next (ELRS) module
        Assert.False(h.Engine.RestartStream());               // explicit restart: refused, logged
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("nothing to restart"));
        Assert.Single(h.Posts);                               // just the beacon's activation
        // next session: noted again
        h.Engine.ResetSessionState();
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        h.Same(XfIp, 16, 16000);
        h.Engine.SetRequest(4000, 16);
        Assert.Equal(2, h.CountLog("ignores the rate and channel count"));
    }

    [Fact]
    public void Request_ChangeWhileStreamingUnknown_Restarts()
    {
        // No beacon: an ELRS module already streaming (it beacons only while idle), or Crossfire
        // (16 channels, beacons ~every 8 s also while streaming). It is never sent joystick_end
        // before that is clear.
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Empty(h.Posts);
        h.Engine.SetRequest(10000, 16);
        Assert.Empty(h.Ends);                                 // may be Crossfire: wait for its beacon
        Assert.Equal(1, h.CountLog("192.168.1.50 may be Crossfire (no beacon from it yet), which is never sent " +
                                   "joystick_end: restarting its stream within 20 s unless its beacon says Crossfire"));
        h.Engine.SetRequest(5000, 16);                        // another change while waiting: said once
        Assert.Equal(1, h.CountLog("may be Crossfire"));
        for (int i = 0; i < 19; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Empty(h.Ends);
        h.Clock.Advance(1);
        h.Same(DefIp, 16, 16000);                             // 20 s of lock without a Crossfire beacon: ELRS
        Assert.Single(h.Ends);                                // decided by the frame (Tick starves while data flows)
        Assert.Equal(5000, h.RestartBegins[0].IntervalUs);
        h.Same(DefIp, 16, 16000);
        h.Engine.Tick();
        Assert.Single(h.Ends);
    }

    [Fact]
    public void Request_ChangeWhileStreamingUnknown_DecidedOnTick_Too()
    {
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(10000, 16);
        for (int i = 0; i < 7; i++) { h.Clock.Advance(2.5); h.Same(DefIp, 16, 16000); }   // 17.5 s
        Assert.Empty(h.Ends);
        h.Clock.Advance(2.6);                                 // 20.1 s; the lock still holds (2.6 s since a frame)
        h.Engine.Tick();
        Assert.Single(h.Ends);
        Assert.Equal(DefIp, h.Engine.Source);
    }

    [Fact]
    public void Request_ChangeWhileStreamingUnknown_CrossfireBeacon_NoRestart()
    {
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(10000, 16);
        h.Clock.Advance(3);
        h.Same(DefIp, 16, 16000);
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        h.Engine.Tick();
        for (int i = 0; i < 12; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Empty(h.Ends);                                 // never a joystick_end to Crossfire
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(1, h.CountLog("Crossfire module 192.168.1.50 ignores the rate and channel count"));
    }

    [Fact]
    public void Request_ChangeWhileStreamingUnknown_Not16_RestartsAtOnce()
    {
        var h = new Harness();
        h.Engine.SetRequest(4000, 8);
        h.Same(DefIp, 8, 16000);                              // 8 channels: surely no Crossfire
        h.Engine.SetRequest(10000, 8);
        Assert.Single(h.Ends);
        Assert.Equal(0, h.CountLog("may be Crossfire"));
    }

    [Fact]
    public void Request_Wait_IsDropped_WhenTheLockGoes_OrOnStop()
    {
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(10000, 16);
        h.Clock.Advance(3.1);
        h.Engine.Tick();                                      // lock released: the next activation carries it
        Assert.Null(h.Engine.Source);
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 10000, 16, false), Assert.Single(h.Posts));
        h.Same(DefIp, 16, 16000);                             // locked again: nothing pending
        h.Clock.Advance(21);
        h.Same(DefIp, 16, 16000);
        h.Engine.Tick();
        Assert.Empty(h.Ends);

        h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(10000, 16);
        h.Engine.Stop();
        h.Clock.Advance(21);
        h.Same(DefIp, 16, 16000);                             // a frame still in flight after Stop
        h.Engine.Tick();
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Request_Wait_IsForTheLockedModuleOnly()
    {
        // A new lock (another module) starts over: the wait belonged to the old one.
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(10000, 16);
        h.Clock.Advance(3.1);
        h.Same("192.168.1.99", 8, 100);                       // the old lock is stale: the new module takes it
        Assert.Equal("192.168.1.99", h.Engine.Source);
        h.Clock.Advance(21);
        h.Same("192.168.1.99", 16, 100);
        Assert.DoesNotContain(h.Ends, p => p.Ip == DefIp);
    }

    [Fact]
    public void Restart_Explicit()
    {
        var h = new Harness();
        Assert.False(h.Engine.RestartStream());               // nothing to restart
        Assert.Equal(1, h.CountLog("No module to restart"));
        Assert.Empty(h.Posts);
        // the module IP, while nothing streams
        h.Engine.SetTarget("10.0.0.1");
        h.Engine.Tick();
        Assert.Single(h.Activated);
        Assert.True(h.Engine.RestartStream());
        Assert.Equal("10.0.0.1", Assert.Single(h.Ends).Ip);
        Assert.Equal(1, h.CountLog("Restarting the stream of 10.0.0.1: 250 Hz, 16 channels"));
        // the locked module wins over the target (ELRS by its beacon: no wait for the kind)
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.1");
        h.Same("10.0.0.1", 16, 100);
        Assert.True(h.Engine.RestartStream());
        Assert.Equal(2, h.Ends.Count(p => p.Ip == "10.0.0.1"));
        Assert.Equal(2, h.Engine.RestartCount);
    }

    [Fact]
    public void Restart_ExplicitUnknown16_WaitsForTheKind()
    {
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Clock.Advance(5.0);
        h.Same(DefIp, 16, 16000);
        Assert.True(h.Engine.RestartStream());                // accepted, but held
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("within 15 s"));
        h.Engine.RestartStream();                             // asked again: logged once
        Assert.Equal(1, h.CountLog("may be Crossfire"));
        h.Clock.Advance(15.0);
        h.Same(DefIp, 16, 16000);
        h.Engine.Tick();
        Assert.Single(h.Ends);
        // after the window, an explicit restart goes out at once
        Assert.True(h.Engine.RestartStream());
        Assert.Equal(2, h.Ends.Count);
    }

    [Fact]
    public void Restart_Wait_DroppedWhenTheLockIsLost()
    {
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(3.1);
        h.Engine.Tick();                                      // lock released
        Assert.Null(h.Engine.Source);
        h.Clock.Advance(30.0);
        h.Engine.Tick();
        Assert.Empty(h.Ends);
        // a new lock (another module) starts clean
        h.Same("192.168.1.51", 16, 100);
        h.Clock.Advance(1);
        h.Same("192.168.1.51", 16, 100);
        h.Engine.Tick();
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Restart_BypassesAndRestartsTheThrottle()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);               // POST at t0
        Assert.Single(h.Activated);
        h.Clock.Advance(1.0);
        h.Engine.SetTarget(DefIp);                            // target: throttle entry removed
        h.Engine.Tick();
        Assert.Equal(2, h.Activated.Count);
        h.Clock.Advance(1.0);
        Assert.True(h.Engine.RestartStream());                // within the throttle: goes out anyway
        Assert.Single(h.Ends);
        h.Clock.Advance(4.5);
        h.Engine.Tick();                                      // 4.5 s after the restart: still throttled
        Assert.Equal(2, h.Activated.Count);
        h.Clock.Advance(0.6);
        h.Engine.Tick();                                      // 5.1 s: the target is POSTed again
        Assert.Equal(3, h.Activated.Count);
    }

    [Fact]
    public void Restart_EndFailing_StillSendsTheBegin()
    {
        var h = new Harness { EndResult = false };
        h.Same(DefIp, 8, 100);                                // 8 channels: no Crossfire, restarted at once
        h.Posts.Clear();
        Assert.True(h.Engine.RestartStream());
        Assert.Equal(new[] { JoystickEngine.ActionEnd, JoystickEngine.ActionBegin }, h.Posts.Select(p => p.Action));
    }

    [Fact]
    public void Restart_WhileStreaming_DoesNotArmTheNoDataHint()
    {
        // The restart's answer comes while the module streams: it proves nothing about the path, and
        // arming the hint then would fire it on any later lock loss.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        Assert.True(h.Engine.RestartStream());
        Assert.Single(h.Ends);
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.1);
        h.Engine.Tick();                                      // lock released
        Assert.Null(h.Engine.Source);
        h.Clock.Advance(10);
        h.Engine.Tick();
        Assert.Equal(0, h.CountLog("NO data arriving"));
    }

    [Fact]
    public void PlainActivationAnswer_WhileItsModuleStreams_DoesNotArmTheHint()
    {
        // The same rule for a beacon's activation whose answer comes after the lock (ELRS answers
        // after it starts streaming). Simulated: the answer is processed while the module streams.
        var h = new Harness();
        h.Engine.ActivateOverride = p =>
        {
            h.Posts.Add(p);
            h.Same(DefIp, 16, 100);   // the stream arrives before the answer
            return true;
        };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        Assert.Equal(DefIp, h.Engine.Source);
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.1);
        h.Engine.Tick();
        h.Clock.Advance(10);
        h.Engine.Tick();
        Assert.Equal(0, h.CountLog("NO data arriving"));
    }

    [Fact]
    public void Restart_OkWhileNotStreaming_ArmsTheNoDataHint()
    {
        var h = new Harness { ActivateResult = false, RestartResult = true };
        h.Engine.SetTarget("10.0.0.5");
        Assert.True(h.Engine.RestartStream());
        h.Clock.Advance(4.1);
        h.Engine.Tick();
        Assert.Equal(1, h.CountLog("NO data arriving"));
    }

    [Fact]
    public void Mismatch_ElrsByBeacon_RestartsOnce()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        for (int i = 0; i < 20; i++) h.Same(DefIp, 8, 100 + i);
        Assert.Equal(20, h.Applied.Count);                    // applied all the same
        var end = Assert.Single(h.Ends);
        Assert.Equal(DefIp, end.Ip);
        Assert.Equal(16, Assert.Single(h.RestartBegins).Channels);
        Assert.Equal(1, h.CountLog("192.168.1.50 streams 8 channels, 16 asked for (another app's request?) - restarting its stream"));

        // ELRS by beacon sending 16 while 8 are asked: restarted too
        h = new Harness();
        h.Engine.SetRequest(4000, 8);
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        Assert.Single(h.Ends);
        Assert.Equal(8, h.RestartBegins[0].Channels);
    }

    [Fact]
    public void Mismatch_UnknownModule()
    {
        var h = new Harness();
        for (int i = 0; i < 5; i++) h.Same(DefIp, 8, 100);   // not 16: not Crossfire
        Assert.Single(h.Ends);

        h = new Harness();
        h.Engine.SetRequest(4000, 8);
        for (int i = 0; i < 5; i++) h.Same(DefIp, 16, 100);  // 16: could be Crossfire
        Assert.Empty(h.Ends);

        h = new Harness();
        for (int i = 0; i < 5; i++) h.Same(DefIp, 16, 100);  // = the request
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Mismatch_Crossfire_Never()
    {
        var h = new Harness();
        h.Engine.SetRequest(4000, 8);
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        for (int i = 0; i < 5; i++) h.Same(XfIp, 16, 100);
        Assert.Empty(h.Ends);
        // even a corrupt-looking count from a known Crossfire is left alone
        h.Same(XfIp, 8, 100);
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Mismatch_OncePerSession_ThenAgainAfterReset()
    {
        var h = new Harness();
        h.Same(DefIp, 8, 100);
        Assert.Single(h.Ends);
        h.Clock.Advance(3.1);
        h.Engine.Tick();                                      // lock released
        Assert.Null(h.Engine.Source);
        h.Same(DefIp, 8, 100);                                // locked again, still 8
        Assert.Single(h.Ends);
        h.Engine.ResetSessionState();
        h.Same(DefIp, 8, 100);
        Assert.Equal(2, h.Ends.Count);
    }

    [Fact]
    public void Mismatch_EachModuleOnce()
    {
        var h = new Harness();
        for (int i = 0; i < 5; i++)
        {
            string ip = $"10.0.1.{i + 1}";
            for (int k = 0; k < 3; k++) h.Same(ip, 8, 100);
            h.Clock.Advance(3.1);
            h.Engine.Tick();
        }
        Assert.Equal(5, h.Ends.Count);
        Assert.Equal(5, h.Ends.Select(p => p.Ip).Distinct().Count());
    }

    [Fact]
    public void Mismatch_SecondSource_IsNotRestarted()
    {
        // A module we ignore (locked to another) is never touched.
        var h = new Harness();
        h.Same(DefIp, 16, 100);
        h.Clock.Advance(1);
        h.Same("192.168.1.99", 8, 100);
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Mismatch_OtherThanTheModuleIp_IsNotRestarted()
    {
        var h = new Harness();
        h.Engine.ActivationIP = "10.0.0.9";
        h.Same("10.0.0.7", 8, 100);
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Kinds_FromBeacons_ThisSession()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.1");
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.2");
        Assert.Equal(ModuleKind.Elrs, h.Engine.KindOf("10.0.0.1"));
        Assert.Equal(ModuleKind.Crossfire, h.Engine.KindOf("10.0.0.2"));
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.3"));
        Assert.Equal(ModuleKind.Unknown, h.Engine.SourceKind);          // nothing locked
        h.Same("10.0.0.2", 16, 100);
        Assert.Equal(ModuleKind.Crossfire, h.Engine.SourceKind);
        // a module IP filters beacons before they are looked at
        h.Engine.ActivationIP = "10.0.0.9";
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.4");
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.4"));
        // a new session forgets them
        h.Engine.ResetSessionState();
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.1"));
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.2"));
    }

    [Fact]
    public void Kind_LearntAfterTheLock_StopsTheRestarts()
    {
        // Crossfire beacons also while streaming (~8 s): once it is known, a change is only noted.
        var h = new Harness();
        h.Same(XfIp, 16, 100);
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        Assert.Equal(ModuleKind.Crossfire, h.Engine.SourceKind);
        h.Engine.SetRequest(2000, 16);
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("ignores the rate and channel count"));
    }

    [Fact]
    public void AfterStop_NothingIsPosted()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Posts.Clear();
        h.Engine.Stop();
        Assert.Equal(DefIp, h.Engine.Source);              // the lock stays until the loop exits
        Assert.True(h.Engine.SetRequest(2000, 16));         // stored for the next start
        Assert.Equal(2000, h.Engine.IntervalUs);
        Assert.Empty(h.Posts);
        Assert.False(h.Engine.RestartStream());
        Assert.Equal(1, h.CountLog("Not running - nothing to restart"));
        // packets still in flight while the loop ends: no restart, no activation
        h.Same(DefIp, 8, 100);
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Engine.Tick();
        Assert.Empty(h.Posts);
        Assert.Equal(0, h.CountLog("Restarting"));
    }

    [Fact]
    public void RestartCount_CountsEveryRestart()
    {
        var h = new Harness();
        h.Same(DefIp, 8, 100);                                // mismatch restart
        h.Engine.RestartStream();                             // explicit
        h.Engine.SetRequest(2000, 8);                         // change
        Assert.Equal(3, h.Engine.RestartCount);
        Assert.Equal(3, h.Ends.Count);
    }
}
