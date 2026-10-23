using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ELRSWifiJoystick.Tests;

// R14, the release (see JoystickEngine): Stop and exit send joystick_end alone to the locked module, then to the
// modules that stream to us while we drop their frames, then to the modules activated less than 3 s ago that have
// not locked since, except a module known as Crossfire. Ported from the Linux core's core/tests/test_wifi_proto.c
// under the same names where there is one; the log lines are the ones every app shares.
public class ReleaseTests
{
    private const string DefIp = "192.168.1.50";

    private static string Sent(string ip) => $"Releasing {ip}: joystick_end, so it stops streaming and broadcasts again";
    private static string Crossfire(string ip) =>
        $"Not releasing {ip}: Crossfire broadcasts while streaming, other apps find it as it is";
    private static string Done(string ip) => $"Released {ip}: it broadcasts again, any app can connect to it";
    private static string Failed(string ip, string why) =>
        $"Releasing {ip} failed: {why} - it may stream on until its WiFi or power is cycled";

    // The lines Release says per module (not the results), from the log line at index from on.
    private static List<string> ReleaseLines(Harness h, int from = 0) =>
        h.Log.Skip(from).Where(l => l.StartsWith("Releasing ") && !l.Contains(" failed: ") || l.StartsWith("Not releasing "))
             .ToList();

    // One Release: it hands over n, and its joystick_end POSTs go to these modules, in this order.
    private static string[] Released(Harness h, int n)
    {
        int posts = h.Posts.Count;
        Assert.Equal(n, h.Engine.Release());
        var sent = h.Posts.Skip(posts).ToList();
        Assert.All(sent, p => Assert.Equal(Harness.Release(p.Ip), p));
        return sent.Select(p => p.Ip).ToArray();
    }

    // n rounds of 0.1 s, each module one 16-channel frame per round in this order, then a tick (the core's
    // stream_rounds): the frames of modules that stream to us.
    private static void Rounds(Harness h, int n, params string?[] ips)
    {
        for (int i = 0; i < n; i++)
        {
            h.Clock.Advance(0.1);
            foreach (string? ip in ips)
                if (ip != null) h.Same(ip, 16, 100);
            h.Engine.Tick();
        }
    }

    // A frame without stick data (all 16 channels at Crossfire's placeholder).
    private static void Placeholder(Harness h, string ip) => h.Same(ip, 16, JoystickEngine.XF_PLACEHOLDER);

    // ---- which modules ----------------------------------------------------------------------------------

    [Fact]
    public void Release_ActivationsAndRestarts_AreNoReleases()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 4000, 16, false), Assert.Single(h.Posts));
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionEnd, 0, 0, true), h.Ends.Single());
        Assert.Empty(h.Releases);
    }

    [Fact]
    public void Release_LockedElrs()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        for (int i = 0; i < 40; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }   // activated 4 s ago
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(ModuleKind.Elrs, h.Engine.KindOf(DefIp));
        int posts = h.Posts.Count, logs = h.Log.Count, states = h.States.Count;
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release(DefIp) }, h.Posts.Skip(posts));
        Assert.Equal(new[] { "Releasing 192.168.1.50: joystick_end, so it stops streaming and broadcasts again",
                             "Released 192.168.1.50: it broadcasts again, any app can connect to it" }, h.Log.Skip(logs));
        Assert.Equal(states, h.States.Count);
        // the engine goes on as before (the host stops after it)
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(ModuleKind.Elrs, h.Engine.KindOf(DefIp));
        h.Clock.Advance(0.1);
        h.Same(DefIp, 16, 100);
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Single(h.Releases);
        Assert.Equal(0, h.Engine.RestartCount);
    }

    [Fact]
    public void Release_UnknownKind_GetsIt()
    {
        // no beacon: an ELRS module streaming since before we started never beaconed to us
        var h = new Harness();
        h.Same("10.0.0.5", 16, 100);
        for (int i = 0; i < 40; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.5", 16, 100); h.Engine.Tick(); }
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.5"));
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release("10.0.0.5") }, h.Posts);
        Assert.Equal(1, h.Log.Count(l => l == Sent("10.0.0.5")));
        // also with 8 channels (surely no Crossfire)
        h = new Harness();
        h.Engine.SetRequest(4000, 8);
        h.Same("10.0.0.5", 8, 100);
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release("10.0.0.5") }, h.Releases);
    }

    [Fact]
    public void Release_KnownCrossfire_LoggedNotSent()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        h.Same(DefIp, 16, 100);
        for (int i = 0; i < 40; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(ModuleKind.Crossfire, h.Engine.KindOf(DefIp));
        int posts = h.Posts.Count, logs = h.Log.Count;
        Assert.Equal(0, h.Engine.Release());
        Assert.Equal(posts, h.Posts.Count);
        Assert.Equal(new[] { "Not releasing 192.168.1.50: Crossfire broadcasts while streaming, other apps find it as it is" },
                     h.Log.Skip(logs));
        // activated 1 s ago, not streaming to us yet
        h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.6");
        h.Clock.Advance(1);
        logs = h.Log.Count;
        Assert.Equal(0, h.Engine.Release());
        Assert.Empty(h.Ends);
        Assert.Equal(new[] { Crossfire("10.0.0.6") }, h.Log.Skip(logs));
        // locked and activated a moment ago: logged once
        h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        h.Same(DefIp, 16, 100);
        logs = h.Log.Count;
        Assert.Equal(0, h.Engine.Release());
        Assert.Equal(new[] { Crossfire(DefIp) }, h.Log.Skip(logs));
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Release_RecentlyActivated_NotLocked()
    {
        // activated less than 3 s ago and not locked (it may be about to stream to us): sent; older: not
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        h.Clock.Advance(2.9);
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release("10.0.0.7") }, h.Releases);
        Assert.Equal(1, h.Log.Count(l => l == Sent("10.0.0.7")));
        // the edge: less than, not at
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        h.Clock.Now = h.Clock.Now.AddTicks(TimeSpan.FromSeconds(JoystickEngine.SOURCE_TIMEOUT_SEC).Ticks - 1);
        Assert.Equal(1, h.Engine.Release());
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC);
        int logs = h.Log.Count;
        Assert.Equal(0, h.Engine.Release());
        Assert.Empty(h.Releases);
        Assert.Equal(logs, h.Log.Count);                      // no module: nothing logged
        // a failed activation counts too: it may have reached the module
        h = new Harness { ActivateResult = false };
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        h.Clock.Advance(1);
        Assert.Equal(1, h.Engine.Release());
        // the module IP, unknown kind, activated a moment ago: it gets it (unknown kind does)
        h = new Harness();
        h.Engine.SetTarget("10.0.0.8");
        h.Clock.Advance(0.5);
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release("10.0.0.8") }, h.Releases);
    }

    [Fact]
    public void Release_LockedAndRecent_EachIpOnce()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Clock.Advance(0.5);
        h.Same(DefIp, 16, 100);
        Assert.Equal(1, h.Engine.Release());
        Assert.Single(h.Releases);
        Assert.Equal(1, h.CountLog("Releasing "));
        // a restart a moment ago: still once
        h.Engine.SetRequest(2000, 16);
        Assert.Single(h.RestartBegins);
        h.Clock.Advance(0.5);
        h.Same(DefIp, 16, 100);
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(2, h.Releases.Count);
        Assert.Equal(2, h.CountLog("Releasing "));
    }

    [Fact]
    public void Release_Several_InOrder()
    {
        // the locked module first, then the ones activated lately in the order they were activated
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A
        h.Clock.Advance(6);
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.9");           // D: 4 s before the release
        h.Clock.Advance(1.5);
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.3");           // C: 2.5 s before
        h.Clock.Advance(0.5);
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A again (throttle passed): 2 s before
        h.Clock.Advance(0.5);
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.4");    // X, Crossfire: 1.5 s before
        h.Clock.Advance(0.5);
        h.Same("10.0.0.5", 16, 100);                          // B streams to us (unknown kind): locked
        for (int i = 0; i < 10; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.5", 16, 100); h.Engine.Tick(); }
        Assert.Equal("10.0.0.5", h.Engine.Source);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.9", "10.0.0.3", "10.0.0.2", "10.0.0.4" }, h.Activated);
        int posts = h.Posts.Count;
        Assert.Equal(3, h.Engine.Release());
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.3", "10.0.0.2" }, h.Posts.Skip(posts).Select(p => p.Ip));
        Assert.All(h.Posts.Skip(posts), p => Assert.True(Harness.IsRelease(p)));
        Assert.Equal(new[] { Sent("10.0.0.5"), Sent("10.0.0.3"), Sent("10.0.0.2"), Crossfire("10.0.0.4") }, ReleaseLines(h));
    }

    [Fact]
    public void Release_NoModule_Silent()
    {
        var h = new Harness();
        Assert.Equal(0, h.Engine.Release());
        Assert.Empty(h.Log);
        Assert.Empty(h.Posts);
        // the lock went (silence): its activation is older than 3 s
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        for (int i = 0; i < 40; i++) { h.Clock.Advance(0.1); h.Engine.Tick(); }
        Assert.Null(h.Engine.Source);
        int logs = h.Log.Count;
        Assert.Equal(0, h.Engine.Release());
        Assert.Equal(logs, h.Log.Count);
        Assert.Empty(h.Releases);
        // its next beacon activates it again: now it gets it (its frames are older than that activation)
        h.Clock.Advance(2);
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Clock.Advance(0.5);
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Harness.Release(DefIp) }, h.Releases);
    }

    // ---- the modules that stream to us while we drop their frames --------------------------------------

    [Fact]
    public void Release_ModuleLeftForAnotherModuleIp()
    {
        // R7: the module left for another module IP gets nothing at the switch. It streams on to us (it does not know
        // it was left), so a release sends it joystick_end while its last frame is less than 3 s old, also one in the
        // same step, before its next frame comes: the switch puts it with the modules whose frames we drop. Known to
        // stream, it goes before the new module IP, activated a moment ago. Its last frame counts, not the switch:
        // quiet that long, it went away.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(0.5);
        h.Same("10.0.0.2", 16, 100);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));
        Assert.Null(h.Engine.Source);
        Assert.Empty(h.Ends);                                 // nothing at the switch (R7): no release, no restart
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));      // a Stop in the same step
        Assert.Equal(new[] { Sent("10.0.0.2"), Sent("10.0.0.3") }, ReleaseLines(h, logs));
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));      // a release changes no table
        // no frame of it since, 2.9 s after its last one: still; at 3 s, gone (and the new module IP's activation is
        // as old)
        h.Clock.Advance(2.9);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        h.Clock.Advance(0.1);
        logs = h.Log.Count;
        Assert.Empty(Released(h, 0));
        Assert.Equal(logs, h.Log.Count);
        // the switch 2 s after its last frame (the lock held): 1 s after the switch it is gone, the new module IP not
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(2);
        h.Engine.Tick();
        Assert.Equal("10.0.0.2", h.Engine.Source);
        h.Engine.SetTarget("10.0.0.3");
        h.Clock.Advance(0.9);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        h.Clock.Advance(0.1);
        Assert.Equal(new[] { "10.0.0.3" }, Released(h, 1));
        // a lock already stale at the switch (no tick since): not a module that streams to us
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(3.05);
        Assert.Equal("10.0.0.2", h.Engine.Source);
        h.Engine.SetTarget("10.0.0.3");
        Assert.Equal(new[] { "10.0.0.3" }, Released(h, 1));
        // the module IP set to the locked module itself, streaming: nothing changes, it is released as the locked one
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(0.5);
        h.Same("10.0.0.2", 16, 100);
        h.Engine.SetTarget("10.0.0.2");
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(0, h.Engine.DroppedForTest);
        Assert.Equal(new[] { "10.0.0.2" }, Released(h, 1));
    }

    [Fact]
    public void Release_ModuleLeft_NewOneLockedSince()
    {
        // The same when the new module IP has locked since, also before the module left sent another frame, and down a
        // chain of switches: each module left goes by its last frame, the one heard longest ago first. Its activation
        // entry keeps that it locked after that activation, so the rule of the modules activated a moment ago does not
        // add it; one restarted or activated again after its last frame gets it by that rule, once.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A: activated, locks
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(0.5);
        h.Same("10.0.0.2", 16, 100);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // B
        h.Clock.Advance(0.1);
        h.Same("10.0.0.3", 16, 100);                          // B locks, before A's next frame
        Assert.Equal("10.0.0.3", h.Engine.Source);
        Assert.True(h.Engine.LockedSinceForTest("10.0.0.2"));
        h.Clock.Advance(0.5);
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.2" }, Released(h, 2));
        Assert.Equal(new[] { Sent("10.0.0.3"), Sent("10.0.0.2") }, ReleaseLines(h, logs));
        // on to C, which locks too: C, then A and B by their last frames
        Assert.True(h.Engine.SetTarget("10.0.0.4"));
        h.Clock.Advance(0.1);
        h.Same("10.0.0.4", 16, 100);
        Assert.True(h.Engine.LockedSinceForTest("10.0.0.3"));
        h.Clock.Advance(0.1);
        Assert.Equal(new[] { "10.0.0.4", "10.0.0.2", "10.0.0.3" }, Released(h, 3));
        // A and B quiet for 3 s, C streams on: only C
        Rounds(h, 30, "10.0.0.4");
        Assert.Equal(new[] { "10.0.0.4" }, Released(h, 1));
        // back to A, activated again and not locked since: C (left a moment ago, it streams on), then A, once
        Assert.True(h.Engine.SetTarget("10.0.0.2"));
        h.Clock.Advance(0.1);
        Assert.Equal(new[] { "10.0.0.4", "10.0.0.2" }, Released(h, 2));
        // A restarted 2 s after its last frame, then left for B, which locks: when A's last frame is 3 s old, A has
        // not locked since the restart, and the rule of the modules activated a moment ago releases it
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Clock.Advance(2);
        h.Engine.SetRequest(2000, 16);
        Assert.Single(h.RestartBegins);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));
        h.Clock.Advance(0.1);
        h.Same("10.0.0.3", 16, 100);
        Assert.Equal("10.0.0.3", h.Engine.Source);
        Assert.False(h.Engine.LockedSinceForTest("10.0.0.2"));
        h.Clock.Advance(1);
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.2" }, Released(h, 2));
        Assert.Equal(1, h.Log.Count(l => l == Sent("10.0.0.2")));
    }

    [Fact]
    public void Release_ModuleLeft_LockedSince_NotByTheActivationRule()
    {
        // The module left by a switch, which locked after its activation, is never added by the rule of the modules
        // activated a moment ago (R7: the module left gets nothing by that rule), also once the new module IP locked:
        // its activation entry says so. Only the dropped table releases it. Here 8 modules that stream to us after its
        // last frame push it out of that table (8 entries, the one heard longest ago goes), so it gets nothing.
        var h = new Harness();
        string[] others = Enumerable.Range(1, JoystickEngine.TableSize).Select(i => $"10.0.4.{i}").ToArray();
        foreach (string ip in others) h.Packet(Frames.Ascii("ELRS"), ip);     // known as ELRS by their beacons
        h.Engine.ResetSessionState();                         // a new session: the kinds stay
        h.Clock.Advance(10);
        Assert.True(h.Engine.SetTarget("10.0.0.2"));          // A: activated
        h.Same("10.0.0.2", 16, 100);                          // locks
        h.Clock.Advance(0.1);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // B
        h.Clock.Advance(0.1);
        h.Same("10.0.0.3", 16, 100);                          // B locks
        Assert.True(h.Engine.LockedSinceForTest("10.0.0.2"));
        h.Clock.Advance(0.1);
        foreach (string ip in others) h.Same(ip, 16, 100);    // they stream to us; A is quiet
        Assert.Equal(JoystickEngine.TableSize, h.Engine.DroppedForTest);
        Assert.Equal(new[] { "10.0.0.3" }.Concat(others), Released(h, 1 + JoystickEngine.TableSize));
        Assert.Equal(0, h.CountLog("Releasing 10.0.0.2"));
    }

    [Fact]
    public void Release_ModuleLeft_ByItsLastFrame()
    {
        // The dropped modules go by when their last frame came, not by when the engine learned of them: C, a 2nd
        // source heard after A's last applied frame, goes after A when the switch to B (which puts A with them) and
        // the release come before the next frame of either.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.5");           // C
        h.Same("10.0.0.2", 16, 100);                          // A locks
        h.Clock.Advance(0.004);
        h.Same("10.0.0.5", 16, 100);                          // C: the 2nd source
        Assert.Equal("10.0.0.2", h.Engine.Source);
        h.Clock.Advance(0.002);
        h.Engine.SetTarget("10.0.0.3");
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.5", "10.0.0.3" }, Released(h, 3));
        // A known as a 2nd source before: the switch moves it on to its last frame, applied since
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.5");
        h.Same("10.0.0.5", 16, 100);                          // C locks
        h.Clock.Advance(0.004);
        h.Same("10.0.0.2", 16, 100);                          // A: the 2nd source
        h.Clock.Advance(3.1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        h.Same("10.0.0.2", 16, 100);                          // A locks
        h.Clock.Advance(0.004);
        h.Same("10.0.0.5", 16, 100);                          // C: the 2nd source now
        h.Clock.Advance(0.002);
        h.Engine.SetTarget("10.0.0.3");
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.5", "10.0.0.3" }, Released(h, 3));
    }

    [Fact]
    public void Release_SecondSource_StreamingToUs()
    {
        // Two ELRS modules beacon, both are activated, A locks and B streams to us as the ignored 2nd source: a release
        // sends both joystick_end (B can't be found by another app either), the locked one first. Not once B is quiet.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.3");
        Assert.Equal(2, h.Activated.Count);
        Rounds(h, 100, "10.0.0.2", "10.0.0.3");               // 10 s: B's activation is long past
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(1, h.CountLog("Ignoring 2nd source 10.0.0.3 (locked to 10.0.0.2)"));
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));      // joystick_end alone: no rate, no channels
        Assert.Equal(new[] { Sent("10.0.0.2"), Sent("10.0.0.3") }, ReleaseLines(h, logs));
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        // B went quiet: less than 3 s after its last frame it still gets it, at 3 s not
        DateTime bLast = h.Clock.Now;
        Rounds(h, 10, "10.0.0.2");
        h.Clock.Now = bLast.AddTicks(TimeSpan.FromSeconds(JoystickEngine.SOURCE_TIMEOUT_SEC).Ticks - 1);
        h.Same("10.0.0.2", 16, 100);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        h.Clock.Now = bLast.AddSeconds(JoystickEngine.SOURCE_TIMEOUT_SEC);
        h.Same("10.0.0.2", 16, 100);
        logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.2" }, Released(h, 1));
        Assert.Equal(new[] { Sent("10.0.0.2") }, ReleaseLines(h, logs));
    }

    [Fact]
    public void Release_SecondSource_Kinds()
    {
        // A 2nd source known as Crossfire by its beacon: logged, not sent. One of unknown kind (it streams to us since
        // before we started, so it never beaconed to us, and we never activated it): sent.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.4");
        Rounds(h, 50, "10.0.0.2", "10.0.0.4");
        Assert.Equal("10.0.0.2", h.Engine.Source);
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.2" }, Released(h, 1));
        Assert.Equal(new[] { Sent("10.0.0.2"), Crossfire("10.0.0.4") }, ReleaseLines(h, logs));
        h = new Harness();
        Rounds(h, 50, "10.0.0.5", "10.0.0.6");                // neither beaconed nor was activated
        Assert.Equal("10.0.0.5", h.Engine.Source);
        Assert.Empty(h.Activated);
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.6"));
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.6" }, Released(h, 2));
        Assert.Equal(1, h.Log.Count(l => l == Sent("10.0.0.6")));
    }

    [Fact]
    public void Release_Dropped_KeptThroughLockChanges()
    {
        // What is known about the modules we drop outlives a change of the lock (the "Ignoring 2nd source" warnings do
        // not): C streamed to us until a moment before B took over the lock from A, which went quiet.
        var h = new Harness();
        Rounds(h, 5, "10.0.0.2");                             // A locks
        DateTime aLast = h.Clock.Now;
        Rounds(h, 29, null, "10.0.0.7");                      // C: a 2nd source while A's lock holds
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(1, h.CountLog("Ignoring 2nd source 10.0.0.7 (locked to 10.0.0.2)"));
        h.Clock.Now = aLast.AddSeconds(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.05);
        h.Same("10.0.0.3", 16, 100);                          // B: A's lock is stale, B takes it
        Assert.Equal("10.0.0.3", h.Engine.Source);
        h.Clock.Advance(0.1);
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.7" }, Released(h, 2));
        Assert.Equal(0, h.Log.Count(l => l == Sent("10.0.0.2")));         // quiet for more than 3 s
        h.Same("10.0.0.7", 16, 100);                          // warned again, now that B has the lock
        Assert.Equal(1, h.CountLog("Ignoring 2nd source 10.0.0.7 (locked to 10.0.0.3)"));
    }

    [Fact]
    public void Release_ModuleLeft_StillStreaming()
    {
        // R7 sends the module left for another module IP nothing at the switch; when it still streams to us at the
        // release (we drop its frames: it is not the module IP, and we activated it or had it locked this session), the
        // release sends it joystick_end. A module we activated this session that never locked (a 2nd source then) the
        // same.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A: activated, locks
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.4");           // D: activated, a 2nd source
        Rounds(h, 10, "10.0.0.2", "10.0.0.4");
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // B
        Assert.Empty(h.Ends);                                 // nothing to A at the switch: no release, no restart
        Rounds(h, 50, "10.0.0.2", "10.0.0.3", "10.0.0.4");    // B locks; A and D stream on to us
        Assert.Equal("10.0.0.3", h.Engine.Source);
        Assert.Empty(h.Ends);
        int logs = h.Log.Count;
        // heard longest ago first: A, then D, each round
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.2", "10.0.0.4" }, Released(h, 3));
        Assert.Equal(new[] { Sent("10.0.0.3"), Sent("10.0.0.2"), Sent("10.0.0.4") }, ReleaseLines(h, logs));
        // A and D quiet for 3 s: only B
        Rounds(h, 31, null, "10.0.0.3");
        Assert.Equal(new[] { "10.0.0.3" }, Released(h, 1));
    }

    [Fact]
    public void Release_ModuleLeft_FoundStreaming()
    {
        // A module found streaming (we never activated it), locked this session, then left by a switch to another
        // module IP, that streams on to us: a release sends it joystick_end, of unknown kind (it never beaconed to us)
        // or known as ELRS, also when the new module IP locked since, and down a chain of switches. A new session
        // forgets that it had the lock.
        var h = new Harness();
        Rounds(h, 20, "10.0.0.6");                            // found streaming, unknown kind: locks
        Assert.Equal("10.0.0.6", h.Engine.Source);
        Assert.Empty(h.Activated);
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.6"));
        Assert.True(h.Engine.SetTarget("10.0.0.7"));
        Assert.Empty(h.Releases);                             // nothing at the switch (R7)
        Rounds(h, 40, "10.0.0.6", "10.0.0.7");                // 10.0.0.7 locks; 10.0.0.6 streams on to us
        Assert.Equal("10.0.0.7", h.Engine.Source);
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.7", "10.0.0.6" }, Released(h, 2));
        Assert.Equal(new[] { Sent("10.0.0.7"), Sent("10.0.0.6") }, ReleaseLines(h, logs));
        // on to 10.0.0.8, which does not answer: both stream on to us and go first, heard longest ago first
        Assert.True(h.Engine.SetTarget("10.0.0.8"));
        Rounds(h, 10, "10.0.0.6", "10.0.0.7");
        Assert.Equal(new[] { "10.0.0.6", "10.0.0.7", "10.0.0.8" }, Released(h, 3));
        // quiet for 3 s: nothing (10.0.0.8's activation is older too, its next try not due yet)
        for (int i = 0; i < 31; i++) { h.Clock.Advance(0.1); h.Engine.Tick(); }
        logs = h.Log.Count;
        Assert.Empty(Released(h, 0));
        Assert.Equal(logs, h.Log.Count);
        // known as ELRS by its beacon in the last session (a new session keeps it)
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.6");
        h.Engine.ResetSessionState();
        Rounds(h, 20, "10.0.0.6");
        Assert.Equal("10.0.0.6", h.Engine.Source);
        h.Engine.SetTarget("10.0.0.7");
        Rounds(h, 40, "10.0.0.6", "10.0.0.7");
        Assert.Equal(new[] { "10.0.0.7", "10.0.0.6" }, Released(h, 2));
        // locked in the last session only: unknown kind, neither activated nor locked this session, left alone
        h = new Harness();
        Rounds(h, 20, "10.0.0.5");
        Assert.Equal("10.0.0.5", h.Engine.Source);
        h.Engine.ResetSessionState();
        h.Engine.SetTarget("10.0.0.7");
        Rounds(h, 40, "10.0.0.5", "10.0.0.7");
        Assert.Equal("10.0.0.7", h.Engine.Source);
        logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.7" }, Released(h, 1));
        Assert.Equal(new[] { Sent("10.0.0.7") }, ReleaseLines(h, logs));
        Assert.Equal(0, h.CountLog("Releasing 10.0.0.5"));
    }

    [Fact]
    public void Release_Held_TableFull()
    {
        // More modules take the lock in one session than the table holds (8): the one locked longest ago is forgotten,
        // and a module that locks again counts from then. With a module IP set, the frames of one forgotten (unknown
        // kind, never activated) are left alone; those of the ones remembered are released.
        var h = new Harness();
        string[] ips = Enumerable.Range(1, 9).Select(i => $"10.0.2.{i}").ToArray();
        foreach (string ip in ips)                            // each locks once the one before went quiet
        {
            h.Same(ip, 16, 100);
            Assert.Equal(ip, h.Engine.Source);
            h.Clock.Advance(3.1);
            h.Engine.Tick();
            Assert.Null(h.Engine.Source);
        }
        h.Same(ips[1], 16, 100);                              // locks again: now the one locked last
        h.Clock.Advance(3.1);
        h.Engine.Tick();
        h.Engine.SetTarget("10.0.0.7");
        for (int i = 0; i < 5; i++)
        {
            h.Clock.Advance(0.1);
            h.Same(ips[0], 16, 100);
            h.Same(ips[1], 16, 100);
            h.Same(ips[2], 16, 100);
            h.Engine.Tick();
        }
        Assert.Equal(new[] { ips[1], ips[2], "10.0.0.7" }, Released(h, 3));
        Assert.Equal(0, h.CountLog($"Releasing {ips[0]}"));
    }

    [Fact]
    public void Release_ModuleIpSet_UnknownOther_LeftAlone()
    {
        // With a module IP set, other modules' beacons are not heard: a module of unknown kind that we neither
        // activated nor had locked this session and that streams to us is left alone (it could be a Crossfire we
        // can't tell). One whose kind a beacon told, in an earlier session too (a new session keeps it), is not: ELRS
        // is released, Crossfire logged.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.6");           // known as ELRS, activated...
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.4");    // known as Crossfire
        h.Engine.ResetSessionState();                         // ...in the last session
        Assert.Equal(ModuleKind.Elrs, h.Engine.KindOf("10.0.0.6"));
        Assert.Equal(ModuleKind.Crossfire, h.Engine.KindOf("10.0.0.4"));
        h.Engine.SetTarget("10.0.0.3");
        // B locks; 10.0.0.5 (unknown), 10.0.0.6 and 10.0.0.4 stream to us
        Rounds(h, 50, "10.0.0.3", "10.0.0.5", "10.0.0.6", "10.0.0.4");
        Assert.Equal("10.0.0.3", h.Engine.Source);
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.6" }, Released(h, 2));
        Assert.Equal(new[] { Sent("10.0.0.3"), Sent("10.0.0.6"), Crossfire("10.0.0.4") }, ReleaseLines(h, logs));
        Assert.Equal(0, h.CountLog("10.0.0.5"));
        // the module IP itself, not locked, sending placeholder frames: released (it streams to us), also when its last
        // activation (the module IP is POSTed again every 5 s while it is not locked) is 3 s old
        h = new Harness();
        h.Engine.SetTarget("10.0.0.3");
        for (int i = 0; i < 80; i++) { h.Clock.Advance(0.1); Placeholder(h, "10.0.0.3"); h.Engine.Tick(); }
        Assert.Equal(2, h.Activated.Count);
        Assert.Null(h.Engine.Source);
        Assert.Equal(new[] { "10.0.0.3" }, Released(h, 1));
    }

    [Fact]
    public void Release_ModuleIpSet_ActivatedUnknownOther()
    {
        // With a module IP set, a module of unknown kind that is not the module IP but that we activated this session
        // (the module IP before a switch, which never locked) streams to us: its frames are dropped, so it is released
        // after the module IP, also once its activation is more than 3 s old.
        var h = new Harness();
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // activated, unknown kind
        h.Clock.Advance(0.1);
        Assert.True(h.Engine.SetTarget("10.0.0.4"));
        Rounds(h, 40, "10.0.0.3", "10.0.0.4");                // both stream to us for 4 s; 10.0.0.4 locks
        Assert.Equal("10.0.0.4", h.Engine.Source);
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.0.3"));
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.4" }, h.Activated);
        Assert.Equal(new[] { "10.0.0.4", "10.0.0.3" }, Released(h, 2));
    }

    [Fact]
    public void Release_PlaceholderStream()
    {
        // A module that streams placeholder frames to us never locks: released while they come (it streams to us),
        // also long after its activation; one known as Crossfire is logged, not sent.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        for (int i = 0; i < 60; i++) { h.Clock.Advance(0.1); Placeholder(h, "10.0.0.2"); h.Engine.Tick(); }
        Assert.Null(h.Engine.Source);
        Assert.Contains((EngineState.Searching, JoystickEngine.NoStickState), h.States);
        Assert.Equal(new[] { "10.0.0.2" }, Released(h, 1));
        Assert.Equal(1, h.Log.Count(l => l == Sent("10.0.0.2")));
        // quiet since: nothing
        for (int i = 0; i < 31; i++) { h.Clock.Advance(0.1); h.Engine.Tick(); }
        Assert.Empty(Released(h, 0));
        // Crossfire
        h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.4");
        for (int i = 0; i < 60; i++) { h.Clock.Advance(0.1); Placeholder(h, "10.0.0.4"); h.Engine.Tick(); }
        int logs = h.Log.Count;
        Assert.Empty(Released(h, 0));
        Assert.Equal(new[] { Crossfire("10.0.0.4") }, h.Log.Skip(logs));
        // placeholder frames from another module while one is locked
        h = new Harness();
        for (int i = 0; i < 60; i++)
        {
            h.Clock.Advance(0.1);
            h.Same("10.0.0.2", 16, 100);
            Placeholder(h, "10.0.0.8");
            h.Engine.Tick();
        }
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.8" }, Released(h, 2));
        // the locked module's own placeholder frames (fast stick movement): released once, as the locked one
        h = new Harness();
        for (int i = 0; i < 20; i++)
        {
            h.Clock.Advance(0.1);
            h.Same("10.0.0.2", 16, 100);
            Placeholder(h, "10.0.0.2");
            h.Engine.Tick();
        }
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(0, h.Engine.DroppedForTest);
        Assert.Equal(new[] { "10.0.0.2" }, Released(h, 1));
    }

    [Fact]
    public void Release_Dropped_ClearedByReset()
    {
        // A new session forgets them, as it forgets the activations: Stop releases before the next Start resets.
        var h = new Harness();
        Rounds(h, 30, "10.0.0.2", "10.0.0.3");
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        h.Engine.ResetSessionState();
        Assert.Equal(0, h.Engine.DroppedForTest);
        Assert.Empty(Released(h, 0));
    }

    [Fact]
    public void Release_Dropped_TableFull()
    {
        // More modules stream to us than the table holds (8): the one heard longest ago is left out, never a module
        // twice.
        var h = new Harness();
        string[] ips = Enumerable.Range(1, 9).Select(i => $"10.0.1.{i}").ToArray();
        h.Same(DefIp, 16, 100);
        for (int r = 0; r < 20; r++)
        {
            h.Clock.Advance(0.1);
            h.Same(DefIp, 16, 100);
            foreach (string ip in ips) h.Same(ip, 16, 100);
            h.Engine.Tick();
        }
        Assert.Equal(JoystickEngine.TableSize, h.Engine.DroppedForTest);
        Assert.Equal(new[] { DefIp }.Concat(ips.Skip(1)), Released(h, 1 + JoystickEngine.TableSize));
        Assert.Equal(0, h.CountLog($"Releasing {ips[0]}"));
    }

    [Fact]
    public void Release_KnownStreamingFirst()
    {
        // The modules known to stream to us go first, the ones activated a moment ago (that may not even be there)
        // after them. A locks, the module IP is set to X (a typo, or a module that is off) and A streams on to us: A,
        // then X.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A: activated, locks
        Rounds(h, 5, "10.0.0.2");
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // X: activated now, never streams
        Rounds(h, 3, "10.0.0.2");
        int logs = h.Log.Count;
        Assert.Equal(new[] { "10.0.0.2", "10.0.0.3" }, Released(h, 2));
        Assert.Equal(new[] { Sent("10.0.0.2"), Sent("10.0.0.3") }, ReleaseLines(h, logs));
        // all three: the locked module, then a 2nd source, then one activated a moment ago
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.4");           // D: activated, never streams
        h.Clock.Advance(0.5);
        Rounds(h, 10, "10.0.0.5", "10.0.0.6");                // found streaming: 10.0.0.5 locks, 10.0.0.6 a 2nd source
        Assert.Equal("10.0.0.5", h.Engine.Source);
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.6", "10.0.0.4" }, Released(h, 3));
    }

    [Fact]
    public void Release_ModuleIpSet_NoPostCanGoOut_ItsLastActivationCounts()
    {
        // The module IP set while no POST can go out (8 modules wait for an answer already): nothing new is sent, but
        // the module's last activation, a moment ago, still counts (its entry stays, only its throttle is lifted), so
        // a release sends it joystick_end, first, as the one activated first. (The table holds 8 activations: here one
        // of the 8 POSTs waiting is the release of the last session, to a module that does not answer.)
        var clock = new TestClock();
        var posts = new List<ActivationPost>();
        var log = new List<string>();
        using var hold = new ManualResetEventSlim();
        var e = new JoystickEngine
        {
            Clock = () => clock.Now,
            OutputOverride = _ => { },
            AsyncPostsForTest = true,
            ActivateOverride = p =>
            {
                lock (posts) posts.Add(p);
                if (p.Ip != "10.0.0.9") hold.Wait(10000);
                return true;
            },
        };
        e.Log += m => { lock (log) log.Add(m); };
        IPEndPoint From(string ip) => new(IPAddress.Parse(ip), 49153);
        string[] others = Enumerable.Range(1, JoystickEngine.TableSize - 1).Select(i => $"10.0.3.{i}").ToArray();
        string[] expected = new[] { "10.0.0.9" }.Concat(others).ToArray();
        int logs;
        try
        {
            // the last session: 10.0.0.5 found streaming, released when it ended; it does not answer
            e.Process(Frames.Channels(Enumerable.Repeat(100, 16).ToArray()), From("10.0.0.5"));
            Assert.Equal(1, e.Release());
            e.ResetSessionState();
            Assert.Equal(1, e.ReleasesPending);
            // this one
            e.Process(Frames.Ascii("ELRS"), From("10.0.0.9"));
            Assert.True(e.WaitForPostsForTest(5000, "10.0.0.9"));      // its joystick_begin went out and was answered
            clock.Advance(0.5);
            foreach (string ip in others) e.Process(Frames.Ascii("ELRS"), From(ip));     // these wait for an answer
            Assert.True(e.SetTarget("10.0.0.9"));
            e.Tick();
            Assert.True(SpinWait.SpinUntil(() => { lock (posts) return posts.Count == 1 + expected.Length; }, 5000));
            lock (posts) Assert.Single(posts, p => p.Ip == "10.0.0.9");         // nothing new to it: no free slot
            clock.Advance(0.5);
            lock (log) logs = log.Count;
            Assert.Equal(expected.Length, e.Release());
        }
        finally
        {
            hold.Set();
        }
        Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
        lock (log) Assert.Equal(expected.Select(Sent), log.Skip(logs).Where(l => l.StartsWith("Releasing ")));
        lock (posts)
            Assert.Equal(new[] { "10.0.0.5" }.Concat(expected).OrderBy(ip => ip),
                         posts.Where(Harness.IsRelease).Select(p => p.Ip).OrderBy(ip => ip));
    }

    [Fact]
    public void Release_PortChange_IsAStopAndAStart()
    {
        // R14: a port change is a stop and a start, with its release (the modules send to port 11000 only: on another
        // port no frame of theirs comes, and a later stop could not release them). The port changes only while
        // stopped: running, the box is locked and applying it does nothing; the Stop releases, then the new port
        // starts.
        const int portA = 27420, portB = 27421;
        var vm = new MainViewModel(portA, null, a => a());
        var posts = new List<ActivationPost>();
        vm.Engine.OutputOverride = _ => { };
        vm.Engine.SkipVJoyForTest = true;
        vm.Engine.ActivateOverride = p => { lock (posts) posts.Add(p); return true; };
        var f16 = Frames.Channels(Enumerable.Repeat(100, 16).ToArray());
        try
        {
            vm.StartStopCommand.Execute(null);                // Start on port A
            Assert.True(vm.IsRunning);
            Assert.False(vm.IsPortEditable);
            Thread.Sleep(250);
            Assert.True(SpinWait.SpinUntil(() => { Send(portA, f16); Thread.Sleep(20); return vm.Engine.Source != null; }, 3000));
            vm.PortText = portB.ToString();
            vm.ApplyPortCommand.Execute(null);                // running: nothing
            Assert.Equal(portA, vm.Engine.Port);
            Assert.True(vm.Engine.IsRunning);
            lock (posts) Assert.DoesNotContain(posts, Harness.IsRelease);
            vm.StartStopCommand.Execute(null);                // Stop: the release
            lock (posts) Assert.Equal(new[] { Harness.Release("127.0.0.1") }, posts.Where(Harness.IsRelease));
            Assert.True(SpinWait.SpinUntil(() => !vm.IsRunning, 3000));
            Assert.True(vm.IsPortEditable);
            vm.PortText = portB.ToString();
            vm.ApplyPortCommand.Execute(null);                // stopped: the new port starts
            Assert.True(vm.Engine.IsRunning);
            Assert.Equal(portB, vm.Engine.Port);
        }
        finally
        {
            vm.Shutdown();
        }
        lock (posts) Assert.Single(posts, Harness.IsRelease);  // nothing streamed on port B: no second release
    }

    [Fact]
    public async Task Release_FromAnotherThread_WaitsForALockBeingTaken()
    {
        // Stop comes from another thread than the frames. A module locking right then counts as the locked one:
        // Release reads the lock in one step, under the gate it is taken under. Read one by one, it could see no
        // lock, then this module as locked since its activation, and send it nothing (it would stream on).
        var clock = new TestClock();
        var posts = new List<ActivationPost>();
        var e = new JoystickEngine
        {
            Clock = () => clock.Now,
            OutputOverride = _ => { },
            ActivateOverride = p => { lock (posts) posts.Add(p); return true; },
        };
        var from = new IPEndPoint(IPAddress.Parse(DefIp), 49153);
        Task<int>? release = null;
        bool waited = false;
        e.Log += m =>
        {
            // said under the gate, the lock being taken
            if (release != null || m != $"Joystick source locked to {DefIp}") return;
            release = Task.Run(e.Release);
            waited = !release.Wait(300);
        };
        e.Process(Frames.Ascii("ELRS"), from);
        clock.Advance(1);
        e.Process(Frames.Channels(Enumerable.Repeat(100, 16).ToArray()), from);
        Assert.NotNull(release);
        Assert.True(waited, "Release waits for the lock being taken");
        Assert.Equal(1, await release!);
        lock (posts)
            Assert.Equal(new[] { new ActivationPost(DefIp, JoystickEngine.ActionBegin, 4000, 16, false), Harness.Release(DefIp) },
                         posts);
    }

    // ---- results -----------------------------------------------------------------------------------------

    [Fact]
    public void Release_SyncResults()
    {
        // the test seam answers at once: the result is logged right after the release line
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        int logs = h.Log.Count;
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Sent(DefIp), "Released 192.168.1.50: it broadcasts again, any app can connect to it" },
                     h.Log.Skip(logs));
        h.EndResult = false;
        logs = h.Log.Count;
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(new[] { Sent(DefIp),
                             "Releasing 192.168.1.50 failed: no answer - it may stream on until its WiFi or power is cycled" },
                     h.Log.Skip(logs));
        // a hook that throws: its message is why
        h.Engine.ActivateOverride = _ => throw new InvalidOperationException("connection refused");
        logs = h.Log.Count;
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(Failed(DefIp, "connection refused"), h.Log.Last());
        Assert.Equal(logs + 2, h.Log.Count);
    }

    [Fact]
    public void ReleaseDone_Texts()
    {
        Assert.Equal("Releasing 10.0.0.1 failed: HTTP 500 - it may stream on until its WiFi or power is cycled",
                     JoystickEngine.ReleaseFailedText("10.0.0.1", 500, ""));
        Assert.Equal(Failed("10.0.0.1", "HTTP 404"), JoystickEngine.ReleaseFailedText("10.0.0.1", 404, "ignored when there is a status"));
        Assert.Equal(Failed("10.0.0.1", "connection refused"), JoystickEngine.ReleaseFailedText("10.0.0.1", 0, "connection refused"));
        Assert.Equal(Failed("10.0.0.1", "no answer"), JoystickEngine.ReleaseFailedText("10.0.0.1", 0, ""));
        Assert.Equal(Failed("10.0.0.1", "no answer"), JoystickEngine.ReleaseFailedText("10.0.0.1", 0, null));
        Assert.Equal(Failed("10.0.0.1", "timed out after 5 s"), JoystickEngine.ReleaseFailedText("10.0.0.1", 0, "timed out after 5 s"));
        // a POST cancelled: the HTTP timeout ran out (the core's wifi_net says the same), or an exit's wait ran out
        // and cut it short, or it had not gone out yet (the Linux app and StickTime Pi say the same)
        Assert.Equal("timed out after 5 s", JoystickEngine.NoAnswerText(aborted: false));
        Assert.Equal("no answer in time", JoystickEngine.NoAnswerText(aborted: true));
        Assert.Equal("Releasing 10.0.0.1 failed: no answer in time - it may stream on until its WiFi or power is cycled",
                     JoystickEngine.ReleaseFailedText("10.0.0.1", 0, JoystickEngine.NoAnswerText(aborted: true)));
        Assert.Equal("Releasing 10.0.0.1 failed: not sent, the wait ran out before its turn - it may stream on until its " +
                     "WiFi or power is cycled", JoystickEngine.ReleaseFailedText("10.0.0.1", 0, JoystickEngine.NotSentText));
        Assert.Equal("Released 10.0.0.1: it broadcasts again, any app can connect to it", Done("10.0.0.1"));
    }

    // ---- a release changes nothing else ------------------------------------------------------------------

    [Fact]
    public void Release_RestartsUnderWay_Untouched()
    {
        // held: it may be Crossfire
        var h = new Harness();
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(1, h.CountLog("may be Crossfire"));
        Assert.Equal(1, h.Engine.Release());
        for (int i = 0; i < 205; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Single(h.RestartBegins);                       // the held restart went out as before
        Assert.Equal(2000, h.RestartBegins[0].IntervalUs);
        // failed: sent again as before
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 5 s"));
        Assert.Equal(1, h.Engine.Release());
        for (int i = 0; i < 51; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.RestartBegins.Count);
    }

    [Fact]
    public void Release_KeepsTheThrottleKindAndAskedRequest()
    {
        // the throttle: a beacon right after the release activates nothing (5 s per module, R13)
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        h.Clock.Advance(1);
        Assert.Equal(1, h.Engine.Release());
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        Assert.Single(h.Activated);
        h.Clock.Advance(4.1);
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.7");
        Assert.Equal(2, h.Activated.Count);
        // the kind and what it was asked for, when the release missed its joystick_end: the module streams on, and
        // its next lock (a new session) is as before - nothing to restart with the same request, a restart at once
        // (ELRS by its beacon) when the request changed meanwhile. (One that took forgets what it was asked:
        // Release_Took_ForgetsTheAskedRequest.)
        foreach (bool changed in new[] { false, true })
        {
            h = new Harness { EndResult = false };
            h.Packet(Frames.Ascii("ELRS"), DefIp);
            h.Same(DefIp, 16, 100);
            Assert.Equal(1, h.Engine.Release());
            Assert.Equal(1, h.CountLog("Releasing 192.168.1.50 failed: no answer"));
            h.Engine.ResetSessionState();                     // a new session (Stop, Start)
            Assert.Equal(ModuleKind.Elrs, h.Engine.KindOf(DefIp));
            if (changed) Assert.True(h.Engine.SetRequest(2000, 16));   // not locked: stored
            Assert.Empty(h.RestartBegins);
            h.Same(DefIp, 16, 100);                           // it streams on: locked again
            Assert.Equal(DefIp, h.Engine.Source);
            if (changed)
                Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, true), Assert.Single(h.RestartBegins));
            else
                Assert.Empty(h.RestartBegins);
        }
    }

    // A module locked to the module IP: its beacon first (known as ELRS) or none (unknown kind), then 2 s of frames
    // and ticks.
    private static Harness LockModuleIp(string ip, bool beacon)
    {
        var h = new Harness();
        if (beacon) h.Packet(Frames.Ascii("ELRS"), ip);
        Assert.True(h.Engine.SetTarget(ip));
        for (int i = 0; i < 20; i++) { h.Clock.Advance(0.1); h.Same(ip, 16, 100); h.Engine.Tick(); }
        Assert.Equal(ip, h.Engine.Source);
        return h;
    }

    [Fact]
    public void Release_Took_ForgetsTheAskedRequest()
    {
        // R14 + R3: a release that took forgets what its module was asked. The module is idle after its joystick_end
        // and streams what its next joystick_begin asks for, so Stop, another rate, Connect must not restart it for
        // the old request when it locks again: known as ELRS (it was restarted at once, "still streams 250 Hz, 16
        // channels from before the change"), or of unknown kind (held as maybe Crossfire, then restarted 20 s into
        // the session, a pause in flight).
        foreach (bool beacon in new[] { true, false })
        {
            var h = LockModuleIp("10.0.0.2", beacon);
            Assert.Equal(1, h.Engine.Release());
            Assert.Equal(1, h.Log.Count(l => l == Done("10.0.0.2")));
            h.Engine.ResetSessionState();                     // a new session (Stop, Start)
            Assert.Equal(beacon ? ModuleKind.Elrs : ModuleKind.Unknown, h.Engine.KindOf("10.0.0.2"));   // kept
            h.Clock.Advance(2);
            Assert.True(h.Engine.SetRequest(2000, 16));       // not locked: stored
            int posts = h.Posts.Count;
            h.Engine.Tick();                                  // the module IP's joystick_begin carries it
            Assert.Equal(new[] { new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 2000, 16, false) },
                         h.Posts.Skip(posts));
            for (int i = 0; i < 250; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.2", 16, 100); h.Engine.Tick(); }
            Assert.Equal("10.0.0.2", h.Engine.Source);
            Assert.Empty(h.RestartBegins);
            Assert.Equal(0, h.CountLog("from before the change"));
            Assert.Equal(0, h.CountLog("may be Crossfire"));
            // a change while it is locked restarts it as always (R2)
            Assert.True(h.Engine.SetRequest(4000, 16));
            Assert.Equal(new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 4000, 16, true),
                         Assert.Single(h.RestartBegins));
        }
        // the request unchanged: nothing either way
        var g = LockModuleIp("10.0.0.2", true);
        Assert.Equal(1, g.Engine.Release());
        g.Engine.ResetSessionState();
        g.Engine.Tick();
        for (int i = 0; i < 30; i++) { g.Clock.Advance(0.1); g.Same("10.0.0.2", 16, 100); g.Engine.Tick(); }
        Assert.Equal("10.0.0.2", g.Engine.Source);
        Assert.Empty(g.RestartBegins);
    }

    [Fact]
    public void Release_Took_WhileLocked()
    {
        // The release's answer while its module is still the locked one (the host stops after it): the lock stays,
        // nothing is sent, and a change while it stays locked restarts it as always (R2). Its next lock takes the
        // request of the joystick_begin it got.
        var h = LockModuleIp("10.0.0.2", true);
        int posts = h.Posts.Count;
        Assert.Equal(1, h.Engine.Release());
        Assert.Equal(posts + 1, h.Posts.Count);
        for (int i = 0; i < 30; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.2", 16, 100); h.Engine.Tick(); }
        Assert.Equal("10.0.0.2", h.Engine.Source);
        Assert.Equal(posts + 1, h.Posts.Count);
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Equal(new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 2000, 16, true),
                     Assert.Single(h.RestartBegins));
        // its joystick_end stopped it: the lock goes, the module IP's joystick_begin starts it again, no restart
        h = LockModuleIp("10.0.0.2", true);
        Assert.Equal(1, h.Engine.Release());
        for (int i = 0; i < 31; i++) { h.Clock.Advance(0.1); h.Engine.Tick(); }
        Assert.Null(h.Engine.Source);
        for (int i = 0; i < 30 && h.Activated.Count < 3; i++) { h.Clock.Advance(0.1); h.Engine.Tick(); }
        Assert.Equal(3, h.Activated.Count);                   // the beacon's, SetTarget's, this one
        h.Clock.Advance(0.05);
        h.Same("10.0.0.2", 16, 100);
        Assert.Equal("10.0.0.2", h.Engine.Source);
        for (int i = 0; i < 10; i++) { h.Clock.Advance(0.1); h.Same("10.0.0.2", 16, 100); h.Engine.Tick(); }
        Assert.Empty(h.RestartBegins);
    }

    // ---- Stop and exit -----------------------------------------------------------------------------------

    [Fact]
    public void Stop_Releases_OnceARun()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.Stop();
        Assert.Equal(new[] { Harness.Release(DefIp) }, h.Releases);
        Assert.Equal(1, h.Log.Count(l => l == Sent(DefIp)));
        Assert.Equal(1, h.Log.Count(l => l == Done(DefIp)));
        h.Engine.Stop();                                      // again (a Stop, then the exit's StopAndWait)
        h.Engine.StopAndWait(100);
        Assert.Single(h.Releases);
        Assert.Equal(1, h.CountLog("Releasing "));
    }

    [Fact]
    public void Stop_ReleasesWhatStreamsToUs_AlsoTheFramesWeDrop()
    {
        // Stop sends joystick_end to the locked module, then to the modules whose frames we drop: here the module left
        // by Connect to another module IP, which streams on, and a 2nd source. Nothing at the switch.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");           // A: locks
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.4");           // D: a 2nd source
        Rounds(h, 10, "10.0.0.2", "10.0.0.4");
        Assert.True(h.Engine.SetTarget("10.0.0.3"));          // B
        Rounds(h, 20, "10.0.0.2", "10.0.0.3", "10.0.0.4");    // B locks; A and D stream on to us
        Assert.Equal("10.0.0.3", h.Engine.Source);
        Assert.Empty(h.Ends);
        h.Engine.Stop();
        Assert.Equal(new[] { "10.0.0.3", "10.0.0.2", "10.0.0.4" }, h.Releases.Select(p => p.Ip));
        Assert.Equal(new[] { Sent("10.0.0.3"), Sent("10.0.0.2"), Sent("10.0.0.4") }, ReleaseLines(h));
        Assert.Equal(new[] { Done("10.0.0.3"), Done("10.0.0.2"), Done("10.0.0.4") }, h.Log.Where(l => l.StartsWith("Released ")));
        h.Engine.StopAndWait(100);                            // the exit after it: no second release
        Assert.Equal(3, h.Releases.Count);
    }

    [Fact]
    public void Stop_ReleasesTheCrossfire_Never()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.StopAndWait(100);
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.Log.Count(l => l == Crossfire(DefIp)));
    }

    [Fact]
    public void Stop_TheReleaseFollowsAnActivationInFlight()
    {
        // The module's joystick_begin is still on its way when Stop comes: the joystick_end goes after it, never
        // before (the module would then start streaming after its release).
        var h = new Harness();
        var posts = new List<ActivationPost>();
        using var inFlight = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            if (p.Action == JoystickEngine.ActionBegin)
            {
                inFlight.Set();
                answer.Wait(5000);
            }
            return true;
        };
        try
        {
            h.Packet(Frames.Ascii("ELRS"), DefIp);
            Assert.True(inFlight.Wait(5000));
            h.Clock.Advance(1);
            h.Engine.Stop();
            Assert.Equal(1, h.Engine.ReleasesPending);
            lock (posts) Assert.Equal(new[] { JoystickEngine.ActionBegin }, posts.Select(p => p.Action));
            answer.Set();
            Assert.True(h.Engine.WaitForReleases(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, h.Engine.ReleasesPending);
            lock (posts) Assert.Equal(new[] { new ActivationPost(DefIp, JoystickEngine.ActionBegin, 4000, 16, false),
                                              Harness.Release(DefIp) }, posts);
            lock (h.Log) Assert.Contains(Done(DefIp), h.Log);
        }
        finally
        {
            answer.Set();
        }
    }

    [Fact]
    public void Exit_WaitsForTheRelease_AtMost1_5s()
    {
        // The module does not answer: the exit (StopAndWait) gives up 1.5 s after the Stop that handed the release
        // over, whatever its join time.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        using var hold = new ManualResetEventSlim();
        using var inFlight = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            if (Harness.IsRelease(p))
            {
                inFlight.Set();
                hold.Wait(10000);
            }
            return true;
        };
        try
        {
            var sw = Stopwatch.StartNew();
            h.Engine.Stop();
            // its POST in flight before the wait runs out: one whose thread started only after it would be cut short
            // before it went out (not sent), and done
            Assert.True(inFlight.Wait(5000));
            h.Engine.StopAndWait(100);
            double s = sw.Elapsed.TotalSeconds;
            Assert.InRange(s, JoystickEngine.RELEASE_WAIT_SEC - 0.1, JoystickEngine.RELEASE_WAIT_SEC + 1.0);
            Assert.Equal(1, h.Engine.ReleasesPending);
        }
        finally
        {
            hold.Set();
        }
        Assert.True(h.Engine.WaitForReleases(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, h.Engine.ReleasesPending);
    }

    [Fact]
    public void Exit_WaitsForAQuickRelease()
    {
        // The module answers in 200 ms: the exit waits for it, so its result is in the log when it returns.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            if (Harness.IsRelease(p)) Thread.Sleep(200);
            return true;
        };
        var sw = Stopwatch.StartNew();
        h.Engine.StopAndWait(100);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.19, JoystickEngine.RELEASE_WAIT_SEC - 0.1);
        Assert.Equal(0, h.Engine.ReleasesPending);
        lock (h.Log) Assert.Contains(Done(DefIp), h.Log);

        // nothing to release: no wait
        h = new Harness();
        h.Engine.AsyncPostsForTest = true;
        sw.Restart();
        h.Engine.StopAndWait(100);
        Assert.True(sw.Elapsed.TotalSeconds < 0.5);
        Assert.Empty(h.Posts);
    }

    [Fact]
    public void Exit_WaitsAtMost1_5sAfterTheStop()
    {
        // The exit's wait counts from the Stop that handed the release over, not from the exit (the Linux app and
        // StickTime Pi count so too): closing 1 s after a Stop waits about 0.5 s more, and closing again after that
        // does not wait at all.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        using var hold = new ManualResetEventSlim();
        using var inFlight = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            if (Harness.IsRelease(p))
            {
                inFlight.Set();
                hold.Wait(10000);
            }
            return true;
        };
        try
        {
            var sinceStop = Stopwatch.StartNew();
            h.Engine.Stop();
            Assert.Equal(1, h.Engine.ReleasesPending);
            // its POST in flight before the wait runs out: one whose thread started only after it would be cut short
            // before it went out (not sent), and done
            Assert.True(inFlight.Wait(5000));
            Thread.Sleep(1000);
            var exit = Stopwatch.StartNew();
            h.Engine.StopAndWait(100);
            double total = sinceStop.Elapsed.TotalSeconds, waited = exit.Elapsed.TotalSeconds;
            Assert.InRange(total, JoystickEngine.RELEASE_WAIT_SEC - 0.1,
                           Math.Max(total - waited, JoystickEngine.RELEASE_WAIT_SEC) + 0.6);
            Assert.True(waited < JoystickEngine.RELEASE_WAIT_SEC - 0.3, $"waited {waited:F2} s: counted from the exit");
            Assert.Equal(1, h.Engine.ReleasesPending);         // cut short (the hook here does not see that)
            exit.Restart();
            h.Engine.StopAndWait(100);                          // its wait is over already
            Assert.True(exit.Elapsed.TotalSeconds < 0.3, $"waited {exit.Elapsed.TotalSeconds:F2} s");
        }
        finally
        {
            hold.Set();
        }
        Assert.True(h.Engine.WaitForReleases(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Exit_ARelease_NotSentBeforeItsTurn()
    {
        // Two releases to one module (a Stop, a Start and a Stop again while the first gets no answer): the second
        // waits behind the first on the module's POST chain. The exit's wait runs out: the first was sent (over HTTP
        // its result is "no answer in time"), the second never goes out, and says so in the Linux app's words.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        var posts = new List<ActivationPost>();
        using var inFlight = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            inFlight.Set();
            hold.Wait(10000);
            return true;
        };
        try
        {
            Assert.Equal(1, h.Engine.Release());
            Assert.True(inFlight.Wait(5000));
            Assert.Equal(1, h.Engine.Release());               // still locked: the same module again, behind the first
            Assert.Equal(2, h.Engine.ReleasesPending);
            Assert.False(h.Engine.WaitForReleases(TimeSpan.FromMilliseconds(100), abort: true));
        }
        finally
        {
            hold.Set();
        }
        Assert.True(h.Engine.WaitForReleases(TimeSpan.FromSeconds(5)));
        lock (posts) Assert.Equal(new[] { Harness.Release(DefIp) }, posts);
        Assert.Equal(2, h.Log.Count(l => l == Sent(DefIp)));
        Assert.Equal(1, h.Log.Count(l => l == Done(DefIp)));    // the hook answered the first
        Assert.Equal(1, h.Log.Count(l => l == Failed(DefIp, "not sent, the wait ran out before its turn")));
    }

    [Fact]
    public void Releases_TheDoneOnes_AreNotKept()
    {
        // Stop after Stop in a long session: each release is kept only until it is done, not until the exit
        var e = new JoystickEngine { OutputOverride = _ => { }, AsyncPostsForTest = true };
        int sent = 0;
        e.ActivateOverride = _ => { Interlocked.Increment(ref sent); return true; };
        e.Process(Frames.Channels(Enumerable.Repeat(100, 16).ToArray()), new IPEndPoint(IPAddress.Parse(DefIp), 49153));
        Assert.Equal(DefIp, e.Source);
        for (int i = 1; i <= 100; i++)
        {
            Assert.Equal(1, e.Release());
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sent) == i, 5000));
        }
        Assert.InRange(e.ReleasesHeldForTest, 1, 2);          // the last one, and the one before if not quite done
        Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, e.ReleasesHeldForTest);
    }

    private static void Send(int port, byte[] data)
    {
        using var c = new UdpClient();
        c.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(20);
        }
        return cond();
    }

    [Fact]
    public void Stop_TheReleaseOutlivesAQuickStart()
    {
        // Over real UDP: Stop, then Start at once (a new session, its POSTs cancelled). The release still goes out
        // and is not cancelled, and the new session's joystick_begin to that module waits behind it.
        const int port = 27417;
        var posts = new List<ActivationPost>();
        var log = new List<string>();
        using var hold = new ManualResetEventSlim();
        var e = new JoystickEngine
        {
            Port = port, SkipVJoyForTest = true, OutputOverride = _ => { }, AsyncPostsForTest = true,
            ActivateOverride = p =>
            {
                lock (posts) posts.Add(p);
                if (Harness.IsRelease(p)) hold.Wait(10000);
                return true;
            },
        };
        e.Log += m => { lock (log) log.Add(m); };
        int Logged(string start) { lock (log) return log.Count(l => l.StartsWith(start)); }
        var f16 = Frames.Channels(Enumerable.Repeat(100, 16).ToArray());
        e.Start();
        try
        {
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, Frames.Ascii("ELRS")); lock (posts) return posts.Count > 0; }, 3000));
            Assert.True(WaitFor(() => { Send(port, f16); return e.Source != null; }, 3000));
            Assert.True(e.WaitForPostsForTest(5000));         // the activation went out
            e.Stop();
            e.Start();
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            // the new run has bound the port (it logs this right after): a datagram sent before that is lost
            Assert.True(WaitFor(() => Logged("Listening for joystick data") == 2, 3000));
            Assert.True(WaitFor(() => { lock (posts) return posts.Any(Harness.IsRelease); }, 3000), "the release goes out");
            Assert.Equal(1, e.ReleasesPending);
            // the new session: the module beacons again (released), its joystick_begin queues behind the release
            int before;
            lock (posts) before = posts.Count;
            // sent until the engine took it (it logs this, then queues the joystick_begin; the next ones are throttled)
            Assert.True(WaitFor(() => { Send(port, Frames.Ascii("ELRS"));
                                        return Logged("Discovery beacon from ELRS module 127.0.0.1") == 2; }, 3000));
            Thread.Sleep(300);
            lock (posts) Assert.Equal(before, posts.Count);
            hold.Set();
            Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
            Assert.True(WaitFor(() => { lock (posts) return posts.Count > before; }, 3000), "then the new activation");
            lock (posts)
            {
                Assert.Single(posts, Harness.IsRelease);
                int rel = posts.FindIndex(Harness.IsRelease);
                Assert.Equal(new ActivationPost("127.0.0.1", JoystickEngine.ActionBegin, 4000, 16, false), posts[rel + 1]);
            }
            lock (log)
            {
                Assert.Equal(1, log.Count(l => l == Sent("127.0.0.1")));
                Assert.Equal(1, log.Count(l => l == Done("127.0.0.1")));
            }
        }
        finally
        {
            hold.Set();
            e.StopAndWait(2000);
        }
    }

    private static void SendFrom(string ip, int port, byte[] data)
    {
        using var c = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), 0));
        c.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public void Stop_AStartAndStopAtOnce_ReleaseNothingOfTheLastSession()
    {
        // Over real UDP: 127.0.0.1 locks, 127.0.0.2 streams as a 2nd source, and Stop releases both. Then Start and
        // Stop at once (a double click): that Stop releases only what the new session saw, here nothing, also before
        // the new run's thread has started (the core resets at the stop, after its release). A few rounds.
        const int port = 27422;
        var posts = new List<ActivationPost>();
        var log = new List<string>();
        var e = new JoystickEngine
        {
            Port = port, SkipVJoyForTest = true, OutputOverride = _ => { },
            ActivateOverride = p => { lock (posts) posts.Add(p); return true; },
        };
        e.Log += m => { lock (log) log.Add(m); };
        var f16 = Frames.Channels(Enumerable.Repeat(100, 16).ToArray());
        int Releases() { lock (posts) return posts.Count(Harness.IsRelease); }
        int Stopped() { lock (log) return log.Count(l => l == "Stopped."); }
        try
        {
            for (int round = 0; round < 3; round++)
            {
                e.Start();
                Assert.True(WaitFor(() => { SendFrom("127.0.0.1", port, f16); return e.Source != null; }, 3000), "locked");
                Assert.Equal("127.0.0.1", e.Source);
                Assert.True(WaitFor(() => { SendFrom("127.0.0.1", port, f16); SendFrom("127.0.0.2", port, f16);
                                            return e.DroppedForTest == 1; }, 3000), "a 2nd source");
                int stopped = Stopped(), before = Releases();
                e.Stop();
                Assert.Equal(before + 2, Releases());
                Assert.True(WaitFor(() => Stopped() > stopped, 3000), "the run ended");
                e.Start();
                e.Stop();                                     // at once
                e.StopAndWait(2000);
                Assert.Equal(before + 2, Releases());
            }
            lock (log)
            {
                Assert.Equal(3, log.Count(l => l == Sent("127.0.0.1")));
                Assert.Equal(3, log.Count(l => l == Sent("127.0.0.2")));
            }
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void Stop_ReleaseTook_TheNextStartRestartsNothing()
    {
        // Over real UDP, the user's flow: locked (ELRS by its beacon, the module IP set to it), Stop (the release
        // takes: the module is idle), another rate, Start. The joystick_begin carries the new rate, and the module
        // that locks is not restarted for the old one. Then a restart in flight when Stop comes (cut short: what the
        // module streams is not known), and the release after it takes: the next lock restarts nothing either. Then
        // the same, but the release fails: the module may stream on with its old request, so its next lock restarts
        // it, and says Stop cut its restart short (the core's words).
        const int port = 27419;
        var posts = new List<ActivationPost>();
        var log = new List<string>();
        using var hold = new ManualResetEventSlim(true);
        using var held = new ManualResetEventSlim();
        bool releaseOk = true;
        var e = new JoystickEngine
        {
            Port = port, SkipVJoyForTest = true, OutputOverride = _ => { }, AsyncPostsForTest = true,
            ActivateOverride = p =>
            {
                lock (posts) posts.Add(p);
                if (p.Restart && p.Action == JoystickEngine.ActionEnd && !hold.IsSet)
                {
                    held.Set();
                    hold.Wait(10000);
                }
                return !Harness.IsRelease(p) || Volatile.Read(ref releaseOk);
            },
        };
        e.Log += m => { lock (log) log.Add(m); };
        var f16 = Frames.Channels(Enumerable.Repeat(100, 16).ToArray());
        const string ip = "127.0.0.1";
        int Count(Func<ActivationPost, bool> f) { lock (posts) return posts.Count(f); }
        int Logged(string part) { lock (log) return log.Count(l => l.Contains(part)); }
        bool Restart(ActivationPost p) => p.Restart && p.Action == JoystickEngine.ActionBegin;
        void LockAgain()
        {
            Assert.True(WaitFor(() => { Send(port, f16); return e.Source == ip; }, 3000), "locked again");
            for (int i = 0; i < 10; i++) { Send(port, f16); Thread.Sleep(50); }
            Assert.True(e.WaitForPostsForTest(5000));
        }
        e.Start();
        try
        {
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, Frames.Ascii("ELRS")); return Count(_ => true) > 0; }, 3000));
            Assert.True(WaitFor(() => { Send(port, f16); return e.Source == ip; }, 3000));
            Assert.True(e.WaitForPostsForTest(5000));
            Assert.True(e.SetTarget(ip));                     // the locked module: nothing sent
            e.Stop();
            Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Logged(Done(ip)));
            Assert.True(e.SetRequest(2000, 16));              // stopped: stored
            e.Start();
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            var begin2000 = new ActivationPost(ip, JoystickEngine.ActionBegin, 2000, 16, false);
            Assert.True(WaitFor(() => Count(p => p == begin2000) == 1, 3000), "the module IP's joystick_begin, 500 Hz");
            LockAgain();
            Assert.Equal(0, Count(Restart));
            Assert.Equal(0, Logged("from before the change"));

            // a restart in flight when Stop comes, then the release after it takes
            hold.Reset();
            Assert.True(e.SetRequest(4000, 16));              // locked, ELRS: restarted at once
            Assert.True(held.Wait(5000));
            e.Stop();
            hold.Set();
            Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Logged(Done(ip)));
            Assert.Equal(0, Count(Restart));                  // cut short: its joystick_begin never went out
            e.Start();
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            var begin4000 = new ActivationPost(ip, JoystickEngine.ActionBegin, 4000, 16, false);
            Assert.True(WaitFor(() => Count(p => p == begin4000) == 2, 3000), "the module IP's joystick_begin");
            LockAgain();
            Assert.Equal(0, Count(Restart));
            Assert.Equal(0, Logged("may still stream its old request"));

            // a restart in flight when Stop comes, and the release after it fails
            held.Reset();
            hold.Reset();
            Volatile.Write(ref releaseOk, false);
            Assert.True(e.SetRequest(2000, 16));              // locked, ELRS: restarted at once
            Assert.True(held.Wait(5000));
            e.Stop();
            hold.Set();
            Assert.True(e.WaitForReleases(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Logged($"Releasing {ip} failed: "));
            Assert.Equal(0, Count(Restart));
            e.Start();
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            Assert.True(WaitFor(() => Count(p => p == begin2000) == 2, 3000), "the module IP's joystick_begin");
            LockAgain();
            // the restart is queued on the engine thread after the lock LockAgain waits for: wait for it, then for
            // its POSTs (its log line comes before it is queued)
            Assert.True(WaitFor(() => Count(Restart) == 1, 3000));
            Assert.True(e.WaitForPostsForTest(5000));
            Assert.Equal(1, Count(Restart));
            Assert.Equal(1, Logged($"{ip} may still stream its old request (Stop cut its restart short) - " +
                                   "restarting its stream with 500 Hz, 16 channels"));
            Assert.Equal(0, Logged("its restart failed"));
        }
        finally
        {
            hold.Set();
            e.StopAndWait(2000);
        }
    }
}
