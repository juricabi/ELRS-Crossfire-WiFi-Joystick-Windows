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
        
        static void Main(string[] args)
        {
            Console.WriteLine("ExpressLRS WiFi Joystick for Windows");
            Console.WriteLine("====================================");
            
            
            // Parse command line arguments for port
            if (args.Length > 0 && int.TryParse(args[0], out int port))
            {
                LISTEN_PORT = port;
                Console.WriteLine($"Using custom port: {LISTEN_PORT}");
            }
            else
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
            Console.WriteLine($"\nListening for ExpressLRS WiFi Joystick on UDP port {LISTEN_PORT}...");
            Console.WriteLine("Make sure your ELRS TX module is connected to the same WiFi network!");
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
                Console.WriteLine($"Listening for joystick data on port {JOYSTICK_PORT}...");
                Console.WriteLine("Make sure your ELRS TX module is in WiFi Joystick mode!");
                
                udpClient = new UdpClient();
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, JOYSTICK_PORT));
                udpClient.Client.ReceiveTimeout = 100;
                
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                DateTime lastPacketTime = DateTime.Now;
                
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
                
                joystick.RelinquishVJD(deviceId);
                Console.WriteLine("\nCleaned up and exiting...");
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
        
        
        private static bool StartJoystickStreaming(string elrsIP)
        {
            try
            {
                Console.WriteLine($"Sending joystick start request to http://{elrsIP}/udpcontrol");
                
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
                    var response = client.PostAsync($"http://{elrsIP}/udpcontrol", formContent).Result;
                    
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
                        // Check if this is a service discovery packet (starts with "ELRS")
                        if (data.Length >= 4 && data[0] == 0x45 && data[1] == 0x4C && data[2] == 0x52 && data[3] == 0x53)
                        {
                            Console.WriteLine("Received service discovery packet - trying to activate joystick mode...");
                            
                            // Extract TX IP from the remote endpoint
                            string txIP = remoteEP.Address.ToString();
                            Console.WriteLine($"Detected ELRS TX IP: {txIP}");
                            
                            // Try to activate joystick mode via HTTP
                            if (StartJoystickStreaming(txIP))
                            {
                                Console.WriteLine("Joystick mode activated! Waiting for data...");
                                // Don't return - continue processing packets
                            }
                            else
                            {
                                Console.WriteLine("Failed to activate joystick mode");
                                return;
                            }
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

