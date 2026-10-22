using System.Collections.Generic;

namespace ELRSWifiJoystick
{
    // The GUI's command line: [port] [--tx <module-ip>] [--rate HZ | --interval US] [--channels N].
    // WPF-free, so it is unit-tested. As before, anything unknown is ignored; a bad value of a known
    // option is reported (the GUI shows it) and that setting keeps its default.
    sealed class AppArgs
    {
        public int Port = 11000;
        public string? TxIp;
        public int IntervalUs = StreamRequest.IntervalDefault;
        public int Channels = StreamRequest.ChannelsDefault;
        public readonly List<string> Errors = new();

        public static AppArgs Parse(IReadOnlyList<string> args)
        {
            var o = new AppArgs();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a is "--tx" or "--crossfire" or "--activate")
                {
                    if (i + 1 < args.Count) o.TxIp = args[++i];
                }
                else if (a is "--rate" or "--interval" or "--channels")
                {
                    if (i + 1 >= args.Count)
                    {
                        o.Errors.Add($"{a} needs a value - use " +
                                     (a == "--rate" ? StreamRequest.RateUse :
                                      a == "--interval" ? StreamRequest.IntervalUse : StreamRequest.ChannelsUse));
                        continue;
                    }
                    string v = args[++i];
                    string err;
                    bool ok = a switch
                    {
                        "--rate" => StreamRequest.TryParseRate(v, out int iv, out err) && Set(ref o.IntervalUs, iv),
                        "--interval" => StreamRequest.TryParseInterval(v, out int iv2, out err) && Set(ref o.IntervalUs, iv2),
                        _ => StreamRequest.TryParseChannels(v, out int ch, out err) && Set(ref o.Channels, ch),
                    };
                    if (!ok) o.Errors.Add($"{a}: {err}");
                }
                else if (int.TryParse(a, out int p) && p > 0 && p < 65536)
                {
                    o.Port = p;
                }
            }
            return o;
        }

        private static bool Set(ref int field, int value)
        {
            field = value;
            return true;
        }
    }
}
