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
            ActivateOverride = ip => { lock (activated) activated.Add(ip); return true; },
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
