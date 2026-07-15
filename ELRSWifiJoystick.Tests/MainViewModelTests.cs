using System.Net;
using Xunit;

namespace ELRSWifiJoystick.Tests;

// ViewModel tests: the VM is WPF-free by design, so it can be driven synchronously by
// feeding packets straight into its engine (with the usual test seams).
public class MainViewModelTests
{
    private static MainViewModel NewVm(out List<string> activated, int port = 11000, string? txIp = null)
    {
        var vm = new MainViewModel(port, txIp, a => a());   // synchronous UI marshaller
        var acts = new List<string>();
        vm.Engine.OutputOverride = _ => { };
        vm.Engine.ActivateOverride = ip => { acts.Add(ip); return true; };
        activated = acts;
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

    [Fact]
    public void TrayText_ReflectsStreamingSource()
    {
        var vm = NewVm(out _);
        Feed(vm, Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        Assert.Contains("192.168.2.138", vm.TrayText);
    }
}
