using Xunit;

namespace ELRSWifiJoystick.Tests;

// Records what would be written to vJoy (SetAxis / SetBtn), and can fail calls like a driver would.
class FakeOutput : IJoystickOutput
{
    public int Buttons = 8;
    public readonly List<(int Axis, int Value)> Axes = new();
    public readonly List<(int Button, bool Pressed)> Btns = new();
    public Func<int, bool> AxisOk = _ => true;
    public Func<int, bool> ButtonOk = _ => true;

    public int ButtonCount => Buttons;
    public bool SetAxis(int axis, int value) { Axes.Add((axis, value)); return AxisOk(axis); }
    public bool SetButton(int button, bool pressed) { Btns.Add((button, pressed)); return ButtonOk(button); }
    public void Clear() { Axes.Clear(); Btns.Clear(); }
}

public class JoystickMapTests
{
    private const int Hi = 32767, Lo = 0, Mid = 16384;

    // 16 channels: CH1-8 = axes, CH9-16 = buttons
    private static int[] Frame(int[] axes, params int[] buttons)
    {
        var f = new int[8 + buttons.Length];
        axes.CopyTo(f, 0);
        buttons.CopyTo(f, 8);
        return f;
    }

    private static readonly int[] A = { 100, 200, 300, 400, 500, 600, 700, 800 };

    [Theory]
    [InlineData(false, 24575, false)]
    [InlineData(false, 24576, true)]       // pressed at 75 %
    [InlineData(false, 32767, true)]
    [InlineData(false, 20480, false)]
    [InlineData(false, 0, false)]
    [InlineData(true, 24575, true)]        // between the thresholds: stays as it was
    [InlineData(true, 20480, true)]
    [InlineData(true, 20479, false)]       // released below 62.5 %
    [InlineData(true, 0, false)]
    public void NextButton_Hysteresis(bool pressed, int raw, bool expected)
        => Assert.Equal(expected, JoystickMap.NextButton(pressed, raw));

    [Fact]
    public void Thresholds_AreTheLinuxVersionsAndTheElrsBleJoysticks()
    {
        Assert.Equal(24576, JoystickMap.PressAt);       // 75 % of 32768
        Assert.Equal(20480, JoystickMap.ReleaseBelow);  // 62.5 %
        Assert.Equal(16384, JoystickMap.Center);
    }

    [Fact]
    public void AxisOrder_IsTheAppsVJoyOrder()
    {
        Assert.Equal(new[]
        {
            HID_USAGES.HID_USAGE_X, HID_USAGES.HID_USAGE_Y, HID_USAGES.HID_USAGE_RX, HID_USAGES.HID_USAGE_RY,
            HID_USAGES.HID_USAGE_RZ, HID_USAGES.HID_USAGE_Z, HID_USAGES.HID_USAGE_SL0, HID_USAGES.HID_USAGE_SL1,
        }, JoystickMap.AxisUsages);
    }

    [Fact]
    public void FirstFrame_WritesEverything_InOrder()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        m.Frame(Frame(A, Hi, Lo, Hi, Lo, Lo, Lo, Lo, Hi), o);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (i, A[i])), o.Axes);
        Assert.Equal(new[] { (0, true), (1, false), (2, true), (3, false), (4, false), (5, false), (6, false), (7, true) }, o.Btns);
        Assert.Equal(0b1000_0101, m.ButtonMask);
    }

    [Fact]
    public void SameFrame_WritesNothing_AChangeOnlyItself()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        var f = Frame(A, Hi, Lo, Lo, Lo, Lo, Lo, Lo, Lo);
        m.Frame(f, o);
        o.Clear();
        m.Frame(f, o);
        Assert.Empty(o.Axes);
        Assert.Empty(o.Btns);
        var g = (int[])f.Clone();
        g[2] = 12345;
        g[9] = Hi;
        m.Frame(g, o);
        Assert.Equal(new[] { (2, 12345) }, o.Axes);
        Assert.Equal(new[] { (1, true) }, o.Btns);
    }

    [Fact]
    public void Button_Hysteresis_AcrossFrames()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        void Ch9(int raw) => m.Frame(Frame(A, raw, Lo, Lo, Lo, Lo, Lo, Lo, Lo), o);
        Ch9(24000);                          // below 75 %: released
        Assert.Equal(0, m.ButtonMask);
        Ch9(24576);                          // pressed
        Assert.Equal(1, m.ButtonMask);
        Ch9(22000);                          // in the gap: stays pressed, nothing written
        Ch9(20480);
        Assert.Equal(1, m.ButtonMask);
        Ch9(20479);                          // released
        Assert.Equal(0, m.ButtonMask);
        Ch9(22000);                          // in the gap from below: stays released
        Assert.Equal(0, m.ButtonMask);
        Assert.Equal(new[] { (0, false), (0, true), (0, false) }, o.Btns.Where(b => b.Button == 0));
    }

    [Fact]
    public void OnlyTheDevicesButtons_AreWritten()
    {
        var m = new JoystickMap();
        var o = new FakeOutput { Buttons = 3 };
        m.Frame(Frame(A, Hi, Hi, Hi, Hi, Hi, Hi, Hi, Hi), o);
        Assert.Equal(new[] { 0, 1, 2 }, o.Btns.Select(b => b.Button));
        Assert.Equal(0xFF, m.ButtonMask);                   // the logical state still has all 8
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]                                        // a driver error: no buttons
    public void DeviceWithoutButtons_GetsNoButtonCalls(int buttons)
    {
        var m = new JoystickMap();
        var o = new FakeOutput { Buttons = buttons };
        m.Frame(Frame(A, Hi, Hi, Hi, Hi, Hi, Hi, Hi, Hi), o);
        Assert.Empty(o.Btns);
        Assert.Equal(8, o.Axes.Count);
    }

    [Fact]
    public void MoreButtonsThanEight_StillOnlyEight()
    {
        var m = new JoystickMap();
        var o = new FakeOutput { Buttons = 128 };
        m.Frame(Frame(A, Hi, Hi, Hi, Hi, Hi, Hi, Hi, Hi), o);
        Assert.Equal(Enumerable.Range(0, 8), o.Btns.Select(b => b.Button));
    }

    [Fact]
    public void ShorterFrame_ReleasesTheButtonsItNoLongerCarries()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        m.Frame(Frame(A, Hi, Hi, Hi, Hi, Hi, Hi, Hi, Hi), o);
        o.Clear();
        m.Frame(Frame(A, Hi, Hi), o);                       // 10 channels: buttons 3-8 not carried
        Assert.Equal(new[] { (2, false), (3, false), (4, false), (5, false), (6, false), (7, false) }, o.Btns);
        Assert.Equal(0b11, m.ButtonMask);
        o.Clear();
        m.Frame(A, o);                                      // 8 channels: no buttons at all
        Assert.Equal(new[] { (0, false), (1, false) }, o.Btns);
        Assert.Equal(0, m.ButtonMask);
        Assert.Empty(o.Axes);                               // the axes didn't change
    }

    [Fact]
    public void ShorterFrame_CentersTheAxesItNoLongerCarries_Once()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        m.Frame(A, o);
        o.Clear();
        m.Frame(new[] { 1, 2, 3, 4 }, o);
        Assert.Equal(new[] { (0, 1), (1, 2), (2, 3), (3, 4), (4, Mid), (5, Mid), (6, Mid), (7, Mid) }, o.Axes);
        o.Clear();
        m.Frame(new[] { 1, 2, 3, 4 }, o);
        Assert.Empty(o.Axes);
    }

    [Fact]
    public void FailedCalls_AreRetriedOnTheNextFrame()
    {
        var m = new JoystickMap();
        var o = new FakeOutput { AxisOk = a => a != 6, ButtonOk = b => b != 1 };
        var f = Frame(A, Lo, Hi);
        m.Frame(f, o);
        o.Clear();
        m.Frame(f, o);                                      // same frame: only the failed ones again
        Assert.Equal(new[] { (6, 700) }, o.Axes);
        Assert.Equal(new[] { (1, true) }, o.Btns);
        o.AxisOk = _ => true;
        o.ButtonOk = _ => true;
        m.Frame(f, o);
        o.Clear();
        m.Frame(f, o);                                      // got through: nothing left
        Assert.Empty(o.Axes);
        Assert.Empty(o.Btns);
    }

    [Fact]
    public void Clear_ForgetsTheDeviceState_AndReleasesEverything()
    {
        // After ResetVJD the device may not show what we last wrote (sliders reset to their own default).
        var m = new JoystickMap();
        var o = new FakeOutput();
        var f = Frame(A, Hi);
        m.Frame(f, o);
        m.Clear();
        Assert.Equal(0, m.ButtonMask);
        o.Clear();
        m.Frame(f, o);
        Assert.Equal(8, o.Axes.Count);
        Assert.Equal(new[] { (0, true), (1, false), (2, false), (3, false), (4, false), (5, false), (6, false), (7, false) }, o.Btns);
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var m = new JoystickMap();
        var o = new FakeOutput();
        m.Frame(new[] { -5, 40000, 32767, 0, 1, 2, 3, 4, 40000 }, o);
        Assert.Equal((0, 0), o.Axes[0]);
        Assert.Equal((1, Hi), o.Axes[1]);
        Assert.Equal(1, m.ButtonMask);
    }

    [Fact]
    public void WithoutAnOutput_OnlyTheLogicalStateMoves()
    {
        var m = new JoystickMap();
        m.Frame(Frame(A, Hi, Lo, Hi), null);
        Assert.Equal(0b101, m.ButtonMask);
        m.Frame(Frame(A, 22000, Lo, 19000), null);          // hysteresis holds without a device too
        Assert.Equal(0b001, m.ButtonMask);
    }

    // ---- through the engine -----------------------------------------------------------------

    [Fact]
    public void Engine_ButtonMask_FollowsCh9To16()
    {
        var h = new Harness();
        var f = Enumerable.Repeat(Mid, 16).ToArray();
        f[8] = Hi;
        f[15] = Hi;
        h.Packet(Frames.Channels(f));
        Assert.Equal(0b1000_0001, h.Engine.ButtonMask);
        Assert.Equal(f, Assert.Single(h.Applied));          // the raw frame is still what is applied
        h.Engine.ResetSessionState();
        Assert.Equal(0, h.Engine.ButtonMask);
    }

    [Fact]
    public void Engine_DroppedFrames_DoNotTouchTheButtons()
    {
        var h = new Harness();
        var f = Enumerable.Repeat(Mid, 16).ToArray();
        f[8] = Hi;
        h.Packet(Frames.Channels(f));
        h.Packet(Frames.Channels(Enumerable.Repeat(0xF26A, 16).ToArray()));     // placeholder frame
        Assert.Equal(1, h.Engine.ButtonMask);
    }
}
