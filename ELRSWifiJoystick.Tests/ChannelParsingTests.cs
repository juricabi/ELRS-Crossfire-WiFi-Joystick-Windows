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
        // Overshoot values must not overflow the vJoy axis. (0xF26A itself is the
        // no-data placeholder and marks the whole frame as corrupt - tested separately.)
        var h = new Harness();
        h.Packet(Frames.Channels(0x816B, 0xFFFF, 100, 32767));
        Assert.Equal(new[] { 32767, 32767, 100, 32767 }, Assert.Single(h.Applied));
    }

    // A module the radio isn't feeding streams 16x 0xF26A. Clamping that to 32767 used to
    // show every axis at 100% - and hand the simulator full throttle.
    [Fact]
    public void AllPlaceholderFrame_IsNotAJoystickSource()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(Enumerable.Repeat(0xF26A, 16).ToArray()));

        Assert.Empty(h.Applied);
        Assert.Empty(h.ChannelEvents);
        Assert.Equal(0, h.StreamingCount);
        Assert.Null(h.Engine.Source);           // must not take the source lock
        Assert.Contains(h.Log, l => l.Contains("no stick data"));
    }

    [Fact]
    public void AllPlaceholderFrame_WarnsOnce_NotPerFrame()
    {
        var h = new Harness();
        var frame = Frames.Channels(Enumerable.Repeat(0xF26A, 16).ToArray());
        for (int i = 0; i < 50; i++) h.Packet(frame);

        Assert.Single(h.Log, l => l.Contains("no stick data"));
    }

    [Fact]
    public void RealStickData_StillLocksAndStreams_AfterPlaceholders()
    {
        var h = new Harness();
        h.Packet(Frames.Channels(Enumerable.Repeat(0xF26A, 16).ToArray()));
        h.Packet(Frames.Channels(16384, 16384, 0, 16384));

        Assert.Equal(new[] { 16384, 16384, 0, 16384 }, Assert.Single(h.Applied));
        Assert.Equal(EngineState.Streaming, h.States[^1].State);
    }

    [Fact]
    public void OvershootFrame_IsClampedAndApplied()
    {
        // A stick pushed past a wide endpoint reads slightly above 32767 (0x816B = 33131
        // observed live) - that is real stick data, clamped to full deflection.
        var h = new Harness();
        h.Packet(Frames.Channels(0x816B, 1000, 2000, 3000));

        Assert.Equal(new[] { 32767, 1000, 2000, 3000 }, Assert.Single(h.Applied));
        Assert.Equal(EngineState.Streaming, h.States[^1].State);
    }

    [Fact]
    public void CorruptFrame_MidStream_IsDroppedSilently()
    {
        // Under fast stick movement the module occasionally emits a corrupt frame mixing
        // the 0xF26A placeholder with garbage (captured live). Applying it would spike
        // axes to 100% for a frame; warning would spam; changing state would flip the UI
        // to "Searching" while streaming is actually fine.
        var h = new Harness();
        h.Packet(Frames.Channels(1000, 2000, 3000, 4000));
        var statesBefore = h.States.Count;
        var logBefore = h.Log.Count;

        h.Packet(Frames.Channels(62058, 62058, 62058, 56051, 556, 35904, 212, 11347));

        Assert.Single(h.Applied);                       // corrupt frame never reached vJoy
        Assert.Equal(statesBefore, h.States.Count);     // no state flicker
        Assert.Equal(logBefore, h.Log.Count);           // no log spam
        Assert.Equal(EngineState.Streaming, h.States[^1].State);
    }

    [Fact]
    public void PlaceholderFlood_ReleasesLock_ThenWarns()
    {
        // If the radio stops feeding the module mid-stream, the module keeps streaming
        // placeholders at ~90 Hz - the socket never times out, so the stale-lock release
        // must happen from the receive path.
        var h = new Harness();
        h.Packet(Frames.Channels(1000, 2000, 3000, 4000));
        h.Clock.Advance(JoystickEngine.SOURCE_TIMEOUT_SEC + 0.1);
        h.Packet(Frames.Channels(Enumerable.Repeat(0xF26A, 16).ToArray()));

        Assert.Null(h.Engine.Source);
        Assert.Equal(EngineState.Searching, h.States[^1].State);
        Assert.Contains(h.Log, l => l.Contains("no stick data"));
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
