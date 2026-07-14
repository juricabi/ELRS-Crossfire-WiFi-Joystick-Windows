using System;
using System.Threading;
using System.Windows.Forms;

namespace ELRSWifiJoystick
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Elevated helper mode: a UAC-elevated copy of ourselves adds the firewall rule
            // and exits. No UI is shown. Handled first, before anything else.
            int fwIdx = Array.IndexOf(args, "--add-firewall-rule");
            if (fwIdx >= 0)
            {
                int fwPort = 11000;
                if (fwIdx + 1 >= args.Length || !int.TryParse(args[fwIdx + 1], out fwPort) || fwPort <= 0)
                    fwPort = 11000;
                FirewallHelper.AddRuleWorker(fwPort);
                return;
            }

            // Single instance only - a second copy would fight over UDP port 11000 and the
            // vJoy device. Tell the user instead of failing cryptically.
            using var mutex = new Mutex(true, "ELRSWifiJoystick_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show(
                    "ELRS / TBS Crossfire WiFi Joystick is already running.\n\nCheck the system tray (bottom-right).",
                    "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Parse optional args: [port] [--tx <module-ip>]
            int listenPort = 11000;
            string? txIp = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--tx" || a == "--crossfire" || a == "--activate") && i + 1 < args.Length)
                    txIp = args[++i];
                else if (int.TryParse(a, out int p) && p > 0 && p < 65536)
                    listenPort = p;
            }

            // Per-monitor DPI awareness so the UI is crisp (not bitmap-scaled/blurry) on
            // scaled displays (125/150/200%).
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(listenPort, txIp));
        }
    }
}
