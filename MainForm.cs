using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ELRSWifiJoystick
{
    // Renders ALL axis bars in one control (a single paint cycle per frame) - far cheaper
    // than 8 separate double-buffered controls. Static parts (names + empty tracks) are
    // pre-rendered to a bitmap; each frame only blits the moving fills and draws the values.
    class AxisView : Control
    {
        private static readonly string[] Names =
            { "Roll  (X)", "Pitch (Y)", "Throttle (RX)", "Yaw (RY)", "Aux 1 (RZ)", "Aux 2 (Z)", "Aux 3 (SL0)", "Aux 4 (SL1)" };
        private static readonly SolidBrush TrackBg = new(Color.FromArgb(46, 49, 56));
        private static readonly SolidBrush NameBrush = new(Color.FromArgb(150, 155, 165));
        private static readonly SolidBrush ValueBrush = new(Color.FromArgb(232, 234, 238));
        private static readonly Pen TickPen = new(Color.FromArgb(88, 94, 104));
        private static readonly Pen BorderPen = new(Color.FromArgb(64, 68, 76));

        public Color BarColor { get; set; } = Color.FromArgb(0x35, 0xC7, 0x59);

        // Base (96-dpi) widths; scaled by the monitor DPI in Relayout.
        private const int NameW = 108;
        private const int ValueW = 52;
        private int Sc(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

        private readonly int[] _values = new int[8];
        private bool _active;
        private Bitmap? _bg;
        private Bitmap? _fillBar;
        private readonly Rectangle[] _bar = new Rectangle[8];

        public AxisView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        // Push the latest channel values (0-32767). Repaints only if something visibly moved.
        public void SetValues(int[] v)
        {
            _active = true;
            int barW = Math.Max(1, _bar[0].Width - 2);
            bool changed = false;
            int n = Math.Min(8, v.Length);
            for (int i = 0; i < n; i++)
            {
                if (v[i] == _values[i]) continue;
                int oldPx = (int)(barW * Math.Clamp(_values[i] / 32767.0, 0, 1));
                int newPx = (int)(barW * Math.Clamp(v[i] / 32767.0, 0, 1));
                int oldPct = Math.Clamp(_values[i], 0, 32767) * 100 / 32767;
                int newPct = Math.Clamp(v[i], 0, 32767) * 100 / 32767;
                _values[i] = v[i];
                if (oldPx != newPx || oldPct != newPct) changed = true;
            }
            if (changed) Invalidate();
        }

        public void Clear()
        {
            _active = false;
            Array.Clear(_values, 0, _values.Length);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private void Relayout()
        {
            if (Width < 20 || Height < 20) return;
            int nameW = Sc(NameW), valueW = Sc(ValueW);
            int rowH = Height / 8;
            int barH = Math.Max(Sc(10), Math.Min(Sc(28), rowH - Sc(8)));
            int barW = Math.Max(20, Width - nameW - valueW);
            for (int i = 0; i < 8; i++)
            {
                int cy = i * rowH + (rowH - barH) / 2;
                _bar[i] = new Rectangle(nameW, cy, barW, barH);
            }
            RebuildBitmaps(barW, barH);
            Invalidate();
        }

        private void RebuildBitmaps(int barW, int barH)
        {
            _bg?.Dispose();
            _fillBar?.Dispose();
            _bg = new Bitmap(Math.Max(2, Width), Math.Max(2, Height));
            using (var g = Graphics.FromImage(_bg))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(BackColor);
                for (int i = 0; i < 8; i++)
                {
                    var b = _bar[i];
                    using var track = Rounded(new Rectangle(b.X, b.Y, b.Width - 1, b.Height - 1), Math.Min(7, b.Height / 2));
                    g.FillPath(TrackBg, track);
                    g.DrawLine(TickPen, b.X + b.Width / 2, b.Y + 3, b.X + b.Width / 2, b.Bottom - 4);
                    g.DrawPath(BorderPen, track);
                    g.DrawString(Names[i], Font, NameBrush, 2, b.Y + (b.Height - Font.Height) / 2f);
                }
            }
            _fillBar = new Bitmap(Math.Max(2, barW), Math.Max(2, barH));
            using (var g = Graphics.FromImage(_fillBar))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var grad = new LinearGradientBrush(new Rectangle(0, 0, barW, barH),
                    ControlPaint.Light(BarColor, 0.35f), BarColor, 90f);
                using var fp = Rounded(new Rectangle(1, 1, barW - 3, barH - 3), Math.Min(6, (barH - 3) / 2));
                g.FillPath(grad, fp);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_bg == null) Relayout();
            if (_bg == null || _fillBar == null) return;
            var g = e.Graphics;
            g.DrawImageUnscaled(_bg, 0, 0);
            for (int i = 0; i < 8; i++)
            {
                var b = _bar[i];
                double v = Math.Clamp(_values[i] / 32767.0, 0, 1);
                int fillW = (int)((b.Width - 2) * v);
                if (fillW > 1)
                {
                    g.SetClip(new Rectangle(b.X, b.Y, fillW + 1, b.Height));
                    g.DrawImageUnscaled(_fillBar, b.X + 1, b.Y + 1);
                    g.ResetClip();
                }
                // Same integer math as the change detection in SetValues, so the label can
                // never disagree with what triggered the repaint.
                int pct = Math.Clamp(_values[i], 0, 32767) * 100 / 32767;
                string val = _active ? $"{pct}%" : "--";
                g.DrawString(val, Font, ValueBrush, b.Right + Sc(6), b.Y + (b.Height - Font.Height) / 2f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _bg?.Dispose(); _fillBar?.Dispose(); }
            base.Dispose(disposing);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Simple dark-themed help dialog with short tutorials for each feature.
    class HelpDialog : Form
    {
        private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

        public HelpDialog(Icon? icon)
        {
            AutoScaleMode = AutoScaleMode.None;   // all pixel sizes scaled manually via S()
            Text = "Help & Tips";
            if (icon != null) Icon = icon;
            BackColor = Color.FromArgb(28, 30, 34);
            ForeColor = Color.FromArgb(232, 234, 238);
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(S(560), S(580));
            MinimumSize = new Size(S(460), S(400));
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;

            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(28, 30, 34),
                ForeColor = Color.FromArgb(210, 214, 220),
                Font = new Font("Segoe UI", 10f),
            };
            BuildText(rtb);
            rtb.SelectionStart = 0;
            rtb.ScrollToCaret();

            // Small right padding keeps the scrollbar near the window edge; the text itself
            // stays clear of the bar via SelectionRightIndent in BuildText.
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(20), S(12), S(8), S(2)), BackColor = Color.FromArgb(28, 30, 34) };
            pad.Controls.Add(rtb);
            Controls.Add(pad);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40), BackColor = Color.FromArgb(28, 30, 34) };
            var ok = new Button { Text = "Got it", Size = new Size(S(100), S(30)), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 40, 46), ForeColor = ForeColor, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            ok.FlatAppearance.BorderColor = Color.FromArgb(90, 95, 105);
            ok.Location = new Point(ClientSize.Width - S(122), S(5));
            ok.Click += (s, e) => Close();
            bottom.Controls.Add(ok);
            Controls.Add(bottom);
            AcceptButton = ok;
        }

        // Shared fonts (app-lifetime statics; RichTextBox only borrows them per selection).
        private static readonly Font HeadingFont = new("Segoe UI Semibold", 11f, FontStyle.Bold);
        private static readonly Font BodyFont = new("Segoe UI", 9.8f);

        private void BuildText(RichTextBox r)
        {
            int rightIndent = S(20);   // keep text clear of the scrollbar
            void H(string t)
            {
                r.SelectionFont = HeadingFont;
                r.SelectionColor = Color.FromArgb(120, 205, 135);
                r.SelectionRightIndent = rightIndent;
                r.AppendText(t + "\n");
            }
            void P(string t)
            {
                r.SelectionFont = BodyFont;
                r.SelectionColor = Color.FromArgb(205, 210, 216);
                r.SelectionRightIndent = rightIndent;
                r.AppendText(t + "\n\n");
            }

            H("What this does");
            P("Turns your ExpressLRS or TBS Crossfire/Tracer radio's WiFi output into a virtual joystick (vJoy), so you can fly sims like VelociDrone or Liftoff wirelessly - no cables.");

            H("Getting started");
            P("1. Install the vJoy driver (once).\n2. Put your TX module on the SAME WiFi network as this PC.\n3. The banner turns green \"Connected\" and the bars move.\n4. In your sim, pick \"vJoy Device\" as the controller and calibrate.");

            H("Status banner");
            P("Orange = searching for a module.  Green = connected & streaming.  Red = a problem (e.g. vJoy not installed).");

            H("Axis bars, Rate & Jitter");
            P("The bars show your live stick and switch positions. Rate is packets per second (~90-100 Hz is normal). Jitter is how steady the timing is - lower is smoother.");

            H("Module IP & Connect");
            P("Leave Module IP blank to auto-discover. To target a specific module (handy if two are on the network), type its IP and press Connect - only that module will drive the joystick.");

            H("Fix Firewall");
            P("Windows Firewall blocks incoming joystick data by default. The app adds the rule for you - just accept the one-time Windows permission prompt. If a \"firewall is blocking\" button appears, click it.");

            H("Minimize for better performance");
            P("Minimizing to the system tray PAUSES the on-screen bars and drops CPU use to nearly zero - the joystick keeps working perfectly in your sim. Recommended while flying.");

            H("Good to know");
            P("The module keeps broadcasting until it is powered off (there is no remote stop) - closing the app only stops reading the data. The app runs as a single instance and minimizes to the tray.");
        }
    }

    class MainForm : Form
    {
        private readonly JoystickEngine engine = new();
        private volatile int[]? latestChannels;
        private bool isStreaming;

        private Panel statusPanel = null!;
        private Label statusLabel = null!;
        private Label statusDetail = null!;
        private AxisView axisView = null!;
        private Label rateLabel = null!;
        private Label vjoyLabel = null!;
        private Label fwLabel = null!;
        private Button fwButton = null!;
        private Button startStopButton = null!;
        private TextBox portBox = null!;
        private TextBox txBox = null!;
        private TextBox logBox = null!;
        private System.Windows.Forms.Timer uiTimer = null!;
        private NotifyIcon tray = null!;
        private Icon? appIcon;

        private static readonly Color Bg = Color.FromArgb(28, 30, 34);
        private static readonly Color Panel2 = Color.FromArgb(38, 40, 46);
        private static readonly Color Fg = Color.FromArgb(232, 234, 238);
        private static readonly Color Muted = Color.FromArgb(150, 155, 165);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        private void EnableDarkTitleBar()
        {
            try
            {
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/11); 19 on older builds.
                if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
            }
            catch { }
        }

        // All pixel sizes are scaled explicitly by the monitor DPI. Fonts are in points and
        // scale automatically, so WinForms auto-scaling is DISABLED to avoid double/partial
        // scaling (on 125%+ displays it scaled the window+fonts but not fixed-size children,
        // clipping buttons and rows).
        private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

        public MainForm(int port, string? txIp)
        {
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

            engine.Port = port;
            engine.ActivationIP = txIp;

            Text = "ELRS / TBS Crossfire WiFi Joystick";
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(S(720), S(740));
            MinimumSize = new Size(S(640), S(600));
            StartPosition = FormStartPosition.CenterScreen;
            // Load the app's own embedded icon (works in the published single-file too).
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = appIcon; } catch { }

            BuildUi(port, txIp);
            WireEngine();

            // Refresh the axis bars at ~100 fps for smooth real-time motion (the data itself
            // arrives ~90-100 Hz). The timer only runs while streaming AND the window is
            // visible (see UpdateTimerState), so it costs nothing when idle or in the tray.
            uiTimer = new System.Windows.Forms.Timer { Interval = 10 };
            uiTimer.Tick += (s, e) => RefreshBars();
            VisibleChanged += (s, e) => UpdateTimerState();

            Load += (s, e) =>
            {
                EnableDarkTitleBar();
                // Make sure inbound joystick data isn't blocked. EnsureAllowed returns
                // immediately if the rule already exists; otherwise it asks once (UAC).
                FirewallHelper.EnsureAllowed(engine.Port, AppendLog);
                RefreshFirewall();
                StartEngine();   // start after the handle exists, so UI marshalling is safe
            };
            FormClosing += (s, e) =>
            {
                uiTimer.Stop();
                engine.StopAndWait(700);   // let the worker release vJoy cleanly
                uiTimer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                appIcon?.Dispose();
            };
        }

        private void BuildUi(int port, string? txIp)
        {
            // ---- status banner ----
            statusPanel = new Panel { Dock = DockStyle.Top, Height = S(82), BackColor = Color.FromArgb(70, 72, 80), Padding = new Padding(0, S(4), 0, S(6)) };
            statusLabel = new Label { Text = "Starting...", Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold), ForeColor = Color.White, AutoSize = false, Dock = DockStyle.Top, Height = S(44), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(16), 0, S(8), 0) };
            statusDetail = new Label { Text = "", ForeColor = Color.FromArgb(232, 232, 232), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(18), 0, S(8), S(2)) };
            statusPanel.Controls.Add(statusDetail);
            statusPanel.Controls.Add(statusLabel);
            Controls.Add(statusPanel);

            // ---- main body ----
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(12)), BackColor = Bg };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, S(300)));  // axes (fixed, compact bars)
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));  // stats
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));  // firewall
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, S(52)));  // settings
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // log (fills remaining space)
            Controls.Add(body);
            body.BringToFront();

            // axes - a single control renders all 8 bars (one paint per frame)
            axisView = new AxisView { Dock = DockStyle.Fill, BackColor = Bg, Font = Font };
            body.Controls.Add(axisView, 0, 0);

            // stats row
            rateLabel = new Label { Text = "Rate: --   Jitter: --", ForeColor = Fg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = false };
            vjoyLabel = new Label { Text = "vJoy: --", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
            var statsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Bg };
            statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74));
            statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            statsRow.Controls.Add(rateLabel, 0, 0);
            statsRow.Controls.Add(vjoyLabel, 1, 0);
            body.Controls.Add(statsRow, 0, 1);

            // firewall row
            fwLabel = new Label { Text = "Firewall: checking...", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            fwButton = MakeButton("Allow in Firewall", 160);
            fwButton.Dock = DockStyle.Right;
            fwButton.Click += (s, e) => { FirewallHelper.EnsureAllowed(engine.Port, AppendLog); RefreshFirewall(); };
            var fwRow = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            fwRow.Controls.Add(fwLabel);
            fwRow.Controls.Add(fwButton);
            body.Controls.Add(fwRow, 0, 2);

            // settings row
            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, WrapContents = false };
            settings.Controls.Add(new Label { Text = "Port:", ForeColor = Muted, AutoSize = true, Margin = new Padding(0, S(13), S(4), 0) });
            portBox = new TextBox { Text = port.ToString(), Width = S(62), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, S(10), 0, 0) };
            // Enter applies the port (the field is only editable while stopped).
            portBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { if (!engine.IsRunning) StartEngine(); e.SuppressKeyPress = true; } };
            settings.Controls.Add(portBox);
            settings.Controls.Add(new Label { Text = "   Module IP:", ForeColor = Muted, AutoSize = true, Margin = new Padding(S(6), S(13), S(4), 0) });
            txBox = new TextBox { Text = txIp ?? "", Width = S(128), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, S(10), 0, 0) };
            txBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Connect(); e.SuppressKeyPress = true; } };
            settings.Controls.Add(txBox);
            var connectButton = MakeButton("Connect", 84);
            connectButton.Margin = new Padding(S(10), S(9), 0, 0);
            connectButton.Click += (s, e) => Connect();
            settings.Controls.Add(connectButton);
            startStopButton = MakeButton("Stop", 74);
            startStopButton.Margin = new Padding(S(8), S(9), 0, 0);
            startStopButton.Click += (s, e) => ToggleEngine();
            settings.Controls.Add(startStopButton);
            var helpButton = MakeButton("Help", 68);
            helpButton.Margin = new Padding(S(8), S(9), 0, 0);
            helpButton.Click += (s, e) => ShowHelp();
            settings.Controls.Add(helpButton);
            body.Controls.Add(settings, 0, 3);

            // log
            logBox = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(20, 21, 24), ForeColor = Color.FromArgb(180, 200, 210), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8.5f) };
            body.Controls.Add(logBox, 0, 4);

            // tray
            tray = new NotifyIcon { Text = "ELRS / Crossfire WiFi Joystick", Visible = true, Icon = appIcon ?? SystemIcons.Application };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Exit", null, (s, e) => { engine.Stop(); tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => RestoreFromTray();
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); else UpdateTimerState(); };
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            UpdateTimerState();   // resume the axis refresh now that we're visible again
        }

        private void WireEngine()
        {
            engine.Log += msg => UiInvoke(() => AppendLog(msg));
            engine.StateChanged += (state, detail) => UiInvoke(() => OnState(state, detail));
            engine.ChannelsUpdated += ch => latestChannels = ch; // no marshalling; UI timer reads it
            engine.StatsUpdated += (hz, jit, cnt) => UiInvoke(() =>
                rateLabel.Text = cnt > 0 ? $"Rate: {hz:0} Hz     Jitter: {jit:0.0} ms     {cnt} pkt/s" : "Rate: --     Jitter: --");
        }

        // Marshal an action to the UI thread, safely ignoring the window not being ready
        // yet or already being disposed (engine events fire on a background thread).
        private void UiInvoke(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(a);
            }
            catch { /* form is closing/disposed - ignore */ }
        }

        private void OnState(EngineState state, string detail)
        {
            (string text, Color color) = state switch
            {
                EngineState.Streaming => ("Connected - streaming", Color.FromArgb(46, 125, 50)),
                EngineState.Searching => ("Searching for module...", Color.FromArgb(198, 128, 30)),
                EngineState.Error => ("Error", Color.FromArgb(150, 45, 45)),
                _ => ("Stopped", Color.FromArgb(70, 72, 80)),
            };
            statusLabel.Text = text;
            statusDetail.Text = detail;
            statusPanel.BackColor = color;
            vjoyLabel.Text = engine.DeviceId > 0 ? $"vJoy device {engine.DeviceId}" : "vJoy: --";
            startStopButton.Text = engine.IsRunning ? "Stop" : "Start";
            // Port can only change while stopped (it needs a re-bind) - grey it out when live
            // so it's obvious a Stop is required rather than silently doing nothing.
            portBox.Enabled = !engine.IsRunning;
            tray.Text = state == EngineState.Streaming ? $"Streaming from {detail}" : "ELRS / Crossfire WiFi Joystick";

            // When not actively streaming, don't leave the bars frozen at the last position.
            isStreaming = state == EngineState.Streaming;
            if (!isStreaming)
            {
                latestChannels = null;
                ClearBars();
            }
            UpdateTimerState();
        }

        // Run the axis-refresh timer only when it can actually be seen and there's data,
        // so the app uses no CPU redrawing when idle, searching, or minimized to the tray.
        private void UpdateTimerState()
        {
            bool shouldRun = isStreaming && Visible && WindowState != FormWindowState.Minimized;
            if (shouldRun)
            {
                if (!uiTimer.Enabled) uiTimer.Start();
            }
            else if (uiTimer.Enabled)
            {
                uiTimer.Stop();
            }
        }

        private void ClearBars() => axisView.Clear();

        private void RefreshBars()
        {
            var ch = latestChannels;
            if (ch != null) axisView.SetValues(ch);
        }

        private void RefreshFirewall()
        {
            bool ok = FirewallHelper.RuleExists(engine.Port);
            if (ok)
            {
                fwLabel.Text = "Firewall: allowed ✓  joystick data can get through";
                fwLabel.ForeColor = Color.FromArgb(120, 205, 135);
                fwButton.Visible = false;   // nothing to do -> don't show a dead button
            }
            else
            {
                fwLabel.Text = "Firewall may block joystick data — click to allow →";
                fwLabel.ForeColor = Color.FromArgb(235, 170, 65);
                fwButton.Visible = true;
                fwButton.Enabled = true;
                fwButton.BackColor = Color.FromArgb(200, 130, 32);   // amber call-to-action
                fwButton.ForeColor = Color.White;
                fwButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(218, 146, 44);
            }
        }

        private void StartEngine()
        {
            if (int.TryParse(portBox.Text.Trim(), out int p) && p > 0 && p < 65536) engine.Port = p;
            engine.ActivationIP = string.IsNullOrWhiteSpace(txBox.Text) ? null : txBox.Text.Trim();
            engine.Start();
            startStopButton.Text = "Stop";
        }

        private void ShowHelp()
        {
            using var dlg = new HelpDialog(appIcon);
            dlg.ShowDialog(this);
        }

        // Apply the Module IP live: connect to it immediately (no Stop/Start needed).
        private void Connect()
        {
            string ip = txBox.Text.Trim();
            if (!engine.IsRunning)
            {
                StartEngine(); // reads the IP field into the engine and starts
            }
            else
            {
                engine.SetTarget(string.IsNullOrWhiteSpace(ip) ? null : ip);
            }
        }

        private void ToggleEngine()
        {
            if (engine.IsRunning)
            {
                engine.Stop();
                engine.ActivationIP = null;   // forget any forced IP - next Start is a clean slate
                txBox.Clear();
                startStopButton.Text = "Start";
            }
            else
            {
                StartEngine();
            }
        }

        private Button MakeButton(string text, int width)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(S(width), S(30)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Fg,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 95, 105);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 58, 66);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(44, 48, 56);
            return b;
        }

        private void AppendLog(string msg)
        {
            if (logBox.TextLength > 20000) logBox.Text = logBox.Text.Substring(10000);
            logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\r\n");
        }
    }
}
