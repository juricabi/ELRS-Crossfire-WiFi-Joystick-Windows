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

    // What a module is, from its discovery beacon ("ELRS" or "VELOCIDRONE"); kept across sessions (R9).
    enum ModuleKind { Unknown, Elrs, Crossfire }

    // One activation POST to a module's /udpcontrol, as the test seam sees it. Action is joystick_begin
    // (with IntervalUs and Channels: the request) or joystick_end (nothing else is sent; both 0).
    // Restart: part of a restart (joystick_end, then joystick_begin).
    readonly record struct ActivationPost(string Ip, string Action, int IntervalUs, int Channels, bool Restart);

    // Core WiFi-joystick engine: receives the ELRS/Crossfire UDP stream, feeds vJoy, and
    // raises events for a UI. Runs its own background thread; events fire on that thread (and on the
    // caller's for SetTarget, SetRequest and RestartStream, and on a POST's for its result), so subscribers
    // must marshal to their UI thread.
    //
    // The rules of the request (the same in the Linux core, core/wifi_proto.h, and in every doc of the apps):
    // R1  Defaults 4000 us (250 Hz) and 16 channels. Supported: interval 1000..100000 us (presets
    //     1000/500/333/250/200/150/100/50 Hz), channels 4..16 (StreamRequest).
    // R2  A change while locked: Crossfire (by its beacon) -> logged once per session, nothing sent. Surely no
    //     Crossfire (its ELRS beacon seen, or frames of a count other than 16) -> restarted now (joystick_end,
    //     then joystick_begin with the request). Unknown kind with 16 channels -> held until XF_BEACON_SEC of the
    //     lock passed without a Crossfire beacon, then restarted; dropped on a Crossfire beacon. A held restart
    //     survives a lock loss of its module and goes out when that module locks again (after its window);
    //     dropped when that module's ELRS beacon gets it a joystick_begin carrying the request, or when another
    //     module takes the lock.
    // R3  Not locked: the next joystick_begin carries the request. What each module was last asked is kept per
    //     module (requestOf, TableSize); a module that locks with another asked request than the request is
    //     restarted under R2's kind rules. A restart that did not take (R6, or cut short by Stop) leaves it
    //     unknown (RequestUnknown): restarted at its next lock unless its ELRS beacon gets it a joystick_begin
    //     carrying the request first.
    // R4  Explicit restart (RestartStream): locked -> as R2 (Crossfire: logged, nothing sent). Not locked, the
    //     module IP set: known as ELRS -> restarted; unknown or Crossfire -> a plain joystick_begin past the
    //     throttle, never joystick_end. Neither -> logged, nothing sent.
    // R5  A module surely no Crossfire streaming a channel count other than the request -> restarted once per
    //     module and session. Unknown kind streaming 16 while fewer are asked -> once R2's window passed without
    //     a Crossfire beacon, restarted once.
    // R6  A restart is done only when both POSTs got a 2xx. A failed one is sent again THROTTLE_SEC after the
    //     last send while that module stays locked and is not known as Crossfire, RestartTries sends per
    //     restart (request change) in all; giving up is logged. Once it is not sent again (given up, or the
    //     module no longer locked) the module is restarted at its next lock (R3). A failed plain
    //     joystick_begin is never retried as a restart.
    // R7  The module IP set to the locked module: the lock stays, nothing sent, logged. Another one: unlocked,
    //     state "connecting to <ip>", joystick_begin at once; a restart to send again or held for the module
    //     being left is dropped.
    // R8  The module IP and the request together (SetTarget(text, intervalUs, channels), one step): the new
    //     module's joystick_begin carries the request, the module being left gets nothing. An engine API: the
    //     GUI and the CLI change the request (at once) and the module IP (Connect) one at a time.
    // R9  A stop/start or a port change (ResetSessionState) starts a new session: lock, throttles and restart
    //     state cleared; the kinds from beacons and the asked requests kept. (The GUI and the CLI also clear the
    //     module IP on Stop - documented; the engine keeps ActivationIP.)
    // R10 Tables are bounded (TableSize); the locked module's kind is never evicted.
    // R11 Lock loss, stop, exit and a switch to another module IP center the 8 axes and release the buttons;
    //     channels a frame does not carry are neutral; short gaps hold the last values (JoystickMap).
    // R12 Tick (lock release, retries, held restarts, target re-POST) runs at least every 100 ms whatever
    //     traffic arrives.
    // R13 One automatic plain activation POST (a beacon, the module IP's tries) per module per THROTTLE_SEC;
    //     SetTarget, RestartStream and restarts go past it, and a restart restarts it.
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
        // One plain activation POST per module per this (R13); a failed restart is sent again this long after
        // its last send (R6).
        internal const double THROTTLE_SEC = 5.0;
        // Sends of a restart that fails (R6), per restart (a request change, an explicit one, a mismatch).
        internal const int RestartTries = 3;
        // What a Crossfire module puts in a channel it has no data for (62058).
        internal const int XF_PLACEHOLDER = 0xF26A;
        internal const string NoStickState = "module found, but it is sending no stick data";
        // Per-module tables, and modules with a POST waiting for its answer (the Linux core's WIFI_TABLE): a
        // host sending beacons from many addresses can't grow memory or tie up a thread per address.
        internal const int TableSize = 8;

        // One shared HttpClient for the whole app (creating one per request can exhaust sockets).
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        // Monotonic, on a UTC base: a step of the system clock (the end of daylight saving time, NTP, by hand)
        // must not hold the lock timeout, the activation throttle or the Crossfire wait for an hour, nor skip them.
        private static readonly DateTime ClockBase = DateTime.UtcNow;
        private static readonly Stopwatch ClockWatch = Stopwatch.StartNew();
        internal static DateTime MonotonicNow() => ClockBase + ClockWatch.Elapsed;

        // ---- test seams (production behaviour is unchanged when these are left alone) ----
        // The CLI compiles this file too and never sets them: no "never assigned" warnings for them there.
#pragma warning disable CS0649
        // Injectable clock so time-based logic (lock timeout, activation throttle) is testable.
        internal Func<DateTime> Clock = MonotonicNow;
        // When set, channel output goes here instead of vJoy (tests record what was applied).
        internal Action<int[]>? OutputOverride;
        // When set, replaces each HTTP POST (tests must never touch the network) and runs synchronously.
        // Returns whether the POST succeeded.
        internal Func<ActivationPost, bool>? ActivateOverride;
        // Skips vJoy init/teardown so the socket loop can be tested without the driver.
        internal bool SkipVJoyForTest;
        // When set, the joystick (axes and buttons, through the map) is written here instead of vJoy:
        // tests record the SetAxis/SetBtn calls.
        internal IJoystickOutput? DeviceForTest;
        // Runs ActivateOverride on the per-module POST chains, off the calling thread, like the real POSTs.
        internal bool AsyncPostsForTest;
#pragma warning restore CS0649

        private vJoy joystick = new vJoy();
        private uint deviceId;
        private IJoystickOutput? output;                // the acquired vJoy device (engine thread)
        private readonly JoystickMap map = new();       // engine thread; ButtonMask is read by UIs
        private bool driving;                           // the joystick shows a module's frames (engine thread)
        private bool writeFailed;                       // a vJoy write failed, none got through since (engine thread)
        private volatile bool running;
        // Set by Stop until the next Start: from then on nothing is POSTed, so a request changed after
        // Stop (or a frame still in flight) can't start or restart a module. The lock itself stays
        // until the loop exits - clearing it here would let that frame lock again for a moment.
        private volatile bool stopRequested;
        // New on every Start and Stop: what an earlier session queued is not POSTed, and its answers count
        // for nothing (the Linux core's activation queue session). Stop also abandons the POSTs in flight.
        private int session;
        private volatile CancellationTokenSource postCancel = new();
        private Thread? thread;
        private UdpClient? udp;

        private volatile string? boundSource;   // also cleared by the UI thread via SetTarget
        private DateTime boundSourceLastData;
        private readonly HashSet<string> noStickData = new();
        private DateTime noStickLast;                   // the last placeholder frame while not locked (engine thread)
        private volatile EngineState state = EngineState.Stopped;     // the last one said (SetState)
        private volatile bool noStickShown;             // ... and it is Searching NoStickState
        private readonly HashSet<string> warned = new();
        // The last activation POST per module (the throttle, R13) and the request it carried (R3).
        private readonly IpTable<Activation> lastActivation = new();
        private readonly record struct Activation(DateTime At, int IntervalUs, int Channels);
        // Set from an activation task thread, read/cleared on the engine thread. The lock is taken (and said)
        // under it too, so an answer racing the first frame can't arm the "no data" hint after the lock, and
        // SetTarget drops a lock under it.
        private readonly object activationGate = new();
        private DateTime? firstActivationTime;
        private bool firewallHintShown;

        // The request (kept across sessions; written by UI threads, read by the engine and POST threads)
        // and what the app learnt about modules. All under requestGate.
        private readonly object requestGate = new();
        private volatile int intervalUs = StreamRequest.IntervalDefault;
        private volatile int channels = StreamRequest.ChannelsDefault;
        // The kind of each module from its beacons. Kept across sessions (R9): a module keeps its kind while it
        // keeps its IP, and an ELRS module streams on after Stop and never beacons while it streams. The kind
        // decides whether a joystick_end may be sent, so the locked module's is never evicted (R10).
        private readonly IpTable<ModuleKind> kinds = new();
        private readonly HashSet<string> restarted = new();     // restarted this session: a mismatch restarts once
        // What each module was last asked for (R3), as far as this app knows: the request of a restart sent to it
        // or of an activation after its ELRS beacon (it was not streaming then), else, from its lock on, that of
        // the last activation sent to it this session, or the request when it locked (found streaming). Kept
        // across sessions - a module keeps streaming after Stop - so a change made while it was not locked
        // (WiFi dropout, Stop, its activation in flight) reaches it as a restart when it locks again.
        // RequestUnknown: a restart of it did not take (it failed, R6, or Stop cut it short) - it may still stream
        // its old request, so its next lock restarts it (CheckRequest).
        private readonly IpTable<(int IntervalUs, int Channels)> requestOf = new();
        private static readonly (int IntervalUs, int Channels) RequestUnknown = (0, 0);
        private bool xfNoted;                                    // "Crossfire ignores the request" logged
        private volatile bool restartWait;                       // a request restart waiting to know it is no Crossfire
        private string? restartWaitIp;                           // ... of this module (held through its lock loss)
        // A restart that failed (R6): sent again to retryIp from retryAt while that module stays locked.
        private string? retryIp;
        private DateTime retryAt;
        // The locked module whose restart failed RestartTries times: not restarted again for its old request
        // while this lock lasts (its next lock restarts it, R3).
        private string? gaveUpIp;
        private int restartTries;                                // sends of the current restart
        private long restartSeq;                                 // the last restart sent: only its result counts
        private string? restartSentIp;                           // ... to this module
        private DateTime restartSentAt;                          // ... and when it went out
        private DateTime boundSince;                             // when the lock was taken
        private volatile int boundChannels;                      // channel count of the last applied frame
        private int restartCount;
        // The POSTs to one module go out one after the other, in order: a restart's joystick_end must
        // never overtake an earlier joystick_begin to the same module, nor its begin the end.
        private readonly Dictionary<string, Task> postChains = new();

        // stats (high-resolution Stopwatch timestamps -> accurate jitter, not ~15ms-granular)
        private readonly List<long> arrivalTicks = new();
        private long lastStats = Stopwatch.GetTimestamp();
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

        // From the beacons seen (Unknown until one arrives: Crossfire beacons ~every 8 s, ELRS only while it is
        // not streaming). Kept across sessions (R9).
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

        // A module IP the way the Linux core's wifi_parse_ip takes it: a.b.c.d, decimal parts 0-255 without
        // leading zeros (HttpClient would read those as octal: another host), spaces/tabs/line ends around it
        // ignored, not 0.0.0.0. Blank: true and null (auto-discover). ip is the text frames are compared with.
        public static bool TryParseModuleIp(string? text, out string? ip)
        {
            ip = null;
            string s = (text ?? "").Trim(' ', '\t', '\r', '\n');
            if (s.Length == 0) return true;
            string[] parts = s.Split('.');
            if (parts.Length != 4) return false;
            var b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                string p = parts[i];
                if (p.Length == 0 || p.Length > 3 || (p.Length > 1 && p[0] == '0')) return false;
                int v = 0;
                foreach (char c in p)
                {
                    if (c < '0' || c > '9') return false;
                    v = v * 10 + (c - '0');
                }
                if (v > 255) return false;
                b[i] = (byte)v;
            }
            if (b.All(x => x == 0)) return false;
            ip = new IPAddress(b).ToString();
            return true;
        }

        public void Start()
        {
            if (running) return;
            // Wait for any previous run to fully exit so its UDP socket is released before
            // we bind again (otherwise a quick Stop->Start races on the port).
            thread?.Join(1500);
            Interlocked.Increment(ref session);
            postCancel = new CancellationTokenSource();
            stopRequested = false;
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "joystick-engine" };
            thread.Start();
        }

        // stopRequested first, so a Start that sees running == false always comes after it.
        public void Stop()
        {
            stopRequested = true;
            Interlocked.Increment(ref session);
            try { postCancel.Cancel(); } catch { /* a POST's cancel callback: the POST is abandoned anyway */ }
            running = false;
        }

        // Stop and wait for the worker to finish (so vJoy is released cleanly) - used on exit.
        public void StopAndWait(int ms)
        {
            Stop();
            try { thread?.Join(ms); } catch { }
        }

        // Set/replace the module IP to activate, live (Connect). Blank: discovery by beacon. Not an IPv4
        // address (a.b.c.d): logged, the target kept, false. The module streaming to us now (locked, data within
        // SOURCE_TIMEOUT_SEC): the lock stays - unlocking it would center the joystick for a moment for nothing,
        // and a joystick_begin would not change its stream - nothing is sent, logged (R7). Else the lock goes -
        // the engine thread centers the joystick on its next tick - the state says Searching "connecting to
        // <ip>", and the activation POST goes out at once (off this thread), past the throttle. A restart to send
        // again or held for another module is dropped: that module is being left (wifi_proto_set_target).
        public bool SetTarget(string? text) => SetTarget(text, null);

        // The module IP and the request changed together, in one step (R8; the Linux app's settings change):
        // the request (clamped) is stored, and the module IP set as SetTarget does. Another module than the one
        // streaming now: that one is being left and gets nothing - no restart, also not from a frame of it
        // handled at the same moment - and the new one's joystick_begin carries the request. The module
        // streaming now kept, or none locked, or a blank module IP: the change works as SetRequest (R2/R3).
        // An invalid module IP changes nothing (false). Any thread.
        public bool SetTarget(string? text, int intervalUs, int channels) =>
            SetTarget(text, (StreamRequest.ClampInterval(intervalUs), StreamRequest.ClampChannels(channels)));

        private bool SetTarget(string? text, (int IntervalUs, int Channels)? request)
        {
            if (!TryParseModuleIp(text, out string? ip))
            {
                Log?.Invoke($"Invalid module IP \"{StreamRequest.Printable((text ?? "").Trim())}\" - use a.b.c.d");
                return false;
            }
            if (ip == null)
            {
                ActivationIP = null;
                if (request is { } r0) SetRequest(r0.IntervalUs, r0.Channels);    // no module is being left
                return true;
            }
            bool live = !stopRequested;
            lock (lastActivation) lastActivation.Remove(ip);        // allow immediate (re)activation
            bool streaming;
            RestartPlan? plan = null;
            // Under the gate a lock is taken and said under: a lock of the module being left that is being taken
            // right now says Streaming before this says Searching (or sees the new module IP and is not taken),
            // and a frame of the new module locks after it.
            lock (activationGate)
            {
                streaming = live && boundSource == ip &&
                            (Clock() - boundSourceLastData).TotalSeconds <= SOURCE_TIMEOUT_SEC;
                if (live && !streaming) SetState(EngineState.Searching, $"connecting to {ip}");
                ActivationIP = ip;
                // The request and the lock drop in one step under requestGate: a frame of the module being left
                // (past the module IP check already) is checked (CheckRequest) either before both - the old
                // request - or after both, unlocked: never restarted for the new request.
                lock (requestGate)
                {
                    if (retryIp != ip) retryIp = null;
                    if (restartWaitIp != ip) restartWait = false;
                    if (request is { } r && (r.IntervalUs != intervalUs || r.Channels != channels))
                    {
                        intervalUs = r.IntervalUs;
                        channels = r.Channels;
                        if (streaming) plan = PlanChangeLocked(ip);     // the module kept: like SetRequest
                    }
                    if (!streaming) boundSource = null;             // let the new target take over
                }
            }
            if (plan is RestartPlan p) Carry(p, ip, null);
            if (streaming)
            {
                Log?.Invoke($"Target module set to {ip} - streaming from it already");
                return true;
            }
            Log?.Invoke($"Target module set to {ip} - connecting...");
            if (live) EnsureStreaming(ip);
            return true;
        }

        // Only store the request (clamped), restarting nothing (wifi_proto_store_request). A host that changes the
        // module IP and the request together uses SetTarget(text, intervalUs, channels), which does both in one
        // step: between this and a SetTarget, a frame of the module streaming now finds the new request and
        // restarts it (CheckRequest). Any thread.
        public void StoreRequest(int intervalUs, int channels)
        {
            lock (requestGate)
            {
                this.intervalUs = StreamRequest.ClampInterval(intervalUs);
                this.channels = StreamRequest.ClampChannels(channels);
            }
        }

        // Set what joystick_begin asks for; values are clamped to the supported range. The Linux core's
        // wifi_proto_set_request: changed while locked to a module that is not Crossfire -> its stream is
        // restarted so they apply (ELRS takes a new request only while not streaming; the restart may wait
        // to know the module is no Crossfire); locked to Crossfire -> logged once per session, nothing sent
        // (it ignores them); not locked -> the next activation carries them, and a module this app knows
        // still streams the old request is restarted when it locks (CheckRequest). Any thread. Returns true
        // when the (clamped) values changed.
        public bool SetRequest(int intervalUs, int channels)
        {
            intervalUs = StreamRequest.ClampInterval(intervalUs);
            channels = StreamRequest.ClampChannels(channels);
            string? src;
            RestartPlan plan;
            lock (requestGate)
            {
                if (intervalUs == this.intervalUs && channels == this.channels) return false;
                this.intervalUs = intervalUs;
                this.channels = channels;
                src = stopRequested ? null : boundSource;
                if (src == null) return true;
                // decided with the change, so a frame's mismatch check can't restart it a second time
                if (PlanChangeLocked(src) is not RestartPlan p) return true;
                plan = p;
            }
            Carry(plan, src, null);
            return true;
        }

        // Under requestGate, the request just changed while src is locked (R2): Crossfire -> noted once per
        // session (null once noted); else restarted, or held to know it is no Crossfire (PlanRestartLocked).
        private RestartPlan? PlanChangeLocked(string src)
        {
            if (KindLocked(src) != ModuleKind.Crossfire) return PlanRestartLocked(src, src);
            if (xfNoted) return null;
            xfNoted = true;
            return new RestartPlan(Plan.NoteCrossfire, intervalUs, channels, 0);
        }

        // Restart the stream of the locked module, else of the module IP, so it uses the request now:
        // POST joystick_end, then joystick_begin (off the receive thread, past the 5 s activation
        // throttle, which it then restarts). Locked to Crossfire: logged, nothing sent - it ignores the request
        // and what joystick_end does to it is unknown. Returns false when there is no module or it is the
        // locked Crossfire. Any thread. (wifi_proto_restart, R4)
        //
        // A locked module that may be Crossfire - 16 channels and no beacon from it yet - is restarted only once
        // XF_BEACON_SEC of lock passed without a Crossfire beacon (true is returned at once: the restart is
        // accepted, and held, also through a lock loss of that module). Its beacon -> only noted (see
        // ResolveRestartWait). The module IP, not locked and not known as ELRS by its beacon (unknown, or
        // Crossfire), only gets a joystick_begin past the throttle: a module that is not streaming takes the
        // request from that alone. A restart that fails is sent again (R6, RetryRestart).
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
            RestartPlan plan;
            lock (requestGate) plan = PlanRestartLocked(ip, bound);
            return Carry(plan, ip, null);
        }

        // What a restart does now: decided under requestGate (PlanRestartLocked), done outside it (Carry).
        private enum Plan { Crossfire, NoteCrossfire, Wait, Waiting, Activate, ActivateCrossfire, Restart }

        private readonly record struct RestartPlan(Plan Kind, int IntervalUs, int Channels, long SecondsLeft);

        // Under requestGate. Locked to Crossfire: nothing - it ignores the request, and what joystick_end does to
        // it is unknown. Locked and maybe Crossfire (KindUnsureLocked): held until XF_BEACON_SEC of lock passed
        // without its beacon (restartWait, restartWaitIp). Not locked and not known as ELRS (unknown, or
        // Crossfire): a plain joystick_begin, never a joystick_end - it may be a Crossfire that has not beaconed
        // yet (the firewall blocks UDP, or it just came up), and a module that is not streaming takes the request
        // from the begin alone. Else joystick_end + joystick_begin: frames of the old request keep coming until
        // the module has it, so no mismatch restart for them, and from then on it streams the request.
        private RestartPlan PlanRestartLocked(string ip, string? bound)
        {
            int iv = intervalUs, ch = channels;
            var k = KindLocked(ip);
            bool locked = ip == bound;
            if (locked && k == ModuleKind.Crossfire) return new RestartPlan(Plan.Crossfire, iv, ch, 0);
            if (locked && KindUnsureLocked(ip))
            {
                bool waiting = restartWait && restartWaitIp == ip;
                restartWait = true;
                restartWaitIp = ip;
                var t = boundSince.AddSeconds(XF_BEACON_SEC) - Clock();
                long left = (t.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
                return new RestartPlan(waiting ? Plan.Waiting : Plan.Wait, iv, ch, left);
            }
            if (!locked && k != ModuleKind.Elrs)
                return new RestartPlan(k == ModuleKind.Crossfire ? Plan.ActivateCrossfire : Plan.Activate, iv, ch, 0);
            // a restart held for it (its window passed a moment ago, not resolved yet) is this one: not a second
            if (restartWaitIp == ip) restartWait = false;
            AddCapped(restarted, ip);
            requestOf.Set(ip, (iv, ch), boundSource, ActivationIP);
            return new RestartPlan(Plan.Restart, iv, ch, 0);
        }

        // Does what PlanRestartLocked decided: logs, POSTs. why: the log line of a restart (default: the
        // explicit one's). False for the locked Crossfire.
        private bool Carry(RestartPlan plan, string ip, string? why)
        {
            string what = $"{StreamRequest.Describe(plan.IntervalUs)}, {plan.Channels} channels";
            switch (plan.Kind)
            {
                case Plan.Crossfire:
                    Log?.Invoke($"Crossfire module {ip} always sends {StreamRequest.CrossfireChannels} channels at its " +
                                "own rate (about 90 Hz) - nothing to restart");
                    return false;
                case Plan.NoteCrossfire:
                    NoteCrossfireIgnores(ip);
                    return true;
                case Plan.Wait:
                    Log?.Invoke($"{ip} may be Crossfire (no beacon from it yet), which is never sent joystick_end: " +
                                $"restarting its stream within {plan.SecondsLeft} s unless its beacon says Crossfire");
                    return true;
                case Plan.Waiting:
                    return true;
                case Plan.Activate:
                case Plan.ActivateCrossfire:
                    Log?.Invoke(plan.Kind == Plan.ActivateCrossfire
                        ? $"Crossfire module {ip} is not streaming to us: activating it (it always sends " +
                          $"{StreamRequest.CrossfireChannels} channels at about 90 Hz)"
                        : $"{ip} has sent no beacon yet (it may be Crossfire, which is never sent joystick_end): " +
                          $"activating it with {what}");
                    lock (lastActivation) lastActivation.Set(ip, new Activation(Clock(), plan.IntervalUs, plan.Channels));
                    Post(ip, restart: false, plan.IntervalUs, plan.Channels);
                    return true;
                default:
                    Log?.Invoke(why ?? $"Restarting the stream of {ip}: {what}");
                    SendRestart(ip, plan.IntervalUs, plan.Channels, fresh: true);
                    return true;
            }
        }

        private void NoteCrossfireIgnores(string ip) =>
            Log?.Invoke($"Crossfire module {ip} ignores the rate and channel count: it always sends " +
                        $"{StreamRequest.CrossfireChannels} channels at about 90 Hz");

        // Locked, no beacon from it yet, 16 channels, not yet XF_BEACON_SEC of lock: maybe Crossfire.
        private bool KindUnsureLocked(string src) =>
            KindLocked(src) == ModuleKind.Unknown && boundChannels == StreamRequest.CrossfireChannels &&
            (Clock() - boundSince).TotalSeconds < XF_BEACON_SEC;

        // A held restart (R2), once its module is known: a Crossfire beacon from it -> noted (once per session),
        // dropped, also while it is not locked; else, while that module is locked and past its window ->
        // restarted. Through a lock loss of its module it is held until that module locks again (a module that
        // streams on through a WiFi pause keeps the old request until it is restarted; its window starts again
        // with the lock). Dropped elsewhere when another module locks or is set as the module IP, when its ELRS
        // beacon gets it a joystick_begin carrying the request (EnsureStreaming), on Stop and reset. Called from
        // Tick and from every applied frame.
        private void ResolveRestartWait()
        {
            if (!restartWait) return;
            string? src = boundSource;
            RestartPlan plan;
            string ip;
            lock (requestGate)
            {
                if (!restartWait || restartWaitIp == null) return;
                if (stopRequested)
                {
                    restartWait = false;
                    return;
                }
                ip = restartWaitIp;
                if (KindLocked(ip) == ModuleKind.Crossfire)
                {
                    restartWait = false;
                    if (xfNoted) return;
                    xfNoted = true;
                    plan = new RestartPlan(Plan.NoteCrossfire, intervalUs, channels, 0);
                }
                else
                {
                    if (src != ip || KindUnsureLocked(ip)) return;
                    restartWait = false;
                    plan = PlanRestartLocked(ip, ip);
                }
            }
            Carry(plan, ip, null);
        }

        // A failed restart (R6), sent again from retryAt while its module stays locked and is not known as
        // Crossfire; else dropped: what it streams stays unknown (RestartFailed), so its next lock restarts it
        // (R3), unless its ELRS beacon gets it a joystick_begin carrying the request first. Tick.
        private void RetryRestart()
        {
            string ip;
            int iv, ch;
            lock (requestGate)
            {
                if (retryIp == null) return;
                ip = retryIp;
                if (stopRequested || boundSource != ip || KindLocked(ip) == ModuleKind.Crossfire)
                {
                    retryIp = null;
                    return;
                }
                if (Clock() < retryAt) return;
                retryIp = null;
                iv = intervalUs;
                ch = channels;
                requestOf.Set(ip, (iv, ch), boundSource, ActivationIP);
            }
            SendRestart(ip, iv, ch, fresh: false);
        }

        // A module still streaming another request than the one asked for keeps it until it is restarted:
        // another app's (seen as a channel count other than asked, R5), or this app's from before a change made
        // while it was not locked (requestOf, R3). Only where it surely is no Crossfire, which ignores the
        // request: ELRS by its beacon, a channel count Crossfire never sends, or XF_BEACON_SEC of lock without a
        // Crossfire beacon (until then the restart waits, see PlanRestartLocked); a Crossfire with an old request
        // is only noted. A channel mismatch once per module and session (never when that table is full), so a
        // module that doesn't do what is asked is never restarted per frame; an old request once per change, and
        // one a restart of it did not take (RequestUnknown) once per lock. Not while a restart of it is held or to
        // be sent again, nor after its restart gave up while that lock lasts, nor for a frame of a module SetTarget
        // let go of meanwhile. The frames are applied all the same.
        private void CheckRequest(string src, int count)
        {
            if (stopRequested || restartWait) return;
            RestartPlan plan;
            string? why = null;
            lock (requestGate)
            {
                if (boundSource != src || restartWait || retryIp == src || gaveUpIp == src) return;
                bool xf = KindLocked(src) == ModuleKind.Crossfire;
                bool mismatch = !xf && count != channels && !restarted.Contains(src) && restarted.Count < TableSize;
                bool old = requestOf.TryGetValue(src, out var had) && (had.IntervalUs != intervalUs || had.Channels != channels);
                if (xf)
                {
                    if (!old || xfNoted) return;
                    xfNoted = true;
                    plan = new RestartPlan(Plan.NoteCrossfire, intervalUs, channels, 0);
                }
                else
                {
                    if (!mismatch && !old) return;
                    string asked = $"{StreamRequest.Describe(intervalUs)}, {channels} channels";
                    why = mismatch
                        ? $"{src} streams {count} channels, {channels} asked for (another app's request?) - restarting its stream"
                        : had == RequestUnknown
                        ? $"{src} may still stream its old request (its last restart did not take) - restarting its stream with {asked}"
                        : $"{src} still streams {StreamRequest.Describe(had.IntervalUs)}, {had.Channels} channels from " +
                          $"before the change - restarting its stream with {asked}";
                    plan = PlanRestartLocked(src, src);
                }
            }
            Carry(plan, src, why);
        }

        // Every state said goes through here: what it says now decides whether "sending no stick data" is shown.
        private void SetState(EngineState s, string detail = "")
        {
            state = s;
            noStickShown = s == EngineState.Searching && detail == NoStickState;
            StateChanged?.Invoke(s, detail);
        }

        // Fresh session state (R9) - a previous run's source lock must not leak into the next
        // one, or the "locked -> Streaming" transition never fires again after a restart. The lock, the
        // throttles and the restart state go; what is known about modules stays: their kind from beacons and
        // what each was asked for (requestOf) - a module streams on after Stop, and an ELRS module that
        // streams sends no beacon.
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
                restarted.Clear();
                xfNoted = false;
                restartWait = false;
                restartWaitIp = null;
                retryIp = null;
                gaveUpIp = null;
                restartSentIp = null;
                restartTries = 0;
            }
            boundChannels = 0;
            map.Clear();
            driving = false;
            writeFailed = false;
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
                    Fail("vJoy not enabled");
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
                    Fail("no free vJoy device");
                    return;
                }

                var st = joystick.GetVJDStatus(deviceId);
                if (st == VjdStat.VJD_STAT_BUSY || st == VjdStat.VJD_STAT_MISS)
                {
                    Log?.Invoke($"ERROR: vJoy device {deviceId} unavailable ({st}).");
                    Fail($"vJoy {st}");
                    return;
                }
                if (!joystick.AcquireVJD(deviceId))
                {
                    Log?.Invoke($"ERROR: failed to acquire vJoy device {deviceId}.");
                    Fail("acquire failed");
                    return;
                }
                joystick.ResetVJD(deviceId);
                map.Clear();    // after the reset the device state is unknown: the centering below writes it all
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
            // Centered and released from the start: vJoy's ResetVJD puts RX, RY, RZ and the sliders at their
            // minimum (throttle down, yaw at full deflection).
            CenterJoystick();

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
                if (!running)
                {
                    Finish();       // stopped before the port was bound: a stop, not a port error
                    return;
                }
                Log?.Invoke($"ERROR: cannot listen on UDP port {Port} - it may already be in use " +
                            $"(another instance, or another app on this port). {bindError?.Message}");
                Cleanup();
                Fail($"UDP port {Port} in use");
                return;
            }

            Log?.Invoke($"Listening for joystick data on UDP {Port}");
            Log?.Invoke($"Asking modules for {StreamRequest.Describe(intervalUs)}, {channels} channels " +
                        "(ELRS; Crossfire always sends 16 channels at about 90 Hz)");
            LogLocalAddresses();
            SetState(EngineState.Searching, "waiting for module");
            if (ActivationIP is string target) EnsureStreaming(target);

            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            long nextTick = 0;
            while (running)
            {
                try
                {
                    byte[] data = udp.Receive(ref remote);
                    Process(data, remote);
                }
                catch (SocketException)
                {
                    // the receive timeout (100 ms)
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"recv error: {ex.Message}");
                }

                // Every 100 ms, whatever arrives: traffic the engine ignores (another module still streaming to
                // this PC) never lets the receive time out, and must not hold the lock timeout, the module IP's
                // activation or the centering (the Linux core ticks on every poll).
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick)
                {
                    nextTick = now + Stopwatch.Frequency / 10;
                    Tick();
                }
                if (now - lastStats >= Stopwatch.Frequency)
                    EmitStats();
            }

            Finish();
        }

        // The run ends by itself (an error): like Stop, nothing may be POSTed until the next Start - and
        // before the state, so a UI that reads IsRunning on it sees that the run ended.
        private void Fail(string detail)
        {
            Stop();
            SetState(EngineState.Error, detail);
        }

        private void Finish()
        {
            Cleanup();
            boundSource = null;
            SetState(EngineState.Stopped, "");
            Log?.Invoke("Stopped. (The module keeps broadcasting until powered off.)");
        }

        // The joystick is left centered with the buttons released, then the device is let go. Not with
        // ResetVJD: that puts RX, RY, RZ and the sliders at their minimum (throttle down, yaw at full
        // deflection), and RelinquishVJD keeps whatever the device shows.
        private void Cleanup()
        {
            try { udp?.Close(); } catch { }
            try { CenterJoystick(); } catch { }
            if (!SkipVJoyForTest)
            {
                try { joystick.RelinquishVJD(deviceId); } catch { }
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
            string? bound = boundSource;
            bool quiet = bound != null && (Clock() - boundSourceLastData).TotalSeconds > SOURCE_TIMEOUT_SEC;
            if (quiet)
            {
                Log?.Invoke($"Source {bound} went quiet - releasing lock");
                boundSource = null;
            }
            // No module drives the joystick any more (the lock went above, or SetTarget dropped it): the axes
            // go to the center and the buttons are released - the sim must not keep stale input (the Linux
            // app does the same). A centering that did not get through is retried.
            if (boundSource == null && (driving || writeFailed)) CenterJoystick();
            if (quiet) SetState(EngineState.Searching, "waiting for module");

            // The module that sent placeholder frames went away (none for SOURCE_TIMEOUT_SEC): no more "sending
            // no stick data". It is said again when they come back (WarnNoStickData).
            if (noStickShown && boundSource == null && (Clock() - noStickLast).TotalSeconds > SOURCE_TIMEOUT_SEC)
                SetState(EngineState.Searching, "waiting for module");

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

            RetryRestart();
            ResolveRestartWait();

            string? target = ActivationIP;
            if (target != null && boundSource == null)
                EnsureStreaming(target);
        }

        internal void Process(byte[] data, IPEndPoint remote)
        {
            if (data.Length < 3) return;

            bool xf = StartsWith(data, XF_BEACON);
            if (xf || StartsWith(data, ELRS_BEACON))
            {
                string ip = remote.Address.ToString();
                if (ActivationIP != null && ip != ActivationIP) return; // only the chosen module
                lock (requestGate) kinds.Set(ip, xf ? ModuleKind.Crossfire : ModuleKind.Elrs, boundSource, ActivationIP);
                bool lockedOther = boundSource != null && boundSource != ip;
                bool streaming = boundSource == ip && (Clock() - boundSourceLastData).TotalSeconds < SOURCE_TIMEOUT_SEC;
                // ELRS beacons only while it is not streaming: it streams what this activation asks for.
                if (!lockedOther && !streaming)
                    EnsureStreaming(ip, $"Discovery beacon from {(xf ? "Crossfire" : "ELRS")} module {ip} - activating", idle: !xf);
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
                // A placeholder flood can't hold the lock: release a stale one at once (the loop ticks
                // every 100 ms anyway).
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
                    if (AddCapped(warned, src)) Log?.Invoke($"Ignoring 2nd source {src} (locked to {boundSource})");
                    return;
                }
            }
            if (boundSource == null)
            {
                DateTime now = Clock();
                lock (activationGate)
                {
                    // SetTarget may have changed the module IP since the check above (under this gate, with the
                    // lock it drops): never lock the module being left.
                    if (ActivationIP != null && src != ActivationIP) return;
                    bool sent;
                    Activation act;
                    lock (lastActivation) sent = lastActivation.TryGetValue(src, out act);
                    lock (requestGate)
                    {
                        boundSince = now;
                        // a restart held or to be sent again for another module is not wanted now; one held for
                        // this module goes on (R2): its window starts again with this lock
                        if (restartWaitIp != src) restartWait = false;
                        if (retryIp != src) retryIp = null;
                        // a new lock: a module whose restart gave up is restarted again, once (R3, RequestUnknown)
                        gaveUpIp = null;
                        // R3: not known what it streams - what the last activation sent to it this session asked
                        // for (it was not streaming then, or it streams what it streamed before, which nothing can
                        // tell), else the request now (found streaming). Either way a change of the request made
                        // while it is not locked from here on (a WiFi pause, a Stop) restarts it when it locks
                        // again: it streams on through those, keeping the old one.
                        if (!requestOf.TryGetValue(src, out _))
                            requestOf.Set(src, sent ? (act.IntervalUs, act.Channels) : (intervalUs, channels), src, ActivationIP);
                        boundChannels = count;      // with the lock: a request change on another thread sees it
                        boundSource = src;
                    }
                    boundSourceLastData = now;
                    firstActivationTime = null;
                    warned.Clear();
                    noStickData.Clear();
                    firewallHintShown = false;
                    // Said under the gate too: SetTarget's Searching can't come between this lock and its Streaming,
                    // so a lock it drops never leaves a green banner (the subscribers only marshal or log).
                    Log?.Invoke($"Joystick source locked to {src}");
                    SetState(EngineState.Streaming, src);
                }
            }
            boundSourceLastData = Clock();
            boundChannels = count;

            Apply(ch);
            ChannelsUpdated?.Invoke(ch);

            frameCount++;
            arrivalTicks.Add(Stopwatch.GetTimestamp());

            CheckRequest(src, count);
            ResolveRestartWait();
        }

        // Warn once per source, not ~90x/sec. The usual cause is a known TBS firmware
        // regression, so point at the fix instead of leaving the user hunting a network fault.
        // The state ends when no placeholder frame came for SOURCE_TIMEOUT_SEC (Tick: the module went away), and
        // is said again (not the log line) when they come back.
        private void WarnNoStickData(string src)
        {
            noStickLast = Clock();
            if (!AddCapped(noStickData, src))
            {
                if (state == EngineState.Searching && !noStickShown && noStickData.Contains(src))
                    SetState(EngineState.Searching, NoStickState);
                return;
            }
            Log?.Invoke($"!! {src} is streaming placeholder frames - no stick data. Either the radio " +
                        "is not feeding the module right now, or the firmware has the known regression " +
                        "(XF 6.42/6.48, WiFi 3.20 - use XF 6.31 or 6.36, WiFi up to 3.10).");
            SetState(EngineState.Searching, NoStickState);
        }

        // Test hook: inject a synthetic arrival timestamp (Stopwatch ticks) for stats tests.
        internal void AddArrivalForTest(long timestamp)
        {
            arrivalTicks.Add(timestamp);
            frameCount++;
        }

        internal void EmitStats()
        {
            lastStats = Stopwatch.GetTimestamp();
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

        // Where the joystick goes: the test's device, else the acquired vJoy device (none while
        // OutputOverride records the frames, or before vJoy is acquired).
        private IJoystickOutput? Device => DeviceForTest ?? (OutputOverride != null ? null : output);

        private void Apply(int[] ch)
        {
            driving = true;
            Wrote(map.Frame(ch, Device));
            OutputOverride?.Invoke(ch);
        }

        // The axes to the center, the buttons released (only what the device does not show already).
        // Engine thread.
        private void CenterJoystick()
        {
            driving = false;
            Wrote(map.CenterAll(Device));
        }

        // A vJoy write failed (the device was changed in 'Configure vJoy', or the driver restarted): said
        // once, and again when the writes get through (the map retries what failed on every frame).
        private void Wrote(bool ok)
        {
            if (ok != writeFailed) return;
            writeFailed = !ok;
            Log?.Invoke(ok ? "The vJoy device takes the sticks again"
                           : "!! Writing to the vJoy device failed - the sim may not see the sticks. " +
                             "Check the device in 'Configure vJoy' (still enabled?).");
        }

        // Adds ip to a per-module set unless the set is full (TableSize): a full set refuses, so a warning is
        // never repeated for every new address, nor a restart sent per frame.
        private static bool AddCapped(HashSet<string> set, string ip) =>
            !set.Contains(ip) && set.Count < TableSize && set.Add(ip);

        // An activation POST to ip, unless one went out less than THROTTLE_SEC ago (the throttle, R13) or
        // TableSize modules wait for an answer already (then the next beacon or tick tries again). why: logged
        // when it goes out. idle: the module is known not to stream (its ELRS beacon), so it will stream what this
        // asks for (R3), and a restart held, in flight or to be sent again for it has nothing left to do (R2, R6).
        private void EnsureStreaming(string ip, string? why = null, bool idle = false)
        {
            if (stopRequested) return;
            DateTime now = Clock();
            int iv, ch;
            lock (requestGate)
            {
                iv = intervalUs;
                ch = channels;
            }
            lock (lastActivation)
            {
                if (lastActivation.TryGetValue(ip, out var t) && (now - t.At).TotalSeconds < THROTTLE_SEC)
                    return;
                if (!PostSlotFree(ip)) return;
                lastActivation.Set(ip, new Activation(now, iv, ch));
            }
            if (idle)
            {
                lock (requestGate)
                {
                    requestOf.Set(ip, (iv, ch), boundSource, ActivationIP);
                    if (restartWaitIp == ip) restartWait = false;
                    if (retryIp == ip) retryIp = null;
                    // a restart of it still in flight decides nothing now: this joystick_begin, queued after it,
                    // carries the request (its failure must not make what it streams unknown)
                    if (restartSentIp == ip) restartSeq++;
                }
            }
            if (why != null) Log?.Invoke(why);
            Post(ip, restart: false, iv, ch);
        }

        // joystick_end + joystick_begin to ip, past the throttle (and it restarts the throttle, R13). fresh: a new
        // restart (a request change, an explicit one, a mismatch), with RestartTries sends of its own; else the
        // retry of a failed one (R6). Only the last restart sent counts: its result is RestartFailed's.
        private void SendRestart(string ip, int iv, int ch, bool fresh)
        {
            DateTime now = Clock();
            long seq;
            lock (requestGate)
            {
                if (fresh) restartTries = 0;
                restartTries++;
                retryIp = null;
                gaveUpIp = null;
                seq = ++restartSeq;
                restartSentIp = ip;
                restartSentAt = now;
            }
            Interlocked.Increment(ref restartCount);
            lock (lastActivation) lastActivation.Set(ip, new Activation(now, iv, ch));
            Post(ip, restart: true, iv, ch, seq);
        }

        // A POST to ip may be queued: its module has a chain already, or fewer than TableSize modules wait for
        // an answer (one that never answers holds its slot for the 5 s timeout).
        private bool PostSlotFree(string ip)
        {
            if (ActivateOverride != null && !AsyncPostsForTest) return true;      // synchronous: nothing waits
            lock (postChains)
            {
                PruneChains();
                return postChains.ContainsKey(ip) || postChains.Count < TableSize;
            }
        }

        // Under postChains: the modules whose POSTs all went out are forgotten.
        private void PruneChains()
        {
            if (postChains.Count == 0) return;
            foreach (var k in postChains.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
                postChains.Remove(k);
        }

        // seq: a restart's (SendRestart).
        private void Post(string ip, bool restart, int iv, int ch, long seq = 0)
        {
            if (stopRequested) return;      // stopping: the last packets of the loop activate nothing
            int s = Volatile.Read(ref session);
            CancellationToken token = postCancel.Token;

            if (ActivateOverride != null && !AsyncPostsForTest)
            {
                // Test seam stays synchronous so tests remain deterministic.
                Done(ip, restart, seq, Activate(ip, restart, iv, ch, s, token), s);
                return;
            }

            // The real HTTP POSTs run off-thread: a slow or unreachable module (5s timeout each) must not
            // stall the receive loop, and Stop/exit must not wait on them either.
            lock (postChains)
            {
                PruneChains();
                postChains.TryGetValue(ip, out var prev);
                postChains[ip] = (prev ?? Task.CompletedTask).ContinueWith(
                    _ => Done(ip, restart, seq, Activate(ip, restart, iv, ch, s, token), s),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        // The result of an activation (on its POST's thread). ok: it took - its joystick_begin got a 2xx and, for a
        // restart, its joystick_end too.
        private void Done(string ip, bool restart, long seq, bool ok, int s)
        {
            if (ok) MarkActivated(ip, s);
            else if (restart) RestartFailed(ip, seq, s);
        }

        // Test hook: waits until every POST queued so far went out (AsyncPostsForTest).
        internal bool WaitForPostsForTest(int ms)
        {
            Task[] chains;
            lock (postChains) chains = postChains.Values.ToArray();
            return Task.WaitAll(chains, ms);
        }

        // Queued in a session that has ended (Stop, or Stop and Start since).
        private bool Stale(int s) => stopRequested || s != Volatile.Read(ref session);

        // One activation. A restart POSTs joystick_end first; its failure does not stop the joystick_begin (the
        // module may simply not be streaming), but the restart took only when both got a 2xx: a streaming module
        // that missed the joystick_end just moved its stream and kept its old request (R6). True = it took.
        // Nothing of a session that has ended goes out.
        private bool Activate(string ip, bool restart, int iv, int ch, int s, CancellationToken token)
        {
            bool ok = false;
            if (!Stale(s))
            {
                bool endOk = !restart || SendPost(new ActivationPost(ip, ActionEnd, 0, 0, true), token);
                if (!Stale(s)) ok = SendPost(new ActivationPost(ip, ActionBegin, iv, ch, restart), token) && endOk;
            }
            // A restart Stop cut short (dropped from the queue, or abandoned in flight): the module may still
            // stream its old request, not what requestOf says since the restart was planned.
            if (restart && !ok && Stale(s)) RestartAbandoned(ip, iv, ch);
            return ok;
        }

        // A restart that failed (R6): the module kept what it streamed, as far as anyone knows, so what it was asked
        // is unknown from now on (RequestUnknown, R3). Still streaming to us, it is sent again THROTTLE_SEC after the
        // last send (RetryRestart, on Tick) while that module stays locked and is not known as Crossfire,
        // RestartTries sends in all; then giving up is logged, and it is left alone while that lock lasts. Once it
        // is not sent again (given up, the lock lost, a new session, another module IP), its next lock restarts it
        // (CheckRequest) - unless its ELRS beacon gets it a joystick_begin carrying the request first (it was not
        // streaming then). Only the last restart sent counts: when a newer one went out (or waits behind this
        // one), or such a joystick_begin was queued after it, that decides.
        private void RestartFailed(string ip, long seq, int s)
        {
            string msg;
            lock (requestGate)
            {
                if (Stale(s) || seq != restartSeq) return;
                if (KindLocked(ip) == ModuleKind.Crossfire) return;
                requestOf.Set(ip, RequestUnknown, boundSource, ActivationIP);
                if (boundSource != ip) return;
                if (restartTries >= RestartTries)
                {
                    gaveUpIp = ip;
                    msg = $"The restart of {ip} failed {restartTries} times - giving up: it may still stream its old rate " +
                          "and channel count";
                }
                else
                {
                    retryIp = ip;
                    retryAt = restartSentAt.AddSeconds(THROTTLE_SEC);
                    var left = retryAt - Clock();
                    msg = left > TimeSpan.Zero
                        ? $"The restart of {ip} failed - sending it again in " +
                          $"{(left.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond} s"
                        : $"The restart of {ip} failed - sending it again now";
                }
            }
            Log?.Invoke(msg);
        }

        // What ip streams is not known, but likely not the request: its next lock restarts it (CheckRequest).
        // Only at a session's end, so never a restart per frame.
        private void RestartAbandoned(string ip, int iv, int ch)
        {
            lock (requestGate)
                if (requestOf.TryGetValue(ip, out var r) && r == (iv, ch)) requestOf.Set(ip, RequestUnknown);
        }

        private bool SendPost(ActivationPost p, CancellationToken token)
        {
            var hook = ActivateOverride;
            return hook != null ? hook(p) : HttpPost(p, token);
        }

        private void MarkActivated(string ip, int s)
        {
            DateTime now = Clock();
            lock (activationGate)
            {
                if (Stale(s)) return;          // the answer of a session that has ended
                // ELRS starts streaming before it answers the POST, so the answer often comes after the lock,
                // and a restart's answer always does. Data from that module already proved the path: arming
                // the "no data" hint then would make it fire after any later lock loss (the Linux core's rule).
                // Under the gate the lock is taken under, so the lock can't come between this and the arming.
                if (ip == boundSource && (now - boundSourceLastData).TotalSeconds <= SOURCE_TIMEOUT_SEC)
                    return;
                firstActivationTime ??= now;
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

        private bool HttpPost(ActivationPost p, CancellationToken token)
        {
            bool end = p.Action == ActionEnd;
            try
            {
                using var form = new FormUrlEncodedContent(FormFields(p));
                using var resp = Http.PostAsync($"http://{p.Ip}/udpcontrol", form, token).GetAwaiter().GetResult();
                Log?.Invoke($"{(end ? "Stream end" : "Activation")} POST -> {p.Ip}: {(int)resp.StatusCode} {resp.StatusCode}");
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;   // Stop: abandoned, nothing to say
            }
            catch (Exception ex)
            {
                Log?.Invoke($"{(end ? "Stream end POST" : "Activation")} failed for {p.Ip}: {ex.Message}");
                return false;
            }
        }

        // A per-module table of at most TableSize entries. Full: the entry set longest ago goes, except those
        // of the locked module and the module IP (their kind decides whether a joystick_end may be sent).
        // Callers lock.
        private sealed class IpTable<T> where T : struct
        {
            private readonly Dictionary<string, (T Value, long Seq)> d = new();
            private long seq;

            public int Count => d.Count;

            public bool TryGetValue(string ip, out T value)
            {
                bool ok = d.TryGetValue(ip, out var e);
                value = e.Value;
                return ok;
            }

            public void Set(string ip, T value, string? keep1 = null, string? keep2 = null)
            {
                if (d.Count >= TableSize && !d.ContainsKey(ip))
                {
                    string? oldest = null;
                    long min = long.MaxValue;
                    foreach (var kv in d)
                        if (kv.Value.Seq < min && kv.Key != keep1 && kv.Key != keep2)
                        {
                            min = kv.Value.Seq;
                            oldest = kv.Key;
                        }
                    if (oldest != null) d.Remove(oldest);
                }
                d[ip] = (value, ++seq);
            }

            public bool Remove(string ip) => d.Remove(ip);
            public void Clear() => d.Clear();
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
