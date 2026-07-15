using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ELRSWifiJoystick
{
    // One axis row shown in the UI. Raises change notifications only when the value
    // actually changes, so a steady stick costs no UI work.
    class AxisItem : INotifyPropertyChanged
    {
        public string Name { get; }
        public AxisItem(string name) => Name = name;

        private int _value;
        public int Value
        {
            get => _value;
            set { if (_value != value) { _value = value; OnChanged(); } }
        }

        private string _percentText = "--";
        public string PercentText
        {
            get => _percentText;
            set { if (_percentText != value) { _percentText = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // The main view model. Deliberately free of WPF types (except ICommand via
    // RelayCommand) so it is fully unit-testable: the view supplies a UI-thread
    // marshaller and subscribes to plain INotifyPropertyChanged.
    class MainViewModel : INotifyPropertyChanged
    {
        private static readonly string[] AxisNames =
            { "Roll  (X)", "Pitch (Y)", "Throttle (RX)", "Yaw (RY)", "Aux 1 (RZ)", "Aux 2 (Z)", "Aux 3 (SL0)", "Aux 4 (SL1)" };

        internal JoystickEngine Engine { get; }
        private readonly Action<Action> ui;

        private readonly List<string> logLines = new();
        private const int LOG_MAX_LINES = 400;

        private volatile int[]? latestChannels;
        private bool axesActive;

        public ObservableCollection<AxisItem> Axes { get; } = new();

        public RelayCommand StartStopCommand { get; }
        public RelayCommand ConnectCommand { get; }
        public RelayCommand ApplyPortCommand { get; }
        public RelayCommand FixFirewallCommand { get; }
        public RelayCommand HelpCommand { get; }

        // Raised when the user asks for help; the view opens the Help window.
        public event Action? HelpRequested;

        public MainViewModel(int port, string? txIp, Action<Action> uiInvoke)
        {
            ui = uiInvoke;
            PortText = port.ToString();
            ModuleIp = txIp ?? "";

            foreach (var n in AxisNames)
                Axes.Add(new AxisItem(n));

            Engine = new JoystickEngine { Port = port, ActivationIP = txIp };
            Engine.Log += msg => ui(() => AppendLog(msg));
            Engine.StateChanged += (s, d) => ui(() => OnEngineState(s, d));
            Engine.ChannelsUpdated += ch => latestChannels = ch;   // no marshalling; timer samples it
            Engine.StatsUpdated += (hz, jit, cnt) => ui(() =>
                StatsText = cnt > 0 ? $"Rate: {hz:0} Hz     Jitter: {jit:0.0} ms     {cnt} pkt/s" : "Rate: --     Jitter: --");

            StartStopCommand = new RelayCommand(ToggleEngine);
            ConnectCommand = new RelayCommand(Connect);
            ApplyPortCommand = new RelayCommand(() => { if (!Engine.IsRunning) StartEngine(); });
            FixFirewallCommand = new RelayCommand(FixFirewall);
            HelpCommand = new RelayCommand(() => HelpRequested?.Invoke());
        }

        // ---- bindable state --------------------------------------------------------------

        private EngineState _state = EngineState.Stopped;
        public EngineState State
        {
            get => _state;
            private set => SetField(ref _state, value);
        }

        private string _bannerTitle = "Starting...";
        public string BannerTitle { get => _bannerTitle; private set => SetField(ref _bannerTitle, value); }

        private string _bannerDetail = "";
        public string BannerDetail { get => _bannerDetail; private set => SetField(ref _bannerDetail, value); }

        private string _statsText = "Rate: --     Jitter: --";
        public string StatsText { get => _statsText; private set => SetField(ref _statsText, value); }

        private string _vjoyText = "vJoy: --";
        public string VJoyText { get => _vjoyText; private set => SetField(ref _vjoyText, value); }

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; private set { if (SetField(ref _isRunning, value)) OnChanged(nameof(IsPortEditable)); } }

        public bool IsPortEditable => !IsRunning;

        private bool _isStreaming;
        public bool IsStreaming { get => _isStreaming; private set => SetField(ref _isStreaming, value); }

        private bool _firewallOk = true;
        public bool FirewallOk { get => _firewallOk; private set => SetField(ref _firewallOk, value); }

        private string _portText = "11000";
        public string PortText { get => _portText; set => SetField(ref _portText, value); }

        private string _moduleIp = "";
        public string ModuleIp { get => _moduleIp; set => SetField(ref _moduleIp, value); }

        private string _logText = "";
        public string LogText { get => _logText; private set => SetField(ref _logText, value); }

        private string _trayText = "ELRS / Crossfire WiFi Joystick";
        public string TrayText { get => _trayText; private set => SetField(ref _trayText, value); }

        // ---- lifecycle -------------------------------------------------------------------

        // Called once by the view after the window is up. The engine starts immediately;
        // firewall work runs on a background thread so the first render never waits on
        // netsh or a UAC prompt.
        public void Initialize()
        {
            StartEngine(refreshFirewall: false);
            EnsureFirewallAsync();
        }

        public void Shutdown() => Engine.StopAndWait(700);

        private void StartEngine(bool refreshFirewall = true)
        {
            if (!int.TryParse(PortText.Trim(), out int p) || p <= 0 || p >= 65536)
            {
                AppendLog($"Invalid port \"{PortText}\" - using 1-65535.");
                return;
            }
            Engine.Port = p;
            Engine.ActivationIP = string.IsNullOrWhiteSpace(ModuleIp) ? null : ModuleIp.Trim();
            Engine.Start();
            IsRunning = true;
            // The rule is per-port, so a custom port needs its own indicator state.
            if (refreshFirewall) RefreshFirewallAsync();
        }

        private void ToggleEngine()
        {
            if (Engine.IsRunning)
            {
                Engine.Stop();
                Engine.ActivationIP = null;   // forget any forced IP - next Start is a clean slate
                ModuleIp = "";
            }
            else
            {
                StartEngine();
            }
        }

        // Apply the Module IP live: connect to it immediately (no Stop/Start needed).
        private void Connect()
        {
            if (!Engine.IsRunning)
                StartEngine();
            else
                Engine.SetTarget(string.IsNullOrWhiteSpace(ModuleIp) ? null : ModuleIp.Trim());
        }

        private void FixFirewall() => EnsureFirewallAsync();

        // Both helpers run netsh off the UI thread. Entered only from the UI thread, so a
        // plain bool suffices to keep one UAC prompt / check in flight at a time.
        private bool firewallBusy;

        // Passive: update the indicator for the current port (no UAC).
        private void RefreshFirewallAsync()
        {
            if (firewallBusy) return;
            int port = Engine.Port;
            Task.Run(() =>
            {
                bool ok = FirewallHelper.RuleExists(port);
                ui(() => FirewallOk = ok);
            });
        }

        // Active: add the rule if missing (one UAC prompt at most), then re-check.
        private void EnsureFirewallAsync()
        {
            if (firewallBusy) return;
            firewallBusy = true;
            int port = Engine.Port;
            Task.Run(() =>
            {
                try
                {
                    FirewallHelper.EnsureAllowed(port, msg => ui(() => AppendLog(msg)));
                    bool ok = FirewallHelper.RuleExists(port);
                    ui(() => { FirewallOk = ok; firewallBusy = false; });
                }
                catch (Exception ex)
                {
                    ui(() => { AppendLog("Firewall check failed: " + ex.Message); firewallBusy = false; });
                }
            });
        }

        // ---- engine events ---------------------------------------------------------------

        private void OnEngineState(EngineState state, string detail)
        {
            State = state;
            BannerTitle = state switch
            {
                EngineState.Streaming => "Connected - streaming",
                EngineState.Searching => "Searching for module...",
                EngineState.Error => "Error",
                _ => "Stopped",
            };
            BannerDetail = detail;
            VJoyText = Engine.DeviceId > 0 ? $"vJoy device {Engine.DeviceId}" : "vJoy: --";
            IsRunning = Engine.IsRunning;
            IsStreaming = state == EngineState.Streaming;
            TrayText = IsStreaming ? $"Streaming from {detail}" : "ELRS / Crossfire WiFi Joystick";

            // When not actively streaming, don't leave the bars frozen at the last position.
            if (!IsStreaming)
            {
                latestChannels = null;
                axesActive = false;
                foreach (var a in Axes) { a.Value = 0; a.PercentText = "--"; }
            }
            else
            {
                axesActive = true;
            }
        }

        // Called by the view's render timer (~60 fps, only while streaming and visible).
        public void TickBars()
        {
            var ch = latestChannels;
            if (ch == null || !axesActive) return;
            int n = Math.Min(Axes.Count, ch.Length);
            for (int i = 0; i < n; i++)
            {
                int v = Math.Clamp(ch[i], 0, 32767);
                Axes[i].Value = v;
                Axes[i].PercentText = $"{v * 100 / 32767}%";
            }
        }

        // ---- log -------------------------------------------------------------------------

        private void AppendLog(string msg)
        {
            logLines.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
            if (logLines.Count > LOG_MAX_LINES)
                logLines.RemoveRange(0, logLines.Count - LOG_MAX_LINES / 2);
            var sb = new StringBuilder();
            foreach (var l in logLines) sb.AppendLine(l);
            LogText = sb.ToString();
        }

        // ---- INotifyPropertyChanged --------------------------------------------------------

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnChanged(name);
            return true;
        }

        private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
