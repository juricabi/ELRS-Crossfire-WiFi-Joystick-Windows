using System.Net;
using System.Text;

namespace ELRSWifiJoystick.Tests;

// Controllable clock so lock-timeout / throttle logic is tested deterministically.
class TestClock
{
    public DateTime Now = new(2026, 1, 1, 12, 0, 0);
    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
}

// A JoystickEngine wired with all test seams: fake clock, recorded output,
// fake activation (never touches vJoy or the network).
class Harness
{
    public readonly JoystickEngine Engine = new();
    public readonly TestClock Clock = new();
    public readonly List<int[]> Applied = new();
    public readonly List<string> Log = new();
    public readonly List<(EngineState State, string Detail)> States = new();
    public readonly List<string> Activated = new();
    public readonly List<int[]> ChannelEvents = new();
    public bool ActivateResult = true;

    public Harness()
    {
        Engine.Clock = () => Clock.Now;
        Engine.OutputOverride = ch => Applied.Add((int[])ch.Clone());
        Engine.ActivateOverride = ip => { Activated.Add(ip); return ActivateResult; };
        Engine.Log += Log.Add;
        Engine.StateChanged += (s, d) => States.Add((s, d));
        Engine.ChannelsUpdated += ch => ChannelEvents.Add((int[])ch.Clone());
    }

    public void Packet(byte[] data, string ip = "192.168.1.50")
        => Engine.Process(data, new IPEndPoint(IPAddress.Parse(ip), 49153));

    public int StreamingCount => States.Count(s => s.State == EngineState.Streaming);
}

static class Frames
{
    // Builds a joystick channel frame: [type=1][count][16-bit LE values]
    public static byte[] Channels(params int[] ch)
    {
        var d = new byte[2 + ch.Length * 2];
        d[0] = 1;
        d[1] = (byte)ch.Length;
        for (int i = 0; i < ch.Length; i++)
        {
            d[2 + i * 2] = (byte)(ch[i] & 0xFF);
            d[3 + i * 2] = (byte)((ch[i] >> 8) & 0xFF);
        }
        return d;
    }

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    public static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
