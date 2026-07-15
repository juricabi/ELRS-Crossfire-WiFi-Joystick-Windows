using System;
using System.Diagnostics;

namespace ELRSWifiJoystick
{
    // Manages the Windows Firewall rule that lets the inbound UDP joystick stream through.
    // Without it, Windows silently drops the data even though activation succeeds - the #1
    // support issue. Adding the rule needs admin, so we relaunch ourselves elevated (one UAC
    // prompt); after that the rule persists and we never prompt again.
    static class FirewallHelper
    {
        public const string RuleName = "ELRS WiFi Joystick";

        public static bool RuleExists() => RuleExists(RuleName);

        internal static bool RuleExists(string ruleName)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh",
                    $"advfirewall firewall show rule name=\"{ruleName}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p!.StandardOutput.ReadToEnd(); // drain so netsh can't block on a full pipe
                p.WaitForExit();
                // Use the exit code (0 = rule found, 1 = no match): netsh's text output is
                // localized, so string matching breaks on non-English Windows.
                return p.ExitCode == 0;
            }
            catch
            {
                return false; // if we can't tell, let the caller try to add it
            }
        }

        // Ensures the rule exists, relaunching elevated (UAC) if needed.
        // Returns true if the rule exists afterwards. onLog receives progress messages.
        public static bool EnsureAllowed(int port, Action<string>? onLog = null)
        {
            try
            {
                if (RuleExists())
                    return true;

                onLog?.Invoke("Firewall: requesting permission to allow inbound joystick data (UAC prompt)...");
                string exe = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "ELRSWifiJoystick.exe";
                var psi = new ProcessStartInfo(exe, $"--add-firewall-rule {port}")
                {
                    UseShellExecute = true, // required for the "runas" elevation verb
                    Verb = "runas",
                };
                using (var p = Process.Start(psi))
                {
                    p!.WaitForExit();
                }

                bool ok = RuleExists();
                onLog?.Invoke(ok
                    ? "Firewall: rule added - inbound joystick data is now allowed."
                    : "Firewall: rule not added (permission declined).");
                return ok;
            }
            catch (Exception ex)
            {
                // Usually the user cancelled the UAC prompt - not fatal.
                onLog?.Invoke($"Firewall: could not add rule automatically ({ex.Message}).");
                return false;
            }
        }

        // Runs inside the elevated helper instance (launched with --add-firewall-rule <port>).
        public static void AddRuleWorker(int port)
        {
            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
                RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" " +
                         $"dir=in action=allow protocol=UDP localport={port} profile=any");
            }
            catch { /* best-effort */ }
        }

        private static void RunNetsh(string arguments)
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p!.WaitForExit();
        }
    }
}
