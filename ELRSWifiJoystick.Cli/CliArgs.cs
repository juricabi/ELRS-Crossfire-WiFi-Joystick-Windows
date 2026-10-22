using System;
using System.Collections.Generic;
using System.Text;

namespace ELRSWifiJoystick
{
    // The CLI's command line, parsed without any I/O (the test project compiles this file too).
    sealed class CliOptions
    {
        public int Port = 11000;
        public string? TxIp;
        public bool NoFirewall;
        public bool Help;
        public int IntervalUs = StreamRequest.IntervalDefault;
        public int Channels = StreamRequest.ChannelsDefault;
        public string? Error;          // bad command line: print it and the usage, exit code 2

        // In order, like before: --help wins only if nothing before it was wrong. --rate and --interval
        // set the same thing, so the later one wins.
        public static CliOptions Parse(IReadOnlyList<string> args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string? v = null;
                bool needsValue = a is "--tx" or "--crossfire" or "--activate" or "--rate" or "--interval" or "--channels";
                if (needsValue)
                {
                    if (i + 1 >= args.Count) return o.Fail($"{a} needs a value" + Use(a));
                    v = args[++i];
                }
                string err;
                switch (a)
                {
                    case "--help": case "-h": case "/?":
                        o.Help = true;
                        return o;
                    case "--tx": case "--crossfire": case "--activate":
                        o.TxIp = v;
                        break;
                    case "--no-firewall":
                        o.NoFirewall = true;
                        break;
                    case "--rate":
                        if (!StreamRequest.TryParseRate(v, out o.IntervalUs, out err)) return o.Fail($"--rate: {err}");
                        break;
                    case "--interval":
                        if (!StreamRequest.TryParseInterval(v, out o.IntervalUs, out err)) return o.Fail($"--interval: {err}");
                        break;
                    case "--channels":
                        if (!StreamRequest.TryParseChannels(v, out o.Channels, out err)) return o.Fail($"--channels: {err}");
                        break;
                    default:
                        if (int.TryParse(a, out int p) && p > 0 && p < 65536) o.Port = p;
                        else return o.Fail($"Unknown argument: {a}");
                        break;
                }
            }
            return o;
        }

        private static string Use(string option) => option switch
        {
            "--rate" => " - use " + StreamRequest.RateUse,
            "--interval" => " - use " + StreamRequest.IntervalUse,
            "--channels" => " - use " + StreamRequest.ChannelsUse,
            _ => " (the module's IP)",
        };

        private CliOptions Fail(string error)
        {
            Error = error;
            return this;
        }
    }

    // One command: a line of redirected stdin ("rate 250"), or a key with what its prompt read.
    static class CliCommand
    {
        // -> (verb, argument). Verbs: quit, toggle, start, stop, target, port, firewall, rate, interval,
        // channels, restart, status, "" (blank line); anything else is "?" + the word.
        public static (string Verb, string? Arg) Parse(string line)
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return ("", null);
            string w = parts[0].ToLowerInvariant();
            string verb = w switch
            {
                "q" or "quit" or "exit" => "quit",
                "s" => "toggle",
                "start" => "start",
                "stop" => "stop",
                "t" or "target" => "target",
                "p" or "port" => "port",
                "f" or "firewall" => "firewall",
                "r" or "rate" => "rate",
                "i" or "interval" => "interval",
                "c" or "channels" => "channels",
                "a" or "restart" => "restart",
                "status" => "status",
                _ => "?" + w,
            };
            return (verb, parts.Length > 1 ? parts[1].Trim() : null);
        }
    }

    // The request commands (keys r/c, stdin rate/interval/channels): validate, say what is asked for,
    // then hand it to the engine (which restarts a streaming ELRS module, or notes that Crossfire ignores it).
    static class CliRequest
    {
        public static void SetRate(JoystickEngine e, string? text, Action<string> log)
        {
            if (StreamRequest.TryParseRate(text, out int iv, out string err)) Apply(e, iv, e.Channels, log);
            else log($"Rate: {err}");
        }

        public static void SetInterval(JoystickEngine e, string? text, Action<string> log)
        {
            if (StreamRequest.TryParseInterval(text, out int iv, out string err)) Apply(e, iv, e.Channels, log);
            else log($"Interval: {err}");
        }

        public static void SetChannels(JoystickEngine e, string? text, Action<string> log)
        {
            if (StreamRequest.TryParseChannels(text, out int ch, out string err)) Apply(e, e.IntervalUs, ch, log);
            else log($"Channels: {err}");
        }

        private static void Apply(JoystickEngine e, int iv, int ch, Action<string> log)
        {
            string what = $"{StreamRequest.Describe(iv)}, {ch} channels";
            if (iv == e.IntervalUs && ch == e.Channels)
            {
                log($"Already asking for {what}.");
                return;
            }
            log($"Asking for {what}.");
            e.SetRequest(iv, ch);
        }

        // The status line: measured rate / asked rate (Crossfire: its own), jitter, CH1-8 in %, and the
        // CH9-16 buttons ("B:1--4----": the number when pressed) when the frame carries them.
        public static string FormatStatus(double rate, double jitter, int[] ch, int askedHz, bool crossfire, int buttonMask)
        {
            var sb = new StringBuilder();
            sb.Append(crossfire ? $"  {rate,3:0} Hz XF" : $"  {rate,3:0}/{askedHz} Hz");
            sb.Append($"  {jitter,4:0.0} ms  |");
            string[] n = { "R", "P", "T", "Y", "A1", "A2", "A3", "A4" };
            for (int i = 0; i < 8 && i < ch.Length; i++)
                sb.Append($" {n[i]}:{Math.Clamp(ch[i], 0, 32767) * 100 / 32767,3}%");
            int buttons = Math.Min(JoystickMap.ButtonCount, ch.Length - JoystickMap.FirstButtonChannel);
            if (buttons > 0)
            {
                sb.Append("  B:");
                for (int i = 0; i < buttons; i++)
                    sb.Append((buttonMask >> i & 1) != 0 ? (char)('1' + i) : '-');
            }
            return sb.ToString();
        }
    }
}
