using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ELRSWifiJoystick
{
    // "The request": what the activation POST (joystick_begin) asks the module for - an update interval
    // and a channel count. The same supported values and defaults as the Linux version and sticktime-pi
    // (core/wifi_proto.h), shared by the engine, the GUI and the CLI.
    //
    // ELRS (3.4.0 and later) takes the interval (below 1000 us it falls back to 10000) and channels 1..16,
    // but only while it is not streaming: a joystick_begin to a streaming module just moves the stream.
    // So a new request reaches a streaming ELRS module only as joystick_end + joystick_begin (a restart).
    // Crossfire starts on the same POST but ignores both (always 16 channels, about 90 Hz).
    static class StreamRequest
    {
        public const int IntervalMin = 1000;        // ELRS turns anything below 1000 us into 10000
        public const int IntervalMax = 100000;      // 10 Hz: slower than this makes no sense for sticks
        public const int IntervalDefault = 4000;    // 250 Hz
        public const int ChannelsMin = 4;           // the protocol's frame minimum
        public const int ChannelsMax = 16;
        public const int ChannelsDefault = 16;
        public const int CrossfireChannels = 16;    // Crossfire always sends 16
        public const int RateMin = 10;              // = IntervalMax
        public const int RateMax = 1000;            // = IntervalMin

        // The rates a UI offers, as intervals: 1000, 500, 333, 250, 200, 150, 100 and 50 Hz.
        public static readonly IReadOnlyList<int> IntervalPresets = new[] { 1000, 2000, 3000, 4000, 5000, 6667, 10000, 20000 };

        public const string RateUse = "1000, 500, 333, 250, 200, 150, 100 or 50 (any 10 to 1000 works)";
        public const string IntervalUse = "1000 to 100000";
        public const string ChannelsUse = "4 to 16";

        public static int ClampInterval(int us) => Math.Clamp(us, IntervalMin, IntervalMax);
        public static int ClampChannels(int n) => Math.Clamp(n, ChannelsMin, ChannelsMax);
        public static bool IsValidInterval(int us) => us >= IntervalMin && us <= IntervalMax;
        public static bool IsValidChannels(int n) => n >= ChannelsMin && n <= ChannelsMax;
        public static bool IsPreset(int us) => IntervalPresets.Contains(us);

        // The rate a UI shows for an interval: 1000000 / us, rounded (4000 -> 250, 3000 -> 333, 6667 -> 150).
        public static int IntervalHz(int us) => us > 0 ? (int)((1000000L + us / 2) / us) : 0;

        // A rate in Hz -> its interval: a preset's own interval (333 -> 3000, 150 -> 6667), else rounded.
        // 0 when the rate is outside RateMin..RateMax.
        public static int RateToInterval(int hz)
        {
            if (hz < RateMin || hz > RateMax) return 0;
            foreach (int p in IntervalPresets)
                if (IntervalHz(p) == hz) return p;
            return (1000000 + hz / 2) / hz;
        }

        // "250 Hz" for a preset, "286 Hz (3500 us)" otherwise (the rounded rate alone would hide the interval).
        public static string Describe(int intervalUs) =>
            IsPreset(intervalUs) ? $"{IntervalHz(intervalUs)} Hz" : $"{IntervalHz(intervalUs)} Hz ({intervalUs} us)";

        public static bool TryParseRate(string? text, out int intervalUs, out string error)
        {
            intervalUs = 0;
            if (!TryParseNumber(text, RateMin, RateMax, RateUse, out long v, out error)) return false;
            intervalUs = RateToInterval((int)v);
            return true;
        }

        public static bool TryParseInterval(string? text, out int intervalUs, out string error)
        {
            intervalUs = 0;
            if (!TryParseNumber(text, IntervalMin, IntervalMax, IntervalUse, out long v, out error)) return false;
            intervalUs = (int)v;
            return true;
        }

        public static bool TryParseChannels(string? text, out int channels, out string error)
        {
            channels = 0;
            if (!TryParseNumber(text, ChannelsMin, ChannelsMax, ChannelsUse, out long v, out error)) return false;
            channels = (int)v;
            return true;
        }

        // A plain decimal integer (surrounding spaces allowed). The error names the supported values,
        // in the same words as the Linux version's.
        private static bool TryParseNumber(string? text, long min, long max, string use, out long value, out string error)
        {
            string t = Printable(text?.Trim() ?? "");
            if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                error = $"\"{t}\" is not a number - use {use}";
                return false;
            }
            if (value < min || value > max)
            {
                error = $"{t} is not supported - use {use}";
                return false;
            }
            error = "";
            return true;
        }

        // Keep an error line one clean line whatever was typed.
        private static string Printable(string s)
        {
            if (s.Length > 40) s = s.Substring(0, 40);
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (c[i] < 0x20 || c[i] == 0x7f) c[i] = '?';
            return new string(c);
        }
    }
}
