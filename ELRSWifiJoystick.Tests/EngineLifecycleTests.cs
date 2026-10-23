using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ELRSWifiJoystick.Tests;

// Integration tests over real UDP sockets (loopback). Each test uses its own port so
// they can run in parallel and never collide with a live module on 11000.
public class EngineLifecycleTests
{
    private static JoystickEngine NewEngine(int port, List<int[]> applied, List<string> activated,
        ManualResetEventSlim? streaming = null, ManualResetEventSlim? error = null,
        ManualResetEventSlim? stopped = null)
    {
        var e = new JoystickEngine
        {
            Port = port,
            SkipVJoyForTest = true,
            OutputOverride = ch => { lock (applied) applied.Add((int[])ch.Clone()); },
            ActivateOverride = p =>
            {
                if (p.Action == JoystickEngine.ActionBegin && !p.Restart) lock (activated) activated.Add(p.Ip);
                return true;
            },
        };
        e.StateChanged += (s, d) =>
        {
            if (s == EngineState.Streaming) streaming?.Set();
            if (s == EngineState.Error) error?.Set();
            if (s == EngineState.Stopped) stopped?.Set();
        };
        return e;
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
    public void FullFlow_BeaconThenChannels_OverRealUdp()
    {
        const int port = 27401;
        var applied = new List<int[]>();
        var activated = new List<string>();
        using var streaming = new ManualResetEventSlim();
        var e = NewEngine(port, applied, activated, streaming);

        e.Start();
        try
        {
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            Thread.Sleep(250); // let the socket bind

            Send(port, Frames.Ascii("VELOCIDRONE"));
            Assert.True(WaitFor(() => { lock (activated) return activated.Count > 0; }, 3000),
                "beacon should trigger activation");

            Send(port, Frames.Channels(100, 200, 300, 400));
            Assert.True(streaming.Wait(3000), "channel data should reach Streaming state");
            Assert.True(WaitFor(() => { lock (applied) return applied.Count > 0; }, 2000));
            lock (applied) Assert.Equal(new[] { 100, 200, 300, 400 }, applied[0]);
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void Restart_RegainsStreaming_WhileModuleKeepsBroadcasting()
    {
        // The real-module regression: the module never stops sending, and a Stop/Start
        // of the engine must still reach Streaming again.
        const int port = 27402;
        var applied = new List<int[]>();
        var activated = new List<string>();
        using var streaming = new ManualResetEventSlim();
        var e = NewEngine(port, applied, activated, streaming);

        e.Start();
        try
        {
            Thread.Sleep(250);
            Send(port, Frames.Channels(1, 2, 3, 4));
            Assert.True(WaitFor(() => streaming.IsSet || SendAndCheck(), 3000), "first run should stream");

            bool SendAndCheck() { Send(port, Frames.Channels(1, 2, 3, 4)); return streaming.IsSet; }

            e.StopAndWait(2000);
            streaming.Reset();

            e.Start();
            Assert.True(WaitFor(() => e.IsRunning, 2000));
            Thread.Sleep(250);

            // module is still broadcasting
            Assert.True(WaitFor(() => { Send(port, Frames.Channels(5, 6, 7, 8)); return streaming.IsSet; }, 4000),
                "after restart the engine must reach Streaming again");
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void PortAlreadyInUse_ReportsErrorState()
    {
        const int port = 27403;
        using var blocker = new UdpClient(port); // occupy the port first

        using var error = new ManualResetEventSlim();
        var e = NewEngine(port, new List<int[]>(), new List<string>(), error: error);

        e.Start();
        try
        {
            Assert.True(error.Wait(3000), "binding an occupied port must surface an Error state");
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void AfterAnErrorExit_NothingIsPosted_UntilTheNextStart()
    {
        const int port = 27407;
        var activated = new List<string>();
        using var error = new ManualResetEventSlim();
        var e = NewEngine(port, new List<int[]>(), activated, error: error);
        var log = new List<string>();
        e.Log += m => { lock (log) log.Add(m); };
        using (var blocker = new UdpClient(port))
        {
            e.Start();
            Assert.True(error.Wait(3000));
            Assert.True(WaitFor(() => !e.IsRunning, 3000));
            e.SetTarget("10.0.0.1");
            Assert.False(e.RestartStream());
            lock (log) Assert.Contains("Not running - nothing to restart", log);
        }
        lock (activated) Assert.Empty(activated);
        // the port is free again: the next start works and activates the target
        e.Start();
        try
        {
            Assert.True(WaitFor(() => { lock (activated) return activated.Contains("10.0.0.1"); }, 3000));
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void Stop_ReleasesThePort()
    {
        const int port = 27404;
        var e = NewEngine(port, new List<int[]>(), new List<string>());
        e.Start();
        Thread.Sleep(400); // bound
        e.StopAndWait(2000);

        // If the engine cleaned up, we can bind the port ourselves now.
        var ex = Record.Exception(() => { using var c = new UdpClient(port); });
        Assert.Null(ex);
    }

    [Fact]
    public void StartTwice_IsIdempotent()
    {
        const int port = 27405;
        var e = NewEngine(port, new List<int[]>(), new List<string>());
        e.Start();
        e.Start(); // second call must be a harmless no-op
        Thread.Sleep(300);
        Assert.True(e.IsRunning);
        e.StopAndWait(2000);
        Assert.False(e.IsRunning);
    }

    private static void SendFrom(string ip, int port, byte[] data)
    {
        using var c = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), 0));
        c.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public void IgnoredTraffic_DoesNotStarveTheTick()
    {
        // Module IP 127.0.0.2, locked; another module (127.0.0.3) keeps streaming to this PC, so a receive
        // never times out. When the target goes quiet its lock must still go (3 s), and the joystick center.
        const int port = 27408;
        var dev = new FakeOutput();
        var e = NewEngine(port, new List<int[]>(), new List<string>());
        e.ActivationIP = "127.0.0.2";
        e.DeviceForTest = dev;
        var log = new List<string>();
        e.Log += m => { lock (log) log.Add(m); };
        e.Start();
        using var stopFlood = new ManualResetEventSlim();
        var flood = new Thread(() =>
        {
            using var c = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
            var f = Frames.Channels(Enumerable.Repeat(100, 16).ToArray());
            while (!stopFlood.Wait(5)) c.Send(f, f.Length, new IPEndPoint(IPAddress.Loopback, port));
        }) { IsBackground = true };
        try
        {
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { SendFrom("127.0.0.2", port, Frames.Channels(Enumerable.Repeat(32767, 16).ToArray()));
                                        return e.Source == "127.0.0.2"; }, 3000));
            Assert.True(WaitFor(() => e.ButtonMask == 0xFF, 2000));
            flood.Start();
            Assert.True(WaitFor(() => { lock (log) return log.Contains("Source 127.0.0.2 went quiet - releasing lock"); }, 5000),
                        "the lock must be released while other traffic arrives");
            Assert.Null(e.Source);
            Assert.True(WaitFor(() => e.ButtonMask == 0, 1000));
        }
        finally
        {
            stopFlood.Set();
            if (flood.IsAlive) flood.Join(2000);
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void StopRightAfterStart_IsAStop_NotAPortError()
    {
        const int port = 27412;
        for (int i = 0; i < 10; i++)
        {
            using var stopped = new ManualResetEventSlim();
            using var error = new ManualResetEventSlim();
            var e = NewEngine(port, new List<int[]>(), new List<string>(), error: error, stopped: stopped);
            var log = new List<string>();
            e.Log += m => { lock (log) log.Add(m); };
            e.Start();
            e.Stop();
            e.StopAndWait(2000);
            Assert.True(stopped.Wait(3000), "a stop reports Stopped");
            Assert.False(error.IsSet, "nothing was wrong with the port");
            lock (log) Assert.DoesNotContain(log, l => l.Contains("cannot listen"));
        }
    }

    [Fact]
    public void TheJoystick_IsCenteredAtStart_AndWhenStopped()
    {
        // vJoy's ResetVJD puts RX, RY, RZ and the sliders at their minimum (throttle down, yaw full left), and
        // RelinquishVJD keeps what the device shows: the app centers all 8 axes and releases the buttons itself.
        const int port = 27413;
        var dev = new FakeOutput();
        var e = NewEngine(port, new List<int[]>(), new List<string>());
        e.DeviceForTest = dev;
        e.Start();
        try
        {
            // the axes are written first, then the buttons: wait for both before reading them
            Assert.True(WaitFor(() => dev.Axes.Count == 8 && dev.Btns.Count == 8, 2000));
            Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, JoystickMap.Center)), dev.Axes);
            Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, false)), dev.Btns);
            Thread.Sleep(150);
            Assert.True(WaitFor(() => { Send(port, Frames.Channels(Enumerable.Repeat(32767, 16).ToArray()));
                                        return e.ButtonMask == 0xFF; }, 3000));
        }
        finally
        {
            e.StopAndWait(2000);
        }
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, JoystickMap.Center)), dev.Axes.TakeLast(8));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, false)), dev.Btns.TakeLast(8));
        Assert.Equal(0, e.ButtonMask);
    }

    [Fact]
    public void RequestChangedWhileStopped_ReachesTheModuleThatKeptStreaming()
    {
        // The module misses the joystick_end of Stop's release (R14), so it keeps streaming (and an ELRS module that
        // streams sends no beacon): the next session's lock restarts it with the request picked meanwhile.
        const int port = 27414;
        var posts = new List<ActivationPost>();
        var e = new JoystickEngine
        {
            Port = port, SkipVJoyForTest = true, OutputOverride = _ => { },
            ActivateOverride = p => { lock (posts) posts.Add(p); return !Harness.IsRelease(p); },
        };
        e.SetRequest(4000, 8);
        var f8 = Frames.Channels(Enumerable.Repeat(100, 8).ToArray());
        e.Start();
        try
        {
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, Frames.Ascii("ELRS")); lock (posts) return posts.Count > 0; }, 3000));
            Assert.True(WaitFor(() => { Send(port, f8); return e.Source != null; }, 3000));
            e.StopAndWait(2000);
            Assert.True(e.SetRequest(2000, 8));                 // nothing is sent while stopped
            lock (posts) Assert.Equal(new[] { Harness.Release("127.0.0.1") }, posts.Where(p => p.Action == JoystickEngine.ActionEnd));
            e.Start();
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, f8); lock (posts) return posts.Any(p => p.Action == JoystickEngine.ActionEnd && p.Restart); }, 3000),
                        "the module that kept streaming the old request is restarted");
            lock (posts) Assert.Equal(new ActivationPost("127.0.0.1", JoystickEngine.ActionBegin, 2000, 8, true), posts[^1]);
        }
        finally
        {
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void RestartCutShortByStop_IsDoneWhenTheModuleLocksAgain()
    {
        // A slow module: the restart for a request change waits behind its activation when Stop drops it. The
        // module never got it, misses the release too (R14: the exit's wait runs out before its turn, so it is not
        // sent), and keeps streaming the old request: the next session's lock must restart it, not take it for done.
        const int port = 27416;
        var posts = new List<ActivationPost>();
        var log = new List<string>();
        using var hold = new ManualResetEventSlim();
        var e = new JoystickEngine
        {
            Port = port, SkipVJoyForTest = true, OutputOverride = _ => { }, AsyncPostsForTest = true,
            ActivateOverride = p =>
            {
                int n;
                lock (posts) { posts.Add(p); n = posts.Count; }
                if (n == 1) hold.Wait(5000);            // the activation hangs
                return !Harness.IsRelease(p);
            },
        };
        e.Log += m => { lock (log) log.Add(m); };
        e.SetRequest(4000, 8);
        var f8 = Frames.Channels(Enumerable.Repeat(100, 8).ToArray());
        e.Start();
        try
        {
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, Frames.Ascii("ELRS")); lock (posts) return posts.Count > 0; }, 3000));
            Assert.True(WaitFor(() => { Send(port, f8); return e.Source != null; }, 3000));
            Assert.True(e.SetRequest(2000, 8));                // queued behind the hanging activation
            e.StopAndWait(2000);                               // the release waits behind it too: at most 1.5 s
            hold.Set();
            Assert.True(e.WaitForPostsForTest(5000));
            lock (posts) Assert.Empty(posts.Where(p => p.Action == JoystickEngine.ActionEnd));
            lock (log) Assert.Contains("Releasing 127.0.0.1 failed: not sent, the wait ran out before its turn - it may " +
                                       "stream on until its WiFi or power is cycled", log);
            e.Start();
            Thread.Sleep(250);
            Assert.True(WaitFor(() => { Send(port, f8); lock (posts) return posts.Any(p => p.Action == JoystickEngine.ActionEnd && p.Restart); }, 3000),
                        "the module that kept the old request is restarted");
            Assert.True(e.WaitForPostsForTest(5000));
            lock (posts) Assert.Equal(new ActivationPost("127.0.0.1", JoystickEngine.ActionBegin, 2000, 8, true), posts[^1]);
        }
        finally
        {
            hold.Set();
            e.StopAndWait(2000);
        }
    }

    [Fact]
    public void StoppedState_IsReported_OnShutdown()
    {
        const int port = 27406;
        using var stopped = new ManualResetEventSlim();
        var e = NewEngine(port, new List<int[]>(), new List<string>(), stopped: stopped);
        e.Start();
        Thread.Sleep(300);
        e.Stop();
        Assert.True(stopped.Wait(3000), "engine must report Stopped when shut down");
    }
}
