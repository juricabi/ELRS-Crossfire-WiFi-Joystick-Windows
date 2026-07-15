using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ELRSWifiJoystick
{
    // View concerns only: DPI-native WPF layout, dark title bar, tray behaviour, and the
    // render timer that samples the latest channel values. All logic lives in the ViewModel.
    public partial class MainWindow : Window
    {
        private readonly MainViewModel vm;
        private readonly DispatcherTimer barTimer;

        // Minimize-to-tray only after the user has actually seen the window. If the shell
        // starts us minimized (e.g. a shortcut set to "Run: Minimized"), hiding to the tray
        // immediately would make the app appear to vanish.
        private bool trayHideArmed;

        public MainWindow(int port, string? txIp)
        {
            InitializeComponent();

            vm = new MainViewModel(port, txIp, a => Dispatcher.BeginInvoke(a));
            DataContext = vm;
            vm.HelpRequested += ShowHelp;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(vm.IsStreaming))
                    UpdateTimerState();
            };

            // ~60 fps sampling of the latest channel values; runs only while streaming AND
            // visible (see UpdateTimerState) so the app is idle-cheap and tray-cheap.
            barTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            barTimer.Tick += (s, e) => vm.TickBars();

            SourceInitialized += (s, e) => DarkTitleBar.Enable(this);
            Loaded += (s, e) => vm.Initialize();
            Activated += (s, e) => trayHideArmed = true;
            IsVisibleChanged += (s, e) => UpdateTimerState();
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized && trayHideArmed)
                    Hide();   // minimize goes to the tray
                UpdateTimerState();
            };
            Closing += (s, e) =>
            {
                barTimer.Stop();
                vm.Shutdown();
                Tray.Dispose();
            };
        }

        private void UpdateTimerState()
        {
            bool shouldRun = vm.IsStreaming && IsVisible && WindowState != WindowState.Minimized;
            if (shouldRun && !barTimer.IsEnabled) barTimer.Start();
            else if (!shouldRun && barTimer.IsEnabled) barTimer.Stop();
        }

        private void ShowHelp()
        {
            var help = new HelpWindow { Owner = this };
            help.ShowDialog();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            UpdateTimerState();
        }

        private void Tray_DoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();
        private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();
        private void TrayExit_Click(object sender, RoutedEventArgs e) => Close();

        private void LogBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => LogBox.ScrollToEnd();
    }

    // Dark window title bar on Windows 10 2004+/11 (no-op elsewhere).
    static class DarkTitleBar
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void Enable(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/11); 19 on older builds.
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { /* cosmetic only */ }
        }
    }
}
