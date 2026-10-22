using Xunit;

namespace ELRSWifiJoystick.Tests;

// The GUI's command line (AppArgs) and the CLI's (CliOptions, CliCommand, CliRequest: the CLI's
// CliArgs.cs is compiled into this test project).
public class CommandLineTests
{
    // ---- GUI ---------------------------------------------------------------------------------

    [Fact]
    public void Gui_Defaults()
    {
        var o = AppArgs.Parse(Array.Empty<string>());
        Assert.Equal(11000, o.Port);
        Assert.Null(o.TxIp);
        Assert.Equal(4000, o.IntervalUs);
        Assert.Equal(16, o.Channels);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Gui_PortAndModule_AsBefore()
    {
        var o = AppArgs.Parse(new[] { "12000", "--tx", "192.168.2.138" });
        Assert.Equal(12000, o.Port);
        Assert.Equal("192.168.2.138", o.TxIp);
        Assert.Equal("10.0.0.1", AppArgs.Parse(new[] { "--crossfire", "10.0.0.1" }).TxIp);
        Assert.Equal("10.0.0.2", AppArgs.Parse(new[] { "--activate", "10.0.0.2" }).TxIp);
        Assert.Null(AppArgs.Parse(new[] { "--tx" }).TxIp);              // no value: ignored, as before
        Assert.Equal(11000, AppArgs.Parse(new[] { "70000", "junk" }).Port);   // unknown/out of range: ignored
        Assert.Empty(AppArgs.Parse(new[] { "70000", "junk", "--whatever" }).Errors);
    }

    [Theory]
    [InlineData(new[] { "--rate", "500" }, 2000, 16)]
    [InlineData(new[] { "--rate", "333" }, 3000, 16)]
    [InlineData(new[] { "--rate", "150" }, 6667, 16)]
    [InlineData(new[] { "--rate", "300" }, 3333, 16)]
    [InlineData(new[] { "--interval", "3500" }, 3500, 16)]
    [InlineData(new[] { "--channels", "8" }, 4000, 8)]
    [InlineData(new[] { "--rate", "500", "--interval", "6000" }, 6000, 16)]   // the later one wins
    [InlineData(new[] { "--interval", "6000", "--rate", "500" }, 2000, 16)]
    [InlineData(new[] { "11001", "--rate", "1000", "--channels", "4", "--tx", "1.2.3.4" }, 1000, 4)]
    public void Gui_Request(string[] args, int us, int ch)
    {
        var o = AppArgs.Parse(args);
        Assert.Empty(o.Errors);
        Assert.Equal(us, o.IntervalUs);
        Assert.Equal(ch, o.Channels);
    }

    [Fact]
    public void Gui_BadValues_AreReported_AndKeepTheDefault()
    {
        var o = AppArgs.Parse(new[] { "--rate", "7", "--channels", "17", "--interval", "fast", "12000" });
        Assert.Equal(new[]
        {
            "--rate: 7 is not supported - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)",
            "--channels: 17 is not supported - use 4 to 16",
            "--interval: \"fast\" is not a number - use 1000 to 100000",
        }, o.Errors);
        Assert.Equal(4000, o.IntervalUs);
        Assert.Equal(16, o.Channels);
        Assert.Equal(12000, o.Port);                                    // the rest still applies
    }

    [Fact]
    public void Gui_MissingValue_IsReported()
    {
        Assert.Equal(new[] { "--channels needs a value - use 4 to 16" }, AppArgs.Parse(new[] { "--channels" }).Errors);
        Assert.StartsWith("--rate needs a value - use 1000, 500", Assert.Single(AppArgs.Parse(new[] { "--rate" }).Errors));
    }

    [Fact]
    public void Gui_ValueOfAnOption_IsNotTakenForAPort()
    {
        var o = AppArgs.Parse(new[] { "--channels", "8" });
        Assert.Equal(11000, o.Port);
        Assert.Equal(8, o.Channels);
    }

    // ---- CLI options -------------------------------------------------------------------------

    [Fact]
    public void Cli_Defaults()
    {
        var o = CliOptions.Parse(Array.Empty<string>());
        Assert.Null(o.Error);
        Assert.False(o.Help);
        Assert.False(o.NoFirewall);
        Assert.Equal((11000, (string?)null, 4000, 16), (o.Port, o.TxIp, o.IntervalUs, o.Channels));
    }

    [Fact]
    public void Cli_AllOptions()
    {
        var o = CliOptions.Parse(new[] { "12000", "--tx", "10.0.0.5", "--rate", "500", "--channels", "8", "--no-firewall" });
        Assert.Null(o.Error);
        Assert.Equal((12000, "10.0.0.5", 2000, 8, true), (o.Port, o.TxIp, o.IntervalUs, o.Channels, o.NoFirewall));
        Assert.Equal(3500, CliOptions.Parse(new[] { "--interval", "3500" }).IntervalUs);
        Assert.Equal(2000, CliOptions.Parse(new[] { "--interval", "3500", "--rate", "500" }).IntervalUs);
        Assert.Equal(3500, CliOptions.Parse(new[] { "--rate", "500", "--interval", "3500" }).IntervalUs);
    }

    [Theory]
    [InlineData(new object[] { new[] { "--help" } })]
    [InlineData(new object[] { new[] { "-h" } })]
    [InlineData(new object[] { new[] { "/?" } })]
    [InlineData(new object[] { new[] { "12000", "--help", "--bogus" } })]       // help stops the parsing
    public void Cli_Help(string[] args)
    {
        var o = CliOptions.Parse(args);
        Assert.True(o.Help);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "Unknown argument: --bogus")]
    [InlineData(new[] { "70000" }, "Unknown argument: 70000")]
    [InlineData(new[] { "--bogus", "--help" }, "Unknown argument: --bogus")]
    [InlineData(new[] { "--rate", "2000" }, "--rate: 2000 is not supported - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)")]
    [InlineData(new[] { "--rate", "fast" }, "--rate: \"fast\" is not a number - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)")]
    [InlineData(new[] { "--interval", "500" }, "--interval: 500 is not supported - use 1000 to 100000")]
    [InlineData(new[] { "--channels", "2" }, "--channels: 2 is not supported - use 4 to 16")]
    [InlineData(new[] { "--channels" }, "--channels needs a value - use 4 to 16")]
    [InlineData(new[] { "--rate" }, "--rate needs a value - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)")]
    [InlineData(new[] { "--interval" }, "--interval needs a value - use 1000 to 100000")]
    [InlineData(new[] { "--tx" }, "--tx needs a value (the module's IP)")]
    public void Cli_Errors(string[] args, string error)
    {
        var o = CliOptions.Parse(args);
        Assert.Equal(error, o.Error);
        Assert.False(o.Help);
    }

    // ---- CLI commands ------------------------------------------------------------------------

    [Theory]
    [InlineData("q", "quit", null)]
    [InlineData("QUIT", "quit", null)]
    [InlineData("exit", "quit", null)]
    [InlineData("s", "toggle", null)]
    [InlineData("start", "start", null)]
    [InlineData("stop", "stop", null)]
    [InlineData("t 192.168.1.5", "target", "192.168.1.5")]
    [InlineData("  t   192.168.1.5  ", "target", "192.168.1.5")]
    [InlineData("t", "target", null)]
    [InlineData("p 12000", "port", "12000")]
    [InlineData("port 12000", "port", "12000")]
    [InlineData("f", "firewall", null)]
    [InlineData("r 500", "rate", "500")]
    [InlineData("rate 500", "rate", "500")]
    [InlineData("rate\t500", "rate", "500")]
    [InlineData("i 3500", "interval", "3500")]
    [InlineData("interval 3500", "interval", "3500")]
    [InlineData("c 8", "channels", "8")]
    [InlineData("channels 8", "channels", "8")]
    [InlineData("a", "restart", null)]
    [InlineData("restart", "restart", null)]
    [InlineData("status", "status", null)]
    [InlineData("", "", null)]
    [InlineData("   ", "", null)]
    [InlineData("bogus 1", "?bogus", "1")]
    public void Cli_CommandParse(string line, string verb, string? arg)
        => Assert.Equal((verb, arg), CliCommand.Parse(line));

    private static (JoystickEngine Engine, List<string> Log, List<ActivationPost> Posts) Engine()
    {
        var e = new JoystickEngine { OutputOverride = _ => { } };
        var log = new List<string>();
        var posts = new List<ActivationPost>();
        e.Log += log.Add;
        e.ActivateOverride = p => { posts.Add(p); return true; };
        return (e, log, posts);
    }

    [Fact]
    public void Cli_SetRate_Validates_SaysWhatIsAsked_AndApplies()
    {
        var (e, log, posts) = Engine();
        var out1 = new List<string>();
        CliRequest.SetRate(e, "500", out1.Add);
        Assert.Equal(new[] { "Asking for 500 Hz, 16 channels." }, out1);
        Assert.Equal(2000, e.IntervalUs);
        CliRequest.SetRate(e, "500", out1.Add);
        Assert.Equal("Already asking for 500 Hz, 16 channels.", out1[^1]);
        CliRequest.SetRate(e, "5", out1.Add);
        Assert.StartsWith("Rate: 5 is not supported - use 1000, 500", out1[^1]);
        Assert.Equal(2000, e.IntervalUs);
        CliRequest.SetInterval(e, "3500", out1.Add);
        Assert.Equal("Asking for 286 Hz (3500 us), 16 channels.", out1[^1]);
        CliRequest.SetInterval(e, "100", out1.Add);
        Assert.Equal("Interval: 100 is not supported - use 1000 to 100000", out1[^1]);
        CliRequest.SetChannels(e, "8", out1.Add);
        Assert.Equal("Asking for 286 Hz (3500 us), 8 channels.", out1[^1]);
        Assert.Equal(8, e.Channels);
        CliRequest.SetChannels(e, "x", out1.Add);
        Assert.Equal("Channels: \"x\" is not a number - use 4 to 16", out1[^1]);
        Assert.Empty(posts);                                // nothing streams: nothing sent
    }

    [Fact]
    public void Cli_SetRate_WhileElrsStreams_RestartsIt()
    {
        var (e, log, posts) = Engine();
        e.Process(Frames.Ascii("ELRS"), new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.1"), 1));
        e.Process(Frames.Channels(Enumerable.Repeat(16000, 16).ToArray()), new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.1"), 1));
        posts.Clear();
        var said = new List<string>();
        CliRequest.SetRate(e, "1000", m => { said.Add(m); log.Add(m); });
        // what is asked for is said first, then the engine restarts the stream
        int asked = log.IndexOf("Asking for 1000 Hz, 16 channels.");
        int restarting = log.FindIndex(l => l.StartsWith("Restarting the stream of 10.0.0.1"));
        Assert.True(asked >= 0 && restarting > asked);
        Assert.Equal(new[] { JoystickEngine.ActionEnd, JoystickEngine.ActionBegin }, posts.Select(p => p.Action));
        Assert.Equal(1000, posts[1].IntervalUs);
    }

    [Fact]
    public void Cli_StatusLine()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { StatusLine(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static void StatusLine()
    {
        var ch16 = Enumerable.Repeat(16384, 16).ToArray();
        string s = CliRequest.FormatStatus(249.4, 0.43, ch16, 250, false, 0b1001);
        Assert.StartsWith("  249/250 Hz   0.4 ms  | R: 50% P: 50% T: 50% Y: 50% A1: 50% A2: 50% A3: 50% A4: 50%", s);
        Assert.EndsWith("  B:1--4----", s);
        // Crossfire: its own rate
        Assert.StartsWith("   91 Hz XF", CliRequest.FormatStatus(91, 0.4, ch16, 250, true, 0));
        // no CH9-16: no buttons; 10 channels: two
        Assert.DoesNotContain("B:", CliRequest.FormatStatus(250, 0.4, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 250, false, 0));
        Assert.EndsWith("B:-2", CliRequest.FormatStatus(250, 0.4, Enumerable.Repeat(0, 10).ToArray(), 250, false, 0b10));
        Assert.DoesNotContain("A1", CliRequest.FormatStatus(250, 0.4, new[] { 1, 2, 3, 4 }, 250, false, 0));
    }
}
