using Xunit;

namespace ELRSWifiJoystick.Tests;

// The rules of the request (R1-R13, see JoystickEngine) where the engine follows the Linux core
// (core/wifi_proto.h): ported from core/tests/test_wifi_proto.c, under the same names where there is one.
public class RulesTests
{
    private const string DefIp = "192.168.1.50";
    private const string XfIp = "192.168.2.138";

    // ---- R2: a held restart survives a lock loss of its module ------------------------------------
    // (Restart_Wait_HeldThroughALockLoss: RequestTests)

    [Fact]
    public void Restart_Wait_ChangeJustPastTheWindow_RestartsOnce()
    {
        // The window has just passed, the held restart is not resolved yet (no frame or tick since): another
        // change restarts the module at once - with the request now, and that is the held one too.
        var h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(2000, 16);                        // held: maybe Crossfire
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1.99); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Empty(h.Ends);                                 // 19.9 s
        h.Clock.Advance(0.2);
        Assert.True(h.Engine.SetRequest(3000, 16));
        Assert.Equal(3000, Assert.Single(h.RestartBegins).IntervalUs);
        h.Engine.Tick();
        h.Same(DefIp, 16, 16000);
        for (int i = 0; i < 30; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Single(h.Ends);
    }

    // ---- R3: what each module was last asked ------------------------------------------------------

    [Fact]
    public void Asked_TargetActivationInFlight_ChangeReachesIt()
    {
        // The module IP set (no beacon from it): the request changes while its activation is in flight. The
        // module starts streaming what that activation asked for: restarted at its lock (after the Crossfire
        // check). The same channel count, so only what was asked tells.
        var h = new Harness();
        h.Engine.SetTarget("10.0.0.1");
        Assert.Equal(new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 4000, 16, false), Assert.Single(h.Posts));
        h.Engine.SetRequest(2000, 16);                        // not locked: stored
        Assert.Single(h.Posts);
        for (int i = 0; i < 3; i++) { h.Same("10.0.0.1", 16, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        Assert.Empty(h.Ends);                                 // 16 channels, no beacon: maybe Crossfire
        Assert.Equal(1, h.CountLog("10.0.0.1 may be Crossfire"));
        for (int i = 0; i < 21; i++) { h.Same("10.0.0.1", 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Equal("10.0.0.1", Assert.Single(h.Ends).Ip);
        Assert.Equal((2000, 16), (h.RestartBegins[0].IntervalUs, h.RestartBegins[0].Channels));
        Assert.Equal(1, h.CountLog("Restarting the stream of 10.0.0.1: 500 Hz, 16 channels"));

        // the module was off: the activations failed, the request changed, then a later one (with the new
        // request) started it: it streams that one - nothing to restart
        h = new Harness { ActivateResult = false };
        h.Engine.SetTarget("10.0.0.1");
        h.Engine.SetRequest(2000, 8);
        h.Clock.Advance(5.1);
        h.ActivateResult = true;
        h.Engine.Tick();                                      // the retry, with 2000 us, 8 channels
        Assert.Equal(new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());
        for (int i = 0; i < 30; i++) { h.Same("10.0.0.1", 8, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Asked_ChangeWhileNotLocked_ReachesAModuleFoundStreaming()
    {
        // A module never asked by a restart or an ELRS-beacon activation (found streaming): a change of the
        // request made while it is not locked reaches it when it locks again - it streams on through a WiFi
        // pause or a stop and keeps the old request otherwise.
        // Found streaming 8 channels (surely no Crossfire); the rate changes during a WiFi pause.
        var h = new Harness();
        h.Engine.SetRequest(4000, 8);
        for (int i = 0; i < 5; i++) { h.Same(DefIp, 8, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        h.Engine.SetRequest(2000, 8);
        Assert.Empty(h.Ends);
        h.Same(DefIp, 8, 16000);
        Assert.Equal(DefIp, Assert.Single(h.Ends).Ip);
        Assert.Equal((2000, 8), (h.RestartBegins[0].IntervalUs, h.RestartBegins[0].Channels));
        Assert.Equal(1, h.CountLog("192.168.1.50 still streams 250 Hz, 8 channels from before the change - restarting " +
                                   "its stream with 500 Hz, 8 channels"));
        for (int i = 0; i < 10; i++) { h.Same(DefIp, 8, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        Assert.Single(h.Ends);                                // once per change

        // 16 channels, no beacon (maybe Crossfire): the restart waits its 20 s of lock, then goes out
        h = new Harness();
        for (int i = 0; i < 5; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        h.Engine.SetRequest(2000, 16);
        for (int i = 0; i < 19; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("may be Crossfire"));
        for (int i = 0; i < 3; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Single(h.Ends);
        Assert.Equal(2000, h.RestartBegins[0].IntervalUs);

        // ... and its beacon says Crossfire meanwhile: never restarted
        h = new Harness();
        for (int i = 0; i < 5; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        h.Engine.SetRequest(2000, 16);
        h.Same(DefIp, 16, 16000);
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        for (int i = 0; i < 30; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Empty(h.Ends);

        // a restart held for the Crossfire check, then a stop and start (or a port change) before its 20 s: it
        // still goes out once the module, streaming on, has been locked 20 s again
        h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(1);
        h.Engine.ResetSessionState();
        for (int i = 0; i < 19; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Empty(h.Ends);
        for (int i = 0; i < 3; i++) { h.Same(DefIp, 16, 16000); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Single(h.Ends);
        Assert.Equal(2000, h.RestartBegins[0].IntervalUs);

        // nothing changed while it was not locked: a pause or a stop restarts nothing
        h = new Harness();
        h.Engine.SetRequest(4000, 8);
        for (int i = 0; i < 5; i++) { h.Same(DefIp, 8, 16000); h.Engine.Tick(); h.Clock.Advance(0.1); }
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        h.Same(DefIp, 8, 16000);
        h.Engine.ResetSessionState();
        h.Same(DefIp, 8, 16000);
        Assert.Empty(h.Ends);
    }

    [Fact]
    public void Asked_CrossfireLocksWithAnOlderRequest_NotedOnly()
    {
        // R3 under R2's kind rules: a Crossfire module that locks with an older asked request is noted (once per
        // session), never sent anything.
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);          // activated with 250 Hz, 16 channels
        h.Same(XfIp, 16, 100);
        h.Clock.Advance(3.5);
        h.Engine.Tick();                                      // a WiFi pause
        Assert.Null(h.Engine.Source);
        Assert.True(h.Engine.SetRequest(2000, 8));
        Assert.Equal(0, h.CountLog("ignores the rate"));      // not locked: nothing to say yet
        int posts = h.Posts.Count;
        for (int i = 0; i < 30; i++) { h.Same(XfIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Equal(posts, h.Posts.Count);
        Assert.Equal(1, h.CountLog("Crossfire module 192.168.2.138 ignores the rate and channel count: it always sends 16 " +
                                   "channels at about 90 Hz"));
        // a new session: said once more
        h.Engine.ResetSessionState();
        for (int i = 0; i < 5; i++) { h.Same(XfIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Equal(2, h.CountLog("ignores the rate and channel count"));
        Assert.Equal(posts, h.Posts.Count);
    }

    // ---- R4: the explicit restart -------------------------------------------------------------------

    [Fact]
    public void Restart_Explicit()
    {
        var h = new Harness();
        Assert.False(h.Engine.RestartStream());               // nothing to restart
        Assert.Equal(1, h.CountLog("No module to restart"));
        Assert.Empty(h.Posts);
        // the module IP, while nothing streams, no beacon from it: it may be Crossfire, which is never sent
        // joystick_end: only a joystick_begin, past the throttle (a module that does not stream takes it)
        h.Engine.SetTarget("10.0.0.1");
        Assert.Single(h.Activated);
        h.Engine.SetRequest(2000, 8);
        Assert.True(h.Engine.RestartStream());
        Assert.Empty(h.Ends);
        Assert.Equal(2, h.Activated.Count);
        Assert.Equal(new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());
        Assert.Equal(1, h.CountLog("10.0.0.1 has sent no beacon yet (it may be Crossfire, which is never sent " +
                                   "joystick_end): activating it with 500 Hz, 8 channels"));
        // its beacon then says Crossfire: still only a joystick_begin past the throttle, never joystick_end
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.1");
        Assert.Equal(2, h.Activated.Count);                   // (the beacon itself: throttled)
        Assert.True(h.Engine.RestartStream());
        Assert.Empty(h.Ends);
        Assert.Equal(3, h.Activated.Count);
        Assert.Equal(new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 2000, 8, false), h.Posts.Last());
        Assert.Equal(1, h.CountLog("Crossfire module 10.0.0.1 is not streaming to us: activating it (it always sends 16 " +
                                   "channels at about 90 Hz)"));
        // locked to it: nothing is sent (logged each time it is asked)
        h.Same("10.0.0.1", 16, 100);
        int posts = h.Posts.Count;
        Assert.False(h.Engine.RestartStream());
        Assert.False(h.Engine.RestartStream());
        Assert.Equal(posts, h.Posts.Count);
        Assert.Equal(2, h.CountLog("Crossfire module 10.0.0.1 always sends 16 channels at its own rate (about 90 Hz) - " +
                                   "nothing to restart"));
        Assert.Equal(0, h.Engine.RestartCount);

        // the module IP known as ELRS by its beacon: a real restart (joystick_end first); locked, the same
        h = new Harness();
        h.Engine.SetTarget("10.0.0.1");
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.1");
        Assert.True(h.Engine.RestartStream());
        Assert.Equal("10.0.0.1", Assert.Single(h.Ends).Ip);
        h.Same("10.0.0.1", 16, 100);
        Assert.True(h.Engine.RestartStream());
        Assert.Equal(2, h.Ends.Count);
    }

    // ---- R6: a restart counts only with both POSTs 2xx; a failed one is sent again -----------------

    [Fact]
    public void Restart_Failed_SentAgainWhileItStreams()
    {
        var h = new Harness { RestartResult = false };       // the joystick_begin of a restart gets no 2xx
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        Assert.True(h.Engine.SetRequest(2000, 8));
        Assert.Single(h.Ends);
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 5 s"));
        h.RestartResult = true;
        for (int i = 0; i < 49; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Single(h.Ends);                                // not before the throttle; old frames restart nothing
        h.Clock.Advance(0.1);
        h.Same(DefIp, 16, 100);
        h.Engine.Tick();                                      // 5 s after the first send
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 8, true), h.RestartBegins[1]);
        Assert.Equal(2, h.Engine.RestartCount);
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Same(DefIp, 8, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);                        // this one took
        Assert.Equal(1, h.CountLog("failed"));
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Single(h.Activated);                           // no plain joystick_begin to the streaming module

        // a failed joystick_end fails the restart too: the module that missed it only moved its stream
        h = new Harness { EndResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(new[] { JoystickEngine.ActionEnd, JoystickEngine.ActionBegin },
                     h.Posts.Skip(1).Select(p => p.Action));  // the begin still goes out
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 5 s"));
        h.EndResult = true;
        for (int i = 0; i < 50; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);

        // a plain joystick_begin that fails is no restart: nothing is sent again for it as one
        h = new Harness { ActivateResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Empty(h.Ends);
        Assert.Equal(0, h.CountLog("The restart of"));
    }

    [Fact]
    public void Restart_Failed_GivesUpAfterTries_ANewRestartStartsOver()
    {
        var h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        DateTime t0 = h.Clock.Now;
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Single(h.Ends);
        var sent = new List<double>();
        int n0 = h.Ends.Count;
        for (int i = 0; i < 400; i++)
        {
            h.Clock.Advance(0.1);
            h.Same(DefIp, 16, 100);
            h.Engine.Tick();
            if (h.Ends.Count != n0) { sent.Add((h.Clock.Now - t0).TotalSeconds); n0 = h.Ends.Count; }
        }
        Assert.Equal(JoystickEngine.RestartTries, h.Ends.Count);
        Assert.Equal(new[] { 5.0, 10.0 }, sent.Select(s => Math.Round(s, 3)));     // every 5 s
        Assert.All(h.RestartBegins, p => Assert.Equal(2000, p.IntervalUs));
        Assert.Equal(JoystickEngine.RestartTries - 1, h.CountLog("sending it again in 5 s"));
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed 3 times - giving up: it may still stream its old " +
                                   "rate and channel count"));
        Assert.Equal(DefIp, h.Engine.Source);
        // a new request is a new restart, with its own tries
        h.RestartResult = true;
        Assert.True(h.Engine.SetRequest(3000, 16));
        Assert.Equal(JoystickEngine.RestartTries + 1, h.Ends.Count);
        Assert.Equal(3000, h.RestartBegins[JoystickEngine.RestartTries].IntervalUs);
        // so is an explicit one
        h.RestartResult = false;
        Assert.True(h.Engine.RestartStream());
        for (int i = 0; i < 40; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2 * JoystickEngine.RestartTries + 1, h.Ends.Count);
        Assert.Equal(2, h.CountLog("giving up"));
        Assert.Single(h.Activated);
    }

    [Fact]
    public void Restart_Failed_OnlyWhileItsModuleStaysLocked()
    {
        // Failed while it streams, then the lock is lost before the second send: not sent again while it does not
        // stream to us. Back without a beacon in between, it streams on with its old request: restarted at its
        // lock, as a new restart (R3; Restart_Failed_NotSentAgain_RestartedAtTheNextLock).
        var h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        h.RestartResult = true;
        h.Clock.Advance(3.1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.Single(h.Ends);
        for (int i = 0; i < 20; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(1, h.CountLog("The restart of"));

        // taken for ELRS (16 channels, 20 s of lock without a beacon), its restart failed, then a Crossfire beacon
        // came from it after all: never a second joystick_end
        h = new Harness { RestartResult = false };
        for (int i = 0; i < 21; i++) { h.Same(DefIp, 16, 100); h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Single(h.Ends);
        Assert.Equal(1, h.CountLog("sending it again"));
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Single(h.Ends);

        // a new session (stop and start, a port change) drops it (R9); the module streaming on is restarted at its
        // first lock of the new session, as a new restart
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        h.Engine.ResetSessionState();
        h.RestartResult = true;
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.Single(h.Ends);                                // not sent again: the retry went with the session
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(2000, h.RestartBegins[1].IntervalUs);
        Assert.Equal(1, h.CountLog("The restart of"));

        // the module IP set to it (it streams to us already, R7): the lock and the retry stay
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.True(h.Engine.SetTarget("10.0.0.2"));
        Assert.Equal("10.0.0.2", h.Engine.Source);
        h.RestartResult = true;
        for (int i = 0; i < 6; i++) { h.Clock.Advance(1); h.Same("10.0.0.2", 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);

        // another module IP (R7): the retry for the module being left is dropped
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));
        Assert.Equal(new ActivationPost("10.0.0.3", JoystickEngine.ActionBegin, 2000, 16, false), h.Posts.Last());
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.Single(h.Ends);
    }

    [Fact]
    public void Restart_Failed_ResultFromThePostThread()
    {
        // The real POSTs run off the engine thread, and the result comes later: 1 s after the restart went out,
        // while the module still streams (the old request) - sent again 5 s after the first send.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        var posts = new List<ActivationPost>();
        using var sent = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            if (p.Action == JoystickEngine.ActionEnd || !p.Restart) return true;
            sent.Set();
            answer.Wait(5000);
            return false;                                     // the joystick_begin of the restart: no 2xx
        };
        h.Engine.SetRequest(2000, 16);
        Assert.True(sent.Wait(5000));
        h.Clock.Advance(1);
        h.Same(DefIp, 16, 100);
        answer.Set();
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 4 s"));
        for (int i = 0; i < 39; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        lock (posts) Assert.Single(posts, p => p.Action == JoystickEngine.ActionEnd);
        h.Clock.Advance(0.1);
        h.Same(DefIp, 16, 100);
        h.Engine.Tick();                                      // 5 s after the first send
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts) Assert.Equal(2, posts.Count(p => p.Action == JoystickEngine.ActionEnd));
        Assert.Equal(2, h.Engine.RestartCount);
    }

    [Fact]
    public void Restart_Failed_ResultAfterTheLockLoss_RestartedAtTheNextLock()
    {
        // The POSTs run off the engine thread: the result of a restart comes after the lock was lost (the module
        // went away while the POST timed out). Nothing is sent again while it is away; back streaming without a
        // beacon in between, it may stream anything (the joystick_end took, the joystick_begin did not):
        // restarted at its lock, once.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        var posts = new List<ActivationPost>();
        using var answer = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            if (p.Action == JoystickEngine.ActionBegin && p.Restart) answer.Wait(5000);
            return p.Action == JoystickEngine.ActionEnd;      // the joystick_end took, the joystick_begin did not
        };
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(3.1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        answer.Set();
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        Assert.Equal(0, h.CountLog("The restart of"));
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts) Assert.Single(posts, p => p.Action == JoystickEngine.ActionEnd);
        h.Engine.ActivateOverride = p => { lock (posts) posts.Add(p); return true; };
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts)
        {
            Assert.Equal(2, posts.Count(p => p.Action == JoystickEngine.ActionEnd));
            Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, true), posts.Last());
        }
        Assert.Equal(1, h.CountLog("192.168.1.50 may still stream its old request (its restart failed) - " +
                                   "restarting its stream with 500 Hz, 16 channels"));
    }

    [Fact]
    public void Restart_Failed_NotSentAgain_RestartedAtTheNextLock()
    {
        // R6 + R3: a restart that did not take leaves the module streaming its old request. Once it is not sent
        // again (the lock lost, given up), the module is restarted at its next lock - not before, never per frame.

        // The parity scenario: the joystick_end fails (the begin only moved the stream), a WiFi pause over 3 s,
        // back with 16 channels while 8 are asked: restarted at once (ELRS), once.
        var h = new Harness { EndResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        for (int i = 0; i < 10; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(0.1); }
        Assert.True(h.Engine.SetRequest(2000, 8));
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 5 s"));
        h.EndResult = true;
        for (int i = 0; i < 10; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(0.1); }
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        h.Clock.Advance(0.5);
        h.Engine.Tick();
        Assert.Single(h.Ends);
        h.Same(DefIp, 16, 100);                               // back, with the old request
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 8, true), h.RestartBegins[1]);
        Assert.Equal(1, h.CountLog("192.168.1.50 may still stream its old request (its restart failed) - " +
                                   "restarting its stream with 500 Hz, 8 channels"));
        for (int i = 0; i < 30; i++) { h.Same(DefIp, 8, 100); h.Engine.Tick(); h.Clock.Advance(0.1); }
        Assert.Equal(2, h.Ends.Count);                        // it took

        // Given up (3 sends): left alone while that lock lasts, whatever streams; the next lock restarts it once,
        // with tries of its own.
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 8);
        for (int i = 0; i < 300; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(JoystickEngine.RestartTries, h.Ends.Count);
        Assert.Equal(1, h.CountLog("giving up"));
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        h.RestartResult = true;
        h.Same(DefIp, 16, 100);
        Assert.Equal(JoystickEngine.RestartTries + 1, h.Ends.Count);
        for (int i = 0; i < 100; i++) { h.Clock.Advance(0.1); h.Same(DefIp, 8, 100); h.Engine.Tick(); }
        Assert.Equal(JoystickEngine.RestartTries + 1, h.Ends.Count);
        Assert.Single(h.Activated);

        // Its ELRS beacon meanwhile (it stopped streaming): that joystick_begin carries the request - nothing to
        // restart at its lock.
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        h.Clock.Advance(2);
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, false), h.Posts.Last());
        for (int i = 0; i < 30; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Single(h.Ends);

        // A module of unknown kind (16 channels, no beacon), taken for ELRS after its 20 s: back, it may be
        // Crossfire again - held for 20 s of the new lock without a Crossfire beacon, then restarted.
        h = new Harness { RestartResult = false };
        for (int i = 0; i < 21; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Single(h.Ends);
        h.RestartResult = true;
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        for (int i = 0; i < 19; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Single(h.Ends);
        Assert.Equal(1, h.CountLog("192.168.1.50 may be Crossfire"));
        for (int i = 0; i < 3; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(2000, h.RestartBegins[1].IntervalUs);

        // ... and its Crossfire beacon in that window: never a second joystick_end
        h = new Harness { RestartResult = false };
        for (int i = 0; i < 21; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(3.5);
        h.Engine.Tick();
        h.Same(DefIp, 16, 100);
        h.Packet(Frames.Ascii("VELOCIDRONE"), DefIp);
        for (int i = 0; i < 30; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.Single(h.Ends);

        // Another module IP (R7) drops the retry for the module being left; when the module IP is set to it again
        // and it streams its old request, it is restarted at its lock.
        h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), "10.0.0.2");
        h.Same("10.0.0.2", 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.True(h.Engine.SetTarget("10.0.0.3"));
        h.RestartResult = true;
        for (int i = 0; i < 10; i++) { h.Clock.Advance(1); h.Engine.Tick(); }
        Assert.Single(h.Ends);
        Assert.True(h.Engine.SetTarget("10.0.0.2"));
        Assert.Equal(new ActivationPost("10.0.0.2", JoystickEngine.ActionBegin, 2000, 16, false), h.Posts.Last());
        Assert.Single(h.Ends);                                // a joystick_begin only: it is not locked
        h.Same("10.0.0.2", 16, 100);
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal("10.0.0.2", h.Ends[1].Ip);
    }

    [Fact]
    public void Restart_Failed_ResultAfterAnElrsBeaconActivation_DecidesNothing()
    {
        // The restart's joystick_begin is still in flight when the lock is lost and the module's ELRS beacon (it
        // stopped streaming) gets it a joystick_begin carrying the request, queued after the restart: the
        // restart's failure, coming then, makes nothing unknown - nothing to restart at the next lock.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        var posts = new List<ActivationPost>();
        using var sent = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            if (p.Action == JoystickEngine.ActionBegin && p.Restart)
            {
                sent.Set();
                answer.Wait(5000);
                return false;
            }
            return true;
        };
        h.Engine.SetRequest(2000, 16);
        Assert.True(sent.Wait(5000));
        h.Clock.Advance(3.1);
        h.Engine.Tick();
        Assert.Null(h.Engine.Source);
        h.Clock.Advance(2);                                   // past the 5 s throttle of the restart
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        answer.Set();
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts) Assert.Equal(new ActivationPost(DefIp, JoystickEngine.ActionBegin, 2000, 16, false), posts.Last());
        Assert.Equal(0, h.CountLog("The restart of"));
        for (int i = 0; i < 30; i++) { h.Same(DefIp, 16, 100); h.Engine.Tick(); h.Clock.Advance(1); }
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts) Assert.Single(posts, p => p.Action == JoystickEngine.ActionEnd);
    }

    [Fact]
    public void Restart_Failed_ANewerRestartGoesFirst()
    {
        // A newer restart while a failed one waits to be sent again goes out at once (with its own tries).
        var h = new Harness { RestartResult = false };
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.SetRequest(2000, 16);
        Assert.Equal(1, h.CountLog("The restart of 192.168.1.50 failed - sending it again in 5 s"));
        h.Clock.Advance(1);
        h.RestartResult = true;
        Assert.True(h.Engine.SetRequest(3000, 16));
        Assert.Equal(2, h.Ends.Count);
        Assert.Equal(3000, h.RestartBegins[1].IntervalUs);
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.Equal(2, h.Ends.Count);

        // The first restart in flight, a newer one queued behind it, then the first one fails: its result is
        // not the last restart's, so nothing is planned for it - the newer one's result decides.
        h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        var posts = new List<ActivationPost>();
        using var answer = new ManualResetEventSlim();
        h.Engine.AsyncPostsForTest = true;
        h.Engine.ActivateOverride = p =>
        {
            lock (posts) posts.Add(p);
            if (p.Action == JoystickEngine.ActionBegin && p.IntervalUs == 2000)
            {
                answer.Wait(5000);
                return false;
            }
            return true;
        };
        h.Engine.SetRequest(2000, 16);
        h.Engine.SetRequest(3000, 16);
        answer.Set();
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        Assert.Equal(0, h.CountLog("The restart of"));
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 100); h.Engine.Tick(); }
        Assert.True(h.Engine.WaitForPostsForTest(5000));
        lock (posts)
        {
            Assert.Equal(2, posts.Count(p => p.Action == JoystickEngine.ActionEnd));
            Assert.Equal(3000, posts.Last().IntervalUs);
        }
    }

    // ---- R7: the module IP ----------------------------------------------------------------------------

    [Fact]
    public void SetTarget_TheModuleStreamingNow_KeepsTheLock()
    {
        // Unlocking it would center the joystick for a moment (a held switch would be released and pressed
        // again) and drop a held restart for nothing, and a joystick_begin would not change its stream.
        var h = new Harness();
        var dev = new FakeOutput();
        h.Engine.DeviceForTest = dev;
        h.Same("10.0.0.1", 16, 32767);
        Assert.Equal("10.0.0.1", h.Engine.Source);
        int states = h.States.Count;
        dev.Clear();
        Assert.True(h.Engine.SetTarget("10.0.0.1"));
        Assert.Equal("10.0.0.1", h.Engine.ActivationIP);
        Assert.Equal("10.0.0.1", h.Engine.Source);
        Assert.Equal(states, h.States.Count);                 // still Streaming: no "connecting to"
        Assert.Empty(h.Posts);
        Assert.Equal(1, h.CountLog("Target module set to 10.0.0.1 - streaming from it already"));
        h.Engine.Tick();
        h.Same("10.0.0.1", 16, 32767);
        Assert.Equal("10.0.0.1", h.Engine.Source);
        Assert.Empty(dev.Axes);                               // never centered
        Assert.Empty(dev.Btns);
        Assert.Equal(0xFF, h.Engine.ButtonMask);
        Assert.Equal(1, h.StreamingCount);                    // never relocked
        Assert.Equal(1, h.CountLog("Joystick source locked"));

        // a held restart (may be Crossfire) stays held, and goes out after its 20 s
        h = new Harness();
        h.Same(DefIp, 16, 16000);
        h.Engine.SetRequest(2000, 16);
        h.Clock.Advance(1);
        h.Same(DefIp, 16, 16000);
        h.Engine.SetTarget(DefIp);
        Assert.Equal(DefIp, h.Engine.Source);
        for (int i = 0; i < 20; i++) { h.Clock.Advance(1); h.Same(DefIp, 16, 16000); h.Engine.Tick(); }
        Assert.Equal(DefIp, Assert.Single(h.Ends).Ip);
        Assert.Equal(2000, h.RestartBegins[0].IntervalUs);

        // the lock is stale (no data for more than 3 s, not released yet): let go, POST at once
        h = new Harness();
        h.Same("10.0.0.9", 16, 100);
        h.Clock.Advance(3.1);
        h.Engine.SetTarget("10.0.0.9");
        Assert.Null(h.Engine.Source);
        Assert.Equal(new[] { "10.0.0.9" }, h.Activated);
        Assert.Equal((EngineState.Searching, "connecting to 10.0.0.9"), h.States.Last());
        Assert.Equal(1, h.CountLog("Target module set to 10.0.0.9 - connecting..."));
        h.Same("10.0.0.9", 16, 100);
        Assert.Equal("10.0.0.9", h.Engine.Source);
        Assert.Equal(2, h.StreamingCount);
    }

    // ---- R9 / R10: a new session keeps what is known about modules -------------------------------------

    [Fact]
    public void Reset_KeepsTheKind_RestartAtOnce()
    {
        // An ELRS module identified by its beacon and still streaming (it sends no beacon while it streams) is
        // restarted at once after a stop and start, not after the Crossfire wait.
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), DefIp);
        h.Same(DefIp, 16, 100);
        h.Engine.ResetSessionState();
        h.Same(DefIp, 16, 100);
        Assert.Equal(DefIp, h.Engine.Source);
        Assert.Equal(ModuleKind.Elrs, h.Engine.SourceKind);
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.Single(h.Ends);
        Assert.Equal(0, h.CountLog("may be Crossfire"));
    }

    [Fact]
    public void Reset_KeepsTheKindOfTheLockedCrossfire_HoweverManyModulesBeacon()
    {
        // A Crossfire module that streams on through a stop and start: never sent joystick_end in the new
        // session, and other modules beaconing can't evict its kind while it is locked (R10).
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), XfIp);
        h.Same(XfIp, 16, 100);
        h.Engine.ResetSessionState();
        h.Same(XfIp, 16, 100);                                // locked again, no beacon yet this session
        for (int i = 0; i < 3 * JoystickEngine.TableSize; i++) h.Packet(Frames.Ascii("ELRS"), $"10.0.1.{i + 1}");
        h.Same(XfIp, 16, 100);
        Assert.Equal(ModuleKind.Crossfire, h.Engine.KindOf(XfIp));
        Assert.Equal(ModuleKind.Crossfire, h.Engine.SourceKind);
        Assert.Equal(ModuleKind.Unknown, h.Engine.KindOf("10.0.1.1"));      // the table holds 8
        Assert.True(h.Engine.SetRequest(2000, 16));
        Assert.False(h.Engine.RestartStream());
        for (int i = 0; i < 30; i++) { h.Clock.Advance(1); h.Same(XfIp, 16, 100); h.Engine.Tick(); }
        Assert.Empty(h.Ends);
        Assert.Equal(1, h.CountLog("Crossfire module 192.168.2.138 ignores the rate and channel count"));
    }

    // ---- the "no stick data" state ----------------------------------------------------------------------

    [Fact]
    public void NoStickData_StateEnds_WhenThePlaceholdersStop()
    {
        // Placeholder frames say "sending no stick data"; when they stop (the module powered off) that goes, and
        // comes back with them.
        var h = new Harness();
        for (int i = 0; i < 10; i++) { h.Same(XfIp, 16, JoystickEngine.XF_PLACEHOLDER); h.Clock.Advance(0.011); h.Engine.Tick(); }
        Assert.Equal((EngineState.Searching, "module found, but it is sending no stick data"), h.States.Last());
        Assert.Equal(1, h.CountLog("is streaming placeholder frames"));
        h.Clock.Advance(2.9);
        h.Engine.Tick();
        Assert.Equal((EngineState.Searching, "module found, but it is sending no stick data"), h.States.Last());
        h.Clock.Advance(0.2);
        h.Engine.Tick();                                      // 3 s without one
        Assert.Equal((EngineState.Searching, "waiting for module"), h.States.Last());
        int n = h.States.Count;
        h.Engine.Tick();
        Assert.Equal(n, h.States.Count);                      // said once
        h.Same(XfIp, 16, JoystickEngine.XF_PLACEHOLDER);      // back: said again (the state only, not the log line)
        Assert.Equal((EngineState.Searching, "module found, but it is sending no stick data"), h.States.Last());
        Assert.Equal(1, h.CountLog("is streaming placeholder frames"));
        // real data locks as before
        h.Packet(Frames.Channels(1, 2, 3, 4), XfIp);
        Assert.Equal(XfIp, h.Engine.Source);
        Assert.Equal((EngineState.Streaming, XfIp), h.States.Last());
    }
}
