using System;
using System.Threading;
using System.Windows;

namespace ELRSWifiJoystick
{
    public partial class App : Application
    {
        private Mutex? singleInstanceMutex;
        private MainWindow? mainWindow;

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

            // Optional args: [port] [--tx <module-ip>] [--rate HZ | --interval US] [--channels N]
            var opt = AppArgs.Parse(args);
            if (opt.Errors.Count > 0)
                MessageBox.Show(
                    "Command line:\n\n" + string.Join("\n", opt.Errors) + "\n\nThese were ignored. " + opt.Summary,
                    "ELRS / Crossfire WiFi Joystick", MessageBoxButton.OK, MessageBoxImage.Warning);

            mainWindow = new MainWindow(opt.Port, opt.TxIp, opt.IntervalUs, opt.Channels);
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        // A logoff, restart or shutdown with the app running (often in the tray): the window's Closing is not raised
        // then, and Windows may end the process soon after this returns. So stop and release the module here, like
        // closing the app (R14): it waits at most 1.5 s for the module's answer.
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            if (!e.Cancel) mainWindow?.EndSession();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned (second instance) */ }
            singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
