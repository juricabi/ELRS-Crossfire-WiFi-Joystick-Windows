using Xunit;

namespace ELRSWifiJoystick.Tests;

public class ChannelParsingTests
{
    [Fact]
    public void Decodes8Channels_LittleEndian()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(0x1234, 0, 32767, 5000, 6000, 7000, 8000, 9000));
        var ch = Assert.Single(h.Applied);
        Assert.Equal(new[] { 0x1234, 0, 32767, 5000, 6000, 7000, 8000, 9000 }, ch);
    }

    [Fact]
    public void Accepts16Channels_CrossfireAlwaysSends16()
    {
        var h = new Harness();
        var vals = Enumerable.Range(100, 16).ToArray();
        h.Packet(Frames.Channels(vals));
        Assert.Equal(vals, Assert.Single(h.Applied));
    }

    [Fact]
    public void AcceptsMinimumOf4Channels()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(10, 20, 30, 40));
        Assert.Equal(new[] { 10, 20, 30, 40 }, Assert.Single(h.Applied));
    }

    [Fact]
    public void RejectsFewerThan4Channels()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(10, 20, 30));
        Assert.Empty(h.Applied);
    }

    [Fact]
    public void RejectsMoreThan16Channels()
    {
        var h = new Harness();
        var d = new byte[2 + 17 * 2];
        d[0] = 1; d[1] = 17;
        h.Packet(d);
        Assert.Empty(h.Applied);
    }

    [Fact]
    public void RejectsTruncatedFrame()
    {
        var h = new Harness();
        var full = Frames.Channels(1, 2, 3, 4, 5, 6, 7, 8);
        var truncated = full.Take(9).ToArray(); // declares 8 channels, data cut short
        h.Packet(truncated);
        Assert.Empty(h.Applied);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 1, 8 })]
    public void RejectsTooShortPackets_WithoutCrashing(byte[] packet)
    {
        var h = new Harness();
        h.Packet(packet);
        Assert.Empty(h.Applied);
        Assert.Empty(h.Activated);
    }

    [Fact]
    public void ClampsOverrangeValues_To15BitMax()
    {
        // A radio-less Crossfire module idles at 0xF26A (62058) - must not overflow vJoy.
        var h = new Harness();
        h.Packet(Frames.Channels(0xF26A, 0xFFFF, 100, 32767));
        Assert.Equal(new[] { 32767, 32767, 100, 32767 }, Assert.Single(h.Applied));
    }

    [Fact]
    public void FrameTypeByte_IsNotValidated()
    {
        // Documents current behaviour: any first byte that isn't a beacon prefix is
        // treated as a channel frame if the count/length are valid.
        var h = new Harness();
        var d = Frames.Channels(1, 2, 3, 4);
        d[0] = 0;
        h.Packet(d);
        Assert.Single(h.Applied);
    }

    [Fact]
    public void ChannelsUpdatedEvent_CarriesTheSameValues()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(11, 22, 33, 44));
        Assert.Equal(new[] { 11, 22, 33, 44 }, Assert.Single(h.ChannelEvents));
    }

    [Fact]
    public void ZeroValues_PassThrough()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(0, 0, 0, 0));
        Assert.Equal(new[] { 0, 0, 0, 0 }, Assert.Single(h.Applied));
    }
}
