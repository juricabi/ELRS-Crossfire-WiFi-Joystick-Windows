using System.Net;
using Xunit;

namespace ELRSWifiJoystick.Tests;

// ViewModel tests: the VM is WPF-free by design, so it can be driven synchronously by
// feeding packets straight into its engine (with the usual test seams).
public class MainViewModelTests
{
    private static MainViewModel NewVm(out List<ActivationPost> posts, int port = 11000, string? txIp = null,
                                       int intervalUs = StreamRequest.IntervalDefault, int channels = StreamRequest.ChannelsDefault)
    {
        var vm = new MainViewModel(port, txIp, a => a(), intervalUs, channels);   // synchronous UI marshaller
        var sent = new List<ActivationPost>();
        vm.Engine.OutputOverride = _ => { };
        vm.Engine.ActivateOverride = p => { sent.Add(p); return true; };
        posts = sent;
        return vm;
    }

    private static void Feed(MainViewModel vm, byte[] data, string ip = "192.168.1.50")
        => vm.Engine.Process(data, new IPEndPoint(IPAddress.Parse(ip), 49153));

    [Fact]
    public void ChannelFrame_SetsStreamingBanner()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        Assert.Equal(EngineState.Streaming, vm.State);
        Assert.Equal("Connected - streaming", vm.BannerTitle);
        Assert.Equal("192.168.2.138", vm.BannerDetail);
        Assert.True(vm.IsStreaming);
    }

    [Fact]
    public void TickBars_UpdatesAxisValuesAndPercent()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(32767, 16384, 0, 8192));
        vm.TickBars();
        Assert.Equal(32767, vm.Axes[0].Value);
        Assert.Equal("100%", vm.Axes[0].PercentText);
        Assert.Equal("50%", vm.Axes[1].PercentText);
        Assert.Equal("0%", vm.Axes[2].PercentText);
    }

    [Fact]
    public void NonStreamingState_ClearsAxes()
    {
        var vm = NewVm(out _);
        var clock = new TestClock();
        vm.Engine.Clock = () => clock.Now;   // install the fake clock before locking

        Feed(vm, Frames.Channels(30000, 30000, 30000, 30000));
        vm.TickBars();
        Assert.True(vm.Axes[0].Value > 0);

        // simulate the stream going quiet -> engine releases the lock via Tick
        clock.Advance(10);
        vm.Engine.Tick();

        Assert.Equal(0, vm.Axes[0].Value);
        Assert.Equal("--", vm.Axes[0].PercentText);
        Assert.False(vm.IsStreaming);
    }

    [Fact]
    public void EngineLog_LandsInLogText()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(1, 2, 3, 4), "10.0.0.5");
        Assert.Contains("Joystick source locked to 10.0.0.5", vm.LogText);
    }

    [Fact]
    public void LogIsCapped()
    {
        var vm = NewVm(out _);
        for (int i = 0; i < 1000; i++)
            Feed(vm, Frames.Ascii("VELOCIDRONE"), $"10.0.{i / 250}.{i % 250}"); // each logs a beacon line
        int lines = vm.LogText.Split('\n').Length;
        Assert.True(lines <= 450, $"log grew to {lines} lines");
    }

    [Fact]
    public void HelpCommand_RaisesHelpRequested()
    {
        var vm = NewVm(out _);
        bool raised = false;
        vm.HelpRequested += () => raised = true;
        vm.HelpCommand.Execute(null);
        Assert.True(raised);
    }

    [Fact]
    public void StartStop_WithInvalidPort_LogsAndDoesNotStart()
    {
        var vm = NewVm(out _);
        vm.PortText = "not-a-port";
        vm.StartStopCommand.Execute(null);
        Assert.False(vm.Engine.IsRunning);
        Assert.Contains("Invalid port", vm.LogText);
    }

    [Fact]
    public void Axes_HaveEightRows_WithExpectedNames()
    {
        var vm = NewVm(out _);
        Assert.Equal(8, vm.Axes.Count);
        Assert.StartsWith("Roll", vm.Axes[0].Name);
        Assert.StartsWith("Aux 4", vm.Axes[7].Name);
    }

    // ---- the request drop-downs -----------------------------------------------------------

    [Fact]
    public void RateOptions_AreThePresets_DefaultMarked()
    {
        var vm = NewVm(out _);
        Assert.Equal(new[] { 1000, 2000, 3000, 4000, 5000, 6667, 10000, 20000 }, vm.RateOptions.Select(o => o.Value));
        Assert.Equal(new[] { "1000 Hz", "500 Hz", "333 Hz", "250 Hz (default)", "200 Hz", "150 Hz", "100 Hz", "50 Hz" },
                     vm.RateOptions.Select(o => o.Label));
        Assert.Equal(4000, vm.SelectedIntervalUs);
        Assert.Equal("250 Hz (default)", vm.RateOptions.Single(o => o.Value == vm.SelectedIntervalUs).ToString());
    }

    [Fact]
    public void ChannelOptions_Are4To16_DefaultMarked()
    {
        var vm = NewVm(out _);
        Assert.Equal(Enumerable.Range(4, 13), vm.ChannelOptions.Select(o => o.Value));
        Assert.Equal("16 (default)", vm.ChannelOptions[^1].Label);
        Assert.Equal("8", vm.ChannelOptions.Single(o => o.Value == 8).Label);
        Assert.Single(vm.ChannelOptions, o => o.Label.Contains("default"));
        Assert.Equal(16, vm.SelectedChannels);
    }

    [Fact]
    public void CommandLineRequest_IsSelected_AndANonPresetIntervalGetsItsOwnEntry()
    {
        var vm = NewVm(out _, intervalUs: 3500, channels: 8);
        Assert.Equal(3500, vm.Engine.IntervalUs);
        Assert.Equal(8, vm.Engine.Channels);
        Assert.Equal(3500, vm.SelectedIntervalUs);
        Assert.Equal(8, vm.SelectedChannels);
        Assert.Equal(9, vm.RateOptions.Count);
        Assert.Equal("286 Hz (3500 us)", vm.RateOptions.Single(o => o.Value == 3500).Label);
        Assert.Equal(new[] { 1000, 2000, 3000, 3500, 4000 }, vm.RateOptions.Take(5).Select(o => o.Value));   // in order
        Assert.Contains("asked 286", vm.StatsText);
    }

    [Fact]
    public void ChangingTheRate_WhileElrsStreams_RestartsItsStream()
    {
        var vm = NewVm(out var posts);
        Feed(vm, Frames.Ascii("ELRS"), "10.0.0.1");
        Feed(vm, Frames.Channels(Enumerable.Repeat(16000, 16).ToArray()), "10.0.0.1");
        posts.Clear();
        vm.SelectedIntervalUs = 2000;
        Assert.Equal(2000, vm.Engine.IntervalUs);
        Assert.Equal(new[]
        {
            new ActivationPost("10.0.0.1", JoystickEngine.ActionEnd, 0, 0, true),
            new ActivationPost("10.0.0.1", JoystickEngine.ActionBegin, 2000, 16, true),
        }, posts);
        Assert.Contains("Asking for 500 Hz, 16 channels.", vm.LogText);
        Assert.Contains("Restarting the stream of 10.0.0.1: 500 Hz, 16 channels", vm.LogText);
        Assert.Contains("asked 500", vm.StatsText);
        // the channels too
        vm.SelectedChannels = 8;
        Assert.Equal(8, vm.Engine.Channels);
        Assert.Equal(8, posts[^1].Channels);
        // setting the same value again sends nothing
        int n = posts.Count;
        vm.SelectedChannels = 8;
        Assert.Equal(n, posts.Count);
    }

    [Fact]
    public void ChangingTheRequest_WhileIdle_IsCarriedByTheNextActivation()
    {
        var vm = NewVm(out var posts);
        vm.SelectedIntervalUs = 10000;
        vm.SelectedChannels = 12;
        Assert.Empty(posts);
        Feed(vm, Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        var p = Assert.Single(posts);
        Assert.Equal((JoystickEngine.ActionBegin, 10000, 12), (p.Action, p.IntervalUs, p.Channels));
    }

    [Fact]
    public void LockedToCrossfire_DisablesTheDropDowns_WithANote()
    {
        var vm = NewVm(out _);
        var clock = new TestClock();
        vm.Engine.Clock = () => clock.Now;                  // install the fake clock before locking
        Assert.True(vm.IsRequestEditable);
        Assert.Equal("", vm.RequestNote);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Feed(vm, Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        Feed(vm, Frames.Channels(Enumerable.Repeat(16000, 16).ToArray()), "192.168.2.138");
        Assert.True(vm.IsCrossfire);
        Assert.False(vm.IsRequestEditable);
        Assert.Contains("Crossfire ignores these", vm.RequestNote);
        Assert.Equal(vm.RequestNote, vm.RequestToolTip);
        Assert.Contains(nameof(vm.IsRequestEditable), changed);
        Assert.Contains(nameof(vm.RequestNote), changed);
        Assert.Contains("Crossfire: its own rate", vm.StatsText);

        // the lock goes: editable again
        clock.Advance(10);
        vm.Engine.Tick();
        Assert.False(vm.IsStreaming);
        Assert.True(vm.IsRequestEditable);
        Assert.Equal("", vm.RequestNote);
    }

    [Fact]
    public void CrossfireBeacon_AfterTheLock_IsPickedUpOnTheNextStatsTick()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(Enumerable.Repeat(16000, 16).ToArray()), "192.168.2.138");
        Assert.False(vm.IsCrossfire);                       // no beacon yet: unknown
        Feed(vm, Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        vm.Engine.EmitStats();
        Assert.True(vm.IsCrossfire);
        Assert.False(vm.IsRequestEditable);
    }

    [Fact]
    public void Stats_ShowAskedVersusActual()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("Rate: 249 Hz (asked 250)     Jitter: 0.4 ms     249 pkt/s     16 ch",
                         MainViewModel.FormatStats(249.3, 0.43, 249, 4000, 16, 16, false));
            Assert.Equal("Rate: 100 Hz (asked 250)     Jitter: 1.0 ms     100 pkt/s     8 ch (asked 16)",
                         MainViewModel.FormatStats(100, 1, 100, 4000, 8, 16, false));
            Assert.Equal("Rate: 91 Hz (Crossfire: its own rate)     Jitter: 0.5 ms     91 pkt/s     16 ch",
                         MainViewModel.FormatStats(91, 0.5, 91, 2000, 16, 8, true));
            Assert.Equal("Rate: -- (asked 333)     Jitter: --", MainViewModel.FormatStats(0, 0, 0, 3000, 0, 16, false));
            Assert.Equal("Rate: -- (Crossfire: its own rate)     Jitter: --", MainViewModel.FormatStats(0, 0, 0, 3000, 16, 16, true));
            Assert.Equal("Rate: 250 Hz (asked 250)     Jitter: 0.1 ms     250 pkt/s",
                         MainViewModel.FormatStats(250, 0.1, 250, 4000, 0, 16, false));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void StatsTick_WhileStreaming_ShowsTheFramesChannelCount()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(Enumerable.Repeat(16000, 16).ToArray()), "10.0.0.2");
        vm.Engine.EmitStats();
        Assert.Contains("(asked 250)", vm.StatsText);
        Assert.EndsWith("16 ch", vm.StatsText);
        Assert.Equal("Rate: -- (asked 250)     Jitter: --", NewVm(out _).StatsText);   // before any data
    }

    [Fact]
    public void Buttons_ShowCh9To16()
    {
        var vm = NewVm(out _);
        Assert.Equal(MainViewModel.ButtonsIdleText, vm.ButtonsText);
        var f = Enumerable.Repeat(16384, 16).ToArray();
        f[8] = 32767;
        f[10] = 30000;
        Feed(vm, Frames.Channels(f));
        vm.TickBars();
        Assert.Equal("Buttons (CH9-CH16):   1 \u25CF   2 \u25CB   3 \u25CF   4 \u25CB   5 \u25CB   6 \u25CB   7 \u25CB   8 \u25CB",
                     vm.ButtonsText);
        Assert.Equal("Buttons (CH9-CH16): none - the module sends 8 channels", MainViewModel.FormatButtons(8, 0));
        Assert.Equal("Buttons (CH9-CH16):   1 \u25CB   2 \u25CF", MainViewModel.FormatButtons(10, 0b10));
        Assert.Equal(MainViewModel.ButtonsIdleText, MainViewModel.FormatButtons(0, 0));
    }

    [Fact]
    public void TickBars_CentersTheAxesTheFrameDoesNotCarry()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(Enumerable.Repeat(30000, 8).ToArray()));
        vm.TickBars();
        Feed(vm, Frames.Channels(1000, 2000, 3000, 4000));
        vm.TickBars();
        Assert.Equal(4000, vm.Axes[3].Value);
        Assert.Equal(JoystickMap.Center, vm.Axes[4].Value);
        Assert.Equal("--", vm.Axes[7].PercentText);
        Assert.Contains("none - the module sends 4 channels", vm.ButtonsText);
    }

    [Fact]
    public void NotStreaming_ResetsTheButtons()
    {
        var vm = NewVm(out _);
        var clock = new TestClock();
        vm.Engine.Clock = () => clock.Now;
        Feed(vm, Frames.Channels(Enumerable.Repeat(32767, 16).ToArray()));
        vm.TickBars();
        Assert.Contains("\u25CF", vm.ButtonsText);
        clock.Advance(10);
        vm.Engine.Tick();
        Assert.Equal(MainViewModel.ButtonsIdleText, vm.ButtonsText);
    }

    [Fact]
    public void TrayText_ReflectsStreamingSource()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        Assert.Contains("192.168.2.138", vm.TrayText);
    }
}
