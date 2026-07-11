using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using vJoyInterfaceWrap;

namespace ELRSWifiJoystick
{
    class Program
    {
        private static vJoy joystick = new vJoy();
        private static uint deviceId = 1;
        private static UdpClient? udpClient;
        private static bool running = true;
        private static int packetCount = 0;
        private static DateTime lastStatsTime = DateTime.Now;
        
        
        // ExpressLRS WiFi Joystick protocol constants
        private const int JOYSTICK_PORT = 11000;
        private static int LISTEN_PORT = JOYSTICK_PORT;

        // Discovery beacons broadcast on the joystick port. ExpressLRS TX modules
        // announce themselves with "ELRS"; TBS Crossfire/Tracer WiFi modules announce
        // with "VELOCIDRONE". Both are activated by the same POST /udpcontrol request
        // and then stream the identical [type][count][16-bit channels] frames.
        private static readonly byte[] ELRS_BEACON = Encoding.ASCII.GetBytes("ELRS");
        private static readonly byte[] CROSSFIRE_BEACON = Encoding.ASCII.GetBytes("VELOCIDRONE");

        // Optional module IP to activate proactively on startup (e.g. a Crossfire module
        // whose ~8s beacon interval you don't want to wait for). Set via --tx <ip>.
        private static string? activationIP;

        // Throttle repeated activation POSTs so the low-RAM module isn't hammered.
        private static readonly Dictionary<string, DateTime> lastActivation = new();

        // Single-source lock: only one module drives the virtual joystick at a time. ELRS
        // and Crossfire both broadcast their channel frames on this port, so without this a
        // second radio on the same network would fight the first and jitter the axes. The
        // lock binds to the first module streaming real data and releases if it goes quiet.
        private static string? boundSource;
        private static DateTime boundSourceLastData;
        private const double SOURCE_TIMEOUT_SEC = 3.0;
        private static readonly HashSet<string> warnedSources = new();
        
        static void Main(string[] args)
        {
            Console.WriteLine("ExpressLRS / TBS Crossfire WiFi Joystick for Windows");
            Console.WriteLine("====================================================");


            // Parse command line arguments: [port] [--tx <module-ip>]
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--tx" || a == "--crossfire" || a == "--activate") && i + 1 < args.Length)
                {
                    activationIP = args[++i];
                    Console.WriteLine($"Will activate module at {activationIP} on startup");
                }
                else if (a == "--help" || a == "-h" || a == "/?")
                {
                    Console.WriteLine("Usage: ELRSWifiJoystick.exe [port] [--tx <module-ip>]");
                    Console.WriteLine("  port           UDP listen port (default 11000)");
                    Console.WriteLine("  --tx <ip>      Activate this module immediately instead of");
                    Console.WriteLine("                 waiting for its discovery beacon (handy for");
                    Console.WriteLine("                 TBS Crossfire, e.g. --tx 192.168.2.138)");
                    return;
                }
                else if (int.TryParse(a, out int port))
                {
                    LISTEN_PORT = port;
                    Console.WriteLine($"Using custom port: {LISTEN_PORT}");
                }
            }
            if (LISTEN_PORT == JOYSTICK_PORT)
            {
                Console.WriteLine($"Using default port: {LISTEN_PORT}");
            }
            
            // Initialize vJoy
            joystick = new vJoy();
            
            if (!joystick.vJoyEnabled())
            {
                Console.WriteLine("ERROR: vJoy driver not enabled!");
                Console.WriteLine("Please install vJoy from http://vjoystick.sourceforge.net/");
                return;
            }
            
            Console.WriteLine($"vJoy Version: {joystick.GetvJoyVersion()}");
            
            // Find an available vJoy device
            deviceId = FindAvailableDevice();
            if (deviceId == 0)
            {
                Console.WriteLine("ERROR: No available vJoy devices found!");
                Console.WriteLine("Please configure more vJoy devices in 'Configure vJoy'");
                return;
            }
            
            Console.WriteLine($"Using vJoy Device {deviceId}");
            
            // Get the device status
            VjdStat status = joystick.GetVJDStatus(deviceId);
            
            switch (status)
            {
                case VjdStat.VJD_STAT_OWN:
                    Console.WriteLine($"vJoy Device {deviceId} is already owned by this feeder");
                    break;
                case VjdStat.VJD_STAT_FREE:
                    Console.WriteLine($"vJoy Device {deviceId} is free");
                    break;
                case VjdStat.VJD_STAT_BUSY:
                    Console.WriteLine($"ERROR: vJoy Device {deviceId} is already owned by another feeder");
                    return;
                case VjdStat.VJD_STAT_MISS:
                    Console.WriteLine($"ERROR: vJoy Device {deviceId} is not installed or disabled");
                    return;
                default:
                    Console.WriteLine($"ERROR: vJoy Device {deviceId} general error");
                    return;
            }
            
            // Acquire the vJoy device
            if (!joystick.AcquireVJD(deviceId))
            {
                Console.WriteLine($"ERROR: Failed to acquire vJoy device {deviceId}");
                return;
            }
            
            Console.WriteLine($"Acquired vJoy device {deviceId}");
            
            // Reset the device
            joystick.ResetVJD(deviceId);
            
            // Start listening for UDP packets
            Console.WriteLine($"\nListening for ExpressLRS / TBS Crossfire WiFi Joystick on UDP port {LISTEN_PORT}...");
            Console.WriteLine("Make sure your ELRS or Crossfire TX module is connected to the same WiFi network!");
            Console.WriteLine("Press Ctrl+C to exit\n");
            
            // Setup Ctrl+C handler
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                running = false;
            };
            
            try
            {
                // Listen for joystick data packets directly
                Console.WriteLine($"Listening for joystick data on port {LISTEN_PORT}...");
                Console.WriteLine("Make sure your ELRS/Crossfire TX module is in WiFi Joystick mode!");

                udpClient = new UdpClient();
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, LISTEN_PORT));
                udpClient.Client.ReceiveTimeout = 100;

                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                DateTime lastPacketTime = DateTime.Now;

                // Proactively activate a known module (e.g. Crossfire via --tx <ip>)
                // instead of waiting for its discovery beacon.
                if (activationIP != null)
                {
                    EnsureStreaming(activationIP);
                }
                
                while (running)
                {
                    try
                    {
                        // Receive joystick data packet
                        byte[] data = udpClient.Receive(ref remoteEP);
                        
                        lastPacketTime = DateTime.Now;
                        packetCount++;
                        
                        // Print stats every second instead of every packet
                        if ((DateTime.Now - lastStatsTime).TotalSeconds >= 1.0)
                        {
                            Console.WriteLine($"Receiving data: {packetCount} packets/sec from {remoteEP.Address}");
                            packetCount = 0;
                            lastStatsTime = DateTime.Now;
                        }
                        
                        // Parse and apply joystick data
                        ProcessELRSPacket(data, remoteEP);
                    }
                    catch (SocketException)
                    {
                        // Timeout - check if we lost connection
                        if ((DateTime.Now - lastPacketTime).TotalSeconds > 2)
                        {
                            Console.WriteLine("Waiting for joystick data...");
                            // Release a stale source lock so another module can take over.
                            if (boundSource != null
                                && (DateTime.Now - boundSourceLastData).TotalSeconds > SOURCE_TIMEOUT_SEC)
                            {
                                Console.WriteLine($"Source {boundSource} went quiet - releasing lock");
                                boundSource = null;
                            }
                            // Re-kick a known module in case its stream stopped or the
                            // initial activation POST was missed (throttled internally).
                            if (activationIP != null)
                            {
                                EnsureStreaming(activationIP);
                            }
                            Thread.Sleep(100);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
            }
            finally
            {
                // Cleanup
                if (udpClient != null)
                {
                    udpClient.Close();
                }

                // Center the axes before releasing so the virtual joystick doesn't freeze
                // at the last received stick position. Note: the module itself keeps
                // broadcasting until it is powered off / rebooted - it has no stop command.
                joystick.ResetVJD(deviceId);
                joystick.RelinquishVJD(deviceId);
                Console.WriteLine("\nCleaned up and exiting...");
                Console.WriteLine("(The TX module keeps broadcasting until powered off - it has no remote stop.)");
            }
        }
        
        private static uint FindAvailableDevice()
        {
            // Check devices 1-16 for availability
            for (uint id = 1; id <= 16; id++)
            {
                VjdStat status = joystick.GetVJDStatus(id);
                Console.WriteLine($"vJoy Device {id}: {status}");
                if (status == VjdStat.VJD_STAT_FREE)
                {
                    return id;
                }
            }
            return 0; // No available device found
        }
        
        
        // Throttled activation: POST /udpcontrol at most once every 5s per module IP so
        // the low-RAM WiFi module isn't flooded by repeated discovery beacons.
        private static void EnsureStreaming(string moduleIP)
        {
            lock (lastActivation)
            {
                if (lastActivation.TryGetValue(moduleIP, out DateTime t)
                    && (DateTime.Now - t).TotalSeconds < 5)
                {
                    return;
                }
                lastActivation[moduleIP] = DateTime.Now;
            }
            StartJoystickStreaming(moduleIP);
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i])
                    return false;
            }
            return true;
        }

        // Log a competing source once (not every packet) while it's being ignored.
        private static void WarnIgnoredSource(string ip)
        {
            if (warnedSources.Add(ip))
            {
                Console.WriteLine($"Ignoring joystick data from {ip} - already locked to {boundSource}");
            }
        }

        private static bool StartJoystickStreaming(string moduleIP)
        {
            try
            {
                Console.WriteLine($"Sending joystick start request to http://{moduleIP}/udpcontrol");
                
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    
                    // Use POST with form data like the Linux version
                    var formData = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("action", "joystick_begin"),
                        new KeyValuePair<string, string>("interval", "10000"),
                        new KeyValuePair<string, string>("channels", "8")
                    };
                    
                    var formContent = new FormUrlEncodedContent(formData);
                    var response = client.PostAsync($"http://{moduleIP}/udpcontrol", formContent).Result;
                    
                    Console.WriteLine($"HTTP Response: {response.StatusCode}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        string content = response.Content.ReadAsStringAsync().Result;
                        Console.WriteLine($"Success! Response: {content}");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"HTTP Error: {response.StatusCode}");
                        string content = response.Content.ReadAsStringAsync().Result;
                        Console.WriteLine($"Response content: {content}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting joystick streaming: {ex.Message}");
                return false;
            }
        }
        
        private static void ProcessELRSPacket(byte[] data, IPEndPoint remoteEP)
        {
            // ExpressLRS WiFi Joystick data packet format:
            // [Type (1 byte), Channel count (1 byte), Channel data (16-bit values)]
            
            if (data.Length < 3)  // Minimum packet size
                return;
            
            try
            {
                // Discovery beacon: ExpressLRS announces with "ELRS", TBS Crossfire/Tracer
                // with "VELOCIDRONE". Both are started by the same POST /udpcontrol. The
                // module keeps beaconing even while it streams, so only (re)activate when
                // we're NOT already receiving data from it and aren't locked to another
                // module - otherwise we'd needlessly re-POST on every beacon.
                bool isCrossfireBeacon = StartsWith(data, CROSSFIRE_BEACON);
                if (isCrossfireBeacon || StartsWith(data, ELRS_BEACON))
                {
                    string txIP = remoteEP.Address.ToString();
                    bool lockedToOther = boundSource != null && boundSource != txIP;
                    bool alreadyStreaming = boundSource == txIP
                        && (DateTime.Now - boundSourceLastData).TotalSeconds < SOURCE_TIMEOUT_SEC;
                    if (!lockedToOther && !alreadyStreaming)
                    {
                        Console.WriteLine($"Discovery beacon from {(isCrossfireBeacon ? "Crossfire" : "ELRS")} module {txIP} - activating joystick mode");
                        EnsureStreaming(txIP);
                    }
                    return;   // beacon carries no channel data
                }

                // Parse packet header
                int frameType = data[0];
                int channelCount = data[1];
                
                // Validate packet
                if (channelCount < 4 || channelCount > 16)
                {
                    return;
                }
                
                if (data.Length < 2 + channelCount * 2)
                {
                    return;
                }

                // Single-source lock: bind to the first module streaming real channel data
                // and ignore any other, so two radios can't fight over the joystick. If the
                // bound module goes quiet, release the lock so another can take over.
                string source = remoteEP.Address.ToString();
                if (boundSource != null && boundSource != source)
                {
                    if ((DateTime.Now - boundSourceLastData).TotalSeconds > SOURCE_TIMEOUT_SEC)
                    {
                        Console.WriteLine($"Source {boundSource} went quiet - releasing lock");
                        boundSource = null;
                    }
                    else
                    {
                        WarnIgnoredSource(source);
                        return;
                    }
                }
                if (boundSource == null)
                {
                    boundSource = source;
                    warnedSources.Clear();
                    Console.WriteLine($"Joystick source locked to {source}");
                }
                boundSourceLastData = DateTime.Now;

                // Parse channels (16-bit LITTLE ENDIAN values, 15-bit range: 0-32767)
                int[] channels = new int[channelCount];
                for (int i = 0; i < channelCount; i++)
                {
                    int offset = 2 + i * 2;
                    // CRITICAL: ELRS sends LITTLE ENDIAN (low byte first)
                    channels[i] = data[offset] | (data[offset + 1] << 8);
                }
                
                // Map channels to vJoy axes - pass through raw values
                // ELRS channels: 0=Roll, 1=Pitch, 2=Throttle, 3=Yaw
                
                // Roll -> X axis (channel 0)
                if (channels.Length > 0)
                {
                    joystick.SetAxis(channels[0], deviceId, HID_USAGES.HID_USAGE_X);
                }
                
                // Pitch -> Y axis (channel 1)
                if (channels.Length > 1)
                {
                    joystick.SetAxis(channels[1], deviceId, HID_USAGES.HID_USAGE_Y);
                }
                
                // Throttle -> RX axis (channel 2)
                if (channels.Length > 2)
                {
                    joystick.SetAxis(channels[2], deviceId, HID_USAGES.HID_USAGE_RX);
                }
                
                // Yaw -> RY axis (channel 3)
                if (channels.Length > 3)
                {
                    joystick.SetAxis(channels[3], deviceId, HID_USAGES.HID_USAGE_RY);
                }
                
                // Channel 4 -> RZ axis
                if (channels.Length > 4)
                {
                    joystick.SetAxis(channels[4], deviceId, HID_USAGES.HID_USAGE_RZ);
                }
                
                // Channel 5 -> Z axis
                if (channels.Length > 5)
                {
                    joystick.SetAxis(channels[5], deviceId, HID_USAGES.HID_USAGE_Z);
                }
                
                // Channel 6 -> Slider 0
                if (channels.Length > 6)
                {
                    joystick.SetAxis(channels[6], deviceId, HID_USAGES.HID_USAGE_SL0);
                }
                
                // Channel 7 -> Slider 1
                if (channels.Length > 7)
                {
                    joystick.SetAxis(channels[7], deviceId, HID_USAGES.HID_USAGE_SL1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing packet: {ex.Message}");
            }
        }
        
    }
}

