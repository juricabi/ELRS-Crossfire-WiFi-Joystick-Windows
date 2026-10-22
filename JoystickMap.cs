using System;

namespace ELRSWifiJoystick
{
    // The joystick device a frame is written to. Index-based so the mapping below is pure and
    // unit-tested; the vJoy implementation lives in JoystickEngine (with every other vJoy call).
    interface IJoystickOutput
    {
        int ButtonCount { get; }                    // buttons the device has (only these are written)
        bool SetAxis(int axis, int value);          // axis 0..7 = CH1..CH8
        bool SetButton(int button, bool pressed);   // button 0..7 = joystick buttons 1..8 = CH9..CH16
    }

    // Channels -> joystick. CH1..CH8 are the axes in this app's vJoy order (X, Y, RX, RY, RZ, Z, SL0, SL1),
    // the raw value 0..32767 written as it is. CH9..CH16 are joystick buttons 1..8, like the ELRS BLE
    // joystick (and the Linux version): pressed at raw >= 24576 (75 %), released below 20480 (62.5 %) -
    // the gap is hysteresis, so a switch sitting near a threshold never chatters.
    //
    // Only what changed is written (a SetAxis/SetBtn is a driver call each). The device state is
    // "unknown" after a reset (vJoy's ResetVJD need not center every axis) and after a failed call, so
    // the next frame writes those again. A channel the frame does not carry (fewer channels asked for,
    // or sent) is no longer driven: its axis goes to the center and its button is released, so nothing
    // stays held from a longer frame.
    sealed class JoystickMap
    {
        public const int AxisCount = 8;
        public const int ButtonCount = 8;
        public const int FirstButtonChannel = 8;    // CH9, 0-based
        public const int AxisMax = 32767;
        public const int Center = 16384;
        public const int PressAt = 24576;           // raw >= this: pressed (75 %)
        public const int ReleaseBelow = 20480;      // raw < this: released (62.5 %)
        private const int Unknown = -1;

        // The vJoy axis of each of CH1..CH8 (bindings in sims depend on this order: never change it).
        internal static readonly HID_USAGES[] AxisUsages =
        {
            HID_USAGES.HID_USAGE_X, HID_USAGES.HID_USAGE_Y, HID_USAGES.HID_USAGE_RX, HID_USAGES.HID_USAGE_RY,
            HID_USAGES.HID_USAGE_RZ, HID_USAGES.HID_USAGE_Z, HID_USAGES.HID_USAGE_SL0, HID_USAGES.HID_USAGE_SL1,
        };

        private readonly bool[] pressed = new bool[ButtonCount];     // logical state (hysteresis)
        private readonly int[] shownAxis = new int[AxisCount];       // what the device shows, or Unknown
        private readonly int[] shownButton = new int[ButtonCount];   // 1 / 0 / Unknown
        private volatile int mask;                                   // read by UI threads

        public JoystickMap() => Clear();

        // Bit i = button i+1 pressed (logical state, whether or not the device has that button).
        public int ButtonMask => mask;

        public static bool NextButton(bool pressed, int raw)
        {
            if (raw >= PressAt) return true;
            if (raw < ReleaseBelow) return false;
            return pressed;
        }

        // Nothing pressed, device state unknown: a new session, or right after ResetVJD.
        public void Clear()
        {
            Array.Clear(pressed);
            Array.Fill(shownAxis, Unknown);
            Array.Fill(shownButton, Unknown);
            mask = 0;
        }

        // One applied frame (raw values, 0..32767). output null: only the logical state is kept (tests,
        // or no device).
        public void Frame(int[] ch, IJoystickOutput? output)
        {
            int n = ch.Length;
            if (output != null)
            {
                for (int i = 0; i < AxisCount; i++)
                {
                    int v = i < n ? Math.Clamp(ch[i], 0, AxisMax) : Center;
                    if (shownAxis[i] == v) continue;
                    shownAxis[i] = output.SetAxis(i, v) ? v : Unknown;
                }
            }

            int buttons = output == null ? 0 : Math.Clamp(output.ButtonCount, 0, ButtonCount);
            int m = 0;
            for (int i = 0; i < ButtonCount; i++)
            {
                int c = FirstButtonChannel + i;
                bool p = c < n && NextButton(pressed[i], Math.Clamp(ch[c], 0, AxisMax));
                pressed[i] = p;
                if (p) m |= 1 << i;
                if (i >= buttons) continue;
                int want = p ? 1 : 0;
                if (shownButton[i] == want) continue;
                shownButton[i] = output!.SetButton(i, p) ? want : Unknown;
            }
            mask = m;
        }
    }
}
