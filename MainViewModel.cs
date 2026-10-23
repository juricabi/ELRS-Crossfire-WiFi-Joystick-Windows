using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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

    // One entry of the Rate / Channels drop-downs: the value, and its text ("250 Hz (default)").
    sealed class RequestOption
    {
        public RequestOption(int value, string label)
        {
            Value = value;
            Label = label;
        }

        public int Value { get; }
        public string Label { get; }
        public override string ToString() => Label;
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
        private double lastHz, lastJitter;  // the last stats tick, shown again when the request changes
        private int lastCount;

        public ObservableCollection<AxisItem> Axes { get; } = new();

        // The request drop-downs: supported values only, the default marked.
        public IReadOnlyList<RequestOption> RateOptions { get; }
        public IReadOnlyList<RequestOption> ChannelOptions { get; } = BuildChannelOptions();

        public RelayCommand StartStopCommand { get; }
        public RelayCommand ConnectCommand { get; }
        public RelayCommand ApplyPortCommand { get; }
        public RelayCommand FixFirewallCommand { get; }
        public RelayCommand HelpCommand { get; }

        // Raised when the user asks for help; the view opens the Help window.
        public event Action? HelpRequested;

        public MainViewModel(int port, string? txIp, Action<Action> uiInvoke,
                             int intervalUs = StreamRequest.IntervalDefault, int channels = StreamRequest.ChannelsDefault)
        {
            ui = uiInvoke;
            PortText = port.ToString();
            ModuleIp = txIp ?? "";

            foreach (var n in AxisNames)
                Axes.Add(new AxisItem(n));

            Engine = new JoystickEngine { Port = port, ActivationIP = txIp };
            Engine.SetRequest(intervalUs, channels);           // not running yet: nothing is sent
            _selectedIntervalUs = Engine.IntervalUs;
            _selectedChannels = Engine.Channels;
            RateOptions = BuildRateOptions(_selectedIntervalUs);
            _statsText = FormatStats(0, 0, 0, Engine.IntervalUs, 0, Engine.Channels, false);

            Engine.Log += msg => ui(() => AppendLog(msg));
            Engine.StateChanged += (s, d) => ui(() => OnEngineState(s, d));
            Engine.ChannelsUpdated += ch => latestChannels = ch;   // no marshalling; timer samples it
            Engine.StatsUpdated += (hz, jit, cnt) => ui(() => OnStats(hz, jit, cnt));

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

        private string _statsText;
        public string StatsText { get => _statsText; private set => SetField(ref _statsText, value); }

        private string _buttonsText = ButtonsIdleText;
        public string ButtonsText { get => _buttonsText; private set => SetField(ref _buttonsText, value); }

        // The request, applied live through the engine (which restarts a streaming ELRS module).
        private int _selectedIntervalUs;
        public int SelectedIntervalUs
        {
            get => _selectedIntervalUs;
            set { if (SetField(ref _selectedIntervalUs, value)) ApplyRequest(); }
        }

        private int _selectedChannels;
        public int SelectedChannels
        {
            get => _selectedChannels;
            set { if (SetField(ref _selectedChannels, value)) ApplyRequest(); }
        }

        // Locked to a Crossfire module: it ignores the request, so the drop-downs are disabled.
        private bool _isCrossfire;
        public bool IsCrossfire
        {
            get => _isCrossfire;
            private set
            {
                if (!SetField(ref _isCrossfire, value)) return;
                OnChanged(nameof(IsRequestEditable));
                OnChanged(nameof(RequestNote));
                OnChanged(nameof(RequestToolTip));
            }
        }

        public bool IsRequestEditable => !IsCrossfire;

        public string RequestNote => IsCrossfire ? CrossfireNote : "";

        public string RequestToolTip => IsCrossfire
            ? CrossfireNote
            : "What the app asks the module for. ELRS (3.4.0 and later) takes a change by restarting its stream - " +
              "at once, or within 20 s for a module that has sent no beacon yet and streams 16 channels (it may be " +
              "a Crossfire). Crossfire ignores it (16 channels, about 90 Hz).";

        internal const string CrossfireNote = "Crossfire ignores these: always 16 channels at about 90 Hz";
        internal const string ButtonsIdleText = "Buttons (CH9-CH16): --";

        private string _vjoyText = "vJoy: --";
        public string VJoyText { get => _vjoyText; private set => SetField(ref _vjoyText, value); }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (!SetField(ref _isRunning, value)) return;
                OnChanged(nameof(IsPortEditable));
                OnChanged(nameof(StartStopToolTip));
            }
        }

        public bool IsPortEditable => !IsRunning;

        // What the Start/Stop button does: Stop (and closing the app) releases the module (R14).
        public string StartStopToolTip => IsRunning ? StopToolTip : StartToolTip;

        internal const string StartToolTip =
            "Start listening: the app finds the module by its broadcast, or connects to the Module IP.";
        internal const string StopToolTip =
            "Stop listening and release the module: it gets joystick_end, stops streaming to this PC and broadcasts " +
            "again, so any app can connect to it. Closing the app does the same. A module known as Crossfire by its " +
            "beacon is not sent it: it broadcasts while streaming, so other apps find it anyway.";

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

        // On closing: stops and releases the module, waiting at most 1.5 s for its answer (R14).
        public void Shutdown() => Engine.StopAndWait(700);

        private void StartEngine(bool refreshFirewall = true)
        {
            if (!int.TryParse(PortText.Trim(), out int p) || p <= 0 || p >= 65536)
            {
                AppendLog($"Invalid port \"{PortText}\" - using 1-65535.");
                return;
            }
            if (!JoystickEngine.TryParseModuleIp(ModuleIp, out string? ip))
            {
                AppendLog($"Invalid module IP \"{StreamRequest.Printable(ModuleIp.Trim())}\" - use a.b.c.d " +
                          "(the IP on the module's WiFi page), or leave it empty to auto-discover.");
                return;
            }
            Engine.Port = p;
            Engine.ActivationIP = ip;
            Engine.Start();
            IsRunning = true;
            // The rule is per-port, so a custom port needs its own indicator state.
            if (refreshFirewall) RefreshFirewallAsync();
        }

        private void ToggleEngine()
        {
            if (Engine.IsRunning)
            {
                Engine.Stop();                // releases the module (R14): it broadcasts again for any app
                Engine.ActivationIP = null;   // forget any forced IP - next Start auto-discovers (README, Help say so)
                ModuleIp = "";
            }
            else
            {
                StartEngine();
            }
        }

        // Apply the Module IP live: connect to it immediately (no Stop/Start needed). The engine checks it
        // (a.b.c.d) and logs a mistake.
        private void Connect()
        {
            if (!Engine.IsRunning)
                StartEngine();
            else
                Engine.SetTarget(ModuleIp);
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

        // ---- the request -----------------------------------------------------------------

        private void ApplyRequest()
        {
            int iv = SelectedIntervalUs, ch = SelectedChannels;
            if (iv == Engine.IntervalUs && ch == Engine.Channels) return;
            AppendLog($"Asking for {StreamRequest.Describe(iv)}, {ch} channels.");
            Engine.SetRequest(iv, ch);
            RenderStats();
        }

        // Presets, highest rate first, plus the current interval when it is not one (--interval 3500).
        internal static IReadOnlyList<RequestOption> BuildRateOptions(int currentUs)
        {
            var list = StreamRequest.IntervalPresets.ToList();
            if (!list.Contains(currentUs)) list.Add(currentUs);
            list.Sort();
            return list.Select(us => new RequestOption(us,
                StreamRequest.Describe(us) + (us == StreamRequest.IntervalDefault ? " (default)" : ""))).ToList();
        }

        internal static IReadOnlyList<RequestOption> BuildChannelOptions()
        {
            var list = new List<RequestOption>();
            for (int n = StreamRequest.ChannelsMin; n <= StreamRequest.ChannelsMax; n++)
                list.Add(new RequestOption(n, n == StreamRequest.ChannelsDefault ? $"{n} (default)" : $"{n}"));
            return list;
        }

        // "Rate: 249 Hz (asked 250)     Jitter: 0.4 ms     249 pkt/s     16 ch". Crossfire streams at its
        // own rate, so nothing was "asked" there. The channel count shows what was asked when it differs.
        internal static string FormatStats(double hz, double jitterMs, int count, int askedIntervalUs,
                                           int channels, int askedChannels, bool crossfire)
        {
            string asked = crossfire ? "Crossfire: its own rate" : $"asked {StreamRequest.IntervalHz(askedIntervalUs)}";
            if (count <= 0) return $"Rate: -- ({asked})     Jitter: --";
            string ch = channels <= 0 ? "" :
                $"     {channels} ch" + (!crossfire && channels != askedChannels ? $" (asked {askedChannels})" : "");
            return string.Format(CultureInfo.CurrentCulture, "Rate: {0:0} Hz ({1})     Jitter: {2:0.0} ms     {3} pkt/s{4}",
                                 hz, asked, jitterMs, count, ch);
        }

        // "Buttons (CH9-CH16):  1 ●  2 ○ ..." from the engine's button state (the frame's channels only).
        internal static string FormatButtons(int channels, int mask)
        {
            int n = Math.Min(JoystickMap.ButtonCount, channels - JoystickMap.FirstButtonChannel);
            if (n <= 0)
                return channels > 0 ? $"Buttons (CH9-CH16): none - the module sends {channels} channels" : ButtonsIdleText;
            var sb = new StringBuilder("Buttons (CH9-CH16):");
            for (int i = 0; i < n; i++)
                sb.Append($"   {i + 1} {((mask >> i & 1) != 0 ? '\u25CF' : '\u25CB')}");
            return sb.ToString();
        }

        private void OnStats(double hz, double jit, int cnt)
        {
            lastHz = hz;
            lastJitter = jit;
            lastCount = cnt;
            UpdateCrossfire();
            RenderStats();
        }

        private void RenderStats()
        {
            int nch = latestChannels?.Length ?? 0;
            StatsText = FormatStats(lastHz, lastJitter, lastCount, Engine.IntervalUs, nch, Engine.Channels, IsCrossfire);
        }

        // Crossfire is known from its beacon, which may come after the lock (every ~8 s): checked on every
        // state change and every stats tick (1 s).
        private void UpdateCrossfire() => IsCrossfire = IsStreaming && Engine.SourceKind == ModuleKind.Crossfire;

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
                lastCount = 0;
                foreach (var a in Axes) { a.Value = 0; a.PercentText = "--"; }
                ButtonsText = ButtonsIdleText;
            }
            else
            {
                axesActive = true;
            }
            UpdateCrossfire();
            RenderStats();
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
            // A channel the frame doesn't carry is centered on the joystick: show that, not a stale bar.
            for (int i = n; i < Axes.Count; i++)
            {
                Axes[i].Value = JoystickMap.Center;
                Axes[i].PercentText = "--";
            }
            ButtonsText = FormatButtons(ch.Length, Engine.ButtonMask);
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
