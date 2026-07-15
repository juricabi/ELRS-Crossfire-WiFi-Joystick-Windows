using Xunit;

namespace ELRSWifiJoystick.Tests;

public class MiscTests
{
    [Theory]
    [InlineData("ELRS", "ELRS", true)]                 // exact
    [InlineData("ELRS123", "ELRS", true)]              // prefix
    [InlineData("ELR", "ELRS", false)]                 // data shorter than prefix
    [InlineData("XLRS", "ELRS", false)]                // mismatch
    [InlineData("VELOCIDRONE", "VELOCIDRONE", true)]
    [InlineData("VELOCIDRONX", "VELOCIDRONE", false)]  // last byte differs
    [InlineData("anything", "", true)]                 // empty prefix matches
    public void StartsWith_Cases(string data, string prefix, bool expected)
    {
        Assert.Equal(expected,
            JoystickEngine.StartsWith(Frames.Ascii(data), Frames.Ascii(prefix)));
    }

    [Fact]
    public void FirewallRule_NonexistentName_ReportsFalse()
    {
        Assert.False(FirewallHelper.RuleExists("Definitely_Not_A_Real_Rule_xyz_12345"));
    }

    [Fact]
    public void FirewallRuleName_DefaultPort_KeepsLegacyName()
    {
        // Existing installs already have this rule - the name must not change for 11000.
        Assert.Equal("ELRS WiFi Joystick", FirewallHelper.RuleNameFor(11000));
    }

    [Fact]
    public void FirewallRuleName_CustomPort_GetsItsOwnRule()
    {
        // A rule for the default port must not mask a missing rule for a custom port.
        Assert.Equal("ELRS WiFi Joystick (UDP 12345)", FirewallHelper.RuleNameFor(12345));
        Assert.False(FirewallHelper.RuleExists(FirewallHelper.RuleNameFor(48999)));
    }

    [Fact]
    public void OutputOverride_ReceivesEveryFrame()
    {
        var h = new Harness();
        for (int i = 0; i < 3; i++)
            h.Packet(Frames.Channels(i, i, i, i));
        Assert.Equal(3, h.Applied.Count);
    }

    [Fact]
    public void LogNeverThrows_OnGarbageInput()
    {
        var h = new Harness();
        var rng = new Random(1234);
        for (int i = 0; i < 500; i++)
        {
            var d = new byte[rng.Next(0, 64)];
            rng.NextBytes(d);
            h.Packet(d); // must never throw, whatever arrives
        }
    }
}
