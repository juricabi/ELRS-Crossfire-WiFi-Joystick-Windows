using Xunit;

namespace ELRSWifiJoystick.Tests;

public class BeaconTests
{
    [Fact]
    public void ElrsBeacon_TriggersActivation()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("ELRS"), "192.168.1.10");
        Assert.Equal(new[] { "192.168.1.10" }, h.Activated);
        Assert.Empty(h.Applied);
        Assert.Contains(h.Log, l => l.Contains("ELRS"));
    }

    [Fact]
    public void CrossfireBeacon_TriggersActivation_AndIsIdentified()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        Assert.Equal(new[] { "192.168.2.138" }, h.Activated);
        Assert.Contains(h.Log, l => l.Contains("Crossfire"));
    }

    [Fact]
    public void BeaconWithTrailingBytes_IsStillABeacon()
    {
        var h = new Harness();
        h.Packet(Frames.Concat(Frames.Ascii("ELRS"), new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Single(h.Activated);
        Assert.Empty(h.Applied); // never parsed as channels
    }

    [Fact]
    public void RepeatedBeacon_IsThrottled_WithinFiveSeconds()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"));
        h.Clock.Advance(2);
        h.Packet(Frames.Ascii("VELOCIDRONE"));
        Assert.Single(h.Activated); // second one throttled
    }

    [Fact]
    public void Beacon_ReactivatesAfterThrottleExpiry_WhenStillNoData()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"));
        h.Clock.Advance(6);
        h.Packet(Frames.Ascii("VELOCIDRONE"));
        Assert.Equal(2, h.Activated.Count);
    }

    [Fact]
    public void Beacon_DoesNotReactivate_WhileDataIsFlowing()
    {
        // The module keeps beaconing while it streams; we must not re-POST every beacon.
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.2.138"); // locks + fresh data
        h.Clock.Advance(1);
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        Assert.Single(h.Activated);
    }

    [Fact]
    public void Beacon_Reactivates_AfterStreamWentStale()
    {
        var h = new Harness();
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.2.138");
        h.Clock.Advance(6); // stream stale AND throttle expired
        h.Packet(Frames.Ascii("VELOCIDRONE"), "192.168.2.138");
        Assert.Equal(2, h.Activated.Count);
    }

    [Fact]
    public void Beacon_FromSecondModule_IgnoredWhileLockedToFirst()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(1, 2, 3, 4), "192.168.1.10"); // lock to first
        h.Clock.Advance(1);
        h.Packet(Frames.Ascii("ELRS"), "192.168.1.99");        // other module beacons
        Assert.DoesNotContain("192.168.1.99", h.Activated);
    }

    [Fact]
    public void ForcedModule_BeaconFromOtherIp_IsIgnored()
    {
        var h = new Harness();
        h.Engine.ActivationIP = "10.0.0.9";
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.7");
        Assert.Empty(h.Activated);
        h.Packet(Frames.Ascii("VELOCIDRONE"), "10.0.0.9");
        Assert.Equal(new[] { "10.0.0.9" }, h.Activated);
    }

    [Fact]
    public void FailedActivation_DoesNotStartFirewallHintTimer()
    {
        var h = new Harness { ActivateResult = false };
        h.Packet(Frames.Ascii("VELOCIDRONE"));
        h.Clock.Advance(10);
        h.Engine.Tick();
        Assert.DoesNotContain(h.Log, l => l.Contains("Firewall"));
    }
}
