using System.Collections.Generic;

namespace ELRSWifiJoystick
{
    // The GUI's command line: [port] [--tx <module-ip>] [--rate HZ | --interval US] [--channels N], each
    // option also as --name=value (like the Linux app). WPF-free, so it is unit-tested. A bad or missing value
    // and an unknown option are reported (the GUI shows them with what it uses instead) and change nothing:
    // an option is never given the next option as its value, nor its own value taken for the port. A word
    // that is no option and no port is ignored, as before.
    sealed class AppArgs
    {
        public int Port = 11000;
        public string? TxIp;
        public int IntervalUs = StreamRequest.IntervalDefault;
        public int Channels = StreamRequest.ChannelsDefault;
        public readonly List<string> Errors = new();

        // What the app runs with, for the message that lists the errors.
        public string Summary =>
            $"Using UDP port {Port}, {StreamRequest.Describe(IntervalUs)}, {Channels} channels, " +
            (TxIp != null ? $"module {TxIp}." : "auto-discovery.");

        public static AppArgs Parse(IReadOnlyList<string> args)
        {
            var o = new AppArgs();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string? v = null;
                int eq = a.StartsWith("--") ? a.IndexOf('=') : -1;
                if (eq > 0)
                {
                    v = a.Substring(eq + 1);
                    a = a.Substring(0, eq);
                }
                if (a is "--tx" or "--crossfire" or "--activate" or "--rate" or "--interval" or "--channels")
                {
                    if (v == null)
                    {
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                        {
                            o.Errors.Add($"{a} needs a value" + Use(a));
                            continue;
                        }
                        v = args[++i];
                    }
                    bool tx = a is "--tx" or "--crossfire" or "--activate";
                    if (tx && string.IsNullOrWhiteSpace(v))
                    {
                        o.Errors.Add($"{a} needs a value" + Use(a));
                        continue;
                    }
                    string err;
                    bool ok = a switch
                    {
                        "--rate" => StreamRequest.TryParseRate(v, out int iv, out err) && Set(ref o.IntervalUs, iv),
                        "--interval" => StreamRequest.TryParseInterval(v, out int iv2, out err) && Set(ref o.IntervalUs, iv2),
                        "--channels" => StreamRequest.TryParseChannels(v, out int ch, out err) && Set(ref o.Channels, ch),
                        _ => TryModuleIp(v, out string? ip, out err) && Set(ref o.TxIp, ip),
                    };
                    if (!ok) o.Errors.Add($"{a}: {err}");
                }
                else if (a.StartsWith("-"))
                {
                    // Its value goes with it: "--channel 8" must not make 8 the port.
                    if (v == null && i + 1 < args.Count && !args[i + 1].StartsWith("-")) a += " " + args[++i];
                    else if (v != null) a = args[i];
                    o.Errors.Add($"Unknown option {a} - ignored");
                }
                else if (int.TryParse(a, out int p) && p > 0 && p < 65536)
                {
                    o.Port = p;
                }
            }
            return o;
        }

        // --tx: a.b.c.d, as the engine compares frames with it (JoystickEngine.TryParseModuleIp).
        private static bool TryModuleIp(string text, out string? ip, out string error)
        {
            error = "";
            if (JoystickEngine.TryParseModuleIp(text, out ip)) return true;
            error = $"\"{StreamRequest.Printable(text.Trim())}\" is not an IPv4 address - use a.b.c.d";
            return false;
        }

        private static string Use(string option) => option switch
        {
            "--rate" => " - use " + StreamRequest.RateUse,
            "--interval" => " - use " + StreamRequest.IntervalUse,
            "--channels" => " - use " + StreamRequest.ChannelsUse,
            _ => " (the module's IP)",
        };

        private static bool Set<T>(ref T field, T value)
        {
            field = value;
            return true;
        }
    }
}
