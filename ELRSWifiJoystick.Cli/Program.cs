using System;
using System.Text;
using System.Threading;

namespace ELRSWifiJoystick
{
    // Console front-end over the exact same JoystickEngine the GUI uses: discovery,
    // activation, single-source lock, vJoy output, firewall handling, stats - just
    // rendered as text. Trimmed self-contained exe (~6 MB) with a fast start.
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

            int port = 11000;
            string? txIp = null;
            bool noFirewall = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a is "--help" or "-h" or "/?") { PrintUsage(); return 0; }
                else if ((a is "--tx" or "--crossfire" or "--activate") && i + 1 < args.Length) txIp = args[++i];
                else if (a == "--no-firewall") noFirewall = true;
                else if (int.TryParse(a, out int p) && p > 0 && p < 65536) port = p;
                else { Console.Error.WriteLine($"Unknown argument: {a}"); PrintUsage(); return 2; }
            }

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

            WriteLine(ConsoleColor.Cyan, $"ELRS / TBS Crossfire WiFi Joystick CLI v3.0  -  listening on UDP {port}");
            WriteLine(ConsoleColor.Gray, "  Features : auto-discovery (ELRS + Crossfire/Tracer) | single-source lock");
            WriteLine(ConsoleColor.Gray, "             vJoy output ~90-100 Hz | rate & jitter stats | firewall auto-rule");
            WriteLine(ConsoleColor.Gray, "             auto-reconnect when the module drops and comes back");
            WriteLine(ConsoleColor.Gray, "  Keys     : [s] start/stop  [t] module IP / connect  [p] port  [f] firewall  [q] quit");
            WriteLine(ConsoleColor.DarkGray, "  Ctrl+C also quits. --help for command-line options.");

            var engine = new JoystickEngine { Port = port, ActivationIP = txIp };
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

            if (!noFirewall)
                FirewallHelper.EnsureAllowed(port, m => Log(m));

            var quit = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };

            engine.Start();

            // Same commands two ways: single keys on an interactive console, or lines on
            // a redirected stdin ("s" / "t <ip>" / "f" / "q") so the CLI is scriptable.
            bool keys = !Console.IsInputRedirected;
            if (!keys)
            {
                new Thread(() =>
                {
                    string? line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        var parts = line.Trim().Split(' ', 2);
                        switch (parts[0].ToLowerInvariant())
                        {
                            case "q": case "quit": quit.Set(); return;
                            case "s": ToggleEngine(engine); break;
                            case "t": SetTargetCmd(engine, parts.Length > 1 ? parts[1] : null); break;
                            case "p": SetPortCmd(engine, parts.Length > 1 ? parts[1] : null); break;
                            case "f": FixFirewall(engine); break;
                            case "": break;
                            default: Log($"Unknown command: {line.Trim()}"); break;
                        }
                    }
                }) { IsBackground = true }.Start();
            }

            // Interactive: live status line at 5 Hz. Redirected: a stats line every 2 s.
            while (!quit.Wait(live ? 200 : 2000))
            {
                DrawStatus();
                while (keys && Console.KeyAvailable)
                {
                    char k = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                    if (k == 'q') quit.Set();
                    else if (k == 's') ToggleEngine(engine);
                    else if (k == 't') PromptTarget(engine);
                    else if (k == 'p') PromptPort(engine);
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
            ClearStatusLine();
            lock (gate)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("Module IP (empty = auto-discover): ");
                Console.ResetColor();
            }
            SetTargetCmd(engine, Console.ReadLine());
        }

        private static void SetTargetCmd(JoystickEngine engine, string? ip)
        {
            ip = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
            if (engine.IsRunning)
                engine.SetTarget(ip);
            else
            {
                engine.ActivationIP = ip;
                engine.Start();
            }
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
            ClearStatusLine();
            lock (gate)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("UDP port (1-65535): ");
                Console.ResetColor();
            }
            SetPortCmd(engine, Console.ReadLine());
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

        // 'f' - mirror of the GUI "Allow in Firewall" button.
        private static void FixFirewall(JoystickEngine engine)
        {
            FirewallHelper.EnsureAllowed(engine.Port, m => Log(m));
            bool ok = FirewallHelper.RuleExists(engine.Port);
            Log(ok ? "Firewall: allowed - joystick data can get through."
                   : "Firewall: rule still missing - data may be blocked.",
                ok ? ConsoleColor.Green : ConsoleColor.Yellow);
        }

        private static string StatusText()
        {
            var ch = latest;
            if (!streaming || ch == null) return "";
            var sb = new StringBuilder();
            sb.Append($"  {rate,3:0} Hz  {jitter,4:0.0} ms  |");
            string[] n = { "R", "P", "T", "Y", "A1", "A2", "A3", "A4" };
            for (int i = 0; i < 8 && i < ch.Length; i++)
                sb.Append($" {n[i]}:{Math.Clamp(ch[i], 0, 32767) * 100 / 32767,3}%");
            return sb.ToString();
        }

        private static void DrawStatus()
        {
            string s = StatusText();
            if (s.Length == 0) { if (live) ClearStatusLine(); return; }
            lock (gate)
            {
                if (live)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("\r" + s.PadRight(SafeWidth() - 1));
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

Usage: ELRSWifiJoystickCli [port] [--tx <module-ip>] [--no-firewall]

  port            UDP port to listen on (default 11000)
  --tx <ip>       Activate a specific module immediately instead of waiting
                  for its discovery beacon (aliases: --crossfire, --activate)
  --no-firewall   Skip the Windows Firewall check/rule
  --help          This help

While running (keys on a console, or lines on redirected stdin):
  s               Start / stop
  t [ip]          Set module IP and connect (empty = auto-discover)
  p [port]        Change the UDP port (only while stopped; applying starts)
  f               Re-check / add the firewall rule
  q               Quit

Requires the vJoy driver (vjoystick.sourceforge.net). Put the ELRS or TBS
Crossfire/Tracer module on the same WiFi network as this PC, then pick the
vJoy device in your simulator. Same engine as the GUI version - just lighter.");
        }
    }
}
