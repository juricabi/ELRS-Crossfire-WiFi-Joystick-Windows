using System;
using System.Threading;
using System.Windows;

namespace ELRSWifiJoystick
{
    public partial class App : Application
    {
        private Mutex? singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // A silent vanish is the worst failure mode for testers. Surface UI-thread
            // exceptions and keep running - the engine thread has its own handling, so
            // the joystick stream survives a cosmetic UI hiccup.
            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                MessageBox.Show(
                    "Unexpected error:\n\n" + ex.Exception.Message,
                    "ELRS / Crossfire WiFi Joystick", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Elevated helper mode: a UAC-elevated copy of ourselves adds the firewall rule
            // and exits. No UI is shown. Handled first, before anything else.
            string[] args = e.Args;
            int fwIdx = Array.IndexOf(args, "--add-firewall-rule");
            if (fwIdx >= 0)
            {
                int fwPort = 11000;
                if (fwIdx + 1 >= args.Length || !int.TryParse(args[fwIdx + 1], out fwPort) || fwPort <= 0)
                    fwPort = 11000;
                FirewallHelper.AddRuleWorker(fwPort);
                Shutdown();
                return;
            }

            // Single instance only - a second copy would fight over UDP port 11000 and the
            // vJoy device. Tell the user instead of failing cryptically.
            singleInstanceMutex = new Mutex(true, "ELRSWifiJoystick_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show(
                    "ELRS / TBS Crossfire WiFi Joystick is already running.\n\nCheck the system tray (bottom-right).",
                    "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
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

            var window = new MainWindow(listenPort, txIp);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned (second instance) */ }
            singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
