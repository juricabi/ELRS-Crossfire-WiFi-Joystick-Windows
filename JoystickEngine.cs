using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vJoyInterfaceWrap;

namespace ELRSWifiJoystick
{
    enum EngineState { Stopped, Searching, Streaming, Error }

    // Core WiFi-joystick engine: receives the ELRS/Crossfire UDP stream, feeds vJoy, and
    // raises events for a UI. Runs its own background thread; all events fire on that thread,
    // so subscribers must marshal to their UI thread.
    class JoystickEngine
    {
        public int Port = 11000;
        // volatile: written by the UI thread (SetTarget/Start), read by the engine thread.
        public volatile string? ActivationIP;

        public event Action<string>? Log;
        public event Action<EngineState, string>? StateChanged;   // state, detail (module ip / reason)
        public event Action<int[]>? ChannelsUpdated;              // raw channel values (0-32767)
        public event Action<double, double, int>? StatsUpdated;   // rateHz, jitterMs, frames/sec

        private static readonly byte[] ELRS_BEACON = Encoding.ASCII.GetBytes("ELRS");
        private static readonly byte[] XF_BEACON = Encoding.ASCII.GetBytes("VELOCIDRONE");
        internal const double SOURCE_TIMEOUT_SEC = 3.0;

        // One shared HttpClient for the whole app (creating one per request can exhaust sockets).
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        // ---- test seams (production behaviour is unchanged when these are left alone) ----
        // Injectable clock so time-based logic (lock timeout, activation throttle) is testable.
        internal Func<DateTime> Clock = () => DateTime.Now;
        // When set, channel output goes here instead of vJoy (tests record what was applied).
        internal Action<int[]>? OutputOverride;
        // When set, replaces the HTTP activation POST (tests must never touch the network).
        internal Func<string, bool>? ActivateOverride;
        // Skips vJoy init/teardown so the socket loop can be tested without the driver.
        internal bool SkipVJoyForTest;

        private vJoy joystick = new vJoy();
        private uint deviceId;
        private volatile bool running;
        private Thread? thread;
        private UdpClient? udp;

        private volatile string? boundSource;   // also cleared by the UI thread via SetTarget
        private DateTime boundSourceLastData;
        private readonly HashSet<string> warned = new();
        private readonly Dictionary<string, DateTime> lastActivation = new();
        // Set from an activation task thread, read/cleared on the engine thread.
        private readonly object activationGate = new();
        private DateTime? firstActivationTime;
        private bool firewallHintShown;

        // stats (high-resolution Stopwatch timestamps -> accurate jitter, not ~15ms-granular)
        private readonly List<long> arrivalTicks = new();
        private DateTime lastStats = DateTime.Now;
        private int frameCount;

        public bool IsRunning => running;
        public uint DeviceId => deviceId;
        public string? Source => boundSource;

        public void Start()
        {
            if (running) return;
            // Wait for any previous run to fully exit so its UDP socket is released before
            // we bind again (otherwise a quick Stop->Start races on the port).
            thread?.Join(1500);
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "joystick-engine" };
            thread.Start();
        }

        public void Stop() => running = false;

        // Stop and wait for the worker to finish (so vJoy is released cleanly) - used on exit.
        public void StopAndWait(int ms)
        {
            running = false;
            try { thread?.Join(ms); } catch { }
        }

        // Set/replace the module IP to activate, live. The actual HTTP activation happens on
        // the engine thread (never blocks the UI). Pass null/empty to rely on beacon discovery.
        public void SetTarget(string? ip)
        {
            string? t = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
            ActivationIP = t;
            if (t != null)
            {
                lock (lastActivation) lastActivation.Remove(t); // allow immediate (re)activation
                boundSource = null;                              // let the new target take over
                Log?.Invoke($"Target module set to {t} - connecting...");
            }
        }

        private void SetState(EngineState s, string detail = "") => StateChanged?.Invoke(s, detail);

        // Fresh session state - a previous run's source lock must not leak into the next
        // one, or the "locked -> Streaming" transition never fires again after a restart.
        internal void ResetSessionState()
        {
            boundSource = null;
            warned.Clear();
            lock (activationGate) firstActivationTime = null;
            firewallHintShown = false;
            arrivalTicks.Clear();
            frameCount = 0;
            lock (lastActivation) lastActivation.Clear();
        }

        private void Run()
        {
            ResetSessionState();

            if (!SkipVJoyForTest)
            {
                joystick = new vJoy();
                if (!joystick.vJoyEnabled())
                {
                    Log?.Invoke("ERROR: vJoy driver not enabled. Install vJoy from vjoystick.sourceforge.net");
                    SetState(EngineState.Error, "vJoy not enabled");
                    running = false;
                    return;
                }
                Log?.Invoke($"vJoy version {joystick.GetvJoyVersion()}");

                deviceId = FindDevice();
                if (deviceId == 0)
                {
                    Log?.Invoke("ERROR: no free vJoy device. Enable one in 'Configure vJoy'.");
                    SetState(EngineState.Error, "no free vJoy device");
                    running = false;
                    return;
                }

                var st = joystick.GetVJDStatus(deviceId);
                if (st == VjdStat.VJD_STAT_BUSY || st == VjdStat.VJD_STAT_MISS)
                {
                    Log?.Invoke($"ERROR: vJoy device {deviceId} unavailable ({st}).");
                    SetState(EngineState.Error, $"vJoy {st}");
                    running = false;
                    return;
                }
                if (!joystick.AcquireVJD(deviceId))
                {
                    Log?.Invoke($"ERROR: failed to acquire vJoy device {deviceId}.");
                    SetState(EngineState.Error, "acquire failed");
                    running = false;
                    return;
                }
                joystick.ResetVJD(deviceId);
                Log?.Invoke($"Using vJoy device {deviceId}");
            }

            // Bind with a short retry: a just-stopped previous run can hold the port for a
            // moment longer (e.g. its thread was inside a blocking activation POST when the
            // user hit Stop, so Start()'s bounded Join returned before the socket closed).
            Exception? bindError = null;
            for (int attempt = 0; attempt < 5 && running; attempt++)
            {
                try
                {
                    udp = new UdpClient();
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                    udp.Client.ReceiveTimeout = 100;
                    bindError = null;
                    break;
                }
                catch (Exception ex)
                {
                    bindError = ex;
                    try { udp?.Close(); } catch { }
                    udp = null;
                    Thread.Sleep(300);
                }
            }
            if (udp == null)
            {
                Log?.Invoke($"ERROR: cannot listen on UDP port {Port} - it may already be in use " +
                            $"(another instance, or another app on this port). {bindError?.Message}");
                SetState(EngineState.Error, $"UDP port {Port} in use");
                Cleanup();
                running = false;
                return;
            }

            Log?.Invoke($"Listening for joystick data on UDP {Port}");
            LogLocalAddresses();
            SetState(EngineState.Searching, "waiting for module");
            if (ActivationIP != null) EnsureStreaming(ActivationIP);

            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    byte[] data = udp.Receive(ref remote);
                    Process(data, remote);
                }
                catch (SocketException)
                {
                    Tick(); // receive timeout (~10x/sec)
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"recv error: {ex.Message}");
                }

                if ((DateTime.Now - lastStats).TotalSeconds >= 1.0)
                    EmitStats();
            }

            Cleanup();
            SetState(EngineState.Stopped, "");
            Log?.Invoke("Stopped. (The module keeps broadcasting until powered off.)");
        }

        private void Cleanup()
        {
            try { udp?.Close(); } catch { }
            if (!SkipVJoyForTest)
            {
                try { joystick.ResetVJD(deviceId); joystick.RelinquishVJD(deviceId); } catch { }
            }
        }

        private uint FindDevice()
        {
            for (uint id = 1; id <= 16; id++)
                if (joystick.GetVJDStatus(id) == VjdStat.VJD_STAT_FREE) return id;
            return 0;
        }

        // The module streams to the network it sees us on; log our IPv4 address(es) so a
        // wrong-subnet / VPN / virtual-adapter mismatch is easy to spot.
        private void LogLocalAddresses()
        {
            try
            {
                var ips = new List<string>();
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        ips.Add(ip.ToString());
                if (ips.Count > 0)
                {
                    Log?.Invoke($"This PC: {string.Join(", ", ips)}");
                    if (ips.Count > 1)
                        Log?.Invoke("Multiple adapters - your module must be on one of these networks.");
                }
            }
            catch { }
        }

        internal void Tick()
        {
            // Release a stale lock so another module can take over.
            if (boundSource != null && (Clock() - boundSourceLastData).TotalSeconds > SOURCE_TIMEOUT_SEC)
            {
                Log?.Invoke($"Source {boundSource} went quiet - releasing lock");
                boundSource = null;
                SetState(EngineState.Searching, "waiting for module");
            }

            // Activated OK but no data -> almost always Windows Firewall. Hint once.
            DateTime? activatedAt;
            lock (activationGate) activatedAt = firstActivationTime;
            if (activatedAt != null && boundSource == null && !firewallHintShown
                && (Clock() - activatedAt.Value).TotalSeconds > 4)
            {
                firewallHintShown = true;
                Log?.Invoke("!! Activated OK but NO data arriving - either Windows Firewall (click 'Allow in Firewall') or the module is not connected to a powered radio.");
                SetState(EngineState.Searching, "activated, but no data (firewall? radio off?)");
            }

            if (ActivationIP != null && boundSource == null)
                EnsureStreaming(ActivationIP);
        }

        internal void Process(byte[] data, IPEndPoint remote)
        {
            if (data.Length < 3) return;

            bool xf = StartsWith(data, XF_BEACON);
            if (xf || StartsWith(data, ELRS_BEACON))
            {
                string ip = remote.Address.ToString();
                if (ActivationIP != null && ip != ActivationIP) return; // only the chosen module
                bool lockedOther = boundSource != null && boundSource != ip;
                bool streaming = boundSource == ip && (Clock() - boundSourceLastData).TotalSeconds < SOURCE_TIMEOUT_SEC;
                if (!lockedOther && !streaming)
                {
                    Log?.Invoke($"Discovery beacon from {(xf ? "Crossfire" : "ELRS")} module {ip} - activating");
                    EnsureStreaming(ip);
                }
                return;
            }

            int count = data[1];
            if (count < 4 || count > 16) return;
            if (data.Length < 2 + count * 2) return;

            string src = remote.Address.ToString();
            // If a specific module was chosen (Module IP / Connect), accept ONLY that one, so a
            // different module that's still broadcasting can't steal or hold the lock.
            if (ActivationIP != null && src != ActivationIP) return;
            if (boundSource != null && boundSource != src)
            {
                if ((Clock() - boundSourceLastData).TotalSeconds > SOURCE_TIMEOUT_SEC)
                    boundSource = null;
                else
                {
                    if (warned.Add(src)) Log?.Invoke($"Ignoring 2nd source {src} (locked to {boundSource})");
                    return;
                }
            }
            if (boundSource == null)
            {
                boundSource = src;
                warned.Clear();
                lock (activationGate) firstActivationTime = null;
                firewallHintShown = false;
                Log?.Invoke($"Joystick source locked to {src}");
                SetState(EngineState.Streaming, src);
            }
            boundSourceLastData = Clock();

            int[] ch = new int[count];
            for (int i = 0; i < count; i++)
            {
                int v = data[2 + i * 2] | (data[2 + i * 2 + 1] << 8);
                // Defensive clamp to the 15-bit protocol range: a radio-less Crossfire module
                // was observed idling at 0xF26A (62058), which would overflow the vJoy axis.
                ch[i] = v > 32767 ? 32767 : v;
            }

            Apply(ch);
            ChannelsUpdated?.Invoke(ch);

            frameCount++;
            arrivalTicks.Add(Stopwatch.GetTimestamp());
        }

        // Test hook: inject a synthetic arrival timestamp (Stopwatch ticks) for stats tests.
        internal void AddArrivalForTest(long timestamp)
        {
            arrivalTicks.Add(timestamp);
            frameCount++;
        }

        internal void EmitStats()
        {
            lastStats = DateTime.Now;
            double hz = 0, jitter = 0;
            if (arrivalTicks.Count > 2)
            {
                double toMs = 1000.0 / Stopwatch.Frequency;
                var gaps = new List<double>();
                for (int i = 1; i < arrivalTicks.Count; i++) gaps.Add((arrivalTicks[i] - arrivalTicks[i - 1]) * toMs);
                double mean = 0; foreach (var g in gaps) mean += g; mean /= gaps.Count;
                double varSum = 0; foreach (var g in gaps) varSum += (g - mean) * (g - mean);
                jitter = Math.Sqrt(varSum / gaps.Count);
                double spanMs = (arrivalTicks[arrivalTicks.Count - 1] - arrivalTicks[0]) * toMs;
                if (spanMs > 0) hz = (arrivalTicks.Count - 1) * 1000.0 / spanMs;
            }
            StatsUpdated?.Invoke(hz, jitter, frameCount);
            frameCount = 0;
            arrivalTicks.Clear();
        }

        internal static bool StartsWith(byte[] d, byte[] p)
        {
            if (d.Length < p.Length) return false;
            for (int i = 0; i < p.Length; i++) if (d[i] != p[i]) return false;
            return true;
        }

        private void Apply(int[] ch)
        {
            if (OutputOverride != null) { OutputOverride(ch); return; }
            void Set(int i, HID_USAGES ax) { if (ch.Length > i) joystick.SetAxis(ch[i], deviceId, ax); }
            Set(0, HID_USAGES.HID_USAGE_X);
            Set(1, HID_USAGES.HID_USAGE_Y);
            Set(2, HID_USAGES.HID_USAGE_RX);
            Set(3, HID_USAGES.HID_USAGE_RY);
            Set(4, HID_USAGES.HID_USAGE_RZ);
            Set(5, HID_USAGES.HID_USAGE_Z);
            Set(6, HID_USAGES.HID_USAGE_SL0);
            Set(7, HID_USAGES.HID_USAGE_SL1);
        }

        private void EnsureStreaming(string ip)
        {
            lock (lastActivation)
            {
                if (lastActivation.TryGetValue(ip, out var t) && (Clock() - t).TotalSeconds < 5)
                    return;
                lastActivation[ip] = Clock();
            }

            if (ActivateOverride != null)
            {
                // Test seam stays synchronous so tests remain deterministic.
                if (ActivateOverride(ip)) MarkActivated();
                return;
            }

            // The real HTTP POST runs off-thread: a slow or unreachable module (5s timeout)
            // must not stall the receive loop, and Stop/exit must not wait on it either.
            Task.Run(() => { if (StartStreaming(ip)) MarkActivated(); });
        }

        private void MarkActivated()
        {
            lock (activationGate)
            {
                if (firstActivationTime == null)
                    firstActivationTime = Clock();
            }
        }

        private bool StartStreaming(string ip)
        {
            try
            {
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("action", "joystick_begin"),
                    new KeyValuePair<string, string>("interval", "10000"),
                    new KeyValuePair<string, string>("channels", "8"),
                });
                var resp = Http.PostAsync($"http://{ip}/udpcontrol", form).Result;
                Log?.Invoke($"Activation POST -> {ip}: {(int)resp.StatusCode} {resp.StatusCode}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Activation failed for {ip}: {ex.Message}");
                return false;
            }
        }
    }
}
