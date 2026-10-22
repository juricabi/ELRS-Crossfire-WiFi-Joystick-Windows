using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

    // What a module is, from its discovery beacon this session ("ELRS" or "VELOCIDRONE").
    enum ModuleKind { Unknown, Elrs, Crossfire }

    // One activation POST to a module's /udpcontrol, as the test seam sees it. Action is joystick_begin
    // (with IntervalUs and Channels: the request) or joystick_end (nothing else is sent; both 0).
    // Restart: part of a restart (joystick_end, then joystick_begin).
    readonly record struct ActivationPost(string Ip, string Action, int IntervalUs, int Channels, bool Restart);

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

        internal const string ActionBegin = "joystick_begin";
        internal const string ActionEnd = "joystick_end";

        private static readonly byte[] ELRS_BEACON = Encoding.ASCII.GetBytes("ELRS");
        private static readonly byte[] XF_BEACON = Encoding.ASCII.GetBytes("VELOCIDRONE");
        internal const double SOURCE_TIMEOUT_SEC = 3.0;
        // Crossfire beacons about every 8 s, also while it streams (ELRS never while it streams): a
        // module locked this long without one (two lost) is no Crossfire.
        internal const double XF_BEACON_SEC = 20.0;
        // What a Crossfire module puts in a channel it has no data for (62058).
        internal const int XF_PLACEHOLDER = 0xF26A;

        // One shared HttpClient for the whole app (creating one per request can exhaust sockets).
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        // ---- test seams (production behaviour is unchanged when these are left alone) ----
        // Injectable clock so time-based logic (lock timeout, activation throttle) is testable.
        internal Func<DateTime> Clock = () => DateTime.Now;
        // When set, channel output goes here instead of vJoy (tests record what was applied).
        internal Action<int[]>? OutputOverride;
        // When set, replaces each HTTP POST (tests must never touch the network) and runs synchronously.
        // Returns whether the POST succeeded.
        internal Func<ActivationPost, bool>? ActivateOverride;
        // Skips vJoy init/teardown so the socket loop can be tested without the driver.
        internal bool SkipVJoyForTest;

        private vJoy joystick = new vJoy();
        private uint deviceId;
        private IJoystickOutput? output;                // the acquired vJoy device (engine thread)
        private readonly JoystickMap map = new();       // engine thread; ButtonMask is read by UIs
        private volatile bool running;
        // Set by Stop until the next Start: from then on nothing is POSTed, so a request changed after
        // Stop (or a frame still in flight) can't start or restart a module. The lock itself stays
        // until the loop exits - clearing it here would let that frame lock again for a moment.
        private volatile bool stopRequested;
        private Thread? thread;
        private UdpClient? udp;

        private volatile string? boundSource;   // also cleared by the UI thread via SetTarget
        private DateTime boundSourceLastData;
        private readonly HashSet<string> noStickData = new();
        private readonly HashSet<string> warned = new();
        private readonly Dictionary<string, DateTime> lastActivation = new();
        // Set from an activation task thread, read/cleared on the engine thread.
        private readonly object activationGate = new();
        private DateTime? firstActivationTime;
        private bool firewallHintShown;

        // The request (kept across sessions; written by UI threads, read by the engine and POST threads)
        // and what this session learnt about modules. All under requestGate.
        private readonly object requestGate = new();
        private volatile int intervalUs = StreamRequest.IntervalDefault;
        private volatile int channels = StreamRequest.ChannelsDefault;
        private readonly Dictionary<string, ModuleKind> kinds = new();
        private readonly HashSet<string> restarted = new();     // restarted for a channel mismatch, or explicitly
        private bool xfNoted;                                    // "Crossfire ignores the request" logged
        private volatile bool restartWait;                       // a request restart waiting to know it is no Crossfire
        private DateTime boundSince;                             // when the lock was taken
        private volatile int boundChannels;                      // channel count of the last applied frame
        private int restartCount;
        // The POSTs to one module go out one after the other, in order: a restart's joystick_end must
        // never overtake an earlier joystick_begin to the same module, nor its begin the end.
        private readonly Dictionary<string, Task> postChains = new();

        // stats (high-resolution Stopwatch timestamps -> accurate jitter, not ~15ms-granular)
        private readonly List<long> arrivalTicks = new();
        private DateTime lastStats = DateTime.Now;
        private int frameCount;

        // Before any vJoy call (they all live in this class), so the DLL matching the
        // installed driver is the one loaded.
        static JoystickEngine() => VJoyNative.Register();

        public bool IsRunning => running;
        public uint DeviceId => deviceId;
        public string? Source => boundSource;
        public int IntervalUs => intervalUs;            // the request: what joystick_begin asks for
        public int Channels => channels;
        public int ButtonMask => map.ButtonMask;        // bit i = joystick button i+1 (CH9+i) pressed
        public int RestartCount => Volatile.Read(ref restartCount);   // restarts sent (lifetime)

        // From the beacons seen this session (Unknown until one arrives: Crossfire beacons ~every 8 s,
        // ELRS only while it is not streaming).
        public ModuleKind KindOf(string ip)
        {
            lock (requestGate) return KindLocked(ip);
        }

        // The kind of the module driving the joystick (Unknown when none).
        public ModuleKind SourceKind
        {
            get
            {
                string? s = boundSource;
                return s == null ? ModuleKind.Unknown : KindOf(s);
            }
        }

        private ModuleKind KindLocked(string ip) => kinds.TryGetValue(ip, out var k) ? k : ModuleKind.Unknown;

        public void Start()
        {
            if (running) return;
            // Wait for any previous run to fully exit so its UDP socket is released before
            // we bind again (otherwise a quick Stop->Start races on the port).
            thread?.Join(1500);
            stopRequested = false;
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "joystick-engine" };
            thread.Start();
        }

        public void Stop()
        {
            stopRequested = true;
            running = false;
        }

        // Stop and wait for the worker to finish (so vJoy is released cleanly) - used on exit.
        public void StopAndWait(int ms)
        {
            Stop();
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

        // Set what joystick_begin asks for; values are clamped to the supported range. The Linux core's
        // wifi_proto_set_request: changed while locked to a module that is not Crossfire -> its stream is
        // restarted so they apply (ELRS takes a new request only while not streaming; RestartStream may
        // wait to know the module is no Crossfire); locked to Crossfire -> logged once per session, nothing
        // sent (it ignores them); not locked -> the next activation carries them. Any thread. Returns true
        // when the (clamped) values changed.
        public bool SetRequest(int intervalUs, int channels)
        {
            intervalUs = StreamRequest.ClampInterval(intervalUs);
            channels = StreamRequest.ClampChannels(channels);
            string? src;
            bool note = false;
            lock (requestGate)
            {
                if (intervalUs == this.intervalUs && channels == this.channels) return false;
                this.intervalUs = intervalUs;
                this.channels = channels;
                src = stopRequested ? null : boundSource;
                if (src == null) return true;               // the next joystick_begin asks for them
                if (KindLocked(src) == ModuleKind.Crossfire)
                {
                    if (xfNoted) return true;
                    xfNoted = note = true;
                }
            }
            if (note) NoteCrossfireIgnores(src);
            else RestartStream();
            return true;
        }

        // Restart the stream of the locked module, else of the module IP, so it uses the request now:
        // POST joystick_end, then joystick_begin (off the receive thread, past the 5 s activation
        // throttle, which it then restarts). Crossfire: logged, nothing sent - it ignores the request and
        // what joystick_end does to it is unknown. Returns false when there is no module or it is
        // Crossfire. Any thread. (wifi_proto_restart)
        //
        // A locked module that may be Crossfire - 16 channels and no beacon from it yet this session - is
        // restarted only once XF_BEACON_SEC of lock passed without a Crossfire beacon (true is returned at
        // once: the restart is accepted, and held). Its beacon -> only noted; the lock lost -> dropped
        // (see ResolveRestartWait).
        public bool RestartStream()
        {
            if (stopRequested)
            {
                Log?.Invoke("Not running - nothing to restart");
                return false;
            }
            string? bound = boundSource;
            string? ip = bound ?? ActivationIP;
            if (ip == null)
            {
                Log?.Invoke("No module to restart - none is streaming and no module IP is set");
                return false;
            }
            bool xf, wait = false, waiting = false;
            long left = 0;
            int iv, ch;
            lock (requestGate)
            {
                xf = KindLocked(ip) == ModuleKind.Crossfire;
                if (!xf && ip == bound && KindUnsureLocked(ip))
                {
                    wait = true;
                    waiting = restartWait;
                    restartWait = true;
                    var t = boundSince.AddSeconds(XF_BEACON_SEC) - Clock();
                    left = (t.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
                }
                // Frames of the old request keep coming until the module has the restart: no mismatch
                // restart for them.
                else if (!xf) restarted.Add(ip);
                iv = intervalUs;
                ch = channels;
            }
            if (xf)
            {
                Log?.Invoke($"Crossfire module {ip} always sends {StreamRequest.CrossfireChannels} channels at its " +
                            "own rate (about 90 Hz) - nothing to restart");
                return false;
            }
            if (wait)
            {
                if (!waiting)
                    Log?.Invoke($"{ip} may be Crossfire (no beacon from it yet), which is never sent joystick_end: " +
                                $"restarting its stream within {left} s unless its beacon says Crossfire");
                return true;
            }
            Log?.Invoke($"Restarting the stream of {ip}: {StreamRequest.Describe(iv)}, {ch} channels");
            SendRestart(ip);
            return true;
        }

        private void NoteCrossfireIgnores(string ip) =>
            Log?.Invoke($"Crossfire module {ip} ignores the rate and channel count: it always sends " +
                        $"{StreamRequest.CrossfireChannels} channels at about 90 Hz");

        // Locked, no beacon from it this session, 16 channels, not yet XF_BEACON_SEC of lock: maybe Crossfire.
        private bool KindUnsureLocked(string src) =>
            KindLocked(src) == ModuleKind.Unknown && boundChannels == StreamRequest.CrossfireChannels &&
            (Clock() - boundSince).TotalSeconds < XF_BEACON_SEC;

        // A held restart, once the module is known: Crossfire -> noted, else restarted; the lock lost ->
        // dropped (the next joystick_begin carries the request). Called from Tick and, as Tick only runs
        // while no data arrives, from every applied frame.
        private void ResolveRestartWait()
        {
            if (!restartWait) return;
            string? src = boundSource;
            bool note = false, restart = false;
            lock (requestGate)
            {
                if (!restartWait) return;
                if (src != null && KindUnsureLocked(src)) return;
                restartWait = false;
                if (src == null || stopRequested) return;
                if (KindLocked(src) == ModuleKind.Crossfire)
                {
                    if (!xfNoted) xfNoted = note = true;
                }
                else restart = true;
            }
            if (note) NoteCrossfireIgnores(src!);
            if (restart) RestartStream();
        }

        private void SetState(EngineState s, string detail = "") => StateChanged?.Invoke(s, detail);

        // Fresh session state - a previous run's source lock must not leak into the next
        // one, or the "locked -> Streaming" transition never fires again after a restart.
        internal void ResetSessionState()
        {
            boundSource = null;
            warned.Clear();
            noStickData.Clear();
            lock (activationGate) firstActivationTime = null;
            firewallHintShown = false;
            arrivalTicks.Clear();
            frameCount = 0;
            lock (lastActivation) lastActivation.Clear();
            lock (requestGate)
            {
                kinds.Clear();
                restarted.Clear();
                xfNoted = false;
                restartWait = false;
            }
            boundChannels = 0;
            map.Clear();
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
                    EndRun();
                    return;
                }
                uint dllVer = 0, drvVer = 0;
                bool match = joystick.DriverMatch(ref dllVer, ref drvVer);
                Log?.Invoke($"vJoy driver {VJoyNative.FormatVersion(drvVer)}, " +
                            $"DLL {VJoyNative.FormatVersion(dllVer)} ({VJoyNative.LoadedFrom ?? "bundled copy"})");
                // Not fatal - but a mismatched DLL acquires the device and then its axis
                // writes silently go nowhere, which looks exactly like a network fault.
                if (!match)
                {
                    if (VJoyNative.LoadedFrom != null)
                        Log?.Invoke("!! vJoy's own DLL does not match its driver - the gamepad may not move. " +
                                    "Reboot if vJoy was just updated, otherwise reinstall vJoy.");
                    else
                        Log?.Invoke("!! The bundled vJoy DLL does not match the driver, and vJoy's own " +
                                    (VJoyNative.LoadError != null ? $"failed to load ({VJoyNative.LoadError})" : "was not found") +
                                    " - the gamepad may not move. Reinstall vJoy.");
                }

                deviceId = FindDevice();
                if (deviceId == 0)
                {
                    Log?.Invoke("ERROR: no free vJoy device. Enable one in 'Configure vJoy'.");
                    SetState(EngineState.Error, "no free vJoy device");
                    EndRun();
                    return;
                }

                var st = joystick.GetVJDStatus(deviceId);
                if (st == VjdStat.VJD_STAT_BUSY || st == VjdStat.VJD_STAT_MISS)
                {
                    Log?.Invoke($"ERROR: vJoy device {deviceId} unavailable ({st}).");
                    SetState(EngineState.Error, $"vJoy {st}");
                    EndRun();
                    return;
                }
                if (!joystick.AcquireVJD(deviceId))
                {
                    Log?.Invoke($"ERROR: failed to acquire vJoy device {deviceId}.");
                    SetState(EngineState.Error, "acquire failed");
                    EndRun();
                    return;
                }
                joystick.ResetVJD(deviceId);
                map.Clear();    // after the reset the device state is unknown: the first frame writes it all
                int buttons;
                try { buttons = Math.Clamp(joystick.GetVJDButtonNumber(deviceId), 0, JoystickMap.ButtonCount); }
                catch { buttons = 0; }
                output = new VJoyOutput(joystick, deviceId, buttons);
                Log?.Invoke($"Using vJoy device {deviceId}");
                if (buttons == JoystickMap.ButtonCount)
                    Log?.Invoke("CH1-CH8 are the axes, CH9-CH16 joystick buttons 1-8");
                else if (buttons > 0)
                    Log?.Invoke($"vJoy device {deviceId} has {buttons} buttons: CH9-CH{8 + buttons} are buttons 1-{buttons}. " +
                                "Give it 8 buttons in 'Configure vJoy' to use CH9-CH16.");
                else
                    Log?.Invoke($"vJoy device {deviceId} has no buttons: CH9-CH16 are not used. " +
                                "Give it 8 buttons in 'Configure vJoy' to use them.");
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
                EndRun();
                return;
            }

            Log?.Invoke($"Listening for joystick data on UDP {Port}");
            Log?.Invoke($"Asking modules for {StreamRequest.Describe(intervalUs)}, {channels} channels " +
                        "(ELRS; Crossfire always sends 16 channels at about 90 Hz)");
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
            boundSource = null;
            SetState(EngineState.Stopped, "");
            Log?.Invoke("Stopped. (The module keeps broadcasting until powered off.)");
        }

        // The run ends by itself (an error): like Stop, nothing may be POSTed until the next Start.
        // stopRequested first, so a Start that sees running == false always comes after it.
        private void EndRun()
        {
            stopRequested = true;
            running = false;
        }

        // ResetVJD centers the axes and releases the buttons before the device is let go.
        private void Cleanup()
        {
            try { udp?.Close(); } catch { }
            if (!SkipVJoyForTest)
            {
                try { joystick.ResetVJD(deviceId); joystick.RelinquishVJD(deviceId); } catch { }
            }
            output = null;
            map.Clear();
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
            // Not if frames are arriving without stick data: that has its own explanation
            // and the firewall is demonstrably fine.
            DateTime? activatedAt;
            lock (activationGate) activatedAt = firstActivationTime;
            if (activatedAt != null && boundSource == null && !firewallHintShown
                && noStickData.Count == 0
                && (Clock() - activatedAt.Value).TotalSeconds > 4)
            {
                firewallHintShown = true;
                Log?.Invoke("!! Activated OK but NO data arriving - either Windows Firewall (click 'Allow in Firewall') or the module is not connected to a powered radio.");
                SetState(EngineState.Searching, "activated, but no data (firewall? radio off?)");
            }

            ResolveRestartWait();

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
                lock (requestGate) kinds[ip] = xf ? ModuleKind.Crossfire : ModuleKind.Elrs;
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

            int[] ch = new int[count];
            int outOfRange = 0;
            bool placeholder = false;
            for (int i = 0; i < count; i++)
            {
                int v = data[2 + i * 2] | (data[2 + i * 2 + 1] << 8);
                if (v == XF_PLACEHOLDER) placeholder = true;
                // Clamp slight overshoot to the 15-bit protocol range: a stick pushed past a
                // wide endpoint legitimately reads a little above 32767 (observed up to 0x816B).
                if (v > 32767) { outOfRange++; v = 32767; }
                ch[i] = v;
            }
            // Frames carrying the 0xF26A placeholder hold no stick data - a real channel can
            // never reach 62058. All 16 channels sit at it while the module has no input
            // (unsupported TBS firmware, radio off), and under fast stick movement the module
            // occasionally emits a corrupt frame mixing placeholders with garbage (observed
            // live: 5 identical such frames in 1791). Applying one would spike axes to 100%
            // for a frame, so drop it: mid-stream vJoy simply holds the last good values for
            // ~11 ms. The all-out-of-range check stays as a backstop for other idle patterns.
            if (placeholder || outOfRange == count)
            {
                // The receive loop only runs Tick() on socket timeouts, and a placeholder
                // flood keeps the socket busy - release a stale lock from here instead.
                Tick();
                if (boundSource == null) WarnNoStickData(src);
                return;
            }

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
                lock (requestGate)
                {
                    boundSince = Clock();
                    restartWait = false;
                    boundSource = src;
                }
                warned.Clear();
                noStickData.Clear();
                lock (activationGate) firstActivationTime = null;
                firewallHintShown = false;
                Log?.Invoke($"Joystick source locked to {src}");
                SetState(EngineState.Streaming, src);
            }
            boundSourceLastData = Clock();
            boundChannels = count;

            Apply(ch);
            ChannelsUpdated?.Invoke(ch);

            frameCount++;
            arrivalTicks.Add(Stopwatch.GetTimestamp());

            if (count != channels) RestartOnMismatch(src, count);
            ResolveRestartWait();
        }

        // A module still streaming another request (another app's, or ours from before a change) keeps
        // it until it is restarted. Only where it surely is no Crossfire (that ignores the request): ELRS
        // by its beacon, or a channel count Crossfire never sends. Once per module and session, so a
        // module that doesn't do what is asked is never restarted per frame. The frames are applied all
        // the same.
        private void RestartOnMismatch(string src, int count)
        {
            if (stopRequested) return;
            int asked;
            lock (requestGate)
            {
                asked = channels;
                if (count == asked || restarted.Contains(src)) return;
                var k = KindLocked(src);
                if (!(k == ModuleKind.Elrs || (k == ModuleKind.Unknown && count != StreamRequest.CrossfireChannels)))
                    return;
                restarted.Add(src);
            }
            Log?.Invoke($"{src} streams {count} channels, {asked} asked for (another app's request?) - restarting its stream");
            SendRestart(src);
        }

        // Warn once per source, not ~90x/sec. The usual cause is a known TBS firmware
        // regression, so point at the fix instead of leaving the user hunting a network fault.
        private void WarnNoStickData(string src)
        {
            if (!noStickData.Add(src)) return;
            Log?.Invoke($"!! {src} is streaming placeholder frames - no stick data. Either the radio " +
                        "is not feeding the module right now, or the firmware has the known regression " +
                        "(XF 6.42/6.48, WiFi 3.20 - use XF 6.31 or 6.36, WiFi up to 3.10).");
            SetState(EngineState.Searching, "module found, but it is sending no stick data");
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
            if (OutputOverride != null) { map.Frame(ch, null); OutputOverride(ch); return; }
            map.Frame(ch, output);
        }

        private void EnsureStreaming(string ip)
        {
            lock (lastActivation)
            {
                if (lastActivation.TryGetValue(ip, out var t) && (Clock() - t).TotalSeconds < 5)
                    return;
                lastActivation[ip] = Clock();
            }
            Post(ip, restart: false);
        }

        // joystick_end + joystick_begin to ip, past the throttle (and it restarts the throttle).
        private void SendRestart(string ip)
        {
            Interlocked.Increment(ref restartCount);
            lock (lastActivation) lastActivation[ip] = Clock();
            Post(ip, restart: true);
        }

        private void Post(string ip, bool restart)
        {
            if (stopRequested) return;      // stopping: the last packets of the loop activate nothing
            int iv, ch;
            lock (requestGate) { iv = intervalUs; ch = channels; }

            if (ActivateOverride != null)
            {
                // Test seam stays synchronous so tests remain deterministic.
                if (Activate(ip, restart, iv, ch)) MarkActivated(ip);
                return;
            }

            // The real HTTP POSTs run off-thread: a slow or unreachable module (5s timeout each) must not
            // stall the receive loop, and Stop/exit must not wait on them either.
            lock (postChains)
            {
                if (postChains.Count > 32)
                    foreach (var k in postChains.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
                        postChains.Remove(k);
                postChains.TryGetValue(ip, out var prev);
                postChains[ip] = (prev ?? Task.CompletedTask).ContinueWith(
                    _ => { if (Activate(ip, restart, iv, ch)) MarkActivated(ip); },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        // One activation. A restart POSTs joystick_end first; its failure does not stop the
        // joystick_begin (the module may simply not be streaming). True = the joystick_begin succeeded.
        private bool Activate(string ip, bool restart, int iv, int ch)
        {
            if (restart) SendPost(new ActivationPost(ip, ActionEnd, 0, 0, true));
            return SendPost(new ActivationPost(ip, ActionBegin, iv, ch, restart));
        }

        private bool SendPost(ActivationPost p)
        {
            var hook = ActivateOverride;
            return hook != null ? hook(p) : HttpPost(p);
        }

        private void MarkActivated(string ip)
        {
            // ELRS starts streaming before it answers the POST, so the answer often comes after the lock,
            // and a restart's answer always does. Data from that module already proved the path: arming
            // the "no data" hint then would make it fire after any later lock loss (the Linux core's rule).
            if (ip == boundSource && (Clock() - boundSourceLastData).TotalSeconds <= SOURCE_TIMEOUT_SEC)
                return;
            lock (activationGate)
            {
                if (firstActivationTime == null)
                    firstActivationTime = Clock();
            }
        }

        // The form body of a POST: "action=joystick_begin&interval=4000&channels=16", or "action=joystick_end".
        internal static KeyValuePair<string, string>[] FormFields(ActivationPost p) =>
            p.Action == ActionEnd
                ? new[] { new KeyValuePair<string, string>("action", ActionEnd) }
                : new[]
                {
                    new KeyValuePair<string, string>("action", p.Action),
                    new KeyValuePair<string, string>("interval", p.IntervalUs.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("channels", p.Channels.ToString(CultureInfo.InvariantCulture)),
                };

        private bool HttpPost(ActivationPost p)
        {
            bool end = p.Action == ActionEnd;
            try
            {
                using var form = new FormUrlEncodedContent(FormFields(p));
                using var resp = Http.PostAsync($"http://{p.Ip}/udpcontrol", form).GetAwaiter().GetResult();
                Log?.Invoke($"{(end ? "Stream end" : "Activation")} POST -> {p.Ip}: {(int)resp.StatusCode} {resp.StatusCode}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"{(end ? "Stream end POST" : "Activation")} failed for {p.Ip}: {ex.Message}");
                return false;
            }
        }

        // The acquired vJoy device. SetAxis/SetBtn work the same on vJoy 2.1.9 and 2.2.x (UpdateVJD's
        // struct layout is version-specific, so it is not used).
        private sealed class VJoyOutput : IJoystickOutput
        {
            private readonly vJoy joy;
            private readonly uint id;

            public VJoyOutput(vJoy joy, uint id, int buttons)
            {
                this.joy = joy;
                this.id = id;
                ButtonCount = buttons;
            }

            public int ButtonCount { get; }
            public bool SetAxis(int axis, int value) => joy.SetAxis(value, id, JoystickMap.AxisUsages[axis]);
            public bool SetButton(int button, bool pressed) => joy.SetBtn(pressed, id, (uint)(button + 1));
        }
    }
}
