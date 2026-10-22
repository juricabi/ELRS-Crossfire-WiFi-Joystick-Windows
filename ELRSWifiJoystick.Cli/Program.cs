using System;
using System.IO;
using System.Threading;

namespace ELRSWifiJoystick
{
    // Console front-end over the exact same JoystickEngine the GUI uses: discovery,
    // activation, the request (rate/channels), single-source lock, vJoy output, firewall
    // handling, stats - just rendered as text. Trimmed self-contained exe (~9 MB), fast start.
    static class CliProgram
    {
        private static readonly object gate = new();
        private static volatile int[]? latest;
        private static double rate, jitter;
        private static volatile bool streaming;
        private static bool live;              // interactive console (not redirected)
        private static bool statusShown;

        static int Main(string[] args)
        {
            // Elevated helper mode: a UAC-elevated copy of ourselves adds the firewall
            // rule and exits (same contract as the GUI).
            int fwIdx = Array.IndexOf(args, "--add-firewall-rule");
            if (fwIdx >= 0)
            {
                int fwPort = 11000;
                if (fwIdx + 1 >= args.Length || !int.TryParse(args[fwIdx + 1], out fwPort) || fwPort <= 0)
                    fwPort = 11000;
                FirewallHelper.AddRuleWorker(fwPort);
                return 0;
            }

            var opt = CliOptions.Parse(args);
            if (opt.Error != null)
            {
                Console.Error.WriteLine(opt.Error);
                PrintUsage();
                return 2;
            }
            if (opt.Help) { PrintUsage(); return 0; }
            int port = opt.Port;

            // Shared with the GUI on purpose: both would fight over the UDP port and the
            // vJoy device, so only one of the two may run.
            using var mutex = new Mutex(true, "ELRSWifiJoystick_SingleInstance", out bool isNew);
            if (!isNew)
            {
                Console.Error.WriteLine("ELRS WiFi Joystick is already running (GUI or CLI). Close it first.");
                return 1;
            }

            live = !Console.IsOutputRedirected;
            try { Console.Title = "ELRS / TBS Crossfire WiFi Joystick (CLI)"; } catch { }

            WriteLine(ConsoleColor.Cyan, $"ELRS / TBS Crossfire WiFi Joystick CLI v3.3  -  listening on UDP {port}");
            WriteLine(ConsoleColor.Gray, "  Features : auto-discovery (ELRS + Crossfire/Tracer) | single-source lock");
            WriteLine(ConsoleColor.Gray, "             vJoy: CH1-8 axes, CH9-16 buttons 1-8 | rate & jitter stats | firewall auto-rule");
            WriteLine(ConsoleColor.Gray, "             ELRS streams at the rate asked for: 250 Hz by default, adjustable (r, --rate)");
            WriteLine(ConsoleColor.Gray, "             auto-reconnect when the module drops and comes back");
            WriteLine(ConsoleColor.Gray, $"  Request  : {StreamRequest.Describe(opt.IntervalUs)}, {opt.Channels} channels" +
                                         "  (Crossfire ignores it: always 16 channels, about 90 Hz)");
            WriteLine(ConsoleColor.Gray, "  Keys     : [s] start/stop  [t] module IP / connect  [p] port  [r] rate  [c] channels");
            WriteLine(ConsoleColor.Gray, "             [a] restart stream  [f] firewall  [q] quit");
            WriteLine(ConsoleColor.DarkGray, "  Ctrl+C also quits. --help for command-line options.");

            var engine = new JoystickEngine { Port = port, ActivationIP = opt.TxIp };
            engine.SetRequest(opt.IntervalUs, opt.Channels);
            engine.Log += msg => Log(msg);
            engine.StateChanged += (s, d) =>
            {
                streaming = s == EngineState.Streaming;
                if (!streaming) { latest = null; rate = 0; }
                var c = s switch
                {
                    EngineState.Streaming => ConsoleColor.Green,
                    EngineState.Searching => ConsoleColor.Yellow,
                    EngineState.Error => ConsoleColor.Red,
                    _ => ConsoleColor.Gray,
                };
                Log($"[{s}] {d}", c);
            };
            engine.ChannelsUpdated += ch => latest = ch;
            engine.StatsUpdated += (hz, jit, cnt) => { rate = hz; jitter = jit; };

            if (!opt.NoFirewall)
                FirewallHelper.EnsureAllowed(port, m => Log(m));

            var quit = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };

            engine.Start();

            // Same commands two ways: single keys on an interactive console, or lines on
            // a redirected stdin ("s" / "t <ip>" / "rate 250" / "q") so the CLI is scriptable.
            bool keys = !Console.IsInputRedirected;
            if (!keys)
            {
                new Thread(() =>
                {
                    // Console.In with BOM detection added: a writer that sends a UTF-8 BOM
                    // (PowerShell 5.1 / .NET Framework StandardInput) would otherwise garble
                    // the first command, so "q" arrived as an unknown command.
                    using var input = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding,
                                                       detectEncodingFromByteOrderMarks: true);
                    string? line;
                    while ((line = input.ReadLine()) != null)
                    {
                        var (verb, arg) = CliCommand.Parse(line);
                        switch (verb)
                        {
                            case "quit": quit.Set(); return;
                            case "toggle": ToggleEngine(engine); break;
                            case "start": if (!engine.IsRunning) ToggleEngine(engine); break;
                            case "stop": if (engine.IsRunning) ToggleEngine(engine); break;
                            case "target": SetTargetCmd(engine, arg); break;
                            case "port": SetPortCmd(engine, arg); break;
                            case "firewall": FixFirewall(engine); break;
                            case "rate": CliRequest.SetRate(engine, arg, m => Log(m)); break;
                            case "interval": CliRequest.SetInterval(engine, arg, m => Log(m)); break;
                            case "channels": CliRequest.SetChannels(engine, arg, m => Log(m)); break;
                            case "restart": engine.RestartStream(); break;
                            case "status": Log(StatusText(engine) is { Length: > 0 } st ? st.Trim() : "Not streaming."); break;
                            case "": break;
                            default: Log($"Unknown command: {line.Trim()}"); break;
                        }
                    }
                }) { IsBackground = true }.Start();
            }

            // Interactive: live status line at 5 Hz. Redirected: a stats line every 2 s.
            while (!quit.Wait(live ? 200 : 2000))
            {
                DrawStatus(engine);
                while (keys && Console.KeyAvailable)
                {
                    char k = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                    if (k == 'q') quit.Set();
                    else if (k == 's') ToggleEngine(engine);
                    else if (k == 't') PromptTarget(engine);
                    else if (k == 'p') PromptPort(engine);
                    else if (k == 'r') PromptRate(engine);
                    else if (k == 'c') PromptChannels(engine);
                    else if (k == 'a') engine.RestartStream();
                    else if (k == 'f') FixFirewall(engine);
                }
            }

            ClearStatusLine();
            Log("Stopping... (the module keeps broadcasting until powered off)");
            engine.StopAndWait(700);
            return 0;
        }

        // 's' - mirror of the GUI Start/Stop button, including its "stop clears the
        // forced target" behaviour so the next start is a clean auto-discover.
        private static void ToggleEngine(JoystickEngine engine)
        {
            if (engine.IsRunning)
            {
                engine.Stop();
                engine.ActivationIP = null;
                Log("Stopped. Press 's' to start again (auto-discover) or 't' to target a module IP.");
            }
            else
            {
                engine.Start();
            }
        }

        // 't' - mirror of the GUI Module IP box + Connect button.
        private static void PromptTarget(JoystickEngine engine)
        {
            SetTargetCmd(engine, Prompt("Module IP (empty = auto-discover): "));
        }

        private static void SetTargetCmd(JoystickEngine engine, string? text)
        {
            if (engine.IsRunning)
                engine.SetTarget(text);         // checks it (a.b.c.d) and says so
            else if (JoystickEngine.TryParseModuleIp(text, out string? ip))
            {
                engine.ActivationIP = ip;
                engine.Start();
            }
            else
                Log($"Invalid module IP \"{StreamRequest.Printable((text ?? "").Trim())}\" - use a.b.c.d", ConsoleColor.Yellow);
        }

        // 'p' - mirror of the GUI Port box: editable only while stopped, and applying
        // it starts the engine (like pressing Enter in the box).
        private static void PromptPort(JoystickEngine engine)
        {
            if (engine.IsRunning)
            {
                Log("Stop first ('s') - the port can only be changed while stopped.");
                return;
            }
            SetPortCmd(engine, Prompt("UDP port (1-65535): "));
        }

        private static void SetPortCmd(JoystickEngine engine, string? text)
        {
            if (engine.IsRunning)
            {
                Log("Stop first ('s') - the port can only be changed while stopped.");
                return;
            }
            if (!int.TryParse(text?.Trim(), out int p) || p <= 0 || p >= 65536)
            {
                Log($"Invalid port \"{text?.Trim()}\" - use 1-65535.");
                return;
            }
            engine.Port = p;
            Log($"Port set to {p}.");
            // The firewall rule is per-port, same as the GUI's indicator refresh.
            if (!FirewallHelper.RuleExists(p))
                Log("Firewall: no rule for this port yet - press 'f' to add it.", ConsoleColor.Yellow);
            engine.Start();
        }

        // 'r' - mirror of the GUI Rate drop-down (any 10-1000 Hz here). Empty input cancels.
        private static void PromptRate(JoystickEngine engine)
        {
            string? text = Prompt($"Rate in Hz (1000 500 333 250 200 150 100 50, any 10-1000; now " +
                                  $"{StreamRequest.Describe(engine.IntervalUs)}, empty = cancel): ");
            if (string.IsNullOrWhiteSpace(text)) Log("Rate unchanged.");
            else CliRequest.SetRate(engine, text, m => Log(m));
        }

        // 'c' - mirror of the GUI Channels drop-down. Empty input cancels.
        private static void PromptChannels(JoystickEngine engine)
        {
            string? text = Prompt($"Channels (4-16; now {engine.Channels}, empty = cancel): ");
            if (string.IsNullOrWhiteSpace(text)) Log("Channels unchanged.");
            else CliRequest.SetChannels(engine, text, m => Log(m));
        }

        private static string? Prompt(string question)
        {
            ClearStatusLine();
            lock (gate)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(question);
                Console.ResetColor();
            }
            return Console.ReadLine();
        }

        // 'f' - mirror of the GUI "Allow in Firewall" button.
        private static void FixFirewall(JoystickEngine engine)
        {
            FirewallHelper.EnsureAllowed(engine.Port, m => Log(m));
            bool ok = FirewallHelper.RuleExists(engine.Port);
            Log(ok ? "Firewall: allowed - joystick data can get through."
                   : "Firewall: rule still missing - data may be blocked.",
                ok ? ConsoleColor.Green : ConsoleColor.Yellow);
        }

        private static string StatusText(JoystickEngine engine)
        {
            var ch = latest;
            if (!streaming || ch == null) return "";
            return CliRequest.FormatStatus(rate, jitter, ch, StreamRequest.IntervalHz(engine.IntervalUs),
                                           engine.SourceKind == ModuleKind.Crossfire, engine.ButtonMask);
        }

        private static void DrawStatus(JoystickEngine engine)
        {
            string s = StatusText(engine);
            if (s.Length == 0) { if (live) ClearStatusLine(); return; }
            lock (gate)
            {
                if (live)
                {
                    // One line, never wrapped: a wrapped line can't be redrawn in place.
                    int w = SafeWidth() - 1;
                    if (s.Length > w) s = s.Substring(0, w);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("\r" + s.PadRight(w));
                    Console.ResetColor();
                    statusShown = true;
                }
                else
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
                }
            }
        }

        private static void ClearStatusLine()
        {
            if (!live) return;
            lock (gate)
            {
                if (statusShown)
                {
                    Console.Write("\r" + new string(' ', SafeWidth() - 1) + "\r");
                    statusShown = false;
                }
            }
        }

        private static void Log(string msg, ConsoleColor color = ConsoleColor.Gray)
        {
            lock (gate)
            {
                if (live && statusShown)
                {
                    Console.Write("\r" + new string(' ', SafeWidth() - 1) + "\r");
                    statusShown = false;
                }
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{DateTime.Now:HH:mm:ss}  ");
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }

        private static void WriteLine(ConsoleColor color, string msg)
        {
            lock (gate)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }

        private static int SafeWidth()
        {
            try { return Math.Max(40, Console.BufferWidth); } catch { return 80; }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"ELRS / TBS Crossfire WiFi Joystick CLI

Usage: ELRSWifiJoystickCli [port] [--tx <module-ip>] [--rate HZ | --interval US]
                           [--channels N] [--no-firewall]

  port            UDP port to listen on (default 11000)
  --tx <ip>       Activate a specific module immediately instead of waiting
                  for its discovery beacon (aliases: --crossfire, --activate)
  --rate HZ       Updates per second to ask the module for: 1000 500 333 250
                  200 150 100 50 (default 250); any 10 to 1000 works
  --interval US   The same as an interval in microseconds: 1000 to 100000
                  (default 4000 = 250 Hz)
  --channels N    Channels to ask for: 4 to 16 (default 16)
  --no-firewall   Skip the Windows Firewall check/rule
  --help          This help
An option's value may also be given as --rate=500. The module IP is an IPv4
address (a.b.c.d), as on the module's WiFi page.

ELRS (3.4.0 and later) streams the rate and channels asked for; a change while
it streams restarts its stream. Crossfire ignores both: it always sends 16
channels at about 90 Hz. CH1-CH8 are the vJoy axes (X Y RX RY RZ Z SL0 SL1),
CH9-CH16 joystick buttons 1-8 (pressed above 75 %).

While running (keys on a console, or lines on redirected stdin):
  s               Start / stop (stdin also: start, stop)
  t [ip]          Set module IP and connect (empty = auto-discover)
  p [port]        Change the UDP port (only while stopped; applying starts)
  r [hz]          Rate to ask for (stdin also: rate <hz>, interval <us>)
  c [n]           Channels to ask for (stdin also: channels <n>)
  a               Restart the module's stream with the request (stdin: restart)
  f               Re-check / add the firewall rule
  status          Print the status line (stdin only)
  q               Quit

Requires the vJoy driver (vjoystick.sourceforge.net). Put the ELRS or TBS
Crossfire/Tracer module on the same WiFi network as this PC, then pick the
vJoy device in your simulator. Same engine as the GUI version - just lighter.");
        }
    }
}
