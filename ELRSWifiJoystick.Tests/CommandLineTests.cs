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
        Assert.Empty(AppArgs.Parse(new[] { "70000", "junk" }).Errors);
        // an unknown option is reported (a typo like --channel 8 must not quietly become the port)
        Assert.Equal(new[] { "Unknown option --whatever - ignored" }, AppArgs.Parse(new[] { "70000", "junk", "--whatever" }).Errors);
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

    [Theory]
    [InlineData(new[] { "--channels", "--rate", "500" }, 11000, 2000, 16, "--channels needs a value - use 4 to 16")]
    [InlineData(new[] { "--tx", "--rate", "500" }, 11000, 2000, 16, "--tx needs a value (the module's IP)")]
    [InlineData(new[] { "--Rate", "500" }, 11000, 4000, 16, "Unknown option --Rate 500 - ignored")]
    [InlineData(new[] { "-r", "500" }, 11000, 4000, 16, "Unknown option -r 500 - ignored")]
    [InlineData(new[] { "--channel", "8" }, 11000, 4000, 16, "Unknown option --channel 8 - ignored")]
    [InlineData(new[] { "--verbose", "--rate", "500" }, 11000, 2000, 16, "Unknown option --verbose - ignored")]
    [InlineData(new[] { "--tx", "elrs_tx.local" }, 11000, 4000, 16, "--tx: \"elrs_tx.local\" is not an IPv4 address - use a.b.c.d")]
    [InlineData(new[] { "--tx", "192.168.001.050" }, 11000, 4000, 16, "--tx: \"192.168.001.050\" is not an IPv4 address - use a.b.c.d")]
    public void Gui_AMistake_IsReported_AndNeverTakenForThePort(string[] args, int port, int us, int ch, string error)
    {
        var o = AppArgs.Parse(args);
        Assert.Equal(new[] { error }, o.Errors);
        Assert.Equal((port, us, ch), (o.Port, o.IntervalUs, o.Channels));
        Assert.Null(o.TxIp);
    }

    [Fact]
    public void Gui_NameEqualsValue_LikeTheLinuxApp()
    {
        var o = AppArgs.Parse(new[] { "--rate=500", "--channels=8", "--tx=10.0.0.1", "12000" });
        Assert.Empty(o.Errors);
        Assert.Equal((12000, "10.0.0.1", 2000, 8), (o.Port, o.TxIp, o.IntervalUs, o.Channels));
        Assert.Equal(new[] { "--rate: 7 is not supported - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)" },
                     AppArgs.Parse(new[] { "--rate=7" }).Errors);
    }

    [Fact]
    public void Gui_TheMessage_SaysWhatIsUsed()
    {
        // "--interval 3500 --rate 7": the bad rate is ignored, 3500 is used - not "the default".
        var o = AppArgs.Parse(new[] { "--interval", "3500", "--rate", "7" });
        Assert.Single(o.Errors);
        Assert.Equal("Using UDP port 11000, 286 Hz (3500 us), 16 channels, auto-discovery.", o.Summary);
        Assert.Equal("Using UDP port 12000, 250 Hz, 8 channels, module 10.0.0.1.",
                     AppArgs.Parse(new[] { "12000", "--channels", "8", "--tx", " 10.0.0.1 " }).Summary);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("  10.0.0.1\t", "10.0.0.1")]
    [InlineData("0.0.0.1", "0.0.0.1")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void ModuleIp_Valid(string? text, string? ip)
    {
        Assert.True(JoystickEngine.TryParseModuleIp(text, out string? got));
        Assert.Equal(ip, got);
    }

    [Theory]
    [InlineData("192.168.001.050")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.")]
    [InlineData(".1.2.3.4")]
    [InlineData("1..2.3")]
    [InlineData("1.2.3.256")]
    [InlineData("1.2.3.1000")]
    [InlineData("0.0.0.0")]
    [InlineData("1.2.3.4:80")]
    [InlineData("+1.2.3.4")]
    [InlineData("1.2.3.4 5")]
    [InlineData("elrs_tx.local")]
    [InlineData("::1")]
    [InlineData("١.2.3.4")]          // a non-ASCII digit
    public void ModuleIp_Invalid(string text)
    {
        Assert.False(JoystickEngine.TryParseModuleIp(text, out string? got));
        Assert.Null(got);
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
        // like the Linux app: --name=value; and the module IP as frames are compared with it
        o = CliOptions.Parse(new[] { "--rate=500", "--channels=8", "--tx= 10.0.0.5 " });
        Assert.Null(o.Error);
        Assert.Equal(("10.0.0.5", 2000, 8), (o.TxIp, o.IntervalUs, o.Channels));
        Assert.Equal("10.0.0.5", CliOptions.Parse(new[] { "--tx", "\t10.0.0.5" }).TxIp);
    }

    [Fact]
    public void Parse_ATooLongValue_IsNotCutIntoAValidOne()
    {
        // The error text is cut to 40 characters; what is parsed must not be.
        string rate = new string('0', 37) + "250xyz";
        Assert.False(StreamRequest.TryParseRate(rate, out _, out string err));
        Assert.StartsWith("\"" + new string('0', 37) + "250", err);
        Assert.Contains("is not a number", err);
        Assert.False(StreamRequest.TryParseChannels(new string('0', 38) + "16junk", out _, out _));
        Assert.False(StreamRequest.TryParseInterval(new string('0', 36) + "2000abcdef", out _, out _));
        Assert.NotNull(CliOptions.Parse(new[] { "--channels", new string('0', 38) + "16junk" }).Error);
        Assert.Single(AppArgs.Parse(new[] { "--interval", new string('0', 36) + "2000abcdef" }).Errors);
        // a long number that is valid stays valid
        Assert.True(StreamRequest.TryParseRate(new string('0', 50) + "250", out int us, out _));
        Assert.Equal(4000, us);
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
    [InlineData(new[] { "--tx", "" }, "--tx needs a value (the module's IP)")]
    [InlineData(new[] { "--tx", "--rate", "500" }, "--tx needs a value (the module's IP)")]
    [InlineData(new[] { "--channels", "--rate", "500" }, "--channels needs a value - use 4 to 16")]
    [InlineData(new[] { "--tx", "elrs_tx.local" }, "--tx: \"elrs_tx.local\" is not an IPv4 address - use a.b.c.d")]
    [InlineData(new[] { "--tx", "10.0.0.01" }, "--tx: \"10.0.0.01\" is not an IPv4 address - use a.b.c.d")]
    [InlineData(new[] { "--rate=7" }, "--rate: 7 is not supported - use 1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)")]
    [InlineData(new[] { "--no-firewall=1" }, "Unknown argument: --no-firewall=1")]
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
        // One line that fits an 80-column console (the default cmd window before Windows 10): the CH9-16
        // buttons at its end must not be cut off. Changed from 96 characters ("R: 50% ... A1: 50%").
        var ch16 = Enumerable.Repeat(16384, 16).ToArray();
        Assert.Equal(" 249/250 Hz  0.4 ms | R: 50 P: 50 T: 50 Y: 50  A:  50  50  50  50  B:1--4----",
                     CliRequest.FormatStatus(249.4, 0.43, ch16, 250, false, 0b1001));
        var full = Enumerable.Repeat(32767, 16).ToArray();
        string widest = CliRequest.FormatStatus(1003, 123.4, full, 1000, false, 0xFF);
        Assert.Equal("1003/1000 Hz 123.4 ms | R:100 P:100 T:100 Y:100  A: 100 100 100 100  B:12345678", widest);
        Assert.True(widest.Length <= 79, $"{widest.Length} characters");
        // Crossfire: its own rate
        Assert.StartsWith("  91 Hz XF  0.4 ms |", CliRequest.FormatStatus(91, 0.4, ch16, 250, true, 0));
        Assert.True(CliRequest.FormatStatus(91, 123.4, full, 250, true, 0xFF).Length <= 79);
        // no CH9-16: no buttons; 10 channels: two
        Assert.DoesNotContain("B:", CliRequest.FormatStatus(250, 0.4, new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 250, false, 0));
        Assert.EndsWith("B:-2", CliRequest.FormatStatus(250, 0.4, Enumerable.Repeat(0, 10).ToArray(), 250, false, 0b10));
        Assert.EndsWith("Y:  0", CliRequest.FormatStatus(250, 0.4, new[] { 1, 2, 3, 4 }, 250, false, 0));
        Assert.EndsWith("A:   0", CliRequest.FormatStatus(250, 0.4, new[] { 1, 2, 3, 4, 5 }, 250, false, 0));
    }
}
